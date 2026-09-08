use std::collections::HashSet;

use crate::models::{GameFile, GameManifest};

pub fn files_to_download(remote: &GameManifest, installed: Option<&GameManifest>) -> Vec<GameFile> {
    let Some(installed) = installed else {
        return remote.files.clone();
    };

    remote
        .files
        .iter()
        .filter(|f| !installed.files.iter().any(|i| is_same_file(i, f)))
        .cloned()
        .collect()
}

pub fn stale_files(remote: &GameManifest, installed: &GameManifest) -> Vec<GameFile> {
    let remote_paths: HashSet<String> = remote
        .files
        .iter()
        .map(|f| f.path.to_ascii_lowercase())
        .collect();

    installed
        .files
        .iter()
        .filter(|f| !remote_paths.contains(&f.path.to_ascii_lowercase()))
        .cloned()
        .collect()
}

pub fn is_same_file(a: &GameFile, b: &GameFile) -> bool {
    a.path.eq_ignore_ascii_case(&b.path)
        && a.size_bytes == b.size_bytes
        && a.sha256.eq_ignore_ascii_case(&b.sha256)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn file(path: &str, size: i64, sha: &str) -> GameFile {
        GameFile {
            path: path.into(),
            size_bytes: size,
            sha256: sha.into(),
        }
    }

    fn manifest(files: Vec<GameFile>) -> GameManifest {
        let total: i64 = files.iter().map(|f| f.size_bytes).sum();
        GameManifest {
            version: "1.0.0".into(),
            total_bytes: total,
            files,
        }
    }

    #[test]
    fn no_installed_manifest_downloads_everything() {
        let remote = manifest(vec![file("a.exe", 100, "abc"), file("b.exe", 100, "abc")]);
        assert_eq!(files_to_download(&remote, None).len(), 2);
    }

    #[test]
    fn matching_install_downloads_nothing() {
        let remote = manifest(vec![file("a.exe", 100, "abc"), file("b.exe", 100, "abc")]);
        let installed = manifest(vec![file("a.exe", 100, "abc"), file("b.exe", 100, "abc")]);
        assert!(files_to_download(&remote, Some(&installed)).is_empty());
    }

    #[test]
    fn path_case_is_ignored() {
        let remote = manifest(vec![file("Game/EXE.exe", 100, "abc")]);
        let installed = manifest(vec![file("game/exe.exe", 100, "abc")]);
        assert!(files_to_download(&remote, Some(&installed)).is_empty());
    }

    #[test]
    fn size_or_hash_change_means_redownload() {
        let installed = manifest(vec![file("a.exe", 100, "abc")]);
        let same = manifest(vec![file("a.exe", 100, "abc")]);
        let size = manifest(vec![file("a.exe", 101, "abc")]);
        let hash = manifest(vec![file("a.exe", 100, "abd")]);
        assert!(files_to_download(&same, Some(&installed)).is_empty());
        assert_eq!(files_to_download(&size, Some(&installed)).len(), 1);
        assert_eq!(files_to_download(&hash, Some(&installed)).len(), 1);
    }

    #[test]
    fn stale_files_are_those_missing_from_remote() {
        let remote = manifest(vec![file("a.exe", 100, "abc")]);
        let installed = manifest(vec![file("a.exe", 100, "abc"), file("old.bin", 100, "abc")]);
        let stale = stale_files(&remote, &installed);
        assert_eq!(stale.len(), 1);
        assert_eq!(stale[0].path, "old.bin");
    }

    #[test]
    fn is_same_file_compares_path_size_hash() {
        assert!(is_same_file(
            &file("a.exe", 100, "abc"),
            &file("A.EXE", 100, "abc")
        ));
        assert!(!is_same_file(
            &file("a.exe", 100, "abc"),
            &file("a.exe", 99, "abc")
        ));
        assert!(!is_same_file(
            &file("a.exe", 100, "abc"),
            &file("a.exe", 100, "xyz")
        ));
    }
}
