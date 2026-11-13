const en: Record<string, string> = {
  "Title": "Library publisher",
  "Subtitle": "Scan the Nextcloud games folder. The app writes manifest.json and metadata.json there. Copy the game files yourself.",
  "ScanFolder": "Nextcloud games folder",
  "Browse": "Browse",
  "Scan": "Scan",
  "Games": "{0} games",
  "Name": "Name",
  "Version": "Version",
  "Tags": "Tags",
  "Notes": "Notes",
  "NotesHint": "Mods, Online-Fix, patches — shown in the launcher under the Steam description.",
  "Description": "Description override",
  "DescriptionHint": "Optional. Leave empty and the launcher fills this from Steam.",
  "Launch": "Launch executable",
  "WorkingDir": "Working directory",
  "Args": "Launch args",
  "SteamHint": "Screenshots and covers are fetched from Steam in the launcher. They are not uploaded to Nextcloud.",
  "InSync": "Up to date",
  "Summary": "+{0}  ~{1}  −{2}",
  "Orphans": "Only in the library folder",
  "Remove": "Remove",
  "Busy": "Working…",
  "ScanProgress": "{0} ({1}/{2}) · {3} ({4}/{5})",
  "ScanDone": "Wrote manifest.json and metadata.json for {0} games.",
  "ErrScan": "Scan failed: {0}",
  "ErrWrite": "Could not write the catalog files: {0}",
};

const pl: Record<string, string> = {
  "Title": "Publikacja biblioteki",
  "Subtitle": "Skanuj folder gier na Nextcloudzie. Aplikacja zapisuje tam manifest.json i metadata.json. Pliki gier wrzucasz sam.",
  "ScanFolder": "Folder gier na Nextcloudzie",
  "Browse": "Przeglądaj",
  "Scan": "Skanuj",
  "Games": "{0} gier",
  "Name": "Nazwa",
  "Version": "Wersja",
  "Tags": "Tagi",
  "Notes": "Notatki",
  "NotesHint": "Mody, Online-Fix, patche — w launcherze pod opisem ze Steam.",
  "Description": "Własny opis",
  "DescriptionHint": "Opcjonalnie. Puste pole uzupełni Steam w launcherze.",
  "Launch": "Plik .exe",
  "WorkingDir": "Katalog roboczy",
  "Args": "Argumenty",
  "SteamHint": "Zrzuty i okładki launcher pobiera ze Steam. Nie wrzucamy ich na Nextcloud.",
  "InSync": "Aktualne",
  "Summary": "+{0}  ~{1}  −{2}",
  "Orphans": "Tylko w folderze biblioteki",
  "Remove": "Usuń",
  "Busy": "Praca…",
  "ScanProgress": "{0} ({1}/{2}) · {3} ({4}/{5})",
  "ScanDone": "Zapisano manifest.json i metadata.json dla {0} gier.",
  "ErrScan": "Skan nieudany: {0}",
  "ErrWrite": "Nie udało się zapisać plików katalogu: {0}",
};

const dict = navigator.language.toLowerCase().startsWith("pl") ? pl : en;

export function t(key: string, ...args: Array<string | number>) {
  let s = dict[key] ?? en[key] ?? key;
  args.forEach((a, i) => {
    s = s.replace(`{${i}}`, String(a));
  });
  return s;
}
