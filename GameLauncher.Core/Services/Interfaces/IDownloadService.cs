namespace GameLauncher.Core.Services.Interfaces;

using GameLauncher.Core.Models;

/// <summary>
/// File transfers for one download task. The install pipeline (IGameService) owns task status
/// transitions and cancellation; this service only moves bytes and reports progress.
/// </summary>
public interface IDownloadService
{
    event Action<DownloadTask>? OnTaskUpdated;
    event Action<DownloadProgress>? OnProgress;

    Task DownloadFilesAsync(
        DownloadTask task,
        IReadOnlyList<DownloadFileRequest> files,
        CancellationToken ct = default);

    Task UpdateInstallStageAsync(string taskId, InstallStage stage);
    Task<IReadOnlyList<DownloadTask>> GetAllTasksAsync();
    Task<DownloadTask?> GetTaskAsync(string taskId);
}
