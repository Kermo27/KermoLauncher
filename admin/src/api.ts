import { invoke } from "@tauri-apps/api/core";
import type { AdminGame, AdminState } from "./types";

export function loadState() {
  return invoke<AdminState>("load_admin_state");
}

export function saveState(state: AdminState) {
  return invoke<void>("save_admin_state", { state });
}

export function scanFolder(folder: string, previous: AdminGame[]) {
  return invoke<AdminGame[]>("scan_games", { folder, previous });
}

export function writeLibrary(folder: string, games: AdminGame[]) {
  return invoke<void>("write_library", { folder, games });
}
