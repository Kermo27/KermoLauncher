export type InstallStatus =
  | "NotInstalled"
  | "Downloading"
  | "Installing"
  | "Installed"
  | "Failed"
  | "Paused";

export type NextcloudConfig = {
  ShareUrl: string;
  ShareToken: string;
  RootFolder: string;
};

export type AppSettings = {
  Nextcloud: NextcloudConfig | null;
  InstallFolder: string;
  MaxParallelDownloads: number;
  AutoUpdate: boolean;
  Theme: string;
  Language: string;
  OnboardingCompleted: boolean;
  LaunchWindowsGamesWithWine: boolean;
  LinuxCompatBackend: string;
  ProtonVersion: string;
  WineCommand: string;
  WinePrefix: string;
};

export type FolderValidation = {
  ok: boolean;
  error: string | null;
  freeBytes: number;
};

export type ShareProbe = {
  gameCount: number;
  rootFolder: string;
};

export type AppInfo = {
  name: string;
  version: string;
};

export type Game = {
  id: string;
  name: string;
  version: string;
  description: string;
  tags: string[];
  dependencies: string[];
  screenshotUrls: string[];
  manifestUrl: string;
  sizeBytes: number;
  launchConfig: {
    executablePath: string;
    workingDirectory: string | null;
    launchArgs: string[] | null;
  } | null;
};

export type GameLocalState = {
  game_id: string;
  status: InstallStatus;
  installed_path: string | null;
  play_time_seconds: number;
  last_played: number | null;
  installed_version: string | null;
};

export type DownloadTask = {
  id: string;
  game_id: string;
  total_bytes: number;
  downloaded_bytes: number;
  status: string;
};

export type LibraryItem = {
  game: Game;
  local: GameLocalState | null;
  coverPath: string | null;
  heroPath: string | null;
  extraTags: string[];
  extraDescription: string | null;
};

export type LaunchResult = {
  success: boolean;
  processId: number | null;
  error: string | null;
};
