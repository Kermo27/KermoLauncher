namespace GameLauncher.Core.Services;

using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

public class DownloadService : IDownloadService, IDisposable
{
    private readonly IWebDavService _webDav;
    private readonly ILocalDbService _db;
    private readonly ILogger<DownloadService> _logger;
    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly Dictionary<string, CancellationTokenSource> _activeDownloads = new();
    private readonly object _activeLock = new();
    private readonly int _maxParallelDownloads;
    private bool _disposed;

    public event Action<DownloadTask>? OnTaskUpdated;
    public event Action<DownloadProgress>? OnProgress;

    public DownloadService(
        IWebDavService webDav,
        ILocalDbService db,
        ILogger<DownloadService> logger,
        int maxParallelDownloads = 2)
    {
        _webDav = webDav;
        _db = db;
        _logger = logger;
        _maxParallelDownloads = maxParallelDownloads;
        _downloadSemaphore = new SemaphoreSlim(maxParallelDownloads, maxParallelDownloads);
    }

    private async Task ProcessDownloadAsync(DownloadTask task)
    {
        await _downloadSemaphore.WaitAsync();
        var cts = new CancellationTokenSource();
        var cancelledByUser = false;
        try
        {
            lock (_activeLock)
            {
                _activeDownloads[task.Id] = cts;
            }

            var updatedTask = task with 
            { 
                Status = DownloadStatus.Downloading,
                InstallStage = InstallStage.Downloading
            };
            await _db.UpsertDownloadTaskAsync(updatedTask);
            OnTaskUpdated?.Invoke(updatedTask);

            var progress = new Progress<DownloadProgress>(p =>
            {
                OnProgress?.Invoke(p);
            });

            await _webDav.DownloadFileAsync(task.RemoteUrl, task.LocalPath, task.Id, progress, cts.Token);

            var completedTask = updatedTask with 
            { 
                Status = DownloadStatus.Completed,
                DownloadedBytes = task.TotalBytes,
                CompletedAt = DateTime.UtcNow,
                InstallStage = InstallStage.Downloading
            };
            await _db.UpsertDownloadTaskAsync(completedTask);
            OnTaskUpdated?.Invoke(completedTask);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            cancelledByUser = true;
            var cancelledTask = task with { Status = DownloadStatus.Cancelled };
            await _db.UpsertDownloadTaskAsync(cancelledTask);
            OnTaskUpdated?.Invoke(cancelledTask);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed for task {TaskId}", task.Id);
            var failedTask = task with { Status = DownloadStatus.Failed, Error = ex.Message };
            await _db.UpsertDownloadTaskAsync(failedTask);
            OnTaskUpdated?.Invoke(failedTask);
        }
        finally
        {
            lock (_activeLock)
            {
                _activeDownloads.Remove(task.Id);
            }
            cts.Dispose();
            _downloadSemaphore.Release();
            if (cancelledByUser)
            {
                _logger.LogInformation("Download {TaskId} cancelled", task.Id);
            }
        }
    }

    private CancellationTokenSource? FindActive(string taskId)
    {
        lock (_activeLock)
        {
            return _activeDownloads.GetValueOrDefault(taskId);
        }
    }

    public Task PauseAsync(string taskId)
    {
        TryCancel(FindActive(taskId));
        return Task.CompletedTask;
    }

    /// <summary>Cancellation can race with the token source being disposed in a finally block.</summary>
    private static void TryCancel(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The download already finished.
        }
    }

    public async Task ResumeAsync(string taskId)
    {
        var task = await _db.GetDownloadTaskAsync(taskId);
        if (task == null) return;

        if (task.Status == DownloadStatus.Paused || task.Status == DownloadStatus.Failed)
        {
            var resumedTask = task with { Status = DownloadStatus.Queued };
            await _db.UpsertDownloadTaskAsync(resumedTask);
            OnTaskUpdated?.Invoke(resumedTask);
            _ = Task.Run(() => ProcessDownloadAsync(resumedTask));
        }
    }

    public async Task CancelAsync(string taskId)
    {
        TryCancel(FindActive(taskId));

        var task = await _db.GetDownloadTaskAsync(taskId);
        if (task != null && task.Status != DownloadStatus.Completed)
        {
            var cancelledTask = task with { Status = DownloadStatus.Cancelled };
            await _db.UpsertDownloadTaskAsync(cancelledTask);
            OnTaskUpdated?.Invoke(cancelledTask);
        }
    }

    public async Task RemoveAsync(string taskId)
    {
        await CancelAsync(taskId);
        await _db.DeleteDownloadTaskAsync(taskId);
    }

    public async Task UpdateInstallStageAsync(string taskId, InstallStage stage)
    {
        var task = await _db.GetDownloadTaskAsync(taskId);
        if (task != null && task.InstallStage != stage)
        {
            var updatedTask = task with { InstallStage = stage };
            await _db.UpsertDownloadTaskAsync(updatedTask);
            OnTaskUpdated?.Invoke(updatedTask);
        }
    }

    public async Task<IReadOnlyList<DownloadTask>> GetAllTasksAsync()
    {
        return await _db.GetAllDownloadTasksAsync();
    }

    public async Task<DownloadTask?> GetTaskAsync(string taskId)
    {
        return await _db.GetDownloadTaskAsync(taskId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancellationTokenSource[] active;
        lock (_activeLock)
        {
            active = _activeDownloads.Values.ToArray();
            _activeDownloads.Clear();
        }
        foreach (var cts in active)
        {
            TryCancel(cts);
        }
        _downloadSemaphore.Dispose();
    }
}