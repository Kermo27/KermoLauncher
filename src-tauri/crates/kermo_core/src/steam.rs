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
    api_base: String,
    assets_cdn: String,
}

impl SteamClient {
    pub fn new() -> Result<Self> {
        Self::with_endpoints(
            "https://store.steampowered.com",
            "https://cdn.cloudflare.steamstatic.com",
            "https://api.steampowered.com",
            "https://shared.akamai.steamstatic.com/store_item_assets",
        )
    }

    pub fn with_bases(store_base: impl Into<String>, cdn_base: impl Into<String>) -> Result<Self> {
        let store = store_base.into();
        let cdn = cdn_base.into();
        Self::with_endpoints(&store, &cdn, &store, &cdn)
    }

    fn with_endpoints(
        store_base: impl Into<String>,
        cdn_base: impl Into<String>,
        api_base: impl Into<String>,
        assets_cdn: impl Into<String>,
    ) -> Result<Self> {
        Ok(Self {
            client: reqwest::Client::builder()
                .user_agent("Mozilla/5.0 (compatible; KermoLauncher/2.0)")
                .build()?,
            store_base: store_base.into(),
            cdn_base: cdn_base.into(),
            api_base: api_base.into(),
            assets_cdn: assets_cdn.into(),
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
        let mut attempted = 0u32;
        let mut failed = 0u32;
        for game in games {
            let cache = db.get_steam_cache(&game.id)?;
            if !needs_steam_enrich(cache.as_ref()) {
                continue;
            }
            attempted += 1;
            if let Err(e) = self.enrich_one(db, game, cover_dir, cache.as_ref()).await {
                failed += 1;
                tracing::warn!("Steam enrich skipped for {}: {e}", game.id);
            }
        }
        if attempted > 0 {
            tracing::info!(
                attempted,
                failed,
                ok = attempted.saturating_sub(failed),
                "Steam enrich finished"
            );
        }
        Ok(())
    }

    async fn enrich_one(
        &self,
        db: &LocalDb,
        game: &Game,
        cover_dir: &Path,
        existing: Option<&SteamCache>,
    ) -> Result<()> {
        let app_id = if let Some(id) = existing.and_then(|c| c.steam_app_id) {
            id as u32
        } else {
            let Some(id) = self.search_app_id(&game.name).await? else {
                return Ok(());
            };
            id
        };
        let details = self.app_details(app_id).await?;
        let assets = self.library_assets(app_id).await;
        let cover_path = match existing.and_then(|c| c.cover_path.clone()) {
            Some(path) if Path::new(&path).is_file() => Some(path),
            _ => {
                self.download_first(
                    &[
                        assets.capsule.clone(),
                        Some(format!(
                            "{}/steam/apps/{app_id}/library_600x900.jpg",
                            self.cdn_base
                        )),
                    ],
                    &cover_dir.join(format!("{}.jpg", game.id)),
                )
                .await?
            }
        };
        let hero_path = match existing.and_then(|c| c.hero_path.clone()) {
            Some(path) if Path::new(&path).is_file() => Some(path),
            _ => {
                self.download_first(
                    &[
                        assets.hero.clone(),
                        Some(format!(
                            "{}/steam/apps/{app_id}/library_hero.jpg",
                            self.cdn_base
                        )),
                    ],
                    &cover_dir.join(format!("{}-hero.jpg", game.id)),
                )
                .await?
            }
        };
        let mut screenshot_paths = existing
            .map(|c| c.screenshot_paths.clone())
            .unwrap_or_default()
            .into_iter()
            .filter(|p| Path::new(p).is_file())
            .collect::<Vec<_>>();
        if screenshot_paths.is_empty() {
            for (i, url) in details.screenshot_urls.iter().take(8).enumerate() {
                if let Some(path) = self
                    .download_image(url, &cover_dir.join(format!("{}-shot-{i}.jpg", game.id)))
                    .await?
                {
                    screenshot_paths.push(path);
                }
            }
        }
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_secs() as i64)
            .unwrap_or(0);
        db.upsert_steam_cache(&SteamCache {
            game_id: game.id.clone(),
            steam_app_id: Some(app_id as i64),
            tags: if details.tags.is_empty() {
                existing.map(|c| c.tags.clone()).unwrap_or_default()
            } else {
                details.tags
            },
            description: if details.description.is_empty() {
                existing.map(|c| c.description.clone()).unwrap_or_default()
            } else {
                details.description
            },
            cover_path,
            hero_path,
            screenshot_paths,
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
            screenshot_urls: data
                .screenshots
                .unwrap_or_default()
                .into_iter()
                .filter_map(|s| s.path_full.or(s.path_thumbnail))
                .collect(),
        })
    }

    async fn library_assets(&self, app_id: u32) -> LibraryAssets {
        let input = serde_json::json!({
            "ids": [{ "appid": app_id }],
            "context": { "language": "english", "country_code": "US", "steam_realm": 1 },
            "data_request": { "include_assets": true }
        });
        let url = format!(
            "{}/IStoreBrowseService/GetItems/v1/",
            self.api_base.trim_end_matches('/')
        );
        let Ok(resp) = self
            .client
            .get(url)
            .query(&[("input_json", input.to_string())])
            .send()
            .await
        else {
            return LibraryAssets::default();
        };
        if !resp.status().is_success() {
            return LibraryAssets::default();
        }
        let Ok(parsed) = resp.json::<GetItemsEnvelope>().await else {
            return LibraryAssets::default();
        };
        let Some(assets) = parsed
            .response
            .store_items
            .into_iter()
            .next()
            .and_then(|item| item.assets)
        else {
            return LibraryAssets::default();
        };
        let format = assets.asset_url_format.unwrap_or_default();
        LibraryAssets {
            capsule: [
                assets.library_capsule,
                assets.library_capsule_2x,
                assets.hero_capsule,
            ]
            .into_iter()
            .flatten()
            .find_map(|name| self.asset_url(&format, &name)),
            hero: assets
                .library_hero
                .or(assets.library_hero_2x)
                .and_then(|name| self.asset_url(&format, &name)),
        }
    }

    fn asset_url(&self, format: &str, filename: &str) -> Option<String> {
        if format.is_empty() || filename.is_empty() {
            return None;
        }
        let path = format.replace("${FILENAME}", filename);
        let path = path
            .split('?')
            .next()
            .unwrap_or(&path)
            .trim_start_matches('/');
        if path.is_empty() {
            return None;
        }
        Some(format!(
            "{}/{}",
            self.assets_cdn.trim_end_matches('/'),
            path
        ))
    }

    async fn download_first(&self, urls: &[Option<String>], dest: &Path) -> Result<Option<String>> {
        for url in urls.iter().flatten() {
            if let Some(path) = self.download_image(url, dest).await? {
                return Ok(Some(path));
            }
        }
        Ok(None)
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

fn local_file(path: Option<&String>) -> bool {
    path.map(|p| Path::new(p).is_file()).unwrap_or(false)
}

fn needs_steam_enrich(cache: Option<&SteamCache>) -> bool {
    let Some(cache) = cache else {
        return true;
    };
    let cover_ok = local_file(cache.cover_path.as_ref());
    let gallery_ok = cache
        .screenshot_paths
        .iter()
        .any(|p| Path::new(p).is_file());
    !cover_ok || !gallery_ok
}

#[derive(Default)]
struct SteamDetails {
    description: String,
    tags: Vec<String>,
    screenshot_urls: Vec<String>,
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
    screenshots: Option<Vec<SteamScreenshot>>,
}

#[derive(Deserialize, Default)]
struct SteamScreenshot {
    path_full: Option<String>,
    path_thumbnail: Option<String>,
}

#[derive(Deserialize)]
struct Genre {
    description: String,
}

#[derive(Default)]
struct LibraryAssets {
    capsule: Option<String>,
    hero: Option<String>,
}

#[derive(Deserialize, Default)]
struct GetItemsEnvelope {
    #[serde(default)]
    response: GetItemsResponse,
}

#[derive(Deserialize, Default)]
struct GetItemsResponse {
    #[serde(default)]
    store_items: Vec<GetItemsStoreItem>,
}

#[derive(Deserialize, Default)]
struct GetItemsStoreItem {
    #[serde(default)]
    assets: Option<StoreAssets>,
}

#[derive(Deserialize, Default)]
struct StoreAssets {
    asset_url_format: Option<String>,
    library_capsule: Option<String>,
    library_capsule_2x: Option<String>,
    hero_capsule: Option<String>,
    library_hero: Option<String>,
    library_hero_2x: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::{Game, SteamCache};
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
            notes: String::new(),
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
            .respond_with(ResponseTemplate::new(200).set_body_string(format!(
                r#"{{"480":{{"success":true,"data":{{"name":"Demo Game","short_description":"A demo.","genres":[{{"description":"Action"}}],"screenshots":[{{"path_full":"{}/ss/full.jpg"}}]}}}}}}"#,
                server.uri()
            )))
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
        Mock::given(method("GET"))
            .and(path("/ss/full.jpg"))
            .respond_with(ResponseTemplate::new(200).set_body_bytes(vec![0xDDu8; 64]))
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
        assert_eq!(cache.screenshot_paths.len(), 1);
    }

    #[tokio::test]
    async fn enrich_runs_when_catalog_has_tags_but_no_screenshots() {
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
            .respond_with(ResponseTemplate::new(200).set_body_string(format!(
                r#"{{"480":{{"success":true,"data":{{"short_description":"A demo.","genres":[{{"description":"Action"}}],"screenshots":[{{"path_full":"{}/ss/full.jpg"}}]}}}}}}"#,
                server.uri()
            )))
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
        Mock::given(method("GET"))
            .and(path("/ss/full.jpg"))
            .respond_with(ResponseTemplate::new(200).set_body_bytes(vec![0xDDu8; 64]))
            .mount(&server)
            .await;

        let file = NamedTempFile::new().unwrap();
        let db = LocalDb::open(file.path());
        let mut g = game("Demo Game");
        g.description = "from catalog".into();
        g.tags = vec!["Indie".into()];
        db.upsert_games(&[g.clone()]).unwrap();
        let covers = tempfile::tempdir().unwrap();
        let client = SteamClient::with_bases(server.uri(), server.uri()).unwrap();
        client
            .enrich_missing_into(&db, &[g], covers.path())
            .await
            .unwrap();
        let cache = db.get_steam_cache("g1").unwrap().unwrap();
        assert_eq!(cache.screenshot_paths.len(), 1);
    }

    #[tokio::test]
    async fn enrich_fetches_cover_even_when_catalog_has_screenshot_urls() {
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
            .respond_with(ResponseTemplate::new(200).set_body_string(format!(
                r#"{{"480":{{"success":true,"data":{{"short_description":"A demo.","genres":[{{"description":"Action"}}],"screenshots":[{{"path_full":"{}/ss/full.jpg"}}]}}}}}}"#,
                server.uri()
            )))
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
        Mock::given(method("GET"))
            .and(path("/ss/full.jpg"))
            .respond_with(ResponseTemplate::new(200).set_body_bytes(vec![0xDDu8; 64]))
            .mount(&server)
            .await;

        let file = NamedTempFile::new().unwrap();
        let db = LocalDb::open(file.path());
        let mut g = game("Demo Game");
        g.description = "from nextcloud".into();
        g.tags = vec!["Indie".into()];
        g.screenshot_urls = vec!["Shift At Midnight/screenshots/1.jpg".into()];
        db.upsert_games(&[g.clone()]).unwrap();
        let covers = tempfile::tempdir().unwrap();
        let client = SteamClient::with_bases(server.uri(), server.uri()).unwrap();
        client
            .enrich_missing_into(&db, &[g], covers.path())
            .await
            .unwrap();
        let cache = db.get_steam_cache("g1").unwrap().unwrap();
        assert!(cache.cover_path.is_some());
        assert_eq!(cache.screenshot_paths.len(), 1);
    }

    #[tokio::test]
    async fn enrich_skips_when_cover_and_gallery_files_exist() {
        let server = MockServer::start().await;
        let file = NamedTempFile::new().unwrap();
        let db = LocalDb::open(file.path());
        let covers = tempfile::tempdir().unwrap();
        let cover = covers.path().join("g1.jpg");
        let shot = covers.path().join("g1-shot-0.jpg");
        std::fs::write(&cover, [0xFFu8; 64]).unwrap();
        std::fs::write(&shot, [0xDDu8; 64]).unwrap();
        db.upsert_games(&[game("Demo Game")]).unwrap();
        db.upsert_steam_cache(&SteamCache {
            game_id: "g1".into(),
            steam_app_id: Some(480),
            tags: vec!["Action".into()],
            description: "cached".into(),
            cover_path: Some(cover.to_string_lossy().into_owned()),
            hero_path: None,
            screenshot_paths: vec![shot.to_string_lossy().into_owned()],
            fetched_at: 1,
        })
        .unwrap();
        let client = SteamClient::with_bases(server.uri(), server.uri()).unwrap();
        client
            .enrich_missing_into(&db, &[game("Demo Game")], covers.path())
            .await
            .unwrap();
        assert_eq!(
            db.get_steam_cache("g1").unwrap().unwrap().description,
            "cached"
        );
    }

    #[test]
    fn asset_url_joins_hashed_filename() {
        let client = SteamClient::with_endpoints(
            "https://store.example",
            "https://cdn.example",
            "https://api.example",
            "https://shared.akamai.steamstatic.com/store_item_assets",
        )
        .unwrap();
        assert_eq!(
            client
                .asset_url(
                    "steam/apps/3527290/${FILENAME}?t=1",
                    "480bd879ac737921bfa2529a6fea15961267ad21/library_600x900.jpg",
                )
                .as_deref(),
            Some(
                "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3527290/480bd879ac737921bfa2529a6fea15961267ad21/library_600x900.jpg"
            )
        );
    }

    #[tokio::test]
    async fn enrich_downloads_hashed_capsule_when_legacy_cdn_404s() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/api/appdetails"))
            .respond_with(ResponseTemplate::new(200).set_body_string(
                r#"{"480":{"success":true,"data":{"short_description":"A demo.","genres":[{"description":"Action"}]}}}"#,
            ))
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path("/IStoreBrowseService/GetItems/v1/"))
            .respond_with(ResponseTemplate::new(200).set_body_string(
                r#"{"response":{"store_items":[{"assets":{"asset_url_format":"steam/apps/480/${FILENAME}?t=1","library_capsule":"abc123/library_600x900.jpg"}}]}}"#,
            ))
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path("/steam/apps/480/library_600x900.jpg"))
            .respond_with(ResponseTemplate::new(404))
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path("/steam/apps/480/abc123/library_600x900.jpg"))
            .respond_with(ResponseTemplate::new(200).set_body_bytes(vec![0xAAu8; 64]))
            .mount(&server)
            .await;

        let file = NamedTempFile::new().unwrap();
        let db = LocalDb::open(file.path());
        let covers = tempfile::tempdir().unwrap();
        let shot = covers.path().join("g1-shot-0.jpg");
        std::fs::write(&shot, [0xDDu8; 64]).unwrap();
        db.upsert_games(&[game("PEAK")]).unwrap();
        db.upsert_steam_cache(&SteamCache {
            game_id: "g1".into(),
            steam_app_id: Some(480),
            tags: vec!["Action".into()],
            description: "cached".into(),
            cover_path: None,
            hero_path: None,
            screenshot_paths: vec![shot.to_string_lossy().into_owned()],
            fetched_at: 1,
        })
        .unwrap();
        let client = SteamClient::with_bases(server.uri(), server.uri()).unwrap();
        client
            .enrich_missing_into(&db, &[game("PEAK")], covers.path())
            .await
            .unwrap();
        let cache = db.get_steam_cache("g1").unwrap().unwrap();
        let cover = PathBuf::from(cache.cover_path.expect("hashed capsule"));
        assert_eq!(std::fs::read(&cover).unwrap(), vec![0xAAu8; 64]);
    }
}
