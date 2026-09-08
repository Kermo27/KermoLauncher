use std::time::Duration;

use crate::models::{Game, GameManifest, NextcloudConfig};
use crate::url_sanitizer::mask_url;
use crate::{Error, Result};

const METADATA_TIMEOUT: Duration = Duration::from_secs(120);

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
            Err(_) => false, // C#: pusty catch → false
        }
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
}
