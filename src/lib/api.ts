import { invoke } from "@tauri-apps/api/core";
import type { AppInfo, AppSettings, FolderValidation, ShareProbe, DownloadTask, LaunchResult, LibraryItem } from "./types";

export function appInfo() {
  return invoke<AppInfo>("app_info");
}

export function getSettings() {
  return invoke<AppSettings>("get_settings");
}

export function saveSettings(settings: AppSettings) {
  return invoke<void>("save_settings", { settings });
}

export function defaultInstallFolder() {
  return invoke<string>("default_install_folder");
}

export function validateInstallFolder(path: string) {
  return invoke<FolderValidation>("validate_install_folder", { path });
}

export function testShare(shareUrl: string) {
  return invoke<ShareProbe>("test_share", { shareUrl });
}

export function refreshCatalog() {
  return invoke<number>("refresh_catalog");
}

export function getLibrary() {
  return invoke<LibraryItem[]>("get_library");
}

export function getDownloadTasks() {
  return invoke<DownloadTask[]>("get_download_tasks");
}

export function installGame(gameId: string) {
  return invoke<void>("install_game", { gameId });
}

export function updateGame(gameId: string) {
  return invoke<void>("update_game", { gameId });
}

export function resumeInstall(gameId: string) {
  return invoke<void>("resume_install", { gameId });
}

export function pauseInstall(gameId: string) {
  return invoke<void>("pause_install", { gameId });
}

export function cancelInstall(gameId: string) {
  return invoke<void>("cancel_install", { gameId });
}

export function uninstallGame(gameId: string) {
  return invoke<void>("uninstall_game", { gameId });
}

export function launchGame(gameId: string) {
  return invoke<LaunchResult>("launch_game", { gameId });
}
