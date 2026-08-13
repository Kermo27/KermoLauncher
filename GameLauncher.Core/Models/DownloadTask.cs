namespace GameLauncher.Core.Models;

public record DownloadTask(
    string Id,
    string GameId,
    string RemoteUrl,
    string LocalPath,
    long TotalBytes,
    long DownloadedBytes,
    DownloadStatus Status,
    string? Error = null,
    DateTime? StartedAt = null,
    DateTime? CompletedAt = null,
    InstallStage InstallStage = InstallStage.Preparing
);

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public record DownloadProgress(
    string TaskId,
    long BytesReceived,
    long TotalBytes,
    double SpeedBytesPerSecond,
    TimeSpan? EstimatedTimeRemaining
);

/// <summary>One file to transfer. Key identifies it in progress bookkeeping.</summary>
public record DownloadFileRequest(
    string Key,
    string RemoteUrl,
    string LocalPath,
    long SizeBytes
);