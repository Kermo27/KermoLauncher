mod logging;

use std::sync::Arc;

use kermo_core::{
    library_items, probe_share, refresh_from_remote, AppSettings, DownloadService, DownloadTask,
    FolderValidation, GameService, LaunchResult, LibraryItem, LocalDb, ProtonInstall, SteamClient,
    WebDavClient,
};
use tauri::{Manager, State};

pub struct AppState {
    db: Arc<LocalDb>,
    games: GameService,
    webdav: WebDavClient,
    steam: SteamClient,
}

fn map_err(e: kermo_core::Error) -> String {
    tracing::error!("{e}");
    e.to_string()
}

fn not_found(game_id: &str) -> String {
    tracing::error!("game not found: {game_id}");
    "Game not found".to_string()
}

#[tauri::command]
fn app_info() -> AppInfo {
    AppInfo {
        name: kermo_core::app_name().to_string(),
        version: kermo_core::app_version().to_string(),
    }
}

#[derive(serde::Serialize)]
struct AppInfo {
    name: String,
    version: String,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct ShareProbe {
    game_count: usize,
    root_folder: String,
}

#[tauri::command]
fn get_settings(state: State<AppState>) -> Result<AppSettings, String> {
    state.db.get_settings().map_err(map_err)
}

#[tauri::command]
fn save_settings(state: State<AppState>, settings: AppSettings) -> Result<(), String> {
    state.db.save_settings(&settings).map_err(map_err)?;
    tracing::info!("settings saved");
    Ok(())
}

#[tauri::command]
fn get_library(state: State<AppState>) -> Result<Vec<LibraryItem>, String> {
    library_items(&state.db).map_err(map_err)
}

#[tauri::command]
fn get_download_tasks(state: State<AppState>) -> Result<Vec<DownloadTask>, String> {
    state.db.get_all_download_tasks().map_err(map_err)
}

#[tauri::command]
async fn refresh_catalog(state: State<'_, AppState>) -> Result<usize, String> {
    tracing::info!("refreshing catalog");
    let n = refresh_from_remote(&state.db, &state.webdav)
        .await
        .map_err(map_err)?;
    if let Ok(games) = state.db.get_all_games() {
        if let Err(e) = state.steam.enrich_missing(&state.db, &games).await {
            tracing::warn!("Steam enrich failed: {e}");
        }
    }
    tracing::info!("catalog refresh finished with {n} games");
    Ok(n)
}

#[tauri::command]
async fn install_game(state: State<'_, AppState>, game_id: String) -> Result<(), String> {
    tracing::info!(game_id, "install requested");
    let game = state
        .db
        .get_game(&game_id)
        .map_err(map_err)?
        .ok_or_else(|| not_found(&game_id))?;
    state.games.install(&game).await.map_err(map_err)
}

#[tauri::command]
async fn update_game(state: State<'_, AppState>, game_id: String) -> Result<(), String> {
    tracing::info!(game_id, "update requested");
    let game = state
        .db
        .get_game(&game_id)
        .map_err(map_err)?
        .ok_or_else(|| not_found(&game_id))?;
    state.games.update(&game).await.map_err(map_err)
}

#[tauri::command]
async fn resume_install(state: State<'_, AppState>, game_id: String) -> Result<(), String> {
    tracing::info!(game_id, "resume requested");
    let game = state
        .db
        .get_game(&game_id)
        .map_err(map_err)?
        .ok_or_else(|| not_found(&game_id))?;
    state.games.resume(&game).await.map_err(map_err)
}

#[tauri::command]
fn pause_install(state: State<AppState>, game_id: String) {
    tracing::info!(game_id, "pause requested");
    state.games.pause(&game_id);
}

#[tauri::command]
fn cancel_install(state: State<AppState>, game_id: String) -> Result<(), String> {
    tracing::info!(game_id, "cancel requested");
    state.games.cancel(&game_id).map_err(map_err)
}

#[tauri::command]
fn uninstall_game(state: State<AppState>, game_id: String) -> Result<(), String> {
    tracing::info!(game_id, "uninstall requested");
    state.games.uninstall(&game_id).map_err(map_err)
}

#[tauri::command]
fn launch_game(state: State<AppState>, game_id: String) -> Result<LaunchResult, String> {
    tracing::info!(game_id, "launch requested");
    state.games.launch(&game_id).map_err(map_err)
}

#[tauri::command]
fn verify_install(state: State<AppState>, game_id: String) -> Result<(), String> {
    tracing::info!(game_id, "verify requested");
    state.games.verify(&game_id).map_err(map_err)
}

#[tauri::command]
fn open_install_folder(state: State<AppState>, game_id: String) -> Result<(), String> {
    let local = state
        .db
        .get_local_state(&game_id)
        .map_err(map_err)?
        .ok_or_else(|| {
            tracing::error!(game_id, "open folder: not installed");
            "Not installed".to_string()
        })?;
    let path = local
        .installed_path
        .filter(|p| !p.is_empty())
        .ok_or_else(|| {
            tracing::error!(game_id, "open folder: missing install path");
            "Not installed".to_string()
        })?;
    tauri_plugin_opener::open_path(&path, None::<&str>).map_err(|e| {
        tracing::error!("open folder failed: {e}");
        e.to_string()
    })
}

#[tauri::command]
fn validate_install_folder(path: String) -> FolderValidation {
    kermo_core::validate_install_folder(&path)
}

#[tauri::command]
fn default_install_folder() -> String {
    kermo_core::default_install_folder()
        .to_string_lossy()
        .into_owned()
}

#[tauri::command]
fn data_directory() -> String {
    kermo_core::data_directory().to_string_lossy().into_owned()
}

#[tauri::command]
fn open_log_folder() -> Result<(), String> {
    let path = kermo_core::log_directory();
    std::fs::create_dir_all(&path).map_err(|e| {
        tracing::error!("create log directory failed: {e}");
        e.to_string()
    })?;
    tauri_plugin_opener::open_path(&path, None::<&str>).map_err(|e| {
        tracing::error!("open log folder failed: {e}");
        e.to_string()
    })
}

#[tauri::command]
fn log_frontend(level: String, message: String) {
    const MAX: usize = 8000;
    let message: String = if message.chars().count() > MAX {
        format!("{}…", message.chars().take(MAX).collect::<String>())
    } else {
        message
    };
    match level.as_str() {
        "error" => tracing::error!(target: "webview", "{message}"),
        "warn" => tracing::warn!(target: "webview", "{message}"),
        _ => tracing::info!(target: "webview", "{message}"),
    }
}

#[tauri::command]
fn list_proton_versions() -> Vec<ProtonInstall> {
    kermo_core::find_proton_installs(None)
}

#[tauri::command]
async fn test_share(state: State<'_, AppState>, share_url: String) -> Result<ShareProbe, String> {
    tracing::info!("testing Nextcloud share");
    let (config, n) = probe_share(&state.webdav, &share_url)
        .await
        .map_err(map_err)?;
    Ok(ShareProbe {
        game_count: n,
        root_folder: config.root_folder,
    })
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    logging::init();
    #[cfg(target_os = "linux")]
    {
        unsafe {
            std::env::set_var("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
        }
    }
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_process::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .setup(|app| {
            let db = Arc::new(LocalDb::open(kermo_core::db_path()));
            db.initialize().map_err(|e| {
                tracing::error!("database init failed: {e}");
                e.to_string()
            })?;
            let webdav = WebDavClient::new().map_err(|e| {
                tracing::error!("webdav client failed: {e}");
                e.to_string()
            })?;
            let downloads = DownloadService::new(webdav.clone(), db.clone());
            let games = GameService::new(downloads, db.clone(), webdav.clone());
            let steam = SteamClient::new().map_err(|e| {
                tracing::error!("steam client failed: {e}");
                e.to_string()
            })?;
            app.manage(AppState {
                db,
                games,
                webdav,
                steam,
            });
            let steam = app.state::<AppState>().steam.clone();
            let db = app.state::<AppState>().db.clone();
            tauri::async_runtime::spawn(async move {
                if let Ok(games) = db.get_all_games() {
                    if let Err(e) = steam.enrich_missing(&db, &games).await {
                        tracing::warn!("Steam enrich failed: {e}");
                    }
                }
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            app_info,
            get_settings,
            save_settings,
            get_library,
            get_download_tasks,
            refresh_catalog,
            test_share,
            install_game,
            update_game,
            resume_install,
            pause_install,
            cancel_install,
            uninstall_game,
            launch_game,
            verify_install,
            open_install_folder,
            validate_install_folder,
            default_install_folder,
            data_directory,
            open_log_folder,
            log_frontend,
            list_proton_versions,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
