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

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::Path;

    #[test]
    fn combine_splits_windows_separators() {
        let root = Path::new("/games");
        assert_eq!(
            combine(root, r"bin\win64\game.exe"),
            Path::new("/games/bin/win64/game.exe")
        );
    }

    #[test]
    fn looks_like_windows_binary_uses_extension() {
        assert!(looks_like_windows_binary(Path::new("game.exe")));
        assert!(looks_like_windows_binary(Path::new("game.bat")));
        assert!(!looks_like_windows_binary(Path::new("game")));
        assert!(!looks_like_windows_binary(Path::new("game.sh")));
    }

    #[test]
    fn try_make_executable_sets_user_execute_on_unix() {
        if cfg!(windows) {
            return;
        }
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("run.sh");
        std::fs::write(&path, "#!/bin/sh\necho hi\n").unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            let mut perms = std::fs::metadata(&path).unwrap().permissions();
            perms.set_mode(0o600);
            std::fs::set_permissions(&path, perms).unwrap();
            try_make_executable(&path);
            let mode = std::fs::metadata(&path).unwrap().permissions().mode();
            assert_ne!(mode & 0o111, 0);
        }
    }

    #[test]
    fn try_make_executable_skips_windows_binaries() {
        if cfg!(windows) {
            return;
        }
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("game.exe");
        std::fs::write(&path, "MZ").unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            let mut perms = std::fs::metadata(&path).unwrap().permissions();
            perms.set_mode(0o600);
            std::fs::set_permissions(&path, perms).unwrap();
            try_make_executable(&path);
            let mode = std::fs::metadata(&path).unwrap().permissions().mode();
            assert_eq!(mode & 0o111, 0);
        }
    }
}
