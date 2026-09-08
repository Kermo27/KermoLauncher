use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};

use crate::game_paths;
use crate::models::AppSettings;
use crate::paths::data_directory;
use crate::proton::{
    self, describe_runtime, find_steam_client_root, find_steam_runtime, find_umu_run, resolve,
    ENTRY_POINT_SCRIPT,
};
use crate::{Error, Result};

pub const BACKEND_PROTON: &str = "Proton";
pub const BACKEND_WINE: &str = "Wine";
pub const ONLINE_FIX_DLL_OVERRIDES: &str = "d3d11=n;d3d10=n;d3d10core=n;dxgi=n;openvr_api_dxvk=n;d3d12=n;d3d12core=n;d3d9=n;d3d8=n;onlinefix64=n;steam_api64=n;steamoverlay64=n;winmm=n,b;winhttp=n,b";
pub const ONLINE_FIX_GAME_ID: &str = "480";
pub const ONLINE_FIX_PREFIX_FOLDER_NAME: &str = "OFME Prefix";

#[derive(Debug, Clone)]
pub struct LaunchSpec {
    pub program: PathBuf,
    pub args: Vec<String>,
    pub cwd: PathBuf,
    pub env: HashMap<String, String>,
}

impl LaunchSpec {
    pub fn env_get(&self, key: &str) -> Option<&str> {
        self.env.get(key).map(String::as_str)
    }
}

pub fn build(
    exe_path: &Path,
    work_dir: &Path,
    launch_args: Option<&[String]>,
    settings: &AppSettings,
    home: Option<&Path>,
) -> Result<LaunchSpec> {
    if cfg!(windows) || !game_paths::looks_like_windows_binary(exe_path) {
        let mut spec = LaunchSpec {
            program: exe_path.to_path_buf(),
            args: Vec::new(),
            cwd: work_dir.to_path_buf(),
            env: HashMap::new(),
        };
        append_args(&mut spec, launch_args);
        return Ok(spec);
    }

    if !settings.launch_windows_games_with_wine {
        return Err(Error::message(
            "This game is a Windows executable. Enable Proton/Wine in Settings, or install a Linux build.",
        ));
    }

    if normalize_backend(&settings.linux_compat_backend) == BACKEND_WINE {
        build_wine(exe_path, work_dir, launch_args, settings)
    } else {
        build_proton(exe_path, work_dir, launch_args, settings, home)
    }
}

pub fn looks_like_online_fix(work_dir: &Path, exe_path: &Path) -> bool {
    for dir in candidate_dirs(work_dir, exe_path) {
        let Ok(entries) = std::fs::read_dir(&dir) else {
            continue;
        };
        for entry in entries.flatten() {
            let name = entry.file_name();
            let name = name.to_string_lossy();
            if name.eq_ignore_ascii_case("OnlineFix.ini")
                || name.eq_ignore_ascii_case("SteamOverlay64.dll")
            {
                return true;
            }
            let lower = name.to_ascii_lowercase();
            if lower.starts_with("onlinefix") && (lower.ends_with(".dll") || lower.ends_with(".ini"))
            {
                return true;
            }
        }
    }
    false
}

pub fn normalize_backend(backend: &str) -> &str {
    if backend.eq_ignore_ascii_case(BACKEND_WINE) {
        BACKEND_WINE
    } else {
        BACKEND_PROTON
    }
}

pub fn resolve_proton_prefix(work_dir: &Path, exe_path: &Path, online_fix: bool) -> PathBuf {
    if online_fix {
        if !work_dir.as_os_str().is_empty() {
            let legacy = work_dir.join(ONLINE_FIX_PREFIX_FOLDER_NAME);
            if legacy.join("pfx").is_dir() {
                return legacy;
            }
        }
        let key = prefix_key(work_dir, exe_path);
        return data_directory().join("prefixes").join(key);
    }
    data_directory().join("protonprefix")
}

