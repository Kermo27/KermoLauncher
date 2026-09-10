use std::collections::HashMap;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProtonInstall {
    pub name: String,
    pub directory: String,
    pub proton_script: String,
    pub required_runtime_app_id: Option<String>,
}

pub const ENTRY_POINT_SCRIPT: &str = "_v2-entry-point";

const RUNTIME_DIR_BY_APP_ID: &[(&str, &str)] = &[
    ("1070560", "SteamLinuxRuntime"),
    ("1391110", "SteamLinuxRuntime_soldier"),
    ("1628350", "SteamLinuxRuntime_sniper"),
    ("4183110", "SteamLinuxRuntime_4"),
];

const UMU_RUNTIME_DIR_BY_APP_ID: &[(&str, &str)] =
    &[("1628350", "steamrt3"), ("4183110", "steamrt4")];

const RUNTIME_LABEL_BY_APP_ID: &[(&str, &str)] = &[
    ("1070560", "Steam Linux Runtime 1.0 (scout)"),
    ("1391110", "Steam Linux Runtime 2.0 (soldier)"),
    ("1628350", "Steam Linux Runtime 3.0 (sniper)"),
    ("4183110", "Steam Linux Runtime 4.0"),
];

pub fn describe_runtime(app_id: &str) -> String {
    RUNTIME_LABEL_BY_APP_ID
        .iter()
        .find(|(id, _)| *id == app_id)
        .map(|(_, label)| format!("{label} (appid {app_id})"))
        .unwrap_or_else(|| format!("Steam Linux Runtime with appid {app_id}"))
}

pub fn default_home() -> PathBuf {
    std::env::var("HOME")
        .map(PathBuf::from)
        .ok()
        .or_else(dirs::home_dir)
        .unwrap_or_else(|| PathBuf::from("."))
}

pub fn find_installed(home: Option<&Path>) -> Vec<ProtonInstall> {
    let home = home.map(Path::to_path_buf).unwrap_or_else(default_home);
    let mut found: HashMap<String, ProtonInstall> = HashMap::new();

    for dir in compat_tool_roots(&home) {
        collect_from_directory(&dir, &mut found, None);
    }
    for dir in steam_common_roots(&home) {
        collect_from_directory(&dir, &mut found, Some("Proton"));
    }

    let mut values: Vec<ProtonInstall> = found.into_values().collect();
    values.sort_by(|a, b| {
        rank(b).cmp(&rank(a)).then_with(|| {
            b.name
                .to_ascii_lowercase()
                .cmp(&a.name.to_ascii_lowercase())
        })
    });
    values
}

pub fn resolve(preferred_version: Option<&str>, home: Option<&Path>) -> Option<ProtonInstall> {
    let installed = find_installed(home);
    if installed.is_empty() {
        return None;
    }
    if let Some(pref) = preferred_version.map(str::trim).filter(|s| !s.is_empty()) {
        if let Some(m) = installed.iter().find(|p| p.name.eq_ignore_ascii_case(pref)) {
            return Some(m.clone());
        }
    }
    let mut ordered = installed;
    ordered.sort_by(|a, b| {
        let ua = if has_required_runtime(a, home) { 1 } else { 0 };
        let ub = if has_required_runtime(b, home) { 1 } else { 0 };
        ub.cmp(&ua)
            .then_with(|| rank(b).cmp(&rank(a)))
            .then_with(|| {
                b.name
                    .to_ascii_lowercase()
                    .cmp(&a.name.to_ascii_lowercase())
            })
    });
    ordered.into_iter().next()
}

pub fn has_required_runtime(install: &ProtonInstall, home: Option<&Path>) -> bool {
    match install.required_runtime_app_id.as_deref() {
        Some(id) if !id.is_empty() => find_steam_runtime(install, home).is_some(),
        _ => true,
    }
}

pub fn rank(install: &ProtonInstall) -> i32 {
    let mut score = 0;
    if !install.directory.starts_with("/usr/") {
        score += 1000;
    }
    if install.name.to_ascii_lowercase().starts_with("ge-proton") {
        score += 100;
    } else if install.name.to_ascii_lowercase().starts_with("proton") {
        score += 50;
    }
    if install.name.to_ascii_lowercase().contains("slr") {
        score -= 200;
    }
    score
}

