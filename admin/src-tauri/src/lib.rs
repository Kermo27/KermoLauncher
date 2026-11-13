use std::path::PathBuf;
use std::time::{Duration, Instant};

use kermo_core::{
    load_state, merge_scanned, save_state, scan_folder_reporting, write_scan_outputs, AdminGame,
    AdminState, ScanProgress,
};
use tauri::{AppHandle, Emitter, State};
use tokio::sync::Mutex;

struct AdminLock(Mutex<()>);

fn map_err(e: kermo_core::Error) -> String {
    e.to_string()
}

#[tauri::command]
fn load_admin_state() -> Result<AdminState, String> {
    load_state().map_err(map_err)
}

#[tauri::command]
fn save_admin_state(state: AdminState) -> Result<(), String> {
    save_state(&state).map_err(map_err)
}

#[tauri::command]
async fn scan_games(
    app: AppHandle,
    folder: String,
    previous: Vec<AdminGame>,
    lock: State<'_, AdminLock>,
) -> Result<Vec<AdminGame>, String> {
    let _guard = lock.0.lock().await;
    let folder_path = PathBuf::from(&folder);
    let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel::<ScanProgress>();
    let worker = tokio::task::spawn_blocking(move || {
        let scanned = scan_folder_reporting(&folder_path, |progress| {
            let _ = tx.send(progress);
        })
        .map_err(map_err)?;
        let games = merge_scanned(scanned, &previous);
        write_scan_outputs(&games, &folder_path).map_err(map_err)?;
        Ok::<_, String>(games)
    });
    let mut last_emit = Instant::now() - Duration::from_secs(1);
    let mut pending = None;
    while let Some(progress) = rx.recv().await {
        pending = Some(progress);
        if last_emit.elapsed() >= Duration::from_millis(80) {
            if let Some(progress) = pending.take() {
                let _ = app.emit("admin-scan", &progress);
                last_emit = Instant::now();
            }
        }
    }
    if let Some(progress) = pending {
        let _ = app.emit("admin-scan", &progress);
    }
    worker.await.map_err(|e| e.to_string())?
}

#[tauri::command]
async fn write_library(
    folder: String,
    games: Vec<AdminGame>,
    lock: State<'_, AdminLock>,
) -> Result<(), String> {
    let _guard = lock.0.lock().await;
    if folder.trim().is_empty() || games.is_empty() {
        return Ok(());
    }
    tokio::task::spawn_blocking(move || write_scan_outputs(&games, PathBuf::from(folder).as_path()))
        .await
        .map_err(|e| e.to_string())?
        .map_err(map_err)
}

pub fn run() {
    #[cfg(target_os = "linux")]
    {
        unsafe {
            std::env::set_var("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
        }
    }
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .manage(AdminLock(Mutex::new(())))
        .invoke_handler(tauri::generate_handler![
            load_admin_state,
            save_admin_state,
            scan_games,
            write_library,
        ])
        .run(tauri::generate_context!())
        .expect("error while running KermoLauncher Admin");
}
