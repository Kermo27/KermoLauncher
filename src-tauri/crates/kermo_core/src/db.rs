use std::path::{Path, PathBuf};
use std::sync::Mutex;

use rusqlite::{params, params_from_iter, Connection, OptionalExtension, Row};

use crate::models::*;
use crate::Result;

pub struct LocalDb {
    path: PathBuf,
    initialized: Mutex<bool>,
    settings_cache: Mutex<Option<AppSettings>>,
}

impl LocalDb {
    pub fn open(path: impl Into<PathBuf>) -> Self {
        Self {
            path: path.into(),
            initialized: Mutex::new(false),
            settings_cache: Mutex::new(None),
        }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    fn connect(&self) -> Result<Connection> {
        if let Some(dir) = self.path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let conn = Connection::open(&self.path)?;
        conn.execute_batch("PRAGMA foreign_keys=ON;")?;
        Ok(conn)
    }

    pub fn initialize(&self) -> Result<()> {
        if *self.initialized.lock().expect("init lock") {
            return Ok(());
        }
        let mut guard = self.initialized.lock().expect("init lock");
        if *guard {
            return Ok(());
        }

        let conn = self.connect()?;
        conn.execute_batch("PRAGMA journal_mode=WAL;")?;

        conn.execute_batch(
            r#"
            CREATE TABLE IF NOT EXISTS games (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                version TEXT,
                description TEXT,
                tags TEXT,
                dependencies TEXT,
                screenshot_urls TEXT,
                remote_zip_url TEXT,
                size_bytes INTEGER,
                sha256 TEXT,
                launch_config TEXT,
                notes TEXT
            );

            CREATE TABLE IF NOT EXISTS game_local_state (
                game_id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                installed_path TEXT,
                play_time_seconds INTEGER DEFAULT 0,
                last_played INTEGER,
                FOREIGN KEY (game_id) REFERENCES games(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS downloads (
                id TEXT PRIMARY KEY,
                game_id TEXT NOT NULL,
                remote_url TEXT NOT NULL,
                local_path TEXT NOT NULL,
                total_bytes INTEGER NOT NULL,
                downloaded_bytes INTEGER DEFAULT 0,
                status TEXT NOT NULL,
                error TEXT,
                started_at INTEGER,
                completed_at INTEGER,
                FOREIGN KEY (game_id) REFERENCES games(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS steam_cache (
                game_id TEXT PRIMARY KEY,
                steam_app_id INTEGER,
                tags TEXT,
                description TEXT,
                cover_path TEXT,
                hero_path TEXT,
                screenshot_paths TEXT,
                fetched_at INTEGER
            );
            "#,
        )?;

        migrate_schema(&conn)?;
        *guard = true;
        Ok(())
    }

    pub fn upsert_games(&self, games: &[Game]) -> Result<()> {
        self.initialize()?;
        let mut conn = self.connect()?;
        let tx = conn.transaction()?;
        for game in games {
            tx.execute(
                r#"
                INSERT INTO games (id, name, version, description, tags, dependencies,
                    screenshot_urls, manifest_url, size_bytes, launch_config, notes)
                VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)
                ON CONFLICT(id) DO UPDATE SET
                    name=?2, version=?3, description=?4, tags=?5, dependencies=?6,
                    screenshot_urls=?7, manifest_url=?8, size_bytes=?9, launch_config=?10,
                    notes=?11
                "#,
                params![
                    game.id,
                    game.name,
                    game.version,
                    game.description,
                    serde_json::to_string(&game.tags)?,
                    serde_json::to_string(&game.dependencies)?,
                    serde_json::to_string(&game.screenshot_urls)?,
                    game.manifest_url,
                    game.size_bytes,
                    game.launch_config
                        .as_ref()
                        .map(serde_json::to_string)
                        .transpose()?,
                    game.notes,
                ],
            )?;
        }
        tx.commit()?;
        Ok(())
    }

    pub fn remove_games_not_in(&self, keep_ids: &[String]) -> Result<()> {
        if keep_ids.is_empty() {
            return Ok(());
        }
        self.initialize()?;
        let mut conn = self.connect()?;
        let tx = conn.transaction()?;
        let placeholders: String = keep_ids.iter().map(|_| "?").collect::<Vec<_>>().join(",");
        let sql_state =
            format!("DELETE FROM game_local_state WHERE game_id NOT IN ({placeholders})");
        let sql_games = format!("DELETE FROM games WHERE id NOT IN ({placeholders})");
        tx.execute(&sql_state, params_from_iter(keep_ids.iter()))?;
        tx.execute(&sql_games, params_from_iter(keep_ids.iter()))?;
        tx.commit()?;
        Ok(())
    }

    pub fn get_game(&self, game_id: &str) -> Result<Option<Game>> {
        self.initialize()?;
        let conn = self.connect()?;
        let mut stmt = conn.prepare(
            r#"
            SELECT id, name, version, description, tags, dependencies, screenshot_urls,
                   manifest_url, size_bytes, launch_config, notes
            FROM games WHERE id = ?1
            "#,
        )?;
        let game = stmt.query_row(params![game_id], map_game).optional()?;
        Ok(game)
    }

    pub fn get_all_games(&self) -> Result<Vec<Game>> {
        self.initialize()?;
        let conn = self.connect()?;
        let mut stmt = conn.prepare("SELECT * FROM games")?;
        let rows = stmt.query_map([], map_game)?;
        Ok(rows.collect::<std::result::Result<Vec<_>, _>>()?)
    }

    pub fn get_games_by_status(&self, status: InstallStatus) -> Result<Vec<Game>> {
        self.initialize()?;
        let conn = self.connect()?;
        let mut stmt = conn.prepare(
            r#"
            SELECT g.* FROM games g
            JOIN game_local_state s ON g.id = s.game_id
            WHERE s.status = ?1
            "#,
        )?;
        let rows = stmt.query_map(params![install_status_str(status)], map_game)?;
        Ok(rows.collect::<std::result::Result<Vec<_>, _>>()?)
    }

    pub fn upsert_local_state(&self, state: &GameLocalState) -> Result<()> {
        self.initialize()?;
        let conn = self.connect()?;
        conn.execute(
            r#"
            INSERT INTO game_local_state (game_id, status, installed_path, play_time_seconds,
                last_played, installed_version, installed_manifest)
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
            ON CONFLICT(game_id) DO UPDATE SET
                status=?2, installed_path=?3, play_time_seconds=?4,
                last_played=?5, installed_version=?6, installed_manifest=?7
            "#,
            params![
                state.game_id,
                install_status_str(state.status),
                state.installed_path,
                state.play_time_seconds,
                state.last_played,
                state.installed_version,
                state
                    .installed_manifest
                    .as_ref()
                    .map(serde_json::to_string)
                    .transpose()?,
            ],
        )?;
        Ok(())
    }

    pub fn get_local_state(&self, game_id: &str) -> Result<Option<GameLocalState>> {
        self.initialize()?;
        let conn = self.connect()?;
        let mut stmt = conn.prepare(
            r#"
            SELECT game_id, status, installed_path, play_time_seconds, last_played,
                   installed_version, installed_manifest
            FROM game_local_state WHERE game_id = ?1
            "#,
        )?;
        Ok(stmt
            .query_row(params![game_id], map_local_state)
            .optional()?)
    }

    pub fn get_all_local_states(&self) -> Result<Vec<GameLocalState>> {
        self.initialize()?;
        let conn = self.connect()?;
        let mut stmt = conn.prepare("SELECT * FROM game_local_state")?;
        let rows = stmt.query_map([], map_local_state)?;
        Ok(rows.collect::<std::result::Result<Vec<_>, _>>()?)
    }

    pub fn upsert_download_task(&self, task: &DownloadTask) -> Result<()> {
        self.initialize()?;
        let conn = self.connect()?;
        conn.execute(
            r#"
            INSERT INTO downloads (id, game_id, remote_url, local_path, total_bytes,
                downloaded_bytes, status, error, started_at, completed_at)
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)
            ON CONFLICT(id) DO UPDATE SET
                total_bytes=?5, downloaded_bytes=?6, status=?7, error=?8, completed_at=?10
            "#,
            params![
                task.id,
                task.game_id,
                task.remote_url,
                task.local_path,
                task.total_bytes,
                task.downloaded_bytes,
                download_status_str(task.status),
                task.error,
                task.started_at,
                task.completed_at,
            ],
        )?;
        Ok(())
    }

    pub fn get_download_task(&self, task_id: &str) -> Result<Option<DownloadTask>> {
        self.initialize()?;
        let conn = self.connect()?;
        let mut stmt = conn.prepare("SELECT * FROM downloads WHERE id = ?1")?;
        Ok(stmt
            .query_row(params![task_id], map_download_task)
            .optional()?)
    }

    pub fn get_all_download_tasks(&self) -> Result<Vec<DownloadTask>> {
        self.initialize()?;
        let conn = self.connect()?;
        let mut stmt = conn.prepare("SELECT * FROM downloads ORDER BY started_at DESC")?;
        let rows = stmt.query_map([], map_download_task)?;
        Ok(rows.collect::<std::result::Result<Vec<_>, _>>()?)
    }

    pub fn delete_download_task(&self, task_id: &str) -> Result<()> {
        self.initialize()?;
        let conn = self.connect()?;
        conn.execute("DELETE FROM downloads WHERE id = ?1", params![task_id])?;
        Ok(())
    }

    pub fn get_settings(&self) -> Result<AppSettings> {
        if let Some(cached) = self.settings_cache.lock().expect("settings lock").clone() {
            return Ok(cached);
        }

        self.initialize()?;
        let conn = self.connect()?;
        let json: Option<String> = conn
            .query_row(
                "SELECT value FROM settings WHERE key = 'app_settings'",
                [],
                |row| row.get(0),
            )
            .optional()?;

        let mut settings = match json {
            Some(raw) => serde_json::from_str(&raw).unwrap_or_default(),
            None => AppSettings::default(),
        };

        if !settings.onboarding_completed && settings.nextcloud.is_some() {
            settings.onboarding_completed = true;
        }

        *self.settings_cache.lock().expect("settings lock") = Some(settings.clone());
        Ok(settings)
    }

    pub fn save_settings(&self, settings: &AppSettings) -> Result<()> {
        self.initialize()?;
        let conn = self.connect()?;
        let json = serde_json::to_string(settings)?;
        conn.execute(
            r#"
            INSERT INTO settings (key, value) VALUES ('app_settings', ?1)
            ON CONFLICT(key) DO UPDATE SET value=?1
            "#,
            params![json],
        )?;
        *self.settings_cache.lock().expect("settings lock") = Some(settings.clone());
        Ok(())
    }

    pub fn upsert_steam_cache(&self, cache: &SteamCache) -> Result<()> {
        self.initialize()?;
        let conn = self.connect()?;
        conn.execute(
            r#"
            INSERT INTO steam_cache (game_id, steam_app_id, tags, description, cover_path, hero_path, screenshot_paths, fetched_at)
            VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)
            ON CONFLICT(game_id) DO UPDATE SET
                steam_app_id=?2, tags=?3, description=?4, cover_path=?5, hero_path=?6,
                screenshot_paths=?7, fetched_at=?8
            "#,
            params![
                cache.game_id,
                cache.steam_app_id,
                serde_json::to_string(&cache.tags)?,
                cache.description,
                cache.cover_path,
                cache.hero_path,
                serde_json::to_string(&cache.screenshot_paths)?,
                cache.fetched_at,
            ],
        )?;
        Ok(())
    }

    pub fn get_steam_cache(&self, game_id: &str) -> Result<Option<SteamCache>> {
        self.initialize()?;
        let conn = self.connect()?;
        let mut stmt = conn.prepare(
            r#"
            SELECT game_id, steam_app_id, tags, description, cover_path, hero_path, screenshot_paths, fetched_at
            FROM steam_cache WHERE game_id = ?1
            "#,
        )?;
        Ok(stmt
            .query_row(params![game_id], map_steam_cache)
            .optional()?)
    }

    pub fn get_all_steam_cache(&self) -> Result<Vec<SteamCache>> {
        self.initialize()?;
        let conn = self.connect()?;
        let mut stmt = conn.prepare("SELECT game_id, steam_app_id, tags, description, cover_path, hero_path, screenshot_paths, fetched_at FROM steam_cache")?;
        let rows = stmt.query_map([], map_steam_cache)?;
        Ok(rows.collect::<std::result::Result<Vec<_>, _>>()?)
    }
}

fn migrate_schema(conn: &Connection) -> Result<()> {
    conn.execute(
        "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)",
        [],
    )?;
    let version: i32 = conn
        .query_row(
            "SELECT COALESCE(MAX(version), 0) FROM schema_version",
            [],
            |row| row.get(0),
        )
        .unwrap_or(0);

        if version < 2 {
        add_column_if_missing(conn, "games", "manifest_url", "TEXT")?;
        add_column_if_missing(conn, "game_local_state", "installed_version", "TEXT")?;
        add_column_if_missing(conn, "game_local_state", "installed_manifest", "TEXT")?;
        conn.execute("INSERT INTO schema_version (version) VALUES (2)", [])?;
    }
    if version < 3 {
        conn.execute_batch(
            r#"
            CREATE TABLE IF NOT EXISTS steam_cache (
                game_id TEXT PRIMARY KEY,
                steam_app_id INTEGER,
                tags TEXT,
                description TEXT,
                cover_path TEXT,
                hero_path TEXT,
                fetched_at INTEGER
            );
            "#,
        )?;
        conn.execute("INSERT INTO schema_version (version) VALUES (3)", [])?;
    }
    if version < 4 {
        add_column_if_missing(conn, "games", "notes", "TEXT")?;
        add_column_if_missing(conn, "steam_cache", "screenshot_paths", "TEXT")?;
        conn.execute("INSERT INTO schema_version (version) VALUES (4)", [])?;
    }
    Ok(())
}

fn add_column_if_missing(conn: &Connection, table: &str, column: &str, ty: &str) -> Result<()> {
    let mut stmt = conn.prepare(&format!("PRAGMA table_info({table})"))?;
    let names: Vec<String> = stmt
        .query_map([], |row| row.get::<_, String>(1))?
        .collect::<std::result::Result<Vec<_>, _>>()?;
    if !names.iter().any(|n| n.eq_ignore_ascii_case(column)) {
        conn.execute(&format!("ALTER TABLE {table} ADD COLUMN {column} {ty}"), [])?;
    }
    Ok(())
}

fn map_game(row: &Row<'_>) -> rusqlite::Result<Game> {
    let tags = parse_json_vec(row.get("tags")?);
    let deps = parse_json_vec(row.get("dependencies")?);
    let shots = parse_json_vec(row.get("screenshot_urls")?);
    let launch_json: Option<String> = row.get("launch_config")?;
    let launch_config = launch_json
        .as_deref()
        .and_then(|s| serde_json::from_str(s).ok());
    let manifest_url: Option<String> = row.get("manifest_url").ok().flatten();
    let size: Option<i64> = row.get("size_bytes").ok().flatten();

    Ok(Game {
        id: row.get("id")?,
        name: row.get("name")?,
        version: row.get::<_, Option<String>>("version")?.unwrap_or_default(),
        description: row
            .get::<_, Option<String>>("description")?
            .unwrap_or_default(),
        tags,
        dependencies: deps,
        screenshot_urls: shots,
        manifest_url: manifest_url.unwrap_or_default(),
        size_bytes: size.unwrap_or(0),
        launch_config,
        notes: row
            .get::<_, Option<String>>("notes")
            .ok()
            .flatten()
            .unwrap_or_default(),
    })
}

fn parse_json_vec(raw: Option<String>) -> Vec<String> {
    raw.as_deref()
        .and_then(|s| serde_json::from_str(s).ok())
        .unwrap_or_default()
}

fn map_steam_cache(row: &Row<'_>) -> rusqlite::Result<SteamCache> {
    let tags = parse_json_vec(row.get("tags")?);
    Ok(SteamCache {
        game_id: row.get("game_id")?,
        steam_app_id: row.get("steam_app_id")?,
        tags,
        description: row.get::<_, Option<String>>("description")?.unwrap_or_default(),
        cover_path: row.get("cover_path")?,
        hero_path: row.get("hero_path")?,
        screenshot_paths: parse_json_vec(row.get("screenshot_paths").ok().flatten()),
        fetched_at: row.get::<_, Option<i64>>("fetched_at")?.unwrap_or(0),
    })
}

fn map_local_state(row: &Row<'_>) -> rusqlite::Result<GameLocalState> {
    let last: Option<i64> = row.get("last_played")?;
    let last_played = last.filter(|&v| v != 0);
    let manifest_json: Option<String> = row.get("installed_manifest")?;
    let installed_manifest = manifest_json
        .as_deref()
        .and_then(|s| serde_json::from_str(s).ok());
    let status_raw: String = row.get("status")?;

    Ok(GameLocalState {
        game_id: row.get("game_id")?,
        status: parse_install_status(&status_raw),
        installed_path: row.get("installed_path")?,
        play_time_seconds: row.get("play_time_seconds")?,
        last_played,
        installed_version: row.get("installed_version")?,
        installed_manifest,
    })
}

fn map_download_task(row: &Row<'_>) -> rusqlite::Result<DownloadTask> {
    let status_raw: String = row.get("status")?;
    Ok(DownloadTask {
        id: row.get("id")?,
        game_id: row.get("game_id")?,
        remote_url: row.get("remote_url")?,
        local_path: row.get("local_path")?,
        total_bytes: row.get("total_bytes")?,
        downloaded_bytes: row.get("downloaded_bytes")?,
        status: parse_download_status(&status_raw),
        error: row.get("error")?,
        started_at: row.get("started_at")?,
        completed_at: row.get("completed_at")?,
        install_stage: InstallStage::Preparing,
    })
}

fn install_status_str(s: InstallStatus) -> &'static str {
    match s {
        InstallStatus::NotInstalled => "NotInstalled",
        InstallStatus::Downloading => "Downloading",
        InstallStatus::Installing => "Installing",
        InstallStatus::Installed => "Installed",
        InstallStatus::Failed => "Failed",
        InstallStatus::Paused => "Paused",
    }
}

fn parse_install_status(s: &str) -> InstallStatus {
    match s {
        "Downloading" => InstallStatus::Downloading,
        "Installing" => InstallStatus::Installing,
        "Installed" => InstallStatus::Installed,
        "Failed" => InstallStatus::Failed,
        "Paused" => InstallStatus::Paused,
        _ => InstallStatus::NotInstalled,
    }
}

fn download_status_str(s: DownloadStatus) -> &'static str {
    match s {
        DownloadStatus::Queued => "Queued",
        DownloadStatus::Downloading => "Downloading",
        DownloadStatus::Paused => "Paused",
        DownloadStatus::Completed => "Completed",
        DownloadStatus::Failed => "Failed",
        DownloadStatus::Cancelled => "Cancelled",
    }
}

