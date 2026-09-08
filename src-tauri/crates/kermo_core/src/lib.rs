mod catalog;
mod db;
mod download;
mod error;
mod game_paths;
mod install;
mod manifest_diff;
mod models;
mod paths;
mod url_sanitizer;
mod webdav;

pub use catalog::refresh_from_remote;
pub use db::LocalDb;
pub use download::DownloadService;
pub use error::{Error, Result};
pub use install::GameService;
pub use manifest_diff::{files_to_download, is_same_file, stale_files};
pub use models::*;
pub use paths::{data_directory, data_directory_from, db_path};
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
