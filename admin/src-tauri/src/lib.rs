use std::path::PathBuf;

use kermo_core::{
    compare_library, guess_dest_folder, load_state, merge_scanned, publish_library, remove_orphan,
    save_state, scan_folder, AdminGame, AdminState, CompareResult, PublishProgress, PublishReport,
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
fn scan_games(folder: String, previous: Vec<AdminGame>) -> Result<Vec<AdminGame>, String> {
    let scanned = scan_folder(&PathBuf::from(&folder)).map_err(map_err)?;
    Ok(merge_scanned(scanned, &previous))
}

#[tauri::command]
fn compare_games(dest: String, games: Vec<AdminGame>) -> Result<CompareResult, String> {
    compare_library(&PathBuf::from(dest), &games).map_err(map_err)
}

#[tauri::command]
async fn publish_games(
    app: AppHandle,
    dest: String,
    games: Vec<AdminGame>,
    _lock: State<'_, AdminLock>,
) -> Result<PublishOutcome, String> {
    let _guard = _lock.0.lock().await;
    let dest = PathBuf::from(dest);
    let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel::<PublishProgress>();
    let worker = tokio::task::spawn_blocking(move || {
        let mut games = games;
        let report = publish_library(&dest, &mut games, |p| {
            let _ = tx.send(p);
        })?;
        Ok::<_, kermo_core::Error>((report, games))
    });
    while let Some(progress) = rx.recv().await {
        let _ = app.emit("admin-progress", &progress);
    }
    let (report, games) = worker.await.map_err(|e| e.to_string())?.map_err(map_err)?;
    Ok(PublishOutcome { report, games })
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct PublishOutcome {
    report: PublishReport,
    games: Vec<AdminGame>,
}

#[tauri::command]
fn remove_orphan_game(dest: String, folder: String) -> Result<(), String> {
    remove_orphan(&PathBuf::from(dest), &folder).map_err(map_err)
}

#[tauri::command]
fn guess_dest() -> String {
    guess_dest_folder()
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
            compare_games,
            publish_games,
            remove_orphan_game,
            guess_dest,
        ])
        .run(tauri::generate_context!())
        .expect("error while running KermoLauncher Admin");
}
