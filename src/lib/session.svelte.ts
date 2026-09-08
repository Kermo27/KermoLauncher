import { getSettings } from "./api";
import { setLanguage } from "./i18n";
import { applyTheme } from "./theme";
import type { AppSettings } from "./types";

export const session = $state({
  ready: false,
  settings: null as AppSettings | null,
  view: "library" as "library" | "settings",
});

export async function boot() {
  const settings = await getSettings();
  applySession(settings);
  session.ready = true;
}

export function applySession(settings: AppSettings) {
  session.settings = settings;
  setLanguage(settings.Language);
  applyTheme(settings.Theme);
}
