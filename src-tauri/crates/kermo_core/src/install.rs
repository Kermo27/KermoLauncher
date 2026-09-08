use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use sha2::{Digest, Sha256};

use crate::db::LocalDb;
use crate::download::DownloadService;
use crate::game_paths;
use crate::manifest_diff::{files_to_download, is_same_file, stale_files};
use crate::models::*;
use crate::paths::data_directory;
use crate::webdav::WebDavClient;
use crate::{Error, Result};

pub struct GameService {
    downloads: DownloadService,
    db: Arc<LocalDb>,
    webdav: WebDavClient,
}

impl GameService {
    pub fn new(downloads: DownloadService, db: Arc<LocalDb>, webdav: WebDavClient) -> Self {
        Self {
            downloads,
            db,
            webdav,
        }
    }

    pub async fn install(&self, game: &Game) -> Result<()> {
        self.install_or_update(game, false).await
    }

    pub async fn update(&self, game: &Game) -> Result<()> {
        self.install_or_update(game, false).await
    }

    pub async fn resume(&self, game: &Game) -> Result<()> {
        self.install_or_update(game, true).await
    }

    async fn install_or_update(&self, game: &Game, resume: bool) -> Result<()> {
        let mut local = self
            .db
            .get_local_state(&game.id)?
            .unwrap_or(GameLocalState {
                game_id: game.id.clone(),
                status: InstallStatus::NotInstalled,
                installed_path: None,
                play_time_seconds: 0,
                last_played: None,
                installed_version: None,
                installed_manifest: None,
            });

        let settings = self.db.get_settings()?;
        let config = settings
            .nextcloud
            .clone()
            .ok_or_else(|| Error::message("Nextcloud is not configured"))?;

        let install_root = if settings.install_folder.trim().is_empty() {
            data_directory().join("games")
        } else {
            PathBuf::from(&settings.install_folder)
        };

        self.discard_tasks_for_game(&game.id)?;

        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_secs() as i64)
            .unwrap_or(0);
        let task_id = format!("{}-{}", game.id, now);
        let staging_dir = install_root.join(".update").join(&game.id);
        let mut download_task = DownloadTask {
            id: task_id.clone(),
            game_id: game.id.clone(),
            remote_url: config.get_file_url(&game.manifest_url),
            local_path: staging_dir.to_string_lossy().into(),
            total_bytes: 0,
            downloaded_bytes: 0,
            status: DownloadStatus::Queued,
            error: None,
            started_at: Some(now),
            completed_at: None,
            install_stage: InstallStage::Preparing,
        };
        self.db.upsert_download_task(&download_task)?;

        let safe_name = game_paths::sanitize_folder_name(&game.name);
        let final_dir = install_root.join(safe_name);
        let installed_manifest = match local.status {
            InstallStatus::Installed | InstallStatus::Paused => local.installed_manifest.clone(),
            _ => None,
        };

        local.status = InstallStatus::Downloading;
        self.db.upsert_local_state(&local)?;

        let result = self
            .run_pipeline(
                game,
                &config,
                &mut local,
                &mut download_task,
                &staging_dir,
                &final_dir,
                installed_manifest.as_ref(),
                resume,
            )
            .await;

