namespace GameLauncher.Core.Models;

public record InstallProgress(
    string GameId,
    InstallStage Stage,
    double ProgressPercent,
    string? CurrentFile = null,
    long BytesProcessed = 0,
    long TotalBytes = 0
);

public enum InstallStage
{
    Preparing,
    Downloading,
    Verifying,
    Extracting,
    Completed,
    Failed
}

public record LaunchResult(
    bool Success,
    int? ProcessId = null,
    string? Error = null
);