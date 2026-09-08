use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

use percent_encoding::{utf8_percent_encode, NON_ALPHANUMERIC};
use serde::Deserialize;

use crate::db::LocalDb;
use crate::models::{Game, SteamCache};
use crate::paths::data_directory;
use crate::Result;

#[derive(Clone)]
pub struct SteamClient {
    client: reqwest::Client,
    store_base: String,
    cdn_base: String,
}

impl SteamClient {
    pub fn new() -> Result<Self> {
        Ok(Self {
            client: reqwest::Client::builder()
                .user_agent("Mozilla/5.0 (compatible; KermoLauncher/2.0)")
                .build()?,
            store_base: "https://store.steampowered.com".into(),
            cdn_base: "https://cdn.cloudflare.steamstatic.com".into(),
        })
    }

    pub fn with_bases(store_base: impl Into<String>, cdn_base: impl Into<String>) -> Result<Self> {
        Ok(Self {
            client: reqwest::Client::builder()
                .user_agent("Mozilla/5.0 (compatible; KermoLauncher/2.0)")
                .build()?,
            store_base: store_base.into(),
            cdn_base: cdn_base.into(),
        })
    }

    pub async fn enrich_missing(&self, db: &LocalDb, games: &[Game]) -> Result<()> {
        self.enrich_missing_into(db, games, &data_directory().join("cache/covers"))
            .await
    }

    pub async fn enrich_missing_into(
        &self,
        db: &LocalDb,
        games: &[Game],
        cover_dir: &Path,
    ) -> Result<()> {
        std::fs::create_dir_all(cover_dir)?;
        for game in games {
            if db.get_steam_cache(&game.id)?.is_some() {
                continue;
            }
            if !game.screenshot_urls.is_empty()
                && !game.tags.is_empty()
                && !game.description.trim().is_empty()
            {
                continue;
            }
            if let Err(e) = self.enrich_one(db, game, cover_dir).await {
                tracing::debug!("Steam enrich skipped for {}: {e}", game.id);
            }
        }
        Ok(())
    }

    async fn enrich_one(&self, db: &LocalDb, game: &Game, cover_dir: &Path) -> Result<()> {
        let Some(app_id) = self.search_app_id(&game.name).await? else {
            return Ok(());
        };
        let details = self.app_details(app_id).await?;
        let cover_path = self
            .download_image(
                &format!("{}/steam/apps/{app_id}/library_600x900.jpg", self.cdn_base),
                &cover_dir.join(format!("{}.jpg", game.id)),
            )
            .await?;
        let hero_path = self
            .download_image(
                &format!("{}/steam/apps/{app_id}/library_hero.jpg", self.cdn_base),
                &cover_dir.join(format!("{}-hero.jpg", game.id)),
            )
            .await?;
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_secs() as i64)
            .unwrap_or(0);
        db.upsert_steam_cache(&SteamCache {
            game_id: game.id.clone(),
            steam_app_id: Some(app_id as i64),
            tags: details.tags,
            description: details.description,
            cover_path,
            hero_path,
            fetched_at: now,
        })?;
        Ok(())
    }

    async fn search_app_id(&self, name: &str) -> Result<Option<u32>> {
        let term = utf8_percent_encode(name, NON_ALPHANUMERIC);
        let url = format!(
            "{}/api/storesearch/?term={term}&l=english&cc=US",
            self.store_base
        );
        let resp = self.client.get(url).send().await?;
        if !resp.status().is_success() {
            return Ok(None);
        }
        let search: StoreSearch = resp.json().await?;
        let items = search.items.unwrap_or_default();
        let exact = items
            .iter()
            .find(|i| i.name.eq_ignore_ascii_case(name))
            .or_else(|| items.first());
        Ok(exact.map(|i| i.id))
    }

    async fn app_details(&self, app_id: u32) -> Result<SteamDetails> {
        let url = format!("{}/api/appdetails?appids={app_id}", self.store_base);
        let resp = self.client.get(url).send().await?;
        if !resp.status().is_success() {
            return Ok(SteamDetails::default());
        }
        let map: serde_json::Map<String, serde_json::Value> = resp.json().await?;
        let Some(entry) = map.get(&app_id.to_string()) else {
            return Ok(SteamDetails::default());
        };
        let parsed: AppDetailsEnvelope = serde_json::from_value(entry.clone())?;
        if !parsed.success {
            return Ok(SteamDetails::default());
        }
        let data = parsed.data.unwrap_or_default();
        Ok(SteamDetails {
            description: data.short_description.unwrap_or_default(),
            tags: data
                .genres
                .unwrap_or_default()
                .into_iter()
                .map(|g| g.description)
                .collect(),
        })
    }

    async fn download_image(&self, url: &str, dest: &Path) -> Result<Option<String>> {
        let resp = self.client.get(url).send().await?;
        if !resp.status().is_success() {
            return Ok(None);
        }
        let bytes = resp.bytes().await?;
        if bytes.len() < 32 {
            return Ok(None);
        }
        if let Some(parent) = dest.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(dest, bytes)?;
        Ok(Some(dest.to_string_lossy().into_owned()))
    }
}

#[derive(Default)]
struct SteamDetails {
    description: String,
    tags: Vec<String>,
}

#[derive(Deserialize)]
struct StoreSearch {
    items: Option<Vec<StoreItem>>,
}

#[derive(Deserialize)]
struct StoreItem {
    id: u32,
    name: String,
}

#[derive(Deserialize, Default)]
struct AppDetailsEnvelope {
    #[serde(default)]
    success: bool,
    data: Option<AppData>,
}

#[derive(Deserialize, Default)]
struct AppData {
    short_description: Option<String>,
    genres: Option<Vec<Genre>>,
}

#[derive(Deserialize)]
struct Genre {
    description: String,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::Game;
    use std::path::PathBuf;
    use tempfile::NamedTempFile;
    use wiremock::matchers::{method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    fn game(name: &str) -> Game {
        Game {
            id: "g1".into(),
            name: name.into(),
            version: "1.0.0".into(),
            description: String::new(),
            tags: vec![],
            dependencies: vec![],
            screenshot_urls: vec![],
            manifest_url: "g1/manifest.json".into(),
            size_bytes: 0,
            launch_config: None,
        }
    }

    #[tokio::test]
    async fn enrich_writes_steam_cache_and_cover() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/api/storesearch/"))
            .respond_with(ResponseTemplate::new(200).set_body_string(
                r#"{"total":1,"items":[{"type":"app","name":"Demo Game","id":480}]}"#,
            ))
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path("/api/appdetails"))
            .respond_with(ResponseTemplate::new(200).set_body_string(
                r#"{"480":{"success":true,"data":{"name":"Demo Game","short_description":"A demo.","genres":[{"description":"Action"}]}}}"#,
            ))
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path("/steam/apps/480/library_600x900.jpg"))
            .respond_with(ResponseTemplate::new(200).set_body_bytes(vec![0xFFu8; 64]))
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path("/steam/apps/480/library_hero.jpg"))
            .respond_with(ResponseTemplate::new(200).set_body_bytes(vec![0xEEu8; 64]))
            .mount(&server)
            .await;

        let file = NamedTempFile::new().unwrap();
        let db = LocalDb::open(file.path());
        db.upsert_games(&[game("Demo Game")]).unwrap();
        let covers = tempfile::tempdir().unwrap();
        let client = SteamClient::with_bases(server.uri(), server.uri()).unwrap();
        client
            .enrich_missing_into(&db, &[game("Demo Game")], covers.path())
            .await
            .unwrap();

        let cache = db.get_steam_cache("g1").unwrap().unwrap();
        assert_eq!(cache.steam_app_id, Some(480));
        assert_eq!(cache.tags, vec!["Action".to_string()]);
        assert_eq!(cache.description, "A demo.");
        assert!(cache.cover_path.is_some());
        let cover = PathBuf::from(cache.cover_path.unwrap());
        assert_eq!(std::fs::read(&cover).unwrap().len(), 64);
    }

    #[tokio::test]
    async fn enrich_skips_when_catalog_already_complete() {
        let server = MockServer::start().await;
        let file = NamedTempFile::new().unwrap();
        let db = LocalDb::open(file.path());
        let mut g = game("Demo Game");
        g.description = "from nextcloud".into();
        g.tags = vec!["Indie".into()];
        g.screenshot_urls = vec!["https://cloud/shot.png".into()];
        db.upsert_games(&[g.clone()]).unwrap();
        let covers = tempfile::tempdir().unwrap();
        let client = SteamClient::with_bases(server.uri(), server.uri()).unwrap();
        client
            .enrich_missing_into(&db, &[g], covers.path())
            .await
            .unwrap();
        assert!(db.get_steam_cache("g1").unwrap().is_none());
    }
}
