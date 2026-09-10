export type LaunchConfig = {
  executablePath: string;
  workingDirectory: string | null;
  launchArgs: string[] | null;
};

export type GameFile = {
  path: string;
  sizeBytes: number;
  sha256: string;
};

export type AdminGame = {
  id: string;
  name: string;
  version: string;
  description: string;
  notes: string;
  tags: string[];
  dependencies: string[];
  localFolder: string;
  remoteFolder: string;
  manifestUrl: string;
  sizeBytes: number;
  files: GameFile[];
  launchConfig: LaunchConfig | null;
};

export type SyncChange = {
  relativePath: string;
  kind: "added" | "changed" | "removed";
  sizeBytes: number;
};

export type GameSyncPlan = {
  gameId: string;
  gameName: string;
  remoteFolder: string;
  destVersion: string | null;
  changes: SyncChange[];
};

export type CompareResult = {
  plans: GameSyncPlan[];
  orphans: string[];
};

export type PublishReport = {
  copied: number;
  bumped: string[];
  orphans: string[];
};

export type PublishProgress = {
  gameId: string;
  relativePath: string;
  completed: number;
  total: number;
};

export type AdminState = {
  scanFolder: string;
  destFolder: string;
  games: AdminGame[];
  selectedId: string | null;
};