pub fn prefix_key(work_dir: &Path, exe_path: &Path) -> String {
    if let Some(stem) = exe_path.file_stem().and_then(|s| s.to_str()) {
        let trimmed = stem.trim();
        if !trimmed.is_empty() {
            return trimmed.to_string();
        }
    }
    if let Some(folder) = work_dir.file_name().and_then(|s| s.to_str()) {
        let folder = folder.trim();
        if !folder.is_empty() {
            return folder.replace(' ', "");
        }
    }
    "default".into()
}

pub fn ensure_steam_client_dlls(work_dir: &Path, steam_root: Option<&Path>) {
    if work_dir.as_os_str().is_empty() || !work_dir.is_dir() {
        return;
    }
    let owned = steam_root
        .map(Path::to_path_buf)
        .or_else(|| find_steam_client_root(None));
    let Some(steam_root) = owned else {
        return;
    };
    for name in ["steamclient64.dll", "steamclient.dll"] {
        let dest = work_dir.join(name);
        if dest.exists() {
            continue;
        }
        for src in [steam_root.join("legacycompat").join(name), steam_root.join(name)] {
            if !src.is_file() {
                continue;
            }
            #[cfg(unix)]
            {
                if std::os::unix::fs::symlink(&src, &dest).is_ok() {
                    break;
                }
            }
            if std::fs::copy(&src, &dest).is_ok() {
                break;
            }
        }
    }
}

pub fn is_steam_running() -> bool {
    let output = std::process::Command::new("pidof")
        .arg("steam")
        .output();
    match output {
        Ok(out) => out.status.success() && !out.stdout.is_empty(),
        Err(_) => false,
    }
}

pub fn ensure_steam_app_id_file(work_dir: &Path) {
    if work_dir.as_os_str().is_empty() || !work_dir.is_dir() {
        return;
    }
    let path = work_dir.join("steam_appid.txt");
    let desired = format!("{ONLINE_FIX_GAME_ID}\n");
    if path.is_file() {
        if let Ok(existing) = std::fs::read_to_string(&path) {
            if existing.trim() == ONLINE_FIX_GAME_ID {
                return;
            }
        }
    }
    let _ = std::fs::write(path, desired);
}

pub fn quote(value: &str) -> String {
    if value.is_empty() {
        return "\"\"".into();
    }
    if !value
        .chars()
        .any(|c| c.is_whitespace() || matches!(c, '"' | '\\' | '\''))
    {
        return value.to_string();
    }
    let mut out = String::from("\"");
    for ch in value.chars() {
        if matches!(ch, '"' | '\\') {
            out.push('\\');
        }
        out.push(ch);
    }
    out.push('"');
    out
}

fn build_wine(
    exe_path: &Path,
    work_dir: &Path,
    launch_args: Option<&[String]>,
    settings: &AppSettings,
) -> Result<LaunchSpec> {
    let wine = if settings.wine_command.trim().is_empty() {
        "wine".to_string()
    } else {
        settings.wine_command.trim().to_string()
    };
    let prefix = if settings.wine_prefix.trim().is_empty() {
        data_directory().join("wineprefix")
    } else {
        PathBuf::from(settings.wine_prefix.trim())
    };
    std::fs::create_dir_all(&prefix)?;

    let mut spec = LaunchSpec {
        program: PathBuf::from(wine),
        args: vec![exe_path.to_string_lossy().into_owned()],
        cwd: work_dir.to_path_buf(),
        env: HashMap::new(),
    };
    append_args(&mut spec, launch_args);
    spec.env
        .insert("WINEPREFIX".into(), prefix.to_string_lossy().into_owned());
    apply_online_fix_environment(
        &mut spec,
        work_dir,
        exe_path,
        None,
        Some(looks_like_online_fix(work_dir, exe_path)),
    );
    Ok(spec)
}

