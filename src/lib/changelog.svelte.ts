import { saveSettings } from "./api";
import { unseenChangelog, type ChangelogRelease } from "./changelog";
import { logError } from "./log";
import { applySession, session } from "./session.svelte";
import { checkForAppUpdate } from "./updater";

const FIRST_RELEASE = "2.0.0";

export const changelogDialog = $state({
  open: false,
  fromVersion: "",
  entries: [] as ChangelogRelease[],
});

export async function maybeShowChangelog() {
  const settings = session.settings;
  const current = session.version.trim();
  if (!settings?.OnboardingCompleted || !current) return;

  const stored = settings.LastSeenVersion?.trim() ?? "";
  const previous = stored || FIRST_RELEASE;
  const entries = unseenChangelog(previous, current);
  if (entries.length) {
    changelogDialog.open = true;
    changelogDialog.fromVersion = previous;
    changelogDialog.entries = entries;
    return;
  }
  if (stored !== current) await persistLastSeen(current);
}

export async function dismissChangelog() {
  changelogDialog.open = false;
  changelogDialog.entries = [];
  if (session.version) await persistLastSeen(session.version);
  if (session.settings?.AutoUpdate && session.settings.OnboardingCompleted) {
    void checkForAppUpdate({ silent: true });
  }
}

async function persistLastSeen(version: string) {
  const settings = session.settings;
  if (!settings || settings.LastSeenVersion === version) return;
  const next = { ...settings, LastSeenVersion: version };
  try {
    await saveSettings(next);
    applySession(next);
  } catch (e) {
    logError("failed to save last seen version", e);
  }
}
