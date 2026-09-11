export type ChangelogLang = "en" | "pl";

export type ChangelogRelease = {
  version: string;
  items: Record<ChangelogLang, string[]>;
};

export const CHANGELOG: ChangelogRelease[] = [
  {
    version: "2.0.1",
    items: {
      en: ["Diagnostic log file for support", "Launcher version shown in the UI"],
      pl: ["Logi diagnostyczne do pliku (łatwiejsze zgłaszanie błędów)", "Wersja launchera widoczna w interfejsie"],
    },
  },
  {
    version: "2.0.2",
    items: {
      en: ["Steam covers and screenshots load on Windows", "What's-new notes after an update"],
      pl: ["Okładki i zrzuty Steam ładują się na Windowsie", "Historia zmian po aktualizacji"],
    },
  },
];

export function compareVersion(a: string, b: string) {
  const left = parts(a);
  const right = parts(b);
  const n = Math.max(left.length, right.length);
  for (let i = 0; i < n; i += 1) {
    const diff = (left[i] ?? 0) - (right[i] ?? 0);
    if (diff) return diff;
  }
  return 0;
}

export function unseenChangelog(previous: string, current: string) {
  return CHANGELOG.filter(
    (entry) => compareVersion(entry.version, previous) > 0 && compareVersion(entry.version, current) <= 0,
  ).sort((a, b) => compareVersion(a.version, b.version));
}

function parts(version: string) {
  return version
    .split(/[.+-]/)
    .map((piece) => Number.parseInt(piece, 10))
    .map((n) => (Number.isFinite(n) ? n : 0));
}
