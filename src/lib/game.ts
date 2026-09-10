import type { DownloadTask, LibraryItem } from "./types";

export function gameFlags(item: LibraryItem) {
  const status = item.local?.status ?? "NotInstalled";
  const busy = status === "Downloading" || status === "Installing";
  return {
      status,
      busy,
      canInstall: status === "NotInstalled" || status === "Failed",
      canLaunch: status === "Installed",
      canUninstall: status === "Installed" || status === "Failed" || status === "Paused",
      canPause: status === "Downloading",
      canResume: status === "Paused",
      canCancel: status === "Downloading" || status === "Paused",
      canVerify: status === "Installed",
      canOpenFolder: status === "Installed" && !!item.local?.installed_path,
      updateAvailable:
        status === "Installed" &&
        !!item.local?.installed_version &&
        item.local.installed_version !== item.game.version,
    };
}

export function taskPct(task: DownloadTask | null) {
  if (!task || task.total_bytes <= 0) return 0;
  return Math.min(100, (100 * task.downloaded_bytes / task.total_bytes));
}
