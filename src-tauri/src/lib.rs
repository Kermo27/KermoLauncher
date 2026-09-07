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

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .invoke_handler(tauri::generate_handler![app_info])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
