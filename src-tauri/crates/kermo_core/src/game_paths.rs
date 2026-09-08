use std::path::{Path, PathBuf};

pub fn combine(root: &Path, relative: &str) -> PathBuf {
    if relative.trim().is_empty() {
        return root.to_path_buf();
    }
    let mut out = root.to_path_buf();
    for part in relative.replace('\\', "/").split('/') {
        let part = part.trim();
        if !part.is_empty() {
            out.push(part);
        }
    }
    out
}

pub fn looks_like_windows_binary(path: &Path) -> bool {
    matches!(
        path.extension()
            .and_then(|e| e.to_str())
            .unwrap_or("")
            .to_ascii_lowercase()
            .as_str(),
        "exe" | "bat" | "cmd" | "msi"
    )
}

pub fn try_make_executable(path: &Path) {
    if cfg!(windows) || !path.is_file() || looks_like_windows_binary(path) {
        return;
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        if let Ok(meta) = std::fs::metadata(path) {
            let mut perms = meta.permissions();
            perms.set_mode(perms.mode() | 0o111);
            let _ = std::fs::set_permissions(path, perms);
        }
    }
}

pub fn sanitize_folder_name(name: &str) -> String {
    name.chars()
        .filter(|c| {
            !c.is_control() && !matches!(c, '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*')
        })
        .collect()
}