        match result {
            Ok(()) => Ok(()),
            Err(e) => {
                local.status = InstallStatus::Failed;
                let _ = self.db.upsert_local_state(&local);
                let _ = self.fail_task(&download_task.id, &e.to_string());
                let _ = delete_dir(&staging_dir);
                Err(e)
            }
        }
    }

    async fn run_pipeline(
        &self,
        game: &Game,
        config: &NextcloudConfig,
        local: &mut GameLocalState,
        download_task: &mut DownloadTask,
        staging_dir: &Path,
        final_dir: &Path,
        installed_manifest: Option<&GameManifest>,
        resume: bool,
    ) -> Result<()> {
        let manifest = self
            .webdav
            .download_manifest(&download_task.remote_url)
            .await?;

        let to_download = files_to_download(&manifest, installed_manifest);
        let stale = installed_manifest
            .map(|inst| stale_files(&manifest, inst))
            .unwrap_or_default();

        if installed_manifest.is_some()
            && installed_manifest.map(|m| m.version.as_str()) == Some(manifest.version.as_str())
            && to_download.is_empty()
            && stale.is_empty()
        {
            local.status = InstallStatus::Installed;
            self.db.upsert_local_state(local)?;
            self.complete_task(&download_task.id)?;
            return Ok(());
        }

        download_task.total_bytes = to_download.iter().map(|f| f.size_bytes).sum();
        download_task.status = DownloadStatus::Downloading;
        download_task.install_stage = InstallStage::Downloading;
        self.db.upsert_download_task(download_task)?;

        if !resume && staging_dir.exists() {
            delete_dir(staging_dir)?;
        }
        std::fs::create_dir_all(staging_dir)?;

        if let (Some(inst), true) = (installed_manifest, final_dir.is_dir()) {
            for file in &inst.files {
                if to_download.iter().any(|f| is_same_file(f, file)) {
                    continue;
                }
                if stale.iter().any(|f| is_same_file(f, file)) {
                    continue;
                }
                let source = game_paths::combine(final_dir, &file.path);
                if !source.is_file() {
                    continue;
                }
                let target = game_paths::combine(staging_dir, &file.path);
                if target.exists() {
                    continue;
                }
                if let Some(parent) = target.parent() {
                    std::fs::create_dir_all(parent)?;
                }
                std::fs::copy(&source, &target)?;
            }
        }

        let requests: Vec<DownloadFileRequest> = to_download
            .iter()
            .map(|file| DownloadFileRequest {
                key: file.path.clone(),
                remote_url: crate::game_file_url(config, &game.manifest_url, &file.path),
                local_path: game_paths::combine(staging_dir, &file.path)
                    .to_string_lossy()
                    .into(),
                size_bytes: file.size_bytes,
            })
            .collect();

        self.downloads
            .download_files(download_task, &requests)
            .await?;

        download_task.install_stage = InstallStage::Verifying;
        self.db.upsert_download_task(download_task)?;

        for file in &to_download {
            let local_path = game_paths::combine(staging_dir, &file.path);
            let sha = sha256_file(&local_path)?;
            if !sha.eq_ignore_ascii_case(&file.sha256) {
                return Err(Error::message(format!(
                    "Checksum mismatch for {}",
                    file.path
                )));
            }
        }

        download_task.install_stage = InstallStage::Extracting;
        self.db.upsert_download_task(download_task)?;

        if final_dir.exists() {
            let backup = PathBuf::from(format!("{}.backup", final_dir.display()));
            if backup.exists() {
                delete_dir(&backup)?;
            }
            std::fs::rename(final_dir, &backup)?;
            if let Err(e) = std::fs::rename(staging_dir, final_dir) {
                if final_dir.exists() {
                    let _ = delete_dir(final_dir);
                }
                let _ = std::fs::rename(&backup, final_dir);
                return Err(e.into());
            }
            let _ = delete_dir(&backup);
        } else {
            std::fs::rename(staging_dir, final_dir)?;
        }

        if let Some(launch) = &game.launch_config {
            game_paths::try_make_executable(&game_paths::combine(
                final_dir,
                &launch.executable_path,
            ));
        }

        local.status = InstallStatus::Installed;
        local.installed_path = Some(final_dir.to_string_lossy().into());
        local.installed_version = Some(manifest.version.clone());
        local.installed_manifest = Some(manifest);
        self.db.upsert_local_state(local)?;
        self.complete_task(&download_task.id)?;
        Ok(())
    }

    pub fn uninstall(&self, game_id: &str) -> Result<()> {
        let mut local = self.db.get_local_state(game_id)?.unwrap_or(GameLocalState {
            game_id: game_id.into(),
            status: InstallStatus::NotInstalled,
            installed_path: None,
            play_time_seconds: 0,
            last_played: None,
            installed_version: None,
            installed_manifest: None,
        });
        if let Some(path) = &local.installed_path {
            if Path::new(path).is_dir() {
                let _ = delete_dir(Path::new(path));
            }
        }
        local.status = InstallStatus::NotInstalled;
        local.installed_path = None;
        local.installed_version = None;
        local.installed_manifest = None;
        self.db.upsert_local_state(&local)?;
        Ok(())
    }

    fn discard_tasks_for_game(&self, game_id: &str) -> Result<()> {
        for task in self.db.get_all_download_tasks()? {
            if task.game_id == game_id {
                let _ = self.db.delete_download_task(&task.id);
            }
        }
        Ok(())
    }

    fn complete_task(&self, task_id: &str) -> Result<()> {
        let _ = self.db.delete_download_task(task_id);
        Ok(())
    }

    fn fail_task(&self, task_id: &str, error: &str) -> Result<()> {
        if let Some(mut task) = self.db.get_download_task(task_id)? {
            task.status = DownloadStatus::Failed;
            task.error = Some(error.into());
            self.db.upsert_download_task(&task)?;
        }
        Ok(())
    }
}

