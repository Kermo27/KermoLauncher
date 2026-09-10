import { invoke } from "@tauri-apps/api/core";
import type { AdminGame, AdminState, CompareResult, PublishReport } from "./types";

export function loadState() {
  return invoke<AdminState>("load_admin_state");
}

export function saveState(state: AdminState) {
  return invoke<void>("save_admin_state", { state });
}

export function scanFolder(folder: string, previous: AdminGame[]) {
  return invoke<AdminGame[]>("scan_games", { folder, previous });
}

export function compareLibrary(dest: string, games: AdminGame[]) {
  return invoke<CompareResult>("compare_games", { dest, games });
}

export function publishLibrary(dest: string, games: AdminGame[]) {
  return invoke<{ report: PublishReport; games: AdminGame[] }>("publish_games", {
    dest,
    games,
  });
}

export function removeOrphan(dest: string, folder: string) {
  return invoke<void>("remove_orphan_game", { dest, folder });
}

export function guessDestFolder() {
  return invoke<string>("guess_dest");
}
