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
    private readonly int _maxParallelDownloads;

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

    public async Task<DownloadTask> QueueDownloadAsync(Game game)
    {
        var settings = await _db.GetSettingsAsync();
        if (settings.Nextcloud == null)
        {
            throw new InvalidOperationException("Nextcloud nie jest skonfigurowany. Uzupełnij dane w zakładce Ustawienia.");
        }

        var installDir = string.IsNullOrEmpty(settings.InstallFolder)
            ? Path.Combine(Utils.AppPaths.DataDirectory, "games")
            : settings.InstallFolder;
        
        // Download zip directly to install folder (temp name), extraction will create final folder
        var zipPath = Path.Combine(installDir, $"{game.Id}.zip");

        Directory.CreateDirectory(installDir);

        var task = new DownloadTask(
            Id: Guid.NewGuid().ToString(),
            GameId: game.Id,
            RemoteUrl: settings.Nextcloud.GetGameZipUrl(game.RemoteZipUrl),
            LocalPath: zipPath,
            TotalBytes: game.SizeBytes,
            DownloadedBytes: 0,
            Status: DownloadStatus.Queued,
            StartedAt: DateTime.UtcNow
        );

        await _db.UpsertDownloadTaskAsync(task);
        OnTaskUpdated?.Invoke(task);
        
        _ = Task.Run(() => ProcessDownloadAsync(task, game));
        
        return task;
    }

    private async Task ProcessDownloadAsync(DownloadTask task, Game game)
    {
        await _downloadSemaphore.WaitAsync();
        try
        {
            var cts = new CancellationTokenSource();
            _activeDownloads[task.Id] = cts;

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
        catch (OperationCanceledException) when (_activeDownloads.TryGetValue(task.Id, out var cts) && cts.IsCancellationRequested)
        {
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
            _activeDownloads.Remove(task.Id);
            _downloadSemaphore.Release();
        }
    }

    public async Task PauseAsync(string taskId)
    {
        if (_activeDownloads.TryGetValue(taskId, out var cts))
        {
            cts.Cancel();
        }
    }

    public async Task ResumeAsync(string taskId)
    {
        var task = await _db.GetDownloadTaskAsync(taskId);
        if (task == null) return;

        var game = await _db.GetGameAsync(task.GameId);
        if (game == null) return;

        if (task.Status == DownloadStatus.Paused || task.Status == DownloadStatus.Failed)
        {
            var resumedTask = task with { Status = DownloadStatus.Queued };
            await _db.UpsertDownloadTaskAsync(resumedTask);
            OnTaskUpdated?.Invoke(resumedTask);
            _ = Task.Run(() => ProcessDownloadAsync(resumedTask, game));
        }
    }

    public async Task CancelAsync(string taskId)
    {
        if (_activeDownloads.TryGetValue(taskId, out var cts))
        {
            cts.Cancel();
        }
        
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
        foreach (var cts in _activeDownloads.Values)
        {
            cts.Cancel();
        }
        _downloadSemaphore.Dispose();
    }
}