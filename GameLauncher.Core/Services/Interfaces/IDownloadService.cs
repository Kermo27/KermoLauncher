namespace GameLauncher.Core.Services.Interfaces;

using GameLauncher.Core.Models;

public interface IDownloadService
{
    event Action<DownloadTask>? OnTaskUpdated;
    event Action<DownloadProgress>? OnProgress;
    
    Task<DownloadTask> QueueDownloadAsync(Game game);
    Task PauseAsync(string taskId);
    Task ResumeAsync(string taskId);
    Task CancelAsync(string taskId);
    Task RemoveAsync(string taskId);
    Task UpdateInstallStageAsync(string taskId, InstallStage stage);
    Task<IReadOnlyList<DownloadTask>> GetAllTasksAsync();
    Task<DownloadTask?> GetTaskAsync(string taskId);
}