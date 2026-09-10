# KermoLauncher

<img src="src-tauri/icons/128x128.png" width="72" alt="KermoLauncher">

Personal game launcher. The catalog lives on a **public Nextcloud share**; the app installs, updates, and launches games, and tracks playtime.

**2.0** is Tauri 2 + Svelte. 1.x C# installs do **not** auto-update to 2.0 — install 2.0 separately. Playtime and settings stay: the same `launcher.db` paths as 1.x.

**Download:** [GitHub Releases](https://github.com/Kermo27/KermoLauncher/releases)

A second app, **KermoLauncher Admin**, publishes games into the synced Nextcloud folder (SHA-256 delta copy). Screenshots and covers are not uploaded; the launcher fetches them from Steam.

## Features

- Game library from a public Nextcloud link (`metadata.json`)
- Cover grid, search, tags, and status filters (installed / in progress / failed / updates)
- Game details: hero, Steam gallery, notes, verify files, open install folder
- Install with SHA-256 verification, per-file delta updates, parallel downloads, pause and resume
- Playtime tracking and last-launched date
- Windows games on Linux through Proton (Wine as a fallback), including Online-Fix
- Themes: light, dark, system — UI in Polish or English
- Launcher self-update from GitHub Releases (2.x → 2.x)
- Admin: scan a test folder, edit notes/version/launch exe, copy only changed files, remove leftover games, bump patch version when files change

## Project structure

```
KermoLauncher/
├── src/                 # launcher UI (SvelteKit)
├── src-tauri/           # launcher shell + kermo_core
├── admin/               # Admin Tool UI (Svelte + Vite)
└── admin/src-tauri/     # Admin Tool shell
```

## Requirements

- Node.js (LTS) and Rust (stable)
- Windows 10/11 (WebView2) or Linux x64 (WebKitGTK 4.1)

On Debian/Ubuntu-like systems:

```bash
sudo apt install libwebkit2gtk-4.1-dev libappindicator3-dev librsvg2-dev patchelf
```

## Building

```bash
npm install
npm run tauri dev
```

Admin Tool (separate app):

```bash
npm run tauri:admin
```

Release installers (NSIS on Windows, AppImage on Linux):

```bash
npm run tauri build
npm run tauri:admin:build
```

Updater artifacts are signed. For a local launcher release build set `TAURI_SIGNING_PRIVATE_KEY` to the path or contents of your private key (optional `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`). CI uses the same variables as GitHub Actions secrets.

Typecheck and tests:

```bash
npm run check
cargo test -p kermo_core --manifest-path src-tauri/Cargo.toml
```

## Releases

Push a `v2.*` tag (or run **Tauri Release**). That builds the launcher:

- Windows NSIS installer
- Linux AppImage
- `latest.json` for in-app updates (copied to the `tauri-latest` GitHub release)

Admin installers are built locally with `npm run tauri:admin:build`. They are not part of the launcher updater.

2.x GitHub releases are **prereleases** so GitHub “Latest” stays on 1.x. 2.0 clients read `tauri-latest`, not Latest.

### Linux

Download the AppImage, `chmod +x`, run it.

Windows games launch through **Proton** by default (Settings → Windows games): `umu-run` when available, otherwise Steam Runtime + `proton run` (runtime taken from Proton’s `toolmanifest.vdf`). Install GE-Proton under Steam’s `compatibilitytools.d`. Wine remains a selectable fallback. When `OnlineFix.ini` / `OnlineFix*.dll` is present, the launcher sets the usual `WINEDLLOVERRIDES` and SpaceWar (`480`) game id automatically and skips umu. Steam should be running for online-fix multiplayer. `protontricks` remains for prefix tooling (VC++, .NET), not launch.

## Nextcloud library layout

```
<shared folder>/
├── metadata.json
└── <Game Name>/
    ├── game files...
    └── manifest.json
```

Each game is a subfolder of raw game files. Admin generates a per-game `manifest.json` (paths, sizes, SHA-256, version) and a catalog `metadata.json`. `screenshots/` and `manifest.json` are excluded from the install. Covers and the gallery come from Steam in the launcher.

`metadata.json`:

```json
[
  {
    "id": "shift-at-midnight",
    "name": "Shift At Midnight",
    "version": "1.1.0",
    "description": "",
    "notes": "Online-Fix, 4 GB patch",
    "tags": ["adventure", "pixel art"],
    "dependencies": [],
    "screenshotUrls": [],
    "manifestUrl": "Shift At Midnight/manifest.json",
    "sizeBytes": 123456789,
    "launchConfig": {
      "executablePath": "game",
      "workingDirectory": null,
      "launchArgs": null
    }
  }
]
```

Per-game `manifest.json`:

```json
{
  "version": "1.1.0",
  "totalBytes": 123456789,
  "files": [
    { "path": "game", "sizeBytes": 123456000, "sha256": "..." },
    { "path": "data/level.dat", "sizeBytes": 789, "sha256": "..." }
  ]
}
```

The launcher downloads only files that changed since the last installed manifest and verifies each SHA-256. Admin skips copying files that already match in the destination folder.

## Setup from a share link

1. In **KermoLauncher Admin**, scan the folder where you test games (not the Nextcloud copy). Edit notes, version, and launch exe as needed.
2. Pick the synced library folder (usually `~/Nextcloud/Games`), **Compare**, then **Publish**. Nextcloud desktop uploads the delta; keep an existing **public link** on that folder.
3. In the launcher: **Settings → Game source (Nextcloud)** — paste the share link and save.
4. Go back to **Library** and click **Refresh**.

## App data

Launcher settings and install state are in SQLite:

- Windows: `%LOCALAPPDATA%\KermoLauncher\launcher.db` (migrated from the old `GameLauncher` folder if that is all you had)
- Linux: `~/.local/share/KermoLauncher/launcher.db`

Admin state (scan/dest folders and edited metadata):

- Windows: `%LOCALAPPDATA%\KermoLauncherAdmin\admin-state.json`
- Linux: `~/.local/share/KermoLauncherAdmin/admin-state.json`

The Nextcloud share link lives only in `launcher.db` — never in the repo or a build.

## License

This project is released into the public domain under the [Unlicense](LICENSE).