fn build_proton(
    exe_path: &Path,
    work_dir: &Path,
    launch_args: Option<&[String]>,
    settings: &AppSettings,
    home: Option<&Path>,
) -> Result<LaunchSpec> {
    let proton = resolve(
        if settings.proton_version.trim().is_empty() {
            None
        } else {
            Some(settings.proton_version.as_str())
        },
        home,
    )
    .ok_or_else(|| {
        Error::message(
            "No Proton install found. Install GE-Proton (Steam → compatibilitytools.d), or switch the Linux backend to Wine in Settings.",
        )
    })?;

    let online_fix = looks_like_online_fix(work_dir, exe_path);
    let steam_root = find_steam_client_root(home);
    let prefix = resolve_proton_prefix(work_dir, exe_path, online_fix);
    std::fs::create_dir_all(&prefix)?;
    let wine_prefix = prefix.join("pfx");
    std::fs::create_dir_all(&wine_prefix)?;

    if online_fix {
        ensure_steam_client_dlls(work_dir, steam_root.as_deref());
        ensure_steam_app_id_file(work_dir);
    }

    let umu = if online_fix { None } else { find_umu_run() };

    let mut spec = if let Some(umu) = umu {
        let mut spec = LaunchSpec {
            program: umu,
            args: vec![exe_path.to_string_lossy().into_owned()],
            cwd: work_dir.to_path_buf(),
            env: HashMap::new(),
        };
        append_args(&mut spec, launch_args);
        spec.env
            .insert("PROTONPATH".into(), proton.directory.clone());
        spec.env.insert(
            "WINEPREFIX".into(),
            wine_prefix.to_string_lossy().into_owned(),
        );
        spec.env.insert(
            "STEAM_COMPAT_DATA_PATH".into(),
            prefix.to_string_lossy().into_owned(),
        );
        spec.env.insert("GAMEID".into(), "0".into());
        spec
    } else {
        let runtime = find_steam_runtime(&proton, home);
        if runtime.is_none() {
            if let Some(runtime_app_id) = proton
                .required_runtime_app_id
                .as_deref()
                .filter(|s| !s.is_empty())
            {
                return Err(Error::message(format!(
                    "{} runs only inside {}, which is not installed. Install that runtime in Steam, or pick a Proton build matching your runtimes in Settings.",
                    proton.name,
                    describe_runtime(runtime_app_id)
                )));
            }
        }

        let mut spec = LaunchSpec {
            program: runtime
                .clone()
                .unwrap_or_else(|| PathBuf::from(&proton.proton_script)),
            args: Vec::new(),
            cwd: work_dir.to_path_buf(),
            env: HashMap::new(),
        };
        if let Some(runtime) = &runtime {
            if runtime.file_name().and_then(|n| n.to_str()) == Some(ENTRY_POINT_SCRIPT) {
                spec.args.push("--verb=run".into());
                spec.args.push("--".into());
            }
            spec.args.push(proton.proton_script.clone());
        }
        spec.args.push("run".into());
        spec.args.push(exe_path.to_string_lossy().into_owned());
        append_args(&mut spec, launch_args);
        spec.env.insert(
            "STEAM_COMPAT_DATA_PATH".into(),
            prefix.to_string_lossy().into_owned(),
        );
        spec.env.insert(
            "WINEPREFIX".into(),
            wine_prefix.to_string_lossy().into_owned(),
        );
        spec
    };

    if let Some(steam_root) = &steam_root {
        let home_dir = home
            .map(Path::to_path_buf)
            .unwrap_or_else(proton::default_home);
        let client_install = home_dir.join(".steam/steam");
        spec.env.insert(
            "STEAM_COMPAT_CLIENT_INSTALL_PATH".into(),
            if client_install.is_dir() {
                client_install.to_string_lossy().into_owned()
            } else {
                steam_root.to_string_lossy().into_owned()
            },
        );
    }

    if online_fix {
        spec.env
            .insert("SteamAppId".into(), ONLINE_FIX_GAME_ID.into());
        spec.env
            .insert("SteamGameId".into(), ONLINE_FIX_GAME_ID.into());
        spec.env
            .insert("SteamOverlayGameId".into(), ONLINE_FIX_GAME_ID.into());
        spec.env
            .insert("SteamAppID".into(), ONLINE_FIX_GAME_ID.into());
    }

    apply_online_fix_environment(
        &mut spec,
        work_dir,
        exe_path,
        steam_root.as_deref(),
        Some(online_fix),
    );
    Ok(spec)
}

