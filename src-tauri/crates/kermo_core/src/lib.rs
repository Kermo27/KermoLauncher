mod error;
mod models;
mod paths;
mod url_sanitizer;

pub use error::{Error, Result};
pub use models::*;
pub use paths::{data_directory, data_directory_from, db_path};
pub use url_sanitizer::mask_url;

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