pub fn find_umu_run() -> Option<PathBuf> {
    if let Some(path_env) = std::env::var_os("PATH") {
        for dir in std::env::split_paths(&path_env) {
            let candidate = dir.join("umu-run");
            if candidate.is_file() {
                return Some(candidate);
            }
        }
    }
    for candidate in ["/usr/bin/umu-run", "/usr/local/bin/umu-run"] {
        let p = PathBuf::from(candidate);
        if p.is_file() {
            return Some(p);
        }
    }
    None
}

pub fn find_steam_client_root(home: Option<&Path>) -> Option<PathBuf> {
    let home = home.map(Path::to_path_buf).unwrap_or_else(default_home);
    for candidate in [
        home.join(".local/share/Steam"),
        home.join(".steam/steam"),
        home.join(".steam/root"),
    ] {
        if candidate.is_dir() {
            return Some(candidate);
        }
    }
    None
}

pub fn find_steam_runtime(install: &ProtonInstall, home: Option<&Path>) -> Option<PathBuf> {
    let app_id = install.required_runtime_app_id.as_deref()?;
    if app_id.is_empty() {
        return None;
    }
    find_runtime_for_app_id(app_id, home)
}

pub fn find_runtime_for_app_id(app_id: &str, home: Option<&Path>) -> Option<PathBuf> {
    let home = home.map(Path::to_path_buf).unwrap_or_else(default_home);
    let dir_name = RUNTIME_DIR_BY_APP_ID
        .iter()
        .find(|(id, _)| *id == app_id)
        .map(|(_, n)| *n)?;

    if let Some(steam_root) = find_steam_client_root(Some(&home)) {
        for library_root in enumerate_steam_library_roots(&steam_root, &home) {
            if let Some(entry) =
                runtime_entry_point(&library_root.join("steamapps").join("common").join(dir_name))
            {
                return Some(entry);
            }
        }
    }

    if let Some((_, umu_dir)) = UMU_RUNTIME_DIR_BY_APP_ID
        .iter()
        .find(|(id, _)| *id == app_id)
    {
        return runtime_entry_point(&home.join(".local/share/umu").join(umu_dir));
    }
    None
}

fn runtime_entry_point(runtime_dir: &Path) -> Option<PathBuf> {
    for name in ["run", ENTRY_POINT_SCRIPT] {
        let candidate = runtime_dir.join(name);
        if candidate.is_file() {
            return Some(candidate);
        }
    }
    None
}

fn read_required_runtime_app_id(proton_dir: &Path) -> Option<String> {
    let manifest = proton_dir.join("toolmanifest.vdf");
    let text = std::fs::read_to_string(manifest).ok()?;
    for line in text.lines() {
        if !line.to_ascii_lowercase().contains("require_tool_appid") {
            continue;
        }
        let parts: Vec<&str> = line.split('"').filter(|p| !p.is_empty()).collect();
        if let Some(app_id) = parts
            .iter()
            .rev()
            .find(|p| !p.is_empty() && p.chars().all(|c| c.is_ascii_digit()))
        {
            return Some((*app_id).to_string());
        }
    }
    None
}

fn compat_tool_roots(home: &Path) -> Vec<PathBuf> {
    vec![
        home.join(".local/share/Steam/compatibilitytools.d"),
        home.join(".steam/root/compatibilitytools.d"),
        home.join(".steam/steam/compatibilitytools.d"),
        PathBuf::from("/usr/share/steam/compatibilitytools.d"),
    ]
}

fn steam_common_roots(home: &Path) -> Vec<PathBuf> {
    vec![
        home.join(".local/share/Steam/steamapps/common"),
        home.join(".steam/steam/steamapps/common"),
        home.join(".steam/root/steamapps/common"),
    ]
}

