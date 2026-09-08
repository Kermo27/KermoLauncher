use std::path::Path;
use std::time::{Duration, Instant};

use reqwest::header::{HeaderValue, RANGE};
use reqwest::StatusCode;
use tokio::io::AsyncWriteExt;

use crate::models::{DownloadProgress, Game, GameManifest, NextcloudConfig};
use crate::url_sanitizer::mask_url;
use crate::{Error, Result};

const METADATA_TIMEOUT: Duration = Duration::from_secs(120);

#[derive(Clone)]
pub struct WebDavClient {
    client: reqwest::Client,
}

impl WebDavClient {
    pub fn new() -> Result<Self> {
        Ok(Self {
            client: reqwest::Client::builder()
                .user_agent(format!("KermoLauncher/{}", env!("CARGO_PKG_VERSION")))
                .connect_timeout(Duration::from_secs(30))
                .build()?,
        })
    }

    pub fn with_client(client: reqwest::Client) -> Self {
        Self { client }
    }

    pub async fn download_metadata(&self, config: &NextcloudConfig) -> Result<Vec<Game>> {
        let url = config.metadata_url();
        tracing::info!("Downloading metadata from {}", mask_url(&url));

        let response = self
            .client
            .get(&url)
            .timeout(METADATA_TIMEOUT)
            .send()
            .await?;
        ensure_success(&response, &url)?;

        let games: Vec<Game> = response.json().await?;
        tracing::info!("Loaded {} games from metadata", games.len());
        Ok(games)
    }

    pub async fn download_manifest(&self, manifest_url: &str) -> Result<GameManifest> {
        tracing::info!("Downloading manifest from {}", mask_url(manifest_url));
        let response = self
            .client
            .get(manifest_url)
            .timeout(METADATA_TIMEOUT)
            .send()
            .await?;
        ensure_success(&response, manifest_url)?;
        let manifest: GameManifest = response.json().await?;
        Ok(manifest)
    }

    pub async fn resolve_config(&self, config: &NextcloudConfig) -> Result<NextcloudConfig> {
        if self.metadata_exists(config, "").await {
            return Ok(NextcloudConfig {
                root_folder: String::new(),
                ..config.clone()
            });
        }
        if self.metadata_exists(config, "Games").await {
            return Ok(NextcloudConfig {
                root_folder: "Games".into(),
                ..config.clone()
            });
        }
        Ok(NextcloudConfig {
            root_folder: String::new(),
            ..config.clone()
        })
    }

    async fn metadata_exists(&self, config: &NextcloudConfig, root_folder: &str) -> bool {
        let url = format!(
            "{}/public.php/dav/files/{}{}/metadata.json",
            config.server_base(),
            config.dav_token(),
            if root_folder.is_empty() {
                String::new()
            } else {
                format!("/{root_folder}")
            }
        );
        match self
            .client
            .head(&url)
            .timeout(METADATA_TIMEOUT)
            .send()
            .await
        {
            Ok(resp) => resp.status().is_success(),
            Err(_) => false,
        }
    }

    pub async fn download_file(
        &self,
        remote_url: &str,
        local_path: &Path,
        task_id: &str,
        mut on_progress: impl FnMut(DownloadProgress),
    ) -> Result<()> {
        tracing::info!(
            "Downloading {} to {}",
            mask_url(remote_url),
            local_path.display()
        );
        if let Some(parent) = local_path.parent() {
            tokio::fs::create_dir_all(parent).await?;
        }
        let existing = match tokio::fs::metadata(local_path).await {
            Ok(meta) => meta.len(),
            Err(_) => 0,
        };
        let response = self.get_with_optional_range(remote_url, existing).await?;
        if response.status() == StatusCode::RANGE_NOT_SATISFIABLE {
            tracing::warn!(
                "Server rejected resume for {}; restarting from scratch",
                local_path.display()
            );
            let _ = tokio::fs::remove_file(local_path).await;
            let retry = self.get_with_optional_range(remote_url, 0).await?;
            ensure_success(&retry, remote_url)?;
            self.write_body(retry, local_path, task_id, 0, &mut on_progress)
                .await?;
            return Ok(());
        }
        ensure_success(&response, remote_url)?;
        self.write_body(response, local_path, task_id, existing, &mut on_progress)
            .await?;
        Ok(())
    }
    async fn get_with_optional_range(
        &self,
        remote_url: &str,
        existing: u64,
    ) -> Result<reqwest::Response> {
        let mut req = self.client.get(remote_url);
        if existing > 0 {
            req = req.header(
                RANGE,
                HeaderValue::from_str(&format!("bytes={existing}-")).expect("ascii range"),
            );
        }
        Ok(req.send().await?)
    }
    async fn write_body(
        &self,
        mut response: reqwest::Response,
        local_path: &Path,
        task_id: &str,
        existing: u64,
        on_progress: &mut impl FnMut(DownloadProgress),
    ) -> Result<()> {
        let remaining = response.content_length().unwrap_or(0);
        let total = existing + remaining;
        let mut downloaded = existing;
        let start = Instant::now();
        let mut last_report = Instant::now();
        let mut file = tokio::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(local_path)
            .await?;
        while let Some(chunk) = response.chunk().await? {
            file.write_all(&chunk).await?;
            downloaded += chunk.len() as u64;
            if last_report.elapsed() >= Duration::from_millis(100) {
                let elapsed = start.elapsed().as_secs_f64();
                let speed = if elapsed > 0.0 {
                    (downloaded - existing) as f64 / elapsed
                } else {
                    0.0
                };
                let left = total.saturating_sub(downloaded) as f64;
                let eta = if speed > 0.0 {
                    Some(left / speed)
                } else {
                    None
                };
                on_progress(DownloadProgress {
                    task_id: task_id.to_string(),
                    bytes_received: downloaded as i64,
                    total_bytes: total as i64,
                    speed_bytes_per_second: speed,
                    estimated_time_remaining_secs: eta,
                });
                last_report = Instant::now();
            }
        }
        file.flush().await?;
        Ok(())
    }
}

