namespace GameLauncher.Core.Models;

public record Game(
    string Id,
    string Name,
    string Version,
    string Description,
    string[] Tags,
    string[] Dependencies,
    string[] ScreenshotUrls,
    string ManifestUrl,
    long SizeBytes,
    LaunchConfig? LaunchConfig = null
);

public record LaunchConfig(
    string ExecutablePath,
    string? WorkingDirectory = null,
    string[]? LaunchArgs = null
);

public record GameFile(
    string Path,
    long SizeBytes,
    string Sha256
);

public record GameManifest(
    string Version,
    long TotalBytes,
    GameFile[] Files
);

public enum InstallStatus
{
    NotInstalled,
    Downloading,
    Installing,
    Installed,
    Failed,

    /// <summary>Download stopped by the user; partial files are kept for a later resume.</summary>
    Paused
}

public record GameLocalState(
    string GameId,
    InstallStatus Status,
    string? InstalledPath = null,
    long PlayTimeSeconds = 0,
    DateTime? LastPlayed = null,
    string? InstalledVersion = null,
    GameManifest? InstalledManifest = null
);