fn enumerate_steam_library_roots(steam_root: &Path, home: &Path) -> Vec<PathBuf> {
    let mut roots = vec![steam_root.to_path_buf()];
    let mut vdf = steam_root.join("steamapps/libraryfolders.vdf");
    if !vdf.is_file() {
        vdf = home.join(".steam/steam/steamapps/libraryfolders.vdf");
    }
    let Ok(text) = std::fs::read_to_string(vdf) else {
        return roots;
    };
    for line in text.lines() {
        let trimmed = line.trim();
        if !trimmed.to_ascii_lowercase().starts_with("\"path\"") {
            continue;
        }
        let parts: Vec<&str> = trimmed.split('"').collect();
        if parts.len() < 4 {
            continue;
        }
        let path = parts[3].replace("\\\\", "\\");
        let p = PathBuf::from(&path);
        if p.is_dir() {
            roots.push(p);
        }
    }
    roots
}

fn collect_from_directory(
    directory: &Path,
    into: &mut HashMap<String, ProtonInstall>,
    name_prefix: Option<&str>,
) {
    let Ok(entries) = std::fs::read_dir(directory) else {
        return;
    };
    for entry in entries.flatten() {
        let dir = entry.path();
        if !dir.is_dir() {
            continue;
        }
        let name = entry.file_name().to_string_lossy().into_owned();
        if let Some(prefix) = name_prefix {
            if !name
                .to_ascii_lowercase()
                .starts_with(&prefix.to_ascii_lowercase())
            {
                continue;
            }
        } else {
            let lower = name.to_ascii_lowercase();
            if !(lower.starts_with("ge-proton")
                || lower.starts_with("proton")
                || lower.contains("proton"))
            {
                continue;
            }
        }
        let script = dir.join("proton");
        if !script.is_file() {
            continue;
        }
        let install = ProtonInstall {
            name: name.clone(),
            directory: dir.to_string_lossy().into_owned(),
            proton_script: script.to_string_lossy().into_owned(),
            required_runtime_app_id: read_required_runtime_app_id(&dir),
        };
        let key = name.to_ascii_lowercase();
        if let Some(existing) = into.get(&key) {
            if rank(existing) >= rank(&install) {
                continue;
            }
        }
        into.insert(key, install);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::tempdir;

    fn write_proton(dir: &Path, name: &str, runtime_app_id: Option<&str>) -> PathBuf {
        let proton_dir = dir.join(name);
        std::fs::create_dir_all(&proton_dir).unwrap();
        std::fs::write(proton_dir.join("proton"), "#!/bin/sh\n").unwrap();
        if let Some(app_id) = runtime_app_id {
            std::fs::write(
                proton_dir.join("toolmanifest.vdf"),
                format!(
                    "\"manifest\"\n{{\n  \"version\" \"2\"\n  \"require_tool_appid\" \"{app_id}\"\n}}\n"
                ),
            )
            .unwrap();
        }
        proton_dir
    }

    fn make_home_with_protons(
        protons: &[(&str, Option<&str>)],
        runtime_dirs: &[&str],
    ) -> tempfile::TempDir {
        let tmp = tempdir().unwrap();
        let home = tmp.path();
        let steam_root = home.join(".local/share/Steam");
        let compat = steam_root.join("compatibilitytools.d");
        std::fs::create_dir_all(&compat).unwrap();
        for (name, runtime) in protons {
            write_proton(&compat, name, *runtime);
        }
        let library = home.join("library");
        for runtime_dir in runtime_dirs {
            let dir = library.join("steamapps/common").join(runtime_dir);
            std::fs::create_dir_all(&dir).unwrap();
            std::fs::write(dir.join("run"), "#!/bin/sh\n").unwrap();
        }
        std::fs::create_dir_all(steam_root.join("steamapps")).unwrap();
        std::fs::write(
            steam_root.join("steamapps/libraryfolders.vdf"),
            format!(
                "\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{}\"\n\t}}\n}}\n",
                library.display()
            ),
        )
        .unwrap();
        tmp
    }

    #[test]
    fn find_installed_sees_proton_script_in_fake_home() {
        let tmp = tempdir().unwrap();
        let proton_dir = tmp
            .path()
            .join(".local/share/Steam/compatibilitytools.d/GE-Proton99-test");
        std::fs::create_dir_all(&proton_dir).unwrap();
        let script = proton_dir.join("proton");
        std::fs::write(&script, "#!/bin/sh\n").unwrap();

        let found = find_installed(Some(tmp.path()));
        let hit = found.iter().find(|p| p.name == "GE-Proton99-test").unwrap();
        assert_eq!(hit.proton_script, script.to_string_lossy());
    }

    #[test]
    fn resolve_prefers_named_version() {
        let tmp = tempdir().unwrap();
        let root = tmp.path().join(".local/share/Steam/compatibilitytools.d");
        for name in ["GE-Proton10-1", "GE-Proton11-2"] {
            write_proton(&root, name, None);
        }
        let resolved = resolve(Some("GE-Proton10-1"), Some(tmp.path())).unwrap();
        assert_eq!(resolved.name, "GE-Proton10-1");
    }

    #[test]
    fn rank_prefers_user_ge_proton_over_usr() {
        let usr = ProtonInstall {
            name: "proton-cachyos-slr".into(),
            directory: "/usr/share/steam/compatibilitytools.d/proton-cachyos-slr".into(),
            proton_script: "/usr/share/steam/compatibilitytools.d/proton-cachyos-slr/proton".into(),
            required_runtime_app_id: None,
        };
        let ge = ProtonInstall {
            name: "GE-Proton11-5".into(),
            directory: "/home/user/.local/share/Steam/compatibilitytools.d/GE-Proton11-5".into(),
            proton_script:
                "/home/user/.local/share/Steam/compatibilitytools.d/GE-Proton11-5/proton".into(),
            required_runtime_app_id: None,
        };
        assert!(rank(&ge) > rank(&usr));
    }

    #[test]
    fn resolve_prefers_user_ge_proton() {
        let tmp = tempdir().unwrap();
        let user_dir = tmp
            .path()
            .join(".local/share/Steam/compatibilitytools.d/GE-Proton11-5");
        std::fs::create_dir_all(&user_dir).unwrap();
        std::fs::write(user_dir.join("proton"), "#!/bin/sh\n").unwrap();
        let resolved = resolve(None, Some(tmp.path())).unwrap();
        assert_eq!(resolved.name, "GE-Proton11-5");
    }

    #[test]
    fn find_steam_runtime_picks_the_runtime_the_build_requires() {
        let tmp = make_home_with_protons(
            &[("GE-Proton11-5", Some("4183110"))],
            &["SteamLinuxRuntime_sniper", "SteamLinuxRuntime_4"],
        );
        let proton = resolve(None, Some(tmp.path())).unwrap();
        assert_eq!(proton.required_runtime_app_id.as_deref(), Some("4183110"));
        let runtime = find_steam_runtime(&proton, Some(tmp.path())).unwrap();
        assert!(runtime
            .to_string_lossy()
            .contains("common/SteamLinuxRuntime_4"));
    }

    #[test]
    fn find_steam_runtime_returns_none_when_required_runtime_is_missing() {
        let tmp = make_home_with_protons(
            &[("GE-Proton11-5", Some("4183110"))],
            &["SteamLinuxRuntime_sniper"],
        );
        let proton = resolve(None, Some(tmp.path())).unwrap();
        assert!(find_steam_runtime(&proton, Some(tmp.path())).is_none());
        assert!(!has_required_runtime(&proton, Some(tmp.path())));
    }

    #[test]
    fn resolve_prefers_build_whose_runtime_is_installed() {
        let tmp = make_home_with_protons(
            &[
                ("GE-Proton10-34", Some("1628350")),
                ("GE-Proton11-5", Some("4183110")),
            ],
            &["SteamLinuxRuntime_sniper"],
        );
        let proton = resolve(None, Some(tmp.path())).unwrap();
        assert_eq!(proton.name, "GE-Proton10-34");
        assert_eq!(
            resolve(Some("GE-Proton11-5"), Some(tmp.path()))
                .unwrap()
                .name,
            "GE-Proton11-5"
        );
    }
}
