use std::collections::{HashMap, HashSet};
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use sha2::{Digest, Sha256};

use crate::db::LocalDb;
use crate::download::DownloadService;
use crate::game_paths;
use crate::launch::{self, LaunchSpec};
use crate::manifest_diff::{files_to_download, is_same_file, stale_files};
use crate::models::*;
use crate::paths::data_directory;
use crate::webdav::WebDavClient;
use crate::{Error, Result};

#[derive(Clone)]
pub struct GameService {
    inner: Arc<Inner>,
}

struct Inner {
    downloads: DownloadService,
    db: Arc<LocalDb>,
    webdav: WebDavClient,
    installs: Mutex<Installs>,
}

struct Installs {
    active: HashMap<String, Arc<AtomicBool>>,
    paused: HashSet<String>,
}

impl GameService {
    pub fn new(downloads: DownloadService, db: Arc<LocalDb>, webdav: WebDavClient) -> Self {
        Self {
            inner: Arc::new(Inner {
                downloads,
                db,
                webdav,
                installs: Mutex::new(Installs {
                    active: HashMap::new(),
                    paused: HashSet::new(),
                }),
            }),
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

    pub fn pause(&self, game_id: &str) {
        let mut installs = self.inner.installs.lock().expect("installs");
        if let Some(flag) = installs.active.get(game_id).cloned() {
            installs.paused.insert(game_id.into());
            flag.store(true, Ordering::Relaxed);
        }
    }

    pub fn cancel(&self, game_id: &str) -> Result<()> {
        let had_active = {
            let mut installs = self.inner.installs.lock().expect("installs");
            installs.paused.remove(game_id);
            if let Some(flag) = installs.active.get(game_id) {
                flag.store(true, Ordering::Relaxed);
                true
            } else {
                false
            }
        };
        if !had_active {
            self.discard_paused(game_id)?;
        }
        Ok(())
    }

    pub fn verify(&self, game_id: &str) -> Result<()> {
        let Some(mut local) = self.inner.db.get_local_state(game_id)? else {
            return Ok(());
        };
        let (Some(manifest), Some(installed_path)) =
            (local.installed_manifest.as_ref(), local.installed_path.as_ref())
        else {
            return Ok(());
        };
        for file in &manifest.files {
            let path = game_paths::combine(Path::new(installed_path), &file.path);
            if !path.is_file() {
                local.status = InstallStatus::Failed;
                self.inner.db.upsert_local_state(&local)?;
                return Ok(());
            }
            let sha = sha256_file(&path)?;
            if !sha.eq_ignore_ascii_case(&file.sha256) {
                local.status = InstallStatus::Failed;
                self.inner.db.upsert_local_state(&local)?;
                return Ok(());
            }
        }
        if local.status != InstallStatus::Installed {
            local.status = InstallStatus::Installed;
            self.inner.db.upsert_local_state(&local)?;
        }
        Ok(())
    }

    pub fn launch(&self, game_id: &str) -> Result<LaunchResult> {
        let local = match self.inner.db.get_local_state(game_id)? {
            Some(state)
                if state
                    .installed_path
                    .as_ref()
                    .is_some_and(|p| Path::new(p).is_dir()) =>
            {
                state
            }
            _ => return Ok(LaunchResult::fail("Game not installed")),
        };
        let Some(game) = self.inner.db.get_game(game_id)? else {
            return Ok(LaunchResult::fail("Game metadata not found"));
        };
        let Some(config) = &game.launch_config else {
            return Ok(LaunchResult::fail("No launch configuration"));
        };
        let installed = PathBuf::from(local.installed_path.as_ref().unwrap());
        let exe_path = game_paths::combine(&installed, &config.executable_path);
        if !exe_path.is_file() {
            return Ok(LaunchResult::fail(format!(
                "Executable not found: {}",
                exe_path.display()
            )));
        }
        game_paths::try_make_executable(&exe_path);
        let work_dir = match &config.working_directory {
            Some(dir) => game_paths::combine(&installed, dir),
            None => installed,
        };

        if cfg!(target_os = "linux")
            && launch::looks_like_online_fix(&work_dir, &exe_path)
            && !launch::is_steam_running()
        {
            return Ok(LaunchResult::fail(
                "Steam must be running to launch Online-Fix games (Steam Overlay / AppID 480).",
            ));
        }

        let settings = self.inner.db.get_settings()?;
        let spec = match launch::build(
            &exe_path,
            &work_dir,
            config.launch_args.as_deref(),
            &settings,
            None,
        ) {
            Ok(spec) => spec,
            Err(e) => return Ok(LaunchResult::fail(e.to_string())),
        };
        log_launch(game_id, &spec);

        let mut cmd = std::process::Command::new(&spec.program);
        cmd.current_dir(&spec.cwd);
        cmd.args(&spec.args);
        for (key, value) in &spec.env {
            cmd.env(key, value);
        }
        let child = match cmd.spawn() {
            Ok(child) => child,
            Err(e) => return Ok(LaunchResult::fail(e.to_string())),
        };
        let pid = child.id();
        self.spawn_playtime(game_id.to_string(), child, local.play_time_seconds);
        Ok(LaunchResult::ok(pid))
    }

    fn spawn_playtime(&self, game_id: String, mut child: std::process::Child, initial: i64) {
        let db = self.inner.db.clone();
        let Ok(handle) = tokio::runtime::Handle::try_current() else {
            return;
        };
        handle.spawn(async move {
            let start = Instant::now();
            let mut last_persist = Instant::now();
            loop {
                match child.try_wait() {
                    Ok(Some(_)) | Err(_) => break,
                    Ok(None) => {
                        if last_persist.elapsed() >= Duration::from_secs(60) {
                            persist_playtime(&db, &game_id, initial, start, false);
                            last_persist = Instant::now();
                        }
                        tokio::time::sleep(Duration::from_secs(1)).await;
                    }
                }
            }
            persist_playtime(&db, &game_id, initial, start, true);
        });
    }

    async fn install_or_update(&self, game: &Game, resume: bool) -> Result<()> {
        let mut local = self
            .inner
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
        let initial_status = local.status;

        let settings = self.inner.db.get_settings()?;
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

        let now = now_secs();
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
        self.inner.db.upsert_download_task(&download_task)?;

        let safe_name = game_paths::sanitize_folder_name(&game.name);
        let final_dir = install_root.join(safe_name);
        let installed_manifest = match local.status {
            InstallStatus::Installed | InstallStatus::Paused => local.installed_manifest.clone(),
            _ => None,
        };

        let cancel = Arc::new(AtomicBool::new(false));
        {
            let mut installs = self.inner.installs.lock().expect("installs");
            installs.active.insert(game.id.clone(), cancel.clone());
        }

        local.status = InstallStatus::Downloading;
        self.inner.db.upsert_local_state(&local)?;

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
                cancel,
            )
            .await;

        let paused = {
            let mut installs = self.inner.installs.lock().expect("installs");
            installs.active.remove(&game.id);
            installs.paused.remove(&game.id)
        };

        match result {
            Ok(()) => Ok(()),
            Err(e) if e.is_cancelled() && paused => {
                local.status = InstallStatus::Paused;
                let _ = self.inner.db.upsert_local_state(&local);
                let _ = self.pause_task(&download_task.id);
                Err(e)
            }
            Err(e) if e.is_cancelled() => {
                local.status = initial_status;
                let _ = self.inner.db.upsert_local_state(&local);
                let _ = self.fail_task(&download_task.id, "Cancelled", true);
                let _ = delete_dir(&staging_dir);
                Err(e)
            }
            Err(e) => {
                local.status = InstallStatus::Failed;
                let _ = self.inner.db.upsert_local_state(&local);
                let _ = self.fail_task(&download_task.id, &e.to_string(), false);
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
        cancel: Arc<AtomicBool>,
    ) -> Result<()> {
        throw_if_cancelled(&cancel)?;
        let manifest = self
            .inner
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
            self.inner.db.upsert_local_state(local)?;
            self.complete_task(&download_task.id)?;
            return Ok(());
        }

        download_task.total_bytes = to_download.iter().map(|f| f.size_bytes).sum();
        download_task.status = DownloadStatus::Downloading;
        download_task.install_stage = InstallStage::Downloading;
        self.inner.db.upsert_download_task(download_task)?;

        if !resume && staging_dir.exists() {
            delete_dir(staging_dir)?;
        }
        std::fs::create_dir_all(staging_dir)?;

        if let (Some(inst), true) = (installed_manifest, final_dir.is_dir()) {
            for file in &inst.files {
                throw_if_cancelled(&cancel)?;
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

        self.inner
            .downloads
            .download_files(download_task, &requests, Some(cancel.clone()))
            .await?;
        throw_if_cancelled(&cancel)?;

        download_task.install_stage = InstallStage::Verifying;
        self.inner.db.upsert_download_task(download_task)?;

        for file in &to_download {
            throw_if_cancelled(&cancel)?;
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
        self.inner.db.upsert_download_task(download_task)?;

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

        if let Some(launch_cfg) = &game.launch_config {
            game_paths::try_make_executable(&game_paths::combine(
                final_dir,
                &launch_cfg.executable_path,
            ));
        }

        local.status = InstallStatus::Installed;
        local.installed_path = Some(final_dir.to_string_lossy().into());
        local.installed_version = Some(manifest.version.clone());
        local.installed_manifest = Some(manifest);
        self.inner.db.upsert_local_state(local)?;
        self.complete_task(&download_task.id)?;
        Ok(())
    }

    pub fn uninstall(&self, game_id: &str) -> Result<()> {
        let mut local = self
            .inner
            .db
            .get_local_state(game_id)?
            .unwrap_or(GameLocalState {
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
        self.inner.db.upsert_local_state(&local)?;
        Ok(())
    }

    fn discard_paused(&self, game_id: &str) -> Result<()> {
        let Some(mut local) = self.inner.db.get_local_state(game_id)? else {
            return Ok(());
        };
        if local.status != InstallStatus::Paused {
            return Ok(());
        }
        let _ = delete_dir(&self.staging_dir(game_id)?);
        self.discard_tasks_for_game(game_id)?;
        local.status = if local
            .installed_path
            .as_ref()
            .is_some_and(|p| Path::new(p).is_dir())
        {
            InstallStatus::Installed
        } else {
            InstallStatus::NotInstalled
        };
        self.inner.db.upsert_local_state(&local)?;
        Ok(())
    }

    fn staging_dir(&self, game_id: &str) -> Result<PathBuf> {
        let settings = self.inner.db.get_settings()?;
        let install_root = if settings.install_folder.trim().is_empty() {
            data_directory().join("games")
        } else {
            PathBuf::from(&settings.install_folder)
        };
        Ok(install_root.join(".update").join(game_id))
    }

    fn discard_tasks_for_game(&self, game_id: &str) -> Result<()> {
        for task in self.inner.db.get_all_download_tasks()? {
            if task.game_id == game_id {
                let _ = self.inner.db.delete_download_task(&task.id);
            }
        }
        Ok(())
    }

    fn complete_task(&self, task_id: &str) -> Result<()> {
        let _ = self.inner.db.delete_download_task(task_id);
        Ok(())
    }

    fn pause_task(&self, task_id: &str) -> Result<()> {
        if let Some(mut task) = self.inner.db.get_download_task(task_id)? {
            task.status = DownloadStatus::Paused;
            self.inner.db.upsert_download_task(&task)?;
        }
        Ok(())
    }

    fn fail_task(&self, task_id: &str, error: &str, cancelled: bool) -> Result<()> {
        if let Some(mut task) = self.inner.db.get_download_task(task_id)? {
            task.status = if cancelled {
                DownloadStatus::Cancelled
            } else {
                DownloadStatus::Failed
            };
            task.error = Some(error.into());
            self.inner.db.upsert_download_task(&task)?;
        }
        Ok(())
    }
}

fn throw_if_cancelled(cancel: &AtomicBool) -> Result<()> {
    if cancel.load(Ordering::Relaxed) {
        Err(Error::Cancelled)
    } else {
        Ok(())
    }
}

fn persist_playtime(db: &LocalDb, game_id: &str, initial: i64, start: Instant, last_played: bool) {
    let Ok(Some(mut local)) = db.get_local_state(game_id) else {
        return;
    };
    local.play_time_seconds = initial + start.elapsed().as_secs() as i64;
    if last_played {
        local.last_played = Some(now_secs());
    }
    let _ = db.upsert_local_state(&local);
}

fn log_launch(game_id: &str, spec: &LaunchSpec) {
    let args = spec
        .args
        .iter()
        .map(|a| launch::quote(a))
        .collect::<Vec<_>>()
        .join(" ");
    tracing::info!("Launching {game_id}: {} {args}", spec.program.display());
    let interesting = [
        "STEAM_COMPAT_DATA_PATH",
        "WINEPREFIX",
        "WINEDLLOVERRIDES",
        "LD_PRELOAD",
        "SteamAppId",
        "STEAM_COMPAT_CLIENT_INSTALL_PATH",
        "PROTONPATH",
        "GAMEID",
    ];
    let env = interesting
        .iter()
        .filter_map(|key| spec.env_get(key).map(|v| format!("{key}={v}")))
        .collect::<Vec<_>>()
        .join(" ");
    if !env.is_empty() {
        tracing::info!("Launch environment for {game_id}: {env}");
    }
}

fn now_secs() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

fn sha256_file(path: &Path) -> Result<String> {
    let mut file = std::fs::File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buf = [0u8; 81920];
    loop {
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(hex::encode(hasher.finalize()))
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

    #[tokio::test]
    async fn cancel_while_paused_discards_staging() {
        let dir = tempdir().unwrap();
        let (svc, db, server, _db_file) = harness(dir.path()).await;
        let body = vec![1u8; 1024];
        let hash = sha(&body);
        let manifest = format!(
            r#"{{"version":"1.0.0","totalBytes":1024,"files":[{{"path":"data.bin","sizeBytes":1024,"sha256":"{hash}"}}]}}"#
        );

        Mock::given(method("GET"))
            .and(path("/public.php/dav/files/tok/g1/manifest.json"))
            .respond_with(ResponseTemplate::new(200).set_body_string(manifest))
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path("/public.php/dav/files/tok/g1/data.bin"))
            .respond_with(
                ResponseTemplate::new(200)
                    .set_body_bytes(body)
                    .set_delay(Duration::from_secs(60)),
            )
            .mount(&server)
            .await;

        let g = game("g1/manifest.json", 1024);
        let svc2 = svc.clone();
        let game2 = g.clone();
        let handle = tokio::spawn(async move { svc2.install(&game2).await });

        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            if dir.path().join(".update/g1").is_dir() {
                break;
            }
            if Instant::now() > deadline {
                panic!("staging dir never appeared");
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }

        svc.pause("g1");
        let err = handle.await.unwrap().unwrap_err();
        assert!(err.is_cancelled());
        let paused = db.get_local_state("g1").unwrap().unwrap();
        assert_eq!(paused.status, InstallStatus::Paused);
        assert!(dir.path().join(".update/g1").is_dir());

        svc.cancel("g1").unwrap();
        let state = db.get_local_state("g1").unwrap().unwrap();
        assert_eq!(state.status, InstallStatus::NotInstalled);
        assert!(!dir.path().join(".update/g1").exists());
        assert!(db.get_all_download_tasks().unwrap().is_empty());
    }

    #[tokio::test]
    async fn verify_marks_corrupt_when_file_missing() {
        let dir = tempdir().unwrap();
        let (svc, db, _server, _db_file) = harness(dir.path()).await;
        let install_dir = dir.path().join("Game 1");
        std::fs::create_dir_all(&install_dir).unwrap();
        db.upsert_local_state(&GameLocalState {
            game_id: "g1".into(),
            status: InstallStatus::Installed,
            installed_path: Some(install_dir.to_string_lossy().into()),
            play_time_seconds: 0,
            last_played: None,
            installed_version: Some("1.0.0".into()),
            installed_manifest: Some(GameManifest {
                version: "1.0.0".into(),
                total_bytes: 5,
                files: vec![GameFile {
                    path: "data.bin".into(),
                    size_bytes: 5,
                    sha256: "deadbeef".into(),
                }],
            }),
        })
        .unwrap();
        svc.verify("g1").unwrap();
        assert_eq!(
            db.get_local_state("g1").unwrap().unwrap().status,
            InstallStatus::Failed
        );
    }
}
