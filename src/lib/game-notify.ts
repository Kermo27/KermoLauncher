import { format } from "./format";
import { t } from "./i18n";
import { toast } from "./toasts.svelte";

export async function runGameAction(
  action: "install" | "update" | "pause" | "resume" | "cancel" | "uninstall",
  name: string,
  fn: () => Promise<unknown>,
  extra = "",
) {
  if (action === "install") {
    toast("info", t("Library.InstallStartedTitle"), format(t("Library.InstallStartedMessage"), name));
  } else if (action === "update") {
    toast("info", t("Library.UpdateStartedTitle"), format(t("Library.UpdateStartedMessage"), name));
  } else if (action === "resume") {
    toast("info", t("Library.DownloadResumedTitle"), format(t("Library.DownloadResumedMessage"), name));
  }

  try {
    await fn();
    if (action === "install") {
      toast("success", t("Library.InstallDoneTitle"), format(t("Library.InstallDoneMessage"), name));
    } else if (action === "update") {
      toast("success", t("Library.UpdateDoneTitle"), format(t("Library.UpdateDoneMessage"), name, extra));
    } else if (action === "pause") {
      toast("info", t("Library.DownloadPausedTitle"), format(t("Library.DownloadPausedMessage"), name));
    } else if (action === "cancel") {
      toast("info", t("Library.DownloadCancelledTitle"), format(t("Library.DownloadCancelledMessage"), name));
    } else if (action === "uninstall") {
      toast("success", t("Library.UninstalledTitle"), format(t("Library.UninstalledMessage"), name));
    }
  } catch (e) {
    const msg = String(e);
    if (action === "uninstall") {
      toast("error", t("Library.UninstallErrorTitle"), msg);
    } else if (action === "install" || action === "update" || action === "resume") {
      toast("error", t("Library.InstallErrorTitle"), format(t("Library.InstallErrorMessage"), name, msg));
    } else {
      toast("error", t("Library.InstallErrorTitle"), msg);
    }
  }
}