fn sha256_file(path: &Path) -> Result<String> {
    let data = std::fs::read(path)?;
    Ok(hex::encode(Sha256::digest(data)))
}

fn delete_dir(path: &Path) -> Result<()> {
    if path.exists() {
        std::fs::remove_dir_all(path)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use sha2::{Digest, Sha256};
    use tempfile::{tempdir, NamedTempFile};
    use wiremock::matchers::{method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    fn sha(bytes: &[u8]) -> String {
        hex::encode(Sha256::digest(bytes))
    }

    fn game(manifest_url: &str, size: i64) -> Game {
        Game {
            id: "g1".into(),
            name: "Game 1".into(),
            version: "1.0.0".into(),
            description: String::new(),
            tags: vec![],
            dependencies: vec![],
            screenshot_urls: vec![],
            manifest_url: manifest_url.into(),
            size_bytes: size,
            launch_config: None,
        }
    }

    async fn harness(install_dir: &Path) -> (GameService, Arc<LocalDb>, MockServer, NamedTempFile) {
        let server = MockServer::start().await;
        let db_file = NamedTempFile::new().unwrap();
        let db = Arc::new(LocalDb::open(db_file.path()));
        let share = format!("{}/s/tok", server.uri());
        db.save_settings(&AppSettings {
            nextcloud: Some(NextcloudConfig {
                share_url: share,
                share_token: "tok".into(),
                root_folder: String::new(),
            }),
            install_folder: install_dir.to_string_lossy().into(),
            onboarding_completed: true,
            ..AppSettings::default()
        })
        .unwrap();
        let g = game("g1/manifest.json", 0);
        db.upsert_games(&[g]).unwrap();
        let webdav = WebDavClient::new().unwrap();
        let downloads = DownloadService::new(webdav.clone(), db.clone());
        (
            GameService::new(downloads, db.clone(), webdav),
            db,
            server,
            db_file,
        )
    }

    #[tokio::test]
    async fn install_puts_files_in_place_and_clears_task() {
        let dir = tempdir().unwrap();
        let (svc, db, server, _db_file) = harness(dir.path()).await;
        let body = b"hello";
        let hash = sha(body);
        let manifest = format!(
            r#"{{"version":"1.0.0","totalBytes":5,"files":[{{"path":"data.bin","sizeBytes":5,"sha256":"{hash}"}}]}}"#
        );

        Mock::given(method("GET"))
            .and(path("/public.php/dav/files/tok/g1/manifest.json"))
            .respond_with(ResponseTemplate::new(200).set_body_string(manifest))
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path("/public.php/dav/files/tok/g1/data.bin"))
            .respond_with(ResponseTemplate::new(200).set_body_string("hello"))
            .mount(&server)
            .await;

        let g = game("g1/manifest.json", 5);
        svc.install(&g).await.unwrap();

        let state = db.get_local_state("g1").unwrap().unwrap();
        assert_eq!(state.status, InstallStatus::Installed);
        let dest = PathBuf::from(state.installed_path.unwrap()).join("data.bin");
        assert_eq!(std::fs::read(&dest).unwrap(), b"hello");
        assert!(db.get_all_download_tasks().unwrap().is_empty());
    }

    #[tokio::test]
    async fn install_fails_on_checksum_mismatch() {
        let dir = tempdir().unwrap();
        let (svc, db, server, _db_file) = harness(dir.path()).await;
        let manifest = r#"{"version":"1.0.0","totalBytes":5,"files":[{"path":"data.bin","sizeBytes":5,"sha256":"deadbeef"}]}"#;

        Mock::given(method("GET"))
            .and(path("/public.php/dav/files/tok/g1/manifest.json"))
            .respond_with(ResponseTemplate::new(200).set_body_string(manifest))
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path("/public.php/dav/files/tok/g1/data.bin"))
            .respond_with(ResponseTemplate::new(200).set_body_string("hello"))
            .mount(&server)
            .await;

        let g = game("g1/manifest.json", 5);
        let err = svc.install(&g).await.unwrap_err();
        assert!(err.to_string().contains("Checksum mismatch"));
        let state = db.get_local_state("g1").unwrap().unwrap();
        assert_eq!(state.status, InstallStatus::Failed);
    }
}
