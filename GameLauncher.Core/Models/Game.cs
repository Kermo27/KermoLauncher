namespace GameLauncher.Core.Models;

public record Game(
    string Id,
    string Name,
    string Version,
    string Description,
    string[] Tags,
    string[] Dependencies,
    string[] ScreenshotUrls,
    string RemoteZipUrl,
    long SizeBytes,
    string Sha256,
    LaunchConfig? LaunchConfig = null
);

public record LaunchConfig(
    string ExecutablePath,
    string? WorkingDirectory = null,
    string[]? LaunchArgs = null
);

public enum InstallStatus
{
    NotInstalled,
    Downloading,
    Installing,
    Installed,
    Failed
}

public record GameLocalState(
    string GameId,
    InstallStatus Status,
    string? InstalledPath = null,
    long PlayTimeSeconds = 0,
    DateTime? LastPlayed = null
);