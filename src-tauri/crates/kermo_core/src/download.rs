use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::stream::{self, StreamExt};

use crate::db::LocalDb;
use crate::models::*;
use crate::webdav::WebDavClient;
use crate::Result;

const DEFAULT_MAX_PARALLEL: usize = 2;

pub struct DownloadService {
    webdav: WebDavClient,
    db: Arc<LocalDb>,
}

impl DownloadService {
    pub fn new(webdav: WebDavClient, db: Arc<LocalDb>) -> Self {
        Self { webdav, db }
    }

    pub async fn download_files(
        &self,
        task: &DownloadTask,
        files: &[DownloadFileRequest],
        cancel: Option<Arc<AtomicBool>>,
    ) -> Result<()> {
        let mut running = task.clone();
        running.status = DownloadStatus::Downloading;
        running.install_stage = InstallStage::Downloading;
        self.db.upsert_download_task(&running)?;

        let settings = self.db.get_settings()?;
        let max_parallel = if settings.max_parallel_downloads > 0 {
            settings.max_parallel_downloads as usize
        } else {
            DEFAULT_MAX_PARALLEL
        };

        let completed = Arc::new(AtomicU64::new(0));
        let in_flight: Arc<Mutex<std::collections::HashMap<String, u64>>> =
            Arc::new(Mutex::new(std::collections::HashMap::new()));

        let webdav = self.webdav.clone();
        let task_id = task.id.clone();
        let first_err: Arc<Mutex<Option<crate::Error>>> = Arc::new(Mutex::new(None));
        let stop_progress = Arc::new(AtomicBool::new(false));
        let progress_task = {
            let db = self.db.clone();
            let task_id = task_id.clone();
            let completed = completed.clone();
            let in_flight = in_flight.clone();
            let stop = stop_progress.clone();
            tokio::spawn(async move {
                while !stop.load(Ordering::Relaxed) {
                    tokio::time::sleep(Duration::from_millis(80)).await;
                    if stop.load(Ordering::Relaxed) {
                        break;
                    }
                    let bytes = progress_bytes(&completed, &in_flight);
                    let _ = db.set_downloaded_bytes(&task_id, bytes as i64);
                }
            })
        };

        stream::iter(files.to_vec())
            .for_each_concurrent(max_parallel, |file| {
                let webdav = webdav.clone();
                let completed = completed.clone();
                let in_flight = in_flight.clone();
                let task_id = task_id.clone();
                let first_err = first_err.clone();
                let cancel = cancel.clone();
                async move {
                    if cancel.as_ref().is_some_and(|c| c.load(Ordering::Relaxed)) {
                        let mut slot = first_err.lock().expect("err");
                        if slot.is_none() {
                            *slot = Some(crate::Error::Cancelled);
                        }
                        return;
                    }
                    let on_disk = prepare_local_file(&file);
                    if on_disk == file.size_bytes as u64 && file.size_bytes > 0 {
                        completed.fetch_add(file.size_bytes as u64, Ordering::Relaxed);
                        return;
                    }

                    {
                        in_flight
                            .lock()
                            .expect("in_flight")
                            .insert(file.key.clone(), on_disk);
                    }

                    let key = file.key.clone();
                    let in_flight_cb = in_flight.clone();
                    let download_result = webdav
                        .download_file(
                            &file.remote_url,
                            Path::new(&file.local_path),
                            &task_id,
                            move |p| {
                                in_flight_cb
                                    .lock()
                                    .expect("in_flight")
                                    .insert(key.clone(), p.bytes_received as u64);
                            },
                            cancel.clone(),
                        )
                        .await;

                    match download_result {
                        Ok(()) => {
                            completed.fetch_add(file.size_bytes as u64, Ordering::Relaxed);
                            in_flight.lock().expect("in_flight").remove(&file.key);
                        }
                        Err(e) => {
                            in_flight.lock().expect("in_flight").remove(&file.key);
                            let mut slot = first_err.lock().expect("err");
                            if slot.is_none() {
                                *slot = Some(e);
                            }
                        }
                    }
                }
            })
            .await;

        stop_progress.store(true, Ordering::Relaxed);
        progress_task.abort();

        if let Some(e) = first_err.lock().expect("err").take() {
            return Err(e);
        }

        let mut done = task.clone();
        done.status = DownloadStatus::Completed;
        done.downloaded_bytes = completed.load(Ordering::Relaxed) as i64;
        self.db.upsert_download_task(&done)?;
        Ok(())
    }
}

fn progress_bytes(
    completed: &AtomicU64,
    in_flight: &Mutex<std::collections::HashMap<String, u64>>,
) -> u64 {
    let inflight = in_flight
        .lock()
        .expect("in_flight")
        .values()
        .copied()
        .sum::<u64>();
    completed.load(Ordering::Relaxed).saturating_add(inflight)
}

