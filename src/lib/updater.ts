import { relaunch } from "@tauri-apps/plugin-process";
import { check } from "@tauri-apps/plugin-updater";
import { ask } from "./confirm.svelte";
import { format } from "./format";
import { t } from "./i18n";
import { logError } from "./log";
import { toast } from "./toasts.svelte";

export async function checkForAppUpdate(opts: { silent?: boolean } = {}) {
  const silent = opts.silent ?? false;
  try {
    const update = await check();
    if (!update) {
      if (!silent) toast("success", t("Updates.UpToDate"), t("Updates.UpToDateMessage"));
      return;
    }

    toast("info", t("Updates.DownloadingTitle"), t("Updates.DownloadingMessage"));
    await update.downloadAndInstall();

    const restart = await ask(
      t("Updates.ReadyTitle"),
      format(t("Updates.ReadyMessage"), update.version),
      t("Updates.RestartNow"),
      { danger: false, cancelLabel: t("Updates.RestartLater") },
    );
    if (restart) {
      toast("info", t("Updates.InstallingTitle"), t("Updates.InstallingMessage"));
      await relaunch();
    } else {
      toast("info", t("Updates.InstallLaterTitle"), t("Updates.InstallLaterMessage"));
    }
  } catch (e) {
    logError("update check failed", e);
    if (!silent) {
      toast("error", t("Updates.CheckFailed"), format(t("Updates.CheckFailedMessage"), String(e)));
    }
  }
}
