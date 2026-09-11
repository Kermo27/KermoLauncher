import { appInfo, getSettings } from "./api";
import { setLanguage } from "./i18n";
import { applyTheme } from "./theme";
import type { AppSettings } from "./types";

export const session = $state({
  ready: false,
  settings: null as AppSettings | null,
  version: "",
  view: "library" as "library" | "settings",
  gameId: null as string | null,
});

export async function boot() {
  const [settings, info] = await Promise.all([getSettings(), appInfo().catch(() => null)]);
  applySession(settings);
  if (info?.version) session.version = info.version;
  session.ready = true;
}

export function applySession(settings: AppSettings) {
  session.settings = settings;
  setLanguage(settings.Language);
  applyTheme(settings.Theme);
}
