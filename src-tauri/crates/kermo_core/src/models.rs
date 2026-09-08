use percent_encoding::{utf8_percent_encode, AsciiSet, NON_ALPHANUMERIC};
use serde::{Deserialize, Serialize};
use url::Url;

const ESCAPE_DATA: &AsciiSet = &NON_ALPHANUMERIC
    .remove(b'-')
    .remove(b'.')
    .remove(b'_')
    .remove(b'~');

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Game {
    #[serde(alias = "Id")]
    pub id: String,
    #[serde(alias = "Name")]
    pub name: String,
    #[serde(default, alias = "Version")]
    pub version: String,
    #[serde(default, alias = "Description")]
    pub description: String,
    #[serde(default, alias = "Tags")]
    pub tags: Vec<String>,
    #[serde(default, alias = "Dependencies")]
    pub dependencies: Vec<String>,
    #[serde(default, alias = "ScreenshotUrls")]
    pub screenshot_urls: Vec<String>,
    #[serde(default, alias = "ManifestUrl")]
    pub manifest_url: String,
    #[serde(default, alias = "SizeBytes")]
    pub size_bytes: i64,
    #[serde(default, alias = "LaunchConfig")]
    pub launch_config: Option<LaunchConfig>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchConfig {
    #[serde(alias = "ExecutablePath")]
    pub executable_path: String,
    #[serde(default, alias = "WorkingDirectory")]
    pub working_directory: Option<String>,
    #[serde(default, alias = "LaunchArgs")]
    pub launch_args: Option<Vec<String>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameFile {
    #[serde(alias = "Path")]
    pub path: String,
    #[serde(alias = "SizeBytes")]
    pub size_bytes: i64,
    #[serde(alias = "Sha256")]
    pub sha256: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameManifest {
    #[serde(alias = "Version")]
    pub version: String,
    #[serde(alias = "TotalBytes")]
    pub total_bytes: i64,
    #[serde(default, alias = "Files")]
    pub files: Vec<GameFile>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum InstallStatus {
    NotInstalled,
    Downloading,
    Installing,
    Installed,
    Failed,
    Paused,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GameLocalState {
    pub game_id: String,
    pub status: InstallStatus,
    pub installed_path: Option<String>,
    pub play_time_seconds: i64,
    pub last_played: Option<i64>,
    pub installed_version: Option<String>,
    pub installed_manifest: Option<GameManifest>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DownloadStatus {
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum InstallStage {
    Preparing,
    Downloading,
    Verifying,
    Extracting,
    Completed,
    Failed,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DownloadTask {
    pub id: String,
    pub game_id: String,
    pub remote_url: String,
    pub local_path: String,
    pub total_bytes: i64,
    pub downloaded_bytes: i64,
    pub status: DownloadStatus,
    pub error: Option<String>,
    pub started_at: Option<i64>,
    pub completed_at: Option<i64>,
    #[serde(skip, default)]
    pub install_stage: InstallStage,
}

impl Default for InstallStage {
    fn default() -> Self {
        Self::Preparing
    }
}

#[derive(Debug, Clone)]
pub struct DownloadProgress {
    pub task_id: String,
    pub bytes_received: i64,
    pub total_bytes: i64,
    pub speed_bytes_per_second: f64,
    pub estimated_time_remaining_secs: Option<f64>,
}

#[derive(Debug, Clone)]
pub struct DownloadFileRequest {
    pub key: String,
    pub remote_url: String,
    pub local_path: String,
    pub size_bytes: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct AppSettings {
    #[serde(default)]
    pub nextcloud: Option<NextcloudConfig>,
    #[serde(default)]
    pub install_folder: String,
    #[serde(default = "default_parallel")]
    pub max_parallel_downloads: i32,
    #[serde(default = "default_true")]
    pub auto_update: bool,
    #[serde(default = "default_system")]
    pub theme: String,
    #[serde(default = "default_system")]
    pub language: String,
    #[serde(default)]
    pub onboarding_completed: bool,
    #[serde(default = "default_true")]
    pub launch_windows_games_with_wine: bool,
    #[serde(default = "default_proton")]
    pub linux_compat_backend: String,
    #[serde(default)]
    pub proton_version: String,
    #[serde(default = "default_wine")]
    pub wine_command: String,
    #[serde(default)]
    pub wine_prefix: String,
}

impl AppSettings {
    pub fn needs_onboarding(&self) -> bool {
        !self.onboarding_completed
    }
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            nextcloud: None,
            install_folder: String::new(),
            max_parallel_downloads: 2,
            auto_update: true,
            theme: default_system(),
            language: default_system(),
            onboarding_completed: false,
            launch_windows_games_with_wine: true,
            linux_compat_backend: default_proton(),
            proton_version: String::new(),
            wine_command: default_wine(),
            wine_prefix: String::new(),
        }
    }
}

fn default_parallel() -> i32 {
    2
}
fn default_true() -> bool {
    true
}
fn default_system() -> String {
    "System".into()
}
fn default_proton() -> String {
    "Proton".into()
}
fn default_wine() -> String {
    "wine".into()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct NextcloudConfig {
    pub share_url: String,
    #[serde(default)]
    pub share_token: String,
    #[serde(default)]
    pub root_folder: String,
}

impl NextcloudConfig {
    pub fn server_base(&self) -> String {
        match Url::parse(&self.share_url) {
            Ok(uri) => {
                let host = uri.host_str().unwrap_or("");
                match uri.port() {
                    Some(port) => format!("{}://{}:{}", uri.scheme(), host, port),
                    None => format!("{}://{}", uri.scheme(), host),
                }
            }
            Err(_) => self.share_url.trim_end_matches('/').to_string(),
        }
    }

    pub fn dav_token(&self) -> String {
        let token = self.share_token.trim();
        if !token.is_empty() {
            return token.to_string();
        }
        let Ok(uri) = Url::parse(&self.share_url) else {
            return String::new();
        };
        let segments: Vec<&str> = uri.path().split('/').filter(|s| !s.is_empty()).collect();
        for i in 0..segments.len().saturating_sub(1) {
            if segments[i] == "s" {
                return segments[i + 1].to_string();
            }
        }
        String::new()
    }

    pub fn webdav_base(&self) -> String {
        let base = format!(
            "{}/public.php/dav/files/{}",
            self.server_base(),
            self.dav_token()
        );
        if self.root_folder.is_empty() {
            base
        } else {
            format!("{base}/{}", self.root_folder)
        }
    }

    pub fn metadata_url(&self) -> String {
        format!("{}/metadata.json", self.webdav_base())
    }

    pub fn get_file_url(&self, relative_path: &str) -> String {
        format!("{}/{}", self.webdav_base(), escape_path(relative_path))
    }
}

pub fn game_file_url(config: &NextcloudConfig, manifest_url: &str, file_path: &str) -> String {
    let normalized = manifest_url.replace('\\', "/");
    let folder = match normalized.rsplit_once('/') {
        Some((dir, _)) => dir.trim_end_matches('/'),
        None => "",
    };
    let relative = if folder.is_empty() {
        file_path.to_string()
    } else {
        format!("{folder}/{file_path}")
    };
    config.get_file_url(&relative)
}

fn escape_path(path: &str) -> String {
    path.split('/')
        .map(|seg| utf8_percent_encode(seg, ESCAPE_DATA).to_string())
        .collect::<Vec<_>>()
        .join("/")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config(root: &str) -> NextcloudConfig {
        NextcloudConfig {
            share_url: "https://cloud.example.com/s/AbCdEf123".into(),
            share_token: "AbCdEf123".into(),
            root_folder: root.into(),
        }
    }

    #[test]
    fn server_base_keeps_nondefault_port() {
        let c = NextcloudConfig {
            share_url: "https://cloud.example.com:8443/s/token".into(),
            share_token: String::new(),
            root_folder: String::new(),
        };
        assert_eq!(c.server_base(), "https://cloud.example.com:8443");
    }

    #[test]
    fn server_base_omits_default_https_port() {
        let c = NextcloudConfig {
            share_url: "https://cloud.example.com/s/token".into(),
            share_token: String::new(),
            root_folder: String::new(),
        };
        assert_eq!(c.server_base(), "https://cloud.example.com");
    }

    #[test]
    fn dav_token_from_field_or_url() {
        assert_eq!(config("").dav_token(), "AbCdEf123");
        let c = NextcloudConfig {
            share_url: "https://cloud.example.com/s/MyToken123".into(),
            share_token: String::new(),
            root_folder: String::new(),
        };
        assert_eq!(c.dav_token(), "MyToken123");
    }

    #[test]
    fn webdav_and_metadata_urls() {
        assert_eq!(
            config("Games").webdav_base(),
            "https://cloud.example.com/public.php/dav/files/AbCdEf123/Games"
        );
        assert_eq!(
            config("").metadata_url(),
            "https://cloud.example.com/public.php/dav/files/AbCdEf123/metadata.json"
        );
        assert_eq!(
            config("Games").get_file_url("witcher-3/manifest.json"),
            "https://cloud.example.com/public.php/dav/files/AbCdEf123/Games/witcher-3/manifest.json"
        );
        assert!(config("")
            .get_file_url("my game/bin/run game.exe")
            .ends_with("/my%20game/bin/run%20game.exe"));
    }

    #[test]
    fn game_file_url_prepends_manifest_folder() {
        let c = NextcloudConfig {
            share_url: "https://cloud.example.com/s/token".into(),
            share_token: "token".into(),
            root_folder: String::new(),
        };
        assert_eq!(
            game_file_url(&c, "test/manifest.json", "testujemy uwu.txt"),
            "https://cloud.example.com/public.php/dav/files/token/test/testujemy%20uwu.txt"
        );
        assert_eq!(
            game_file_url(&c, "manifest.json", "game.exe"),
            "https://cloud.example.com/public.php/dav/files/token/game.exe"
        );
        assert!(game_file_url(&c, "test\\manifest.json", "a.bin").ends_with("/test/a.bin"));
    }

    #[test]
    fn settings_json_is_pascal_case_like_csharp() {
        let json = serde_json::to_string(&AppSettings::default()).unwrap();
        assert!(json.contains("\"MaxParallelDownloads\":2"));
        assert!(json.contains("\"OnboardingCompleted\":false"));
        assert!(!json.contains("max_parallel_downloads"));
    }

    #[test]
    fn needs_onboarding_follows_completed_flag() {
        let mut s = AppSettings::default();
        assert!(s.needs_onboarding());
        s.onboarding_completed = true;
        s.nextcloud = None;
        assert!(!s.needs_onboarding());
    }

    #[test]
    fn game_deserializes_pascal_case_like_csharp() {
        let g: Game = serde_json::from_str(
            r#"{"Id":"x","Name":"X","ManifestUrl":"x/manifest.json","SizeBytes":1}"#,
        )
        .unwrap();
        assert_eq!(g.id, "x");
        assert_eq!(g.manifest_url, "x/manifest.json");
        assert_eq!(g.size_bytes, 1);
    }
}