fn parse_download_status(s: &str) -> DownloadStatus {
    match s {
        "Downloading" => DownloadStatus::Downloading,
        "Paused" => DownloadStatus::Paused,
        "Completed" => DownloadStatus::Completed,
        "Failed" => DownloadStatus::Failed,
        "Cancelled" => DownloadStatus::Cancelled,
        _ => DownloadStatus::Queued,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::NamedTempFile;

    fn game(id: &str) -> Game {
        Game {
            id: id.into(),
            name: id.into(),
            version: "1.0.0".into(),
            description: String::new(),
            tags: vec![],
            dependencies: vec![],
            screenshot_urls: vec![],
            manifest_url: format!("{id}/manifest.json"),
            size_bytes: 0,
            launch_config: None,
            notes: String::new(),
        }
    }

    fn db() -> (LocalDb, NamedTempFile) {
        let file = NamedTempFile::new().unwrap();
        (LocalDb::open(file.path()), file)
    }

    #[test]
    fn remove_games_not_in_deletes_missing_games_and_states() {
        let (db, _file) = db();
        db.upsert_games(&[game("a"), game("b"), game("c")]).unwrap();
        db.upsert_local_state(&GameLocalState {
            game_id: "b".into(),
            status: InstallStatus::Installed,
            installed_path: None,
            play_time_seconds: 0,
            last_played: None,
            installed_version: None,
            installed_manifest: None,
        })
        .unwrap();
        db.upsert_local_state(&GameLocalState {
            game_id: "c".into(),
            status: InstallStatus::Failed,
            installed_path: None,
            play_time_seconds: 0,
            last_played: None,
            installed_version: None,
            installed_manifest: None,
        })
        .unwrap();

        db.remove_games_not_in(&["a".into(), "b".into()]).unwrap();

        let mut ids: Vec<_> = db
            .get_all_games()
            .unwrap()
            .into_iter()
            .map(|g| g.id)
            .collect();
        ids.sort();
        assert_eq!(ids, ["a", "b"]);
        assert!(db.get_local_state("b").unwrap().is_some());
        assert!(db.get_local_state("c").unwrap().is_none());
    }

    #[test]
    fn remove_games_not_in_empty_list_is_noop() {
        let (db, _file) = db();
        db.upsert_games(&[game("a")]).unwrap();
        db.remove_games_not_in(&[]).unwrap();
        assert_eq!(db.get_all_games().unwrap().len(), 1);
    }

    #[test]
    fn get_settings_migrates_existing_share_as_completed() {
        let (db, _file) = db();
        db.save_settings(&AppSettings {
            onboarding_completed: false,
            nextcloud: Some(NextcloudConfig {
                share_url: "https://cloud.example/s/abc".into(),
                share_token: String::new(),
                root_folder: String::new(),
            }),
            ..AppSettings::default()
        })
        .unwrap();


        let reopened = LocalDb::open(db.path());
        let settings = reopened.get_settings().unwrap();
        assert!(settings.onboarding_completed);
        assert!(!settings.needs_onboarding());
    }

    #[test]
    fn reads_pascal_case_settings_blob_from_csharp() {
        let (db, _file) = db();
        db.initialize().unwrap();
        let conn = db.connect().unwrap();
        conn.execute(
            "INSERT INTO settings (key, value) VALUES ('app_settings', ?1)",
            params![
                r#"{"InstallFolder":"/games","MaxParallelDownloads":4,"AutoUpdate":true,"Theme":"Dark","Language":"pl","OnboardingCompleted":true}"#
            ],
        )
        .unwrap();
        drop(conn);

        let reopened = LocalDb::open(db.path());
        let s = reopened.get_settings().unwrap();
        assert_eq!(s.install_folder, "/games");
        assert_eq!(s.max_parallel_downloads, 4);
        assert_eq!(s.theme, "Dark");
        assert_eq!(s.language, "pl");
        assert!(s.onboarding_completed);
    }

    #[test]
    fn steam_cache_is_created_for_existing_v2_database() {
        let file = NamedTempFile::new().unwrap();
        {
            let conn = Connection::open(file.path()).unwrap();
            conn.execute_batch(
                r#"
                CREATE TABLE games (id TEXT PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE schema_version (version INTEGER NOT NULL);
                INSERT INTO schema_version (version) VALUES (2);
                "#,
            )
            .unwrap();
        }
        let db = LocalDb::open(file.path());
        assert!(db.get_all_steam_cache().unwrap().is_empty());
    }
}