fn ensure_success(response: &reqwest::Response, url: &str) -> Result<()> {
    let status = response.status();
    if status.is_success() {
        Ok(())
    } else {
        Err(Error::http_status(status, url))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use reqwest::StatusCode;
    use std::fs;
    use tempfile::tempdir;
    use wiremock::matchers::header;
    use wiremock::matchers::{method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    fn share(server: &MockServer, token: &str) -> NextcloudConfig {
        NextcloudConfig {
            share_url: format!("{}/s/{token}", server.uri()),
            share_token: token.into(),
            root_folder: String::new(),
        }
    }

    #[tokio::test]
    async fn download_metadata_parses_camel_case_catalog() {
        let server = MockServer::start().await;
        let body = r#"[{
            "id": "shift-at-midnight",
            "name": "Shift At Midnight",
            "version": "1.1.0",
            "description": "demo",
            "tags": ["adventure"],
            "screenshotUrls": ["Shift At Midnight/screenshots/1.jpg"],
            "manifestUrl": "Shift At Midnight/manifest.json",
            "sizeBytes": 123
        }]"#;

        Mock::given(method("GET"))
            .and(path("/public.php/dav/files/tok/metadata.json"))
            .respond_with(ResponseTemplate::new(200).set_body_string(body))
            .mount(&server)
            .await;

        let client = WebDavClient::with_client(reqwest::Client::new());
        let games = client
            .download_metadata(&share(&server, "tok"))
            .await
            .unwrap();
        assert_eq!(games.len(), 1);
        assert_eq!(games[0].id, "shift-at-midnight");
        assert_eq!(
            games[0].screenshot_urls[0],
            "Shift At Midnight/screenshots/1.jpg"
        );
        assert_eq!(games[0].size_bytes, 123);
    }

    #[tokio::test]
    async fn resolve_config_detects_games_subfolder() {
        let server = MockServer::start().await;
        Mock::given(method("HEAD"))
            .and(path("/public.php/dav/files/tok/metadata.json"))
            .respond_with(ResponseTemplate::new(404))
            .mount(&server)
            .await;
        Mock::given(method("HEAD"))
            .and(path("/public.php/dav/files/tok/Games/metadata.json"))
            .respond_with(ResponseTemplate::new(200))
            .mount(&server)
            .await;

        let client = WebDavClient::new().unwrap();
        let resolved = client.resolve_config(&share(&server, "tok")).await.unwrap();
        assert_eq!(resolved.root_folder, "Games");
    }

    #[tokio::test]
    async fn http_error_masks_token_in_message() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/public.php/dav/files/SecretTok/metadata.json"))
            .respond_with(ResponseTemplate::new(StatusCode::FORBIDDEN.as_u16()))
            .mount(&server)
            .await;

        let client = WebDavClient::new().unwrap();
        let err = client
            .download_metadata(&share(&server, "SecretTok"))
            .await
            .unwrap_err();
        let msg = err.to_string();
        assert!(!msg.contains("SecretTok"), "{msg}");
        assert!(msg.contains("***"), "{msg}");
    }

    #[tokio::test]
    async fn download_file_writes_body() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/file.bin"))
            .respond_with(ResponseTemplate::new(200).set_body_string("hello"))
            .mount(&server)
            .await;

        let dir = tempdir().unwrap();
        let dest = dir.path().join("file.bin");
        WebDavClient::new()
            .unwrap()
            .download_file(&format!("{}/file.bin", server.uri()), &dest, "t1", |_| {})
            .await
            .unwrap();
        assert_eq!(fs::read(&dest).unwrap(), b"hello");
    }

    #[tokio::test]
    async fn download_file_sends_range_when_partial_exists() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/file.bin"))
            .and(header("range", "bytes=4-"))
            .respond_with(ResponseTemplate::new(206).set_body_string("world"))
            .mount(&server)
            .await;

        let dir = tempdir().unwrap();
        let dest = dir.path().join("file.bin");
        fs::write(&dest, b"hell").unwrap();

        WebDavClient::new()
            .unwrap()
            .download_file(&format!("{}/file.bin", server.uri()), &dest, "t1", |_| {})
            .await
            .unwrap();
        assert_eq!(fs::read(&dest).unwrap(), b"hellworld");
    }

    #[tokio::test]
    async fn download_file_restarts_on_416() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/file.bin"))
            .and(header("range", "bytes=3-"))
            .respond_with(ResponseTemplate::new(416))
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path("/file.bin"))
            .respond_with(ResponseTemplate::new(200).set_body_string("abc"))
            .mount(&server)
            .await;

        let dir = tempdir().unwrap();
        let dest = dir.path().join("file.bin");
        fs::write(&dest, b"xxx").unwrap();

        WebDavClient::new()
            .unwrap()
            .download_file(&format!("{}/file.bin", server.uri()), &dest, "t1", |_| {})
            .await
            .unwrap();
        assert_eq!(fs::read(&dest).unwrap(), b"abc");
    }
}
