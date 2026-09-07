use std::path::{Path, PathBuf};

pub fn data_directory() -> PathBuf {
    let base = dirs::data_local_dir().expect("LocalAppData / XDG_DATA_HOME");
    data_directory_from(&base)
}

pub fn data_directory_from(base: &Path) -> PathBuf {
    let new_dir = base.join("KermoLauncher");
    let old_dir = base.join("GameLauncher");

    let _ = migrate_legacy(&new_dir, &old_dir);

    new_dir
}

pub fn db_path() -> PathBuf {
    data_directory().join("launcher.db")
}

fn migrate_legacy(new_dir: &Path, old_dir: &Path) -> std::io::Result<()> {
    std::fs::create_dir_all(new_dir)?;

    if !old_dir.exists() {
        return Ok(());
    }

    if std::fs::read_dir(new_dir)?.next().is_some() {
        return Ok(());
    }

    for entry in std::fs::read_dir(old_dir)? {
        let entry = entry?;
        let dest = new_dir.join(entry.file_name());
        std::fs::rename(entry.path(), dest)?;
    }

    if std::fs::read_dir(old_dir)?.next().is_none() {
        std::fs::remove_dir(old_dir)?;
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    #[test]
    fn migrates_legacy_folder_when_new_is_empty() {
        let tmp = tempfile::tempdir().unwrap();
        let old = tmp.path().join("GameLauncher");
        let new = tmp.path().join("KermoLauncher");
        fs::create_dir_all(&old).unwrap();
        fs::write(old.join("launcher.db"), b"old").unwrap();
        let resolved = data_directory_from(tmp.path());
        assert_eq!(resolved, new);
        assert_eq!(fs::read(new.join("launcher.db")).unwrap(), b"old");
        assert!(!old.exists());
    }
    #[test]
    fn does_not_clobber_existing_new_folder() {
        let tmp = tempfile::tempdir().unwrap();
        let old = tmp.path().join("GameLauncher");
        let new = tmp.path().join("KermoLauncher");
        fs::create_dir_all(&old).unwrap();
        fs::create_dir_all(&new).unwrap();
        fs::write(old.join("old.db"), b"old").unwrap();
        fs::write(new.join("new.db"), b"new").unwrap();
        data_directory_from(tmp.path());
        assert!(new.join("new.db").exists());
        assert!(old.join("old.db").exists());
    }
}
