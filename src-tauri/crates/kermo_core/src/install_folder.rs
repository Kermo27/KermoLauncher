use std::path::{Path, PathBuf};

use crate::models::FolderValidation;
use crate::paths::data_directory;

pub fn default_path() -> PathBuf {
    data_directory().join("games")
}

pub fn try_validate(path: &str) -> FolderValidation {
    if path.trim().is_empty() {
        return FolderValidation {
            ok: false,
            error: Some("empty".into()),
            free_bytes: 0,
        };
    }
    let full = match Path::new(path.trim()).canonicalize().or_else(|_| {
        std::fs::create_dir_all(path.trim())?;
        Path::new(path.trim()).canonicalize()
    }) {
        Ok(p) => p,
        Err(_) => {
            if let Err(create_err) = std::fs::create_dir_all(path.trim()) {
                return FolderValidation {
                    ok: false,
                    error: Some(create_err.to_string()),
                    free_bytes: 0,
                };
            }
            PathBuf::from(path.trim())
        }
    };

    if let Err(e) = std::fs::create_dir_all(&full) {
        return FolderValidation {
            ok: false,
            error: Some(e.to_string()),
            free_bytes: 0,
        };
    }

    let probe = full.join(".kermo-write-test");
    if let Err(e) = std::fs::write(&probe, "ok") {
        return FolderValidation {
            ok: false,
            error: Some(e.to_string()),
            free_bytes: 0,
        };
    }
    let _ = std::fs::remove_file(&probe);

    FolderValidation {
        ok: true,
        error: None,
        free_bytes: available_bytes(&full),
    }
}

fn available_bytes(path: &Path) -> u64 {
    #[cfg(unix)]
    {
        use std::ffi::CString;
        let Ok(c) = CString::new(path.to_string_lossy().as_bytes()) else {
            return 0;
        };
        let mut s: libc::statvfs = unsafe { std::mem::zeroed() };
        if unsafe { libc::statvfs(c.as_ptr(), &mut s) } == 0 {
            return s.f_bavail as u64 * s.f_frsize as u64;
        }
        0
    }
    #[cfg(not(unix))]
    {
        let _ = path;
        1
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::tempdir;

    #[test]
    fn try_validate_accepts_writable_directory() {
        let dir = tempdir().unwrap();
        let path = dir.path().join("games");
        let result = try_validate(path.to_str().unwrap());
        assert!(result.ok);
        assert!(result.error.is_none());
        assert!(result.free_bytes > 0);
        assert!(path.is_dir());
    }

    #[test]
    fn try_validate_rejects_empty_path() {
        let result = try_validate("  ");
        assert!(!result.ok);
        assert_eq!(result.error.as_deref(), Some("empty"));
    }
}