fn apply_online_fix_environment(
    spec: &mut LaunchSpec,
    work_dir: &Path,
    exe_path: &Path,
    steam_root: Option<&Path>,
    online_fix: Option<bool>,
) {
    let online_fix = online_fix.unwrap_or_else(|| looks_like_online_fix(work_dir, exe_path));
    if !online_fix {
        return;
    }
    spec.env
        .insert("WINEDLLOVERRIDES".into(), ONLINE_FIX_DLL_OVERRIDES.into());

    let owned = steam_root
        .map(Path::to_path_buf)
        .or_else(|| find_steam_client_root(None));
    let Some(steam_root) = owned else {
        return;
    };
    spec.env
        .insert("ENABLE_VK_LAYER_VALVE_steam_overlay_1".into(), "1".into());
    let overlay64 = steam_root.join("ubuntu12_64/gameoverlayrenderer.so");
    if !overlay64.is_file() {
        return;
    }
    let existing = spec
        .env
        .get("LD_PRELOAD")
        .cloned()
        .or_else(|| std::env::var("LD_PRELOAD").ok());
    let mut parts = vec![overlay64.to_string_lossy().into_owned()];
    if let Some(existing) = existing {
        for part in existing.split(':').filter(|s| !s.is_empty()) {
            if !parts.iter().any(|p| p == part) {
                parts.push(part.to_string());
            }
        }
    }
    spec.env.insert("LD_PRELOAD".into(), parts.join(":"));
}

fn candidate_dirs(work_dir: &Path, exe_path: &Path) -> Vec<PathBuf> {
    let mut seen = HashSet::new();
    let mut out = Vec::new();
    let mut push = |p: PathBuf| {
        let full = std::fs::canonicalize(&p).unwrap_or(p);
        if seen.insert(full.clone()) {
            out.push(full);
        }
    };
    if !work_dir.as_os_str().is_empty() {
        push(work_dir.to_path_buf());
    }
    if let Some(exe_dir) = exe_path.parent() {
        if !exe_dir.as_os_str().is_empty() {
            push(exe_dir.to_path_buf());
        }
    }
    out
}

