use std::collections::{HashMap, HashSet};
use std::fs::{self, File};
use std::io::Read;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::manifest_diff::is_same_file;
use crate::models::{Game, GameFile, GameManifest, LaunchConfig};
use crate::paths::admin_data_directory;
use crate::{Error, Result};

const SCREENSHOT_DIR: &str = "screenshots";

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AdminGame {
    pub id: String,
    pub name: String,
    pub version: String,
    #[serde(default)]
    pub description: String,
    #[serde(default)]
    pub notes: String,
    #[serde(default)]
    pub tags: Vec<String>,
    #[serde(default)]
    pub dependencies: Vec<String>,
    pub local_folder: String,
    pub remote_folder: String,
    pub manifest_url: String,
    pub size_bytes: i64,
    #[serde(default)]
    pub files: Vec<GameFile>,
    #[serde(default)]
    pub launch_config: Option<LaunchConfig>,
}

impl AdminGame {
    pub fn to_catalog(&self) -> Game {
        Game {
            id: self.id.clone(),
            name: self.name.clone(),
            version: self.version.clone(),
            description: self.description.clone(),
            notes: self.notes.clone(),
            tags: self.tags.clone(),
            dependencies: self.dependencies.clone(),
            screenshot_urls: vec![],
            manifest_url: self.manifest_url.clone(),
            size_bytes: self.size_bytes,
            launch_config: self.launch_config.clone(),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SyncChangeKind {
    Added,
    Changed,
    Removed,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SyncChange {
    pub relative_path: String,
    pub kind: SyncChangeKind,
    pub size_bytes: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameSyncPlan {
    pub game_id: String,
    pub game_name: String,
    pub remote_folder: String,
    pub dest_version: Option<String>,
    pub changes: Vec<SyncChange>,
}

impl GameSyncPlan {
    pub fn is_up_to_date(&self) -> bool {
        self.changes.is_empty()
    }
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AdminState {
    #[serde(default)]
    pub scan_folder: String,
    #[serde(default)]
    pub dest_folder: String,
    #[serde(default)]
    pub games: Vec<AdminGame>,
    #[serde(default)]
    pub selected_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompareResult {
    pub plans: Vec<GameSyncPlan>,
    pub orphans: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PublishReport {
    pub copied: usize,
    pub bumped: Vec<String>,
    pub orphans: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PublishProgress {
    pub game_id: String,
    pub relative_path: String,
    pub completed: u32,
    pub total: u32,
}

pub fn state_path() -> PathBuf {
    admin_data_directory().join("admin-state.json")
}

pub fn load_state() -> Result<AdminState> {
    load_state_from(&state_path())
}

pub fn load_state_from(path: &Path) -> Result<AdminState> {
    if !path.exists() {
        return Ok(AdminState::default());
    }
    let json = fs::read_to_string(path)?;
    Ok(serde_json::from_str(&json)?)
}

pub fn save_state(state: &AdminState) -> Result<()> {
    save_state_to(&state_path(), state)
}

pub fn save_state_to(path: &Path, state: &AdminState) -> Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    fs::write(path, serde_json::to_string_pretty(state)?)?;
    if !state.scan_folder.trim().is_empty() && !state.games.is_empty() {
        write_scan_outputs(&state.games, Path::new(&state.scan_folder))?;
    }
    Ok(())
}

pub fn guess_dest_folder() -> String {
    dirs::home_dir()
        .unwrap_or_default()
        .join("Nextcloud")
        .join("Games")
        .to_string_lossy()
        .into_owned()
}

pub fn scan_folder(folder: &Path) -> Result<Vec<AdminGame>> {
    if !folder.is_dir() {
        return Err(Error::message(format!(
            "Folder not found: {}",
            folder.display()
        )));
    }
    let mut games = Vec::new();
    let mut dirs: Vec<_> = fs::read_dir(folder)?
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .filter(|p| p.is_dir())
        .collect();
    dirs.sort();
    for dir in dirs {
        if let Some(game) = scan_game_directory(&dir)? {
            games.push(game);
        }
    }
    Ok(games)
}

pub fn merge_scanned(scanned: Vec<AdminGame>, previous: &[AdminGame]) -> Vec<AdminGame> {
    let old: HashMap<&str, &AdminGame> = previous.iter().map(|g| (g.id.as_str(), g)).collect();
    scanned
        .into_iter()
        .map(|mut game| {
            if let Some(prev) = old.get(game.id.as_str()) {
                game.name = prev.name.clone();
                game.version = prev.version.clone();
                game.description = prev.description.clone();
                game.notes = prev.notes.clone();
                game.tags = prev.tags.clone();
                game.dependencies = prev.dependencies.clone();
                game.launch_config = match &prev.launch_config {
                    Some(c) if !c.executable_path.trim().is_empty() => prev.launch_config.clone(),
                    _ => game.launch_config,
                };
            }
            game
        })
        .collect()
}

pub fn write_scan_outputs(games: &[AdminGame], scan_folder: &Path) -> Result<()> {
    for game in games {
        write_game_manifest(Path::new(&game.local_folder), game)?;
    }
    let catalog: Vec<Game> = games.iter().map(AdminGame::to_catalog).collect();
    fs::create_dir_all(scan_folder)?;
    fs::write(
        scan_folder.join("metadata.json"),
        serde_json::to_string_pretty(&catalog)?,
    )?;
    Ok(())
}

pub fn compare_library(dest_root: &Path, games: &[AdminGame]) -> Result<CompareResult> {
    let mut plans = Vec::with_capacity(games.len());
    for game in games {
        plans.push(compare_game(game, dest_root)?);
    }
    Ok(CompareResult {
        orphans: list_orphans(dest_root, games),
        plans,
    })
}

pub fn compare_game(game: &AdminGame, dest_root: &Path) -> Result<GameSyncPlan> {
    let dest_dir = dest_root.join(&game.remote_folder);
    let dest_manifest = read_folder_manifest(&dest_dir)?;
    Ok(plan(game, dest_manifest.as_ref()))
}

pub fn publish_library(
    dest_root: &Path,
    games: &mut [AdminGame],
    mut on_progress: impl FnMut(PublishProgress),
) -> Result<PublishReport> {
    fs::create_dir_all(dest_root)?;
    let mut copied = 0;
    let mut bumped = Vec::new();
    for game in games.iter_mut() {
        let plan = compare_game(game, dest_root)?;
        if plan.changes.is_empty() {
            continue;
        }
        if should_bump_patch(&game.version, plan.dest_version.as_deref()) {
            game.version = bump_patch(&game.version);
            bumped.push(game.id.clone());
        }
        copied += apply_plan(dest_root, game, &plan, &mut on_progress)?;
    }
    upsert_catalog(dest_root, games)?;
    Ok(PublishReport {
        copied,
        bumped,
        orphans: list_orphans(dest_root, games),
    })
}

pub fn list_orphans(dest_root: &Path, games: &[AdminGame]) -> Vec<String> {
    let mut known = HashSet::new();
    for game in games {
        known.insert(game.remote_folder.to_ascii_lowercase());
    }
    let mut out = Vec::new();
    let Ok(entries) = fs::read_dir(dest_root) else {
        return out;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_dir() {
            continue;
        }
        let name = entry.file_name().to_string_lossy().into_owned();
        if name.starts_with('.') {
            continue;
        }
        if !known.contains(&name.to_ascii_lowercase()) {
            out.push(name);
        }
    }
    out.sort();
    out
}

pub fn remove_orphan(dest_root: &Path, remote_folder: &str) -> Result<()> {
    let name = Path::new(remote_folder)
        .file_name()
        .ok_or_else(|| Error::message("Invalid folder name"))?;
    if name != Path::new(remote_folder).as_os_str() {
        return Err(Error::message("Invalid folder name"));
    }
    let dir = dest_root.join(name);
    let dest_root = dest_root.canonicalize().unwrap_or_else(|_| dest_root.to_path_buf());
    if dir.exists() {
        let canon = dir.canonicalize()?;
        if !canon.starts_with(&dest_root) {
            return Err(Error::message("Refusing to delete outside the library folder"));
        }
        fs::remove_dir_all(&canon)?;
    }
    prune_catalog(dest_root.as_path(), &name.to_string_lossy())?;
    Ok(())
}

pub fn bump_patch(version: &str) -> String {
    let raw = version.trim();
    if raw.is_empty() {
        return "1.0.1".into();
    }
    let parts: Vec<&str> = raw.split('.').collect();
    if parts.len() >= 3 {
        if let (Ok(major), Ok(minor), Ok(patch)) = (
            parts[0].parse::<u64>(),
            parts[1].parse::<u64>(),
            numeric_prefix(parts[2]).parse::<u64>(),
        ) {
            return format!("{major}.{minor}.{}", patch + 1);
        }
    }
    if parts.len() == 2 {
        if let (Ok(major), Ok(minor)) = (parts[0].parse::<u64>(), parts[1].parse::<u64>()) {
            return format!("{major}.{minor}.1");
        }
    }
    if parts.len() == 1 {
        if let Ok(major) = parts[0].parse::<u64>() {
            return format!("{major}.0.1");
        }
    }
    "1.0.1".into()
}

pub fn should_bump_patch(local_version: &str, dest_version: Option<&str>) -> bool {
    dest_version.is_some_and(|d| local_version.trim().eq_ignore_ascii_case(d.trim()))
}

pub fn guess_launch_config(dir_name: &str, files: &[GameFile]) -> Option<LaunchConfig> {
    let mut exes: Vec<&str> = files
        .iter()
        .map(|f| f.path.as_str())
        .filter(|p| p.rsplit('/').next().unwrap_or(p).to_ascii_lowercase().ends_with(".exe"))
        .collect();
    if exes.is_empty() {
        exes = files
            .iter()
            .map(|f| f.path.as_str())
            .filter(|p| p.to_ascii_lowercase().ends_with(".x86_64"))
            .collect();
    }
    if exes.is_empty() {
        return None;
    }
    let useful: Vec<&str> = exes
        .iter()
        .copied()
        .filter(|p| !is_junk_exe(p.rsplit('/').next().unwrap_or(p)))
        .collect();
    let pool = if useful.is_empty() { exes } else { useful };
    let root: Vec<&str> = pool.iter().copied().filter(|p| !p.contains('/')).collect();
    let pool = if root.is_empty() { pool } else { root };
    let needle = alnum(dir_name);
    let named = pool.iter().copied().find(|p| {
        let stem = Path::new(p)
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or("");
        let hay = alnum(stem);
        !needle.is_empty() && (hay.contains(&needle) || needle.contains(&hay))
    });
    Some(LaunchConfig {
        executable_path: named.unwrap_or(pool[0]).to_string(),
        working_directory: None,
        launch_args: None,
    })
}

fn scan_game_directory(dir: &Path) -> Result<Option<AdminGame>> {
    let dir_name = dir
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("")
        .to_string();
    if dir_name.is_empty() {
        return Ok(None);
    }

    let mut paths = Vec::new();
    collect_files(dir, dir, &mut paths)?;
    paths.sort();
    if paths.is_empty() {
        return Ok(None);
    }

    let manifest_path = dir.join("manifest.json");
    let existing = try_load_manifest(&manifest_path);
    let manifest_mtime = fs::metadata(&manifest_path)
        .ok()
        .and_then(|m| m.modified().ok());

    let mut files = Vec::with_capacity(paths.len());
    let mut total = 0i64;
    for path in &paths {
        let rel = path
            .strip_prefix(dir)
            .unwrap_or(path)
            .to_string_lossy()
            .replace('\\', "/");
        let meta = fs::metadata(path)?;
        let size = meta.len() as i64;
        let sha = reuse_sha(&existing, &rel, size, meta.modified().ok(), manifest_mtime)
            .map(Ok)
            .unwrap_or_else(|| hash_file(path))?;
        files.push(GameFile {
            path: rel,
            size_bytes: size,
            sha256: sha,
        });
        total += size;
    }

    let launch_config = guess_launch_config(&dir_name, &files);
    let id = dir_name.to_ascii_lowercase().replace(' ', "-");
    Ok(Some(AdminGame {
        id,
        name: format_game_name(&dir_name),
        version: existing
            .as_ref()
            .map(|m| m.version.clone())
            .filter(|v| !v.trim().is_empty())
            .unwrap_or_else(|| "1.0.0".into()),
        description: String::new(),
        notes: String::new(),
        tags: vec![],
        dependencies: vec![],
        local_folder: dir.to_string_lossy().into_owned(),
        remote_folder: dir_name.clone(),
        manifest_url: format!("{dir_name}/manifest.json"),
        size_bytes: total,
        files,
        launch_config,
    }))
}

fn reuse_sha(
    existing: &Option<GameManifest>,
    rel: &str,
    size: i64,
    file_mtime: Option<std::time::SystemTime>,
    manifest_mtime: Option<std::time::SystemTime>,
) -> Option<String> {
    let manifest = existing.as_ref()?;
    let prev = manifest
        .files
        .iter()
        .find(|f| f.path.eq_ignore_ascii_case(rel))?;
    if prev.size_bytes != size || prev.sha256.is_empty() {
        return None;
    }
    let file_mtime = file_mtime?;
    let manifest_mtime = manifest_mtime?;
    if file_mtime <= manifest_mtime {
        Some(prev.sha256.clone())
    } else {
        None
    }
}

fn collect_files(dir: &Path, game_dir: &Path, out: &mut Vec<PathBuf>) -> Result<()> {
    for entry in fs::read_dir(dir)? {
        let entry = entry?;
        let path = entry.path();
        if path.is_dir() {
            collect_files(&path, game_dir, out)?;
            continue;
        }
        let rel = path
            .strip_prefix(game_dir)
            .unwrap_or(&path)
            .to_string_lossy()
            .replace('\\', "/");
        if !is_excluded(&rel) {
            out.push(path);
        }
    }
    Ok(())
}

fn is_excluded(rel: &str) -> bool {
    let n = rel.replace('\\', "/");
    n.eq_ignore_ascii_case("manifest.json")
        || n.to_ascii_lowercase()
            .starts_with(&(SCREENSHOT_DIR.to_string() + "/"))
}

fn try_load_manifest(path: &Path) -> Option<GameManifest> {
    let json = fs::read_to_string(path).ok()?;
    let manifest: GameManifest = serde_json::from_str(&json).ok()?;
    if manifest.version.trim().is_empty() {
        return None;
    }
    Some(manifest)
}

fn read_folder_manifest(game_dir: &Path) -> Result<Option<GameManifest>> {
    if let Some(from_file) = try_load_manifest(&game_dir.join("manifest.json")) {
        return Ok(Some(from_file));
    }
    if !game_dir.is_dir() {
        return Ok(None);
    }
    Ok(scan_game_directory(game_dir)?.map(|g| GameManifest {
        version: g.version,
        total_bytes: g.size_bytes,
        files: g.files,
    }))
}

fn plan(game: &AdminGame, dest: Option<&GameManifest>) -> GameSyncPlan {
    let dest_by_path: HashMap<String, &GameFile> = dest
        .map(|m| {
            m.files
                .iter()
                .map(|f| (f.path.to_ascii_lowercase(), f))
                .collect()
        })
        .unwrap_or_default();
    let mut changes = Vec::new();
    let mut local_paths = HashSet::new();
    for local in &game.files {
        local_paths.insert(local.path.to_ascii_lowercase());
        match dest_by_path.get(&local.path.to_ascii_lowercase()) {
            None => changes.push(SyncChange {
                relative_path: local.path.clone(),
                kind: SyncChangeKind::Added,
                size_bytes: local.size_bytes,
            }),
            Some(dest_file) if !is_same_file(local, dest_file) => changes.push(SyncChange {
                relative_path: local.path.clone(),
                kind: SyncChangeKind::Changed,
                size_bytes: local.size_bytes,
            }),
            _ => {}
        }
    }
    if let Some(dest) = dest {
        for file in &dest.files {
            if !local_paths.contains(&file.path.to_ascii_lowercase()) {
                changes.push(SyncChange {
                    relative_path: file.path.clone(),
                    kind: SyncChangeKind::Removed,
                    size_bytes: file.size_bytes,
                });
            }
        }
    }
    GameSyncPlan {
        game_id: game.id.clone(),
        game_name: game.name.clone(),
        remote_folder: game.remote_folder.clone(),
        dest_version: dest.map(|m| m.version.clone()),
        changes,
    }
}

fn apply_plan(
    dest_root: &Path,
    game: &AdminGame,
    plan: &GameSyncPlan,
    on_progress: &mut impl FnMut(PublishProgress),
) -> Result<usize> {
    let dest_game = dest_root.join(&game.remote_folder);
    fs::create_dir_all(&dest_game)?;
    let total = plan.changes.len() as u32 + 1;
    let mut done = 0u32;
    let src_root = Path::new(&game.local_folder);

    for change in &plan.changes {
        match change.kind {
            SyncChangeKind::Added | SyncChangeKind::Changed => {
                let src = src_root.join(change.relative_path.replace('/', std::path::MAIN_SEPARATOR_STR));
                let dest = dest_game.join(change.relative_path.replace('/', std::path::MAIN_SEPARATOR_STR));
                if src.is_file() {
                    if let Some(parent) = dest.parent() {
                        fs::create_dir_all(parent)?;
                    }
                    fs::copy(&src, &dest)?;
                }
            }
            SyncChangeKind::Removed => {
                let dest = dest_game.join(change.relative_path.replace('/', std::path::MAIN_SEPARATOR_STR));
                if dest.is_file() {
                    fs::remove_file(&dest)?;
                    remove_empty_parents(&dest_game, dest.parent());
                }
            }
        }
        done += 1;
        on_progress(PublishProgress {
            game_id: game.id.clone(),
            relative_path: change.relative_path.clone(),
            completed: done,
            total,
        });
    }

    write_game_manifest(&dest_game, game)?;
    done += 1;
    on_progress(PublishProgress {
        game_id: game.id.clone(),
        relative_path: "manifest.json".into(),
        completed: done,
        total,
    });
    Ok(plan.changes.len())
}

fn write_game_manifest(game_dir: &Path, game: &AdminGame) -> Result<()> {
    if !game_dir.is_dir() {
        return Ok(());
    }
    let manifest = GameManifest {
        version: game.version.clone(),
        total_bytes: game.size_bytes,
        files: game.files.clone(),
    };
    fs::write(
        game_dir.join("manifest.json"),
        serde_json::to_string_pretty(&manifest)?,
    )?;
    Ok(())
}

fn upsert_catalog(dest_root: &Path, games: &[AdminGame]) -> Result<()> {
    let path = dest_root.join("metadata.json");
    let mut by_id: HashMap<String, Game> = HashMap::new();
    if path.exists() {
        let existing: Vec<Game> = serde_json::from_str(&fs::read_to_string(&path)?)?;
        for game in existing {
            by_id.insert(game.id.to_ascii_lowercase(), game);
        }
    }
    for game in games {
        by_id.insert(game.id.to_ascii_lowercase(), game.to_catalog());
    }
    write_catalog(&path, by_id.into_values().collect())
}

fn prune_catalog(dest_root: &Path, remote_folder: &str) -> Result<()> {
    let path = dest_root.join("metadata.json");
    if !path.exists() {
        return Ok(());
    }
    let existing: Vec<Game> = serde_json::from_str(&fs::read_to_string(&path)?)?;
    let prefix = format!("{remote_folder}/");
    let keep: Vec<Game> = existing
        .into_iter()
        .filter(|g| {
            let url = g.manifest_url.replace('\\', "/");
            !url.eq_ignore_ascii_case(&format!("{remote_folder}/manifest.json"))
                && !url
                    .to_ascii_lowercase()
                    .starts_with(&prefix.to_ascii_lowercase())
        })
        .collect();
    write_catalog(&path, keep)
}

fn write_catalog(path: &Path, mut games: Vec<Game>) -> Result<()> {
    games.sort_by(|a, b| a.name.to_ascii_lowercase().cmp(&b.name.to_ascii_lowercase()));
    fs::write(path, serde_json::to_string_pretty(&games)?)?;
    Ok(())
}

fn remove_empty_parents(root: &Path, start: Option<&Path>) {
    let Ok(root) = root.canonicalize() else {
        return;
    };
    let mut dir = start.map(Path::to_path_buf);
    while let Some(current) = dir {
        let Ok(canon) = current.canonicalize() else {
            break;
        };
        if canon == root || !canon.starts_with(&root) {
            break;
        }
        let empty = fs::read_dir(&canon)
            .map(|mut d| d.next().is_none())
            .unwrap_or(false);
        if !empty {
            break;
        }
        dir = canon.parent().map(Path::to_path_buf);
        let _ = fs::remove_dir(&canon);
    }
}

fn hash_file(path: &Path) -> Result<String> {
    let mut file = File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buf = [0u8; 81920];
    loop {
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(hex::encode(hasher.finalize()))
}

fn format_game_name(dir_name: &str) -> String {
    let parts: Vec<&str> = dir_name
        .split(['-', '_', ' '])
        .filter(|p| !p.is_empty())
        .collect();
    if parts.is_empty() {
        return dir_name.to_string();
    }
    parts
        .into_iter()
        .map(|w| {
            let mut chars = w.chars();
            match chars.next() {
                Some(c) => format!("{}{}", c.to_uppercase(), chars.as_str()),
                None => String::new(),
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

fn alnum(s: &str) -> String {
    s.chars()
        .filter(|c| c.is_ascii_alphanumeric())
        .map(|c| c.to_ascii_lowercase())
        .collect()
}

fn is_junk_exe(file_name: &str) -> bool {
    let n = file_name.to_ascii_lowercase();
    [
        "unins",
        "crash",
        "easyanticheat",
        "redist",
        "vcredist",
        "dxsetup",
        "unitycrash",
        "crashhandler",
        "installer",
        "setup",
        "dotnet",
        "python",
        "notification_helper",
        "overlay",
    ]
    .iter()
        .any(|k| n.contains(k))
}

fn numeric_prefix(value: &str) -> String {
    value
        .chars()
        .take_while(|c| c.is_ascii_digit())
        .collect::<String>()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;
    use tempfile::tempdir;

    fn write_game(dir: &Path, files: &[(&str, &str)]) {
        for (rel, body) in files {
            let path = dir.join(rel);
            if let Some(parent) = path.parent() {
                fs::create_dir_all(parent).unwrap();
            }
            fs::write(path, body).unwrap();
        }
    }

    #[test]
    fn scan_excludes_manifest_and_screenshots() {
        let root = tempdir().unwrap();
        let game = root.path().join("game-a");
        write_game(
            &game,
            &[
                ("game.exe", "cccc"),
                ("screenshots/shot.png", "img"),
                ("manifest.json", "{}"),
            ],
        );
        let games = scan_folder(root.path()).unwrap();
        let g = games.into_iter().next().unwrap();
        assert_eq!(g.files.len(), 1);
        assert_eq!(g.files[0].path, "game.exe");
        assert!(g.to_catalog().screenshot_urls.is_empty());
        assert_eq!(g.launch_config.as_ref().unwrap().executable_path, "game.exe");
    }

    #[test]
    fn scan_reuses_hash_when_size_and_mtime_match() {
        let root = tempdir().unwrap();
        let game = root.path().join("game-c");
        write_game(&game, &[("game.exe", "eeeeeeeeee"), ("level.dat", "ffff")]);
        let first = scan_folder(root.path()).unwrap().remove(0);
        write_scan_outputs(&[first.clone()], root.path()).unwrap();

        let second = scan_folder(root.path()).unwrap().remove(0);
        assert_eq!(first.files, second.files);
    }

    #[test]
    fn scan_rehashes_same_size_when_mtime_is_newer() {
        let root = tempdir().unwrap();
        let game = root.path().join("game-c");
        let exe = game.join("game.exe");
        write_game(&game, &[("game.exe", "eeeeeeeeee"), ("level.dat", "ffff")]);
        let first = scan_folder(root.path()).unwrap().remove(0);
        write_scan_outputs(&[first.clone()], root.path()).unwrap();
        std::thread::sleep(Duration::from_millis(10));
        fs::write(&exe, "gggggggggg").unwrap();
        let second = scan_folder(root.path()).unwrap().remove(0);
        let old = first
            .files
            .iter()
            .find(|f| f.path == "game.exe")
            .unwrap()
            .sha256
            .clone();
        let new = second
            .files
            .iter()
            .find(|f| f.path == "game.exe")
            .unwrap()
            .sha256
            .clone();
        assert_ne!(old, new);
    }

    #[test]
    fn publish_copies_delta_skips_screenshots_bumps_patch_and_writes_notes() {
        let root = tempdir().unwrap();
        let src_root = root.path().join("src");
        let dest_root = root.path().join("dest");
        let src = src_root.join("Demo");
        let dest = dest_root.join("Demo");
        write_game(
            &src,
            &[
                ("keep.exe", "keep"),
                ("new.dll", "new"),
                ("patched.dat", "v2"),
                ("nested/old.txt", "x"),
                ("screenshots/shot.png", "nope"),
            ],
        );
        write_game(
            &dest,
            &[
                ("keep.exe", "keep"),
                ("patched.dat", "v1"),
                ("gone.pak", "old"),
                ("nested/old.txt", "x"),
            ],
        );

        let mut games = scan_folder(&src_root).unwrap();
        games[0].notes = "Online-Fix".into();
        games[0].version = "1.0.0".into();
        write_scan_outputs(&games, &src_root).unwrap();

        let dest_scan = scan_folder(&dest_root).unwrap();
        write_scan_outputs(&dest_scan, &dest_root).unwrap();

        let report = publish_library(&dest_root, &mut games, |_| {}).unwrap();
        assert_eq!(games[0].version, "1.0.1");
        assert_eq!(report.bumped, vec!["demo"]);
        assert!(dest.join("new.dll").is_file());
        assert_eq!(fs::read_to_string(dest.join("patched.dat")).unwrap(), "v2");
        assert!(!dest.join("gone.pak").exists());
        assert!(!dest.join("screenshots/shot.png").exists());
        let catalog: Vec<Game> =
            serde_json::from_str(&fs::read_to_string(dest_root.join("metadata.json")).unwrap())
                .unwrap();
        assert_eq!(catalog[0].notes, "Online-Fix");
        assert!(catalog[0].screenshot_urls.is_empty());
        assert_eq!(catalog[0].version, "1.0.1");
    }

    #[test]
    fn publish_cleans_empty_dirs_after_delete() {
        let root = tempdir().unwrap();
        let src_root = root.path().join("src");
        let dest_root = root.path().join("dest");
        write_game(&src_root.join("Demo"), &[("keep.exe", "keep")]);
        write_game(
            &dest_root.join("Demo"),
            &[("keep.exe", "keep"), ("gone/nested/old.pak", "x")],
        );
        let mut games = scan_folder(&src_root).unwrap();
        write_scan_outputs(&games, &src_root).unwrap();
        let dest_scan = scan_folder(&dest_root).unwrap();
        write_scan_outputs(&dest_scan, &dest_root).unwrap();
        publish_library(&dest_root, &mut games, |_| {}).unwrap();
        assert!(!dest_root.join("Demo/gone").exists());
    }

    #[test]
    fn orphans_are_listed_and_removed() {
        let root = tempdir().unwrap();
        let dest = root.path().join("dest");
        write_game(&dest.join("Keep"), &[("a.exe", "a")]);
        write_game(&dest.join("Gone"), &[("b.exe", "b")]);
        let keep = scan_folder(&dest).unwrap();
        let keep_only: Vec<_> = keep.into_iter().filter(|g| g.id == "keep").collect();
        upsert_catalog(
            &dest,
            &[AdminGame {
                id: "gone".into(),
                name: "Gone".into(),
                version: "1.0.0".into(),
                description: String::new(),
                notes: String::new(),
                tags: vec![],
                dependencies: vec![],
                local_folder: dest.join("Gone").to_string_lossy().into_owned(),
                remote_folder: "Gone".into(),
                manifest_url: "Gone/manifest.json".into(),
                size_bytes: 1,
                files: vec![],
                launch_config: None,
            }],
        )
        .unwrap();
        upsert_catalog(&dest, &keep_only).unwrap();
        let names = list_orphans(&dest, &keep_only);
        assert_eq!(names, vec!["Gone"]);
        remove_orphan(&dest, "Gone").unwrap();
        assert!(!dest.join("Gone").exists());
        let catalog: Vec<Game> =
            serde_json::from_str(&fs::read_to_string(dest.join("metadata.json")).unwrap()).unwrap();
        assert!(catalog.iter().all(|g| g.id != "gone"));
    }

    #[test]
    fn guess_exe_prefers_root_matching_name() {
        let files = vec![
            GameFile {
                path: "bin/helper.exe".into(),
                size_bytes: 1,
                sha256: "a".into(),
            },
            GameFile {
                path: "unins000.exe".into(),
                size_bytes: 1,
                sha256: "a".into(),
            },
            GameFile {
                path: "My Game.exe".into(),
                size_bytes: 1,
                sha256: "a".into(),
            },
        ];
        let launch = guess_launch_config("My Game", &files).unwrap();
        assert_eq!(launch.executable_path, "My Game.exe");
    }

    #[test]
    fn bump_patch_semver() {
        assert_eq!(bump_patch("1.2.0"), "1.2.1");
        assert_eq!(bump_patch("2.0"), "2.0.1");
        assert_eq!(bump_patch("3"), "3.0.1");
        assert!(should_bump_patch("1.0.0", Some("1.0.0")));
        assert!(!should_bump_patch("2.0.0", Some("1.0.0")));
        assert!(!should_bump_patch("1.0.0", None));
    }

    #[test]
    fn format_name_skips_empty_tokens() {
        assert_eq!(format_game_name("mod--pack"), "Mod Pack");
    }

    #[test]
    fn notes_only_publish_updates_catalog_without_bump() {
        let root = tempdir().unwrap();
        let src_root = root.path().join("src");
        let dest_root = root.path().join("dest");
        write_game(&src_root.join("Demo"), &[("game.exe", "x")]);
        let mut games = scan_folder(&src_root).unwrap();
        publish_library(&dest_root, &mut games, |_| {}).unwrap();
        assert_eq!(games[0].version, "1.0.0");
        games[0].notes = "added a mod".into();
        let report = publish_library(&dest_root, &mut games, |_| {}).unwrap();
        assert!(report.bumped.is_empty());
        let catalog: Vec<Game> =
            serde_json::from_str(&fs::read_to_string(dest_root.join("metadata.json")).unwrap())
                .unwrap();
        assert_eq!(catalog[0].notes, "added a mod");
        assert_eq!(catalog[0].version, "1.0.0");
    }
}
