mod catalog;
mod db;
mod download;
mod error;
mod game_paths;
mod install;
mod install_folder;
mod launch;
mod manifest_diff;
mod models;
mod paths;
mod proton;
mod publish;
mod steam;
mod url_sanitizer;
mod webdav;

pub use catalog::{library_items, probe_share, refresh_from_remote};
pub use db::LocalDb;
pub use download::DownloadService;
pub use error::{Error, Result};
pub use install::GameService;
pub use install_folder::{
    default_path as default_install_folder, try_validate as validate_install_folder,
};
pub use launch::{build as build_launch, looks_like_online_fix, prefix_key, LaunchSpec};
pub use manifest_diff::{files_to_download, is_same_file, stale_files};
pub use models::*;
pub use paths::{admin_data_directory, data_directory, data_directory_from, db_path, log_directory};
pub use proton::{
    find_installed as find_proton_installs, resolve as resolve_proton, ProtonInstall,
};
pub use publish::{
    compare_library, guess_dest_folder, load_state, load_state_from, merge_scanned,
    publish_library, remove_orphan, save_state, save_state_to, scan_folder, AdminGame, AdminState,
    CompareResult, GameSyncPlan, PublishProgress, PublishReport, SyncChange, SyncChangeKind,
};
pub use steam::SteamClient;
pub use url_sanitizer::mask_url;
pub use webdav::WebDavClient;

pub fn app_name() -> &'static str {
    "KermoLauncher"
}

pub fn app_version() -> &'static str {
    env!("CARGO_PKG_VERSION")
}

#[cfg(test)]
mod tests {
    #[test]
    fn app_name_is_set() {
        assert_eq!(super::app_name(), "KermoLauncher");
    }
}