fn append_args(spec: &mut LaunchSpec, launch_args: Option<&[String]>) {
    if let Some(args) = launch_args {
        spec.args.extend(args.iter().cloned());
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::AppSettings;
    use tempfile::tempdir;

    fn settings_wine(prefix: &Path) -> AppSettings {
        AppSettings {
            launch_windows_games_with_wine: true,
            linux_compat_backend: BACKEND_WINE.into(),
            wine_command: "/usr/bin/wine".into(),
            wine_prefix: prefix.to_string_lossy().into(),
            ..AppSettings::default()
        }
    }

    #[test]
    fn build_uses_wine_when_backend_is_wine() {
        if cfg!(windows) {
            return;
        }
        let tmp = tempdir().unwrap();
        let spec = build(
            Path::new("/games/Demo/game.exe"),
            Path::new("/games/Demo"),
            Some(&["-windowed".into()]),
            &settings_wine(tmp.path()),
            None,
        )
        .unwrap();
        assert_eq!(spec.program, PathBuf::from("/usr/bin/wine"));
        assert_eq!(
            spec.args,
            vec!["/games/Demo/game.exe".to_string(), "-windowed".into()]
        );
        assert_eq!(
            spec.env_get("WINEPREFIX"),
            Some(tmp.path().to_string_lossy().as_ref())
        );
        assert!(spec.env_get("WINEDLLOVERRIDES").is_none());
    }

    #[test]
    fn build_uses_umu_or_proton_when_backend_is_proton() {
        if cfg!(windows) {
            return;
        }
        let tmp = tempdir().unwrap();
        let proton_dir = tmp
            .path()
            .join(".local/share/Steam/compatibilitytools.d/GE-Proton99-launch");
        std::fs::create_dir_all(&proton_dir).unwrap();
        let proton_script = proton_dir.join("proton");
        std::fs::write(&proton_script, "#!/bin/sh\n").unwrap();

        let settings = AppSettings {
            launch_windows_games_with_wine: true,
            linux_compat_backend: BACKEND_PROTON.into(),
            proton_version: "GE-Proton99-launch".into(),
            ..AppSettings::default()
        };
        let spec = build(
            Path::new("/games/Demo/game.exe"),
            Path::new("/games/Demo"),
            Some(&["-novid".into()]),
            &settings,
            Some(tmp.path()),
        )
        .unwrap();
        let expected_prefix =
            resolve_proton_prefix(Path::new("/games/Demo"), Path::new("/games/Demo/game.exe"), false);
        let umu = find_umu_run();
        if let Some(umu) = umu {
            assert_eq!(spec.program, umu);
            assert_eq!(
                spec.args,
                vec!["/games/Demo/game.exe".to_string(), "-novid".into()]
            );
        } else {
            assert_eq!(spec.program, proton_script);
            assert_eq!(
                spec.args,
                vec![
                    "run".to_string(),
                    "/games/Demo/game.exe".into(),
                    "-novid".into()
                ]
            );
        }
        assert_eq!(
            spec.env_get("STEAM_COMPAT_DATA_PATH"),
            Some(expected_prefix.to_string_lossy().as_ref())
        );
        assert!(spec.env_get("WINEDLLOVERRIDES").is_none());
    }

    #[test]
    fn build_applies_online_fix_overrides_when_markers_present() {
        if cfg!(windows) {
            return;
        }
        let home = tempdir().unwrap();
        let game_dir = tempdir().unwrap();
        let proton_dir = home
            .path()
            .join(".local/share/Steam/compatibilitytools.d/GE-Proton99-of");
        let steam_root = home.path().join(".local/share/Steam");
        std::fs::create_dir_all(&proton_dir).unwrap();
        std::fs::create_dir_all(steam_root.join("ubuntu12_64")).unwrap();
        std::fs::create_dir_all(steam_root.join("ubuntu12_32")).unwrap();
        std::fs::write(proton_dir.join("proton"), "#!/bin/sh\n").unwrap();
        std::fs::write(steam_root.join("ubuntu12_64/gameoverlayrenderer.so"), "so64").unwrap();
        std::fs::write(steam_root.join("ubuntu12_32/gameoverlayrenderer.so"), "so32").unwrap();
        std::fs::write(game_dir.path().join("OnlineFix.ini"), "[Main]\n").unwrap();
        std::fs::write(game_dir.path().join("OnlineFix64.dll"), "x").unwrap();
        std::fs::write(game_dir.path().join("game.exe"), "MZ").unwrap();

        let settings = AppSettings {
            launch_windows_games_with_wine: true,
            linux_compat_backend: BACKEND_PROTON.into(),
            proton_version: "GE-Proton99-of".into(),
            ..AppSettings::default()
        };
        let exe = game_dir.path().join("game.exe");
        assert!(looks_like_online_fix(game_dir.path(), &exe));
        let spec = build(&exe, game_dir.path(), None, &settings, Some(home.path())).unwrap();
        assert_eq!(spec.env_get("WINEDLLOVERRIDES"), Some(ONLINE_FIX_DLL_OVERRIDES));
        assert_eq!(spec.env_get("SteamAppId"), Some(ONLINE_FIX_GAME_ID));
        let expected_compat = data_directory().join("prefixes").join("game");
        assert_eq!(
            spec.env_get("STEAM_COMPAT_DATA_PATH"),
            Some(expected_compat.to_string_lossy().as_ref())
        );
        assert_eq!(
            spec.env_get("WINEPREFIX"),
            Some(expected_compat.join("pfx").to_string_lossy().as_ref())
        );
        assert_eq!(
            spec.env_get("ENABLE_VK_LAYER_VALVE_steam_overlay_1"),
            Some("1")
        );
        assert_eq!(
            std::fs::read_to_string(game_dir.path().join("steam_appid.txt"))
                .unwrap()
                .trim(),
            ONLINE_FIX_GAME_ID
        );
        assert_eq!(spec.program, proton_dir.join("proton"));
        let preload = spec.env_get("LD_PRELOAD").unwrap();
        assert!(preload.contains("ubuntu12_64/gameoverlayrenderer.so"));
        assert!(!preload.contains("ubuntu12_32/gameoverlayrenderer.so"));
    }

    #[test]
    fn prefix_key_uses_exe_stem() {
        assert_eq!(
            prefix_key(
                Path::new("/games/Shift At Midnight"),
                Path::new("/games/Shift At Midnight/ShiftAtMidnight.exe")
            ),
            "ShiftAtMidnight"
        );
    }

    #[test]
    fn ensure_steam_client_dlls_symlinks_from_steam_root() {
        if cfg!(windows) {
            return;
        }
        let home = tempdir().unwrap();
        let game_dir = tempdir().unwrap();
        let steam = home.path().join(".local/share/Steam");
        std::fs::create_dir_all(steam.join("legacycompat")).unwrap();
        std::fs::write(steam.join("legacycompat/steamclient64.dll"), "dll").unwrap();
        ensure_steam_client_dlls(game_dir.path(), Some(&steam));
        assert!(game_dir.path().join("steamclient64.dll").exists());
    }

    #[test]
    fn build_non_online_fix_can_still_use_umu() {
        if cfg!(windows) {
            return;
        }
        let Some(_) = find_umu_run() else {
            return;
        };
        let home = tempdir().unwrap();
        let proton_dir = home
            .path()
            .join(".local/share/Steam/compatibilitytools.d/GE-Proton99-umu");
        std::fs::create_dir_all(&proton_dir).unwrap();
        std::fs::write(proton_dir.join("proton"), "#!/bin/sh\n").unwrap();
        let settings = AppSettings {
            launch_windows_games_with_wine: true,
            linux_compat_backend: BACKEND_PROTON.into(),
            proton_version: "GE-Proton99-umu".into(),
            ..AppSettings::default()
        };
        let spec = build(
            Path::new("/games/Demo/game.exe"),
            Path::new("/games/Demo"),
            None,
            &settings,
            Some(home.path()),
        )
        .unwrap();
        let expected_prefix =
            resolve_proton_prefix(Path::new("/games/Demo"), Path::new("/games/Demo/game.exe"), false);
        assert!(spec.program.to_string_lossy().contains("umu-run"));
        assert_eq!(
            spec.env_get("WINEPREFIX"),
            Some(expected_prefix.join("pfx").to_string_lossy().as_ref())
        );
        assert_eq!(
            spec.env_get("STEAM_COMPAT_DATA_PATH"),
            Some(expected_prefix.to_string_lossy().as_ref())
        );
    }

    #[test]
    fn build_runs_native_binary_directly() {
        let settings = AppSettings {
            launch_windows_games_with_wine: true,
            ..AppSettings::default()
        };
        let spec = build(
            Path::new("/games/Demo/game"),
            Path::new("/games/Demo"),
            None,
            &settings,
            None,
        )
        .unwrap();
        assert_eq!(spec.program, PathBuf::from("/games/Demo/game"));
        assert!(spec.args.is_empty());
    }

    #[test]
    fn build_throws_when_compat_disabled_for_windows_exe_on_unix() {
        if cfg!(windows) {
            return;
        }
        let settings = AppSettings {
            launch_windows_games_with_wine: false,
            ..AppSettings::default()
        };
        let err = build(
            Path::new("/games/Demo/game.exe"),
            Path::new("/games/Demo"),
            None,
            &settings,
            None,
        )
        .unwrap_err();
        assert!(err.to_string().contains("Windows executable"));
    }

    #[test]
    fn quote_escapes_as_needed() {
        assert_eq!(quote("has space"), "\"has space\"");
        assert_eq!(quote("quote\"me"), "\"quote\\\"me\"");
    }
}