fn prepare_local_file(file: &DownloadFileRequest) -> u64 {
    let path = Path::new(&file.local_path);
    let Ok(meta) = std::fs::metadata(path) else {
        return 0;
    };
    let len = meta.len();
    if len > file.size_bytes as u64 {
        let _ = std::fs::remove_file(path);
        return 0;
    }
    len
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::{AppSettings, Game};
    use std::sync::Arc;
    use tempfile::{tempdir, NamedTempFile};
    use wiremock::matchers::{method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    fn task() -> DownloadTask {
        DownloadTask {
            id: "t1".into(),
            game_id: "g1".into(),
            remote_url: String::new(),
            local_path: String::new(),
            total_bytes: 5,
            downloaded_bytes: 0,
            status: DownloadStatus::Queued,
            error: None,
            started_at: None,
            completed_at: None,
            install_stage: InstallStage::Preparing,
        }
    }

    fn seed_game(db: &LocalDb) {
        db.upsert_games(&[Game {
            id: "g1".into(),
            name: "g1".into(),
            version: "1.0.0".into(),
            description: String::new(),
            tags: vec![],
            dependencies: vec![],
            screenshot_urls: vec![],
            manifest_url: "g1/manifest.json".into(),
            size_bytes: 0,
            launch_config: None,
            notes: String::new(),
        }])
        .unwrap();
    }

    #[tokio::test]
    async fn skips_file_already_complete_on_disk() {
        let server = MockServer::start().await;
        let dir = tempdir().unwrap();
        let dest = dir.path().join("a.bin");
        std::fs::write(&dest, b"hello").unwrap();

        let file = NamedTempFile::new().unwrap();
        let db = Arc::new(LocalDb::open(file.path()));
        seed_game(&db);
        db.save_settings(&AppSettings {
            max_parallel_downloads: 2,
            ..AppSettings::default()
        })
        .unwrap();

        let svc = DownloadService::new(WebDavClient::new().unwrap(), db);
        svc.download_files(
            &task(),
            &[DownloadFileRequest {
                key: "a.bin".into(),
                remote_url: format!("{}/a.bin", server.uri()),
                local_path: dest.to_string_lossy().into(),
                size_bytes: 5,
            }],
            None,
        )
        .await
        .unwrap();

        assert_eq!(std::fs::read(&dest).unwrap(), b"hello");
    }

    #[tokio::test]
    async fn deletes_oversized_file_and_redownloads() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/a.bin"))
            .respond_with(ResponseTemplate::new(200).set_body_string("hi"))
            .mount(&server)
            .await;

        let dir = tempdir().unwrap();
        let dest = dir.path().join("a.bin");
        std::fs::write(&dest, b"TOOLONG").unwrap();

        let file = NamedTempFile::new().unwrap();
        let db = Arc::new(LocalDb::open(file.path()));
        seed_game(&db);
        db.save_settings(&AppSettings::default()).unwrap();

        let svc = DownloadService::new(WebDavClient::new().unwrap(), db);
        let mut t = task();
        t.total_bytes = 2;
        svc.download_files(
            &t,
            &[DownloadFileRequest {
                key: "a.bin".into(),
                remote_url: format!("{}/a.bin", server.uri()),
                local_path: dest.to_string_lossy().into(),
                size_bytes: 2,
            }],
            None,
        )
        .await
        .unwrap();

        assert_eq!(std::fs::read(&dest).unwrap(), b"hi");
    }

    #[test]
    fn progress_bytes_adds_finished_files_and_partial_downloads() {
        let completed = AtomicU64::new(100);
        let in_flight = Mutex::new(std::collections::HashMap::from([
            ("a".into(), 40u64),
            ("b".into(), 10),
        ]));
        assert_eq!(progress_bytes(&completed, &in_flight), 150);
    }

    #[tokio::test]
    async fn persists_partial_progress_while_the_file_is_still_downloading() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/a.bin"))
            .respond_with(
                ResponseTemplate::new(206)
                    .insert_header("content-range", "bytes 3-8/9")
                    .set_body_string("lo-rest")
                    .set_delay(Duration::from_millis(800)),
            )
            .mount(&server)
            .await;

        let dir = tempdir().unwrap();
        let dest = dir.path().join("a.bin");
        std::fs::write(&dest, b"hel").unwrap();

        let file = NamedTempFile::new().unwrap();
        let db = Arc::new(LocalDb::open(file.path()));
        seed_game(&db);
        db.save_settings(&AppSettings::default()).unwrap();
        let mut running = task();
        running.status = DownloadStatus::Downloading;
        running.total_bytes = 9;
        db.upsert_download_task(&running).unwrap();

        let svc = DownloadService::new(WebDavClient::new().unwrap(), db.clone());
        let requests = vec![DownloadFileRequest {
            key: "a.bin".into(),
            remote_url: format!("{}/a.bin", server.uri()),
            local_path: dest.to_string_lossy().into(),
            size_bytes: 9,
        }];
        let download = tokio::spawn(async move {
            svc.download_files(&running, &requests, None).await
        });

        tokio::time::sleep(Duration::from_millis(400)).await;
        let partial = db.get_download_task("t1").unwrap().unwrap().downloaded_bytes;
        download.await.unwrap().unwrap();
        assert!(
            partial > 0 && partial < 9,
            "progress stayed at {partial} until the download finished"
        );
    }
}
