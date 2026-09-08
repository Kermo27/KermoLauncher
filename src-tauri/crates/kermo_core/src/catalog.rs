use crate::db::LocalDb;
use crate::webdav::WebDavClient;
use crate::Result;

pub async fn refresh_from_remote(db: &LocalDb, webdav: &WebDavClient) -> Result<usize> {
    let mut settings = db.get_settings()?;
    let Some(config) = settings.nextcloud.clone() else {
        return Ok(0);
    };

    let resolved = webdav.resolve_config(&config).await?;
    if resolved.root_folder != config.root_folder {
        settings.nextcloud = Some(resolved.clone());
        db.save_settings(&settings)?;
    }

    let games = webdav.download_metadata(&resolved).await?;
    db.upsert_games(&games)?;
    if !games.is_empty() {
        let ids: Vec<String> = games.iter().map(|g| g.id.clone()).collect();
        db.remove_games_not_in(&ids)?;
    }
    Ok(games.len())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::{AppSettings, Game, NextcloudConfig};
    use crate::LocalDb;
    use tempfile::NamedTempFile;
    use wiremock::matchers::{method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    #[tokio::test]
    async fn refresh_upserts_games_and_prunes_removed() {
        let server = MockServer::start().await;
        Mock::given(method("HEAD"))
            .and(path("/public.php/dav/files/tok/metadata.json"))
            .respond_with(ResponseTemplate::new(200))
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path("/public.php/dav/files/tok/metadata.json"))
            .respond_with(ResponseTemplate::new(200).set_body_string(
                r#"[{"id":"keep","name":"Keep","manifestUrl":"keep/manifest.json"}]"#,
            ))
            .mount(&server)
            .await;

        let file = NamedTempFile::new().unwrap();
        let db = LocalDb::open(file.path());
        db.upsert_games(&[Game {
            id: "gone".into(),
            name: "Gone".into(),
            version: String::new(),
            description: String::new(),
            tags: vec![],
            dependencies: vec![],
            screenshot_urls: vec![],
            manifest_url: "gone/manifest.json".into(),
            size_bytes: 0,
            launch_config: None,
        }])
        .unwrap();
        db.save_settings(&AppSettings {
            nextcloud: Some(NextcloudConfig {
                share_url: format!("{}/s/tok", server.uri()),
                share_token: "tok".into(),
                root_folder: String::new(),
            }),
            onboarding_completed: true,
            ..AppSettings::default()
        })
        .unwrap();

        let n = refresh_from_remote(&db, &WebDavClient::new().unwrap())
            .await
            .unwrap();
        assert_eq!(n, 1);
        let ids: Vec<_> = db
            .get_all_games()
            .unwrap()
            .into_iter()
            .map(|g| g.id)
            .collect();
        assert_eq!(ids, ["keep"]);
    }
}
