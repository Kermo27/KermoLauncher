namespace GameLauncher.Core.Services;

using System.Diagnostics;
using System.Security.Cryptography;
using Models;
using Interfaces;
using Utils;
using Microsoft.Extensions.Logging;

public class GameService : IGameService
{
    private readonly IDownloadService _downloadService;
    private readonly ILocalDbService _db;
    private readonly IWebDavService _webDav;
    private readonly ILogger<GameService> _logger;

    public event Action<GameLocalState>? OnGameStateChanged;
    public event Action<DownloadTask>? OnTaskUpdated;
    public event Action<DownloadProgress>? OnProgress;

    public GameService(
        IDownloadService downloadService,
        ILocalDbService db,
        IWebDavService webDav,
        ILogger<GameService> logger)
    {
        _downloadService = downloadService;
        _db = db;
        _webDav = webDav;
        _logger = logger;
    }

    public Task InstallAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
        => InstallOrUpdateAsync(game, isUpdate: false, progress, ct);

    public Task UpdateAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
        => InstallOrUpdateAsync(game, isUpdate: true, progress, ct);

    private async Task InstallOrUpdateAsync(Game game, bool isUpdate, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var localState = await _db.GetLocalStateAsync(game.Id) ?? new GameLocalState(game.Id, InstallStatus.NotInstalled);
        var settings = await _db.GetSettingsAsync();
        if (settings.Nextcloud == null)
        {
            throw new InvalidOperationException("Nextcloud is not configured");
        }
        var config = settings.Nextcloud;

        var installRoot = string.IsNullOrWhiteSpace(settings.InstallFolder)
            ? Path.Combine(Utils.AppPaths.DataDirectory, "games")
            : settings.InstallFolder;

        var taskId = Guid.NewGuid().ToString();
        var downloadTask = new DownloadTask(
            Id: taskId,
            GameId: game.Id,
            RemoteUrl: config.GetFileUrl(game.ManifestUrl),
            LocalPath: Path.Combine(installRoot, ".update", game.Id),
            TotalBytes: 0,
            DownloadedBytes: 0,
            Status: DownloadStatus.Queued,
            StartedAt: DateTime.UtcNow
        );
        await _db.UpsertDownloadTaskAsync(downloadTask);
        OnTaskUpdated?.Invoke(downloadTask);

        var stagingDir = downloadTask.LocalPath;
        var safeName = string.Join("", game.Name.Split(Path.GetInvalidFileNameChars()));
        var finalDir = Path.Combine(installRoot, safeName);
        var installedManifest = localState.Status == InstallStatus.Installed ? localState.InstalledManifest : null;

        try
        {
            localState = localState with { Status = InstallStatus.Downloading };
            await _db.UpsertLocalStateAsync(localState);
            OnGameStateChanged?.Invoke(localState);

            // Stage 1: Fetch manifest
            progress?.Report(new InstallProgress(game.Id, InstallStage.Preparing, 0));
            await UpdateStageAsync(downloadTask.Id, InstallStage.Preparing);
            var manifest = await _webDav.DownloadManifestAsync(downloadTask.RemoteUrl, ct);

            if (installedManifest != null && installedManifest.Version == manifest.Version)
            {
                // Already up to date
                await CompleteTaskAsync(downloadTask.Id);
                return;
            }

            var toDownload = ManifestDiff.ComputeFilesToDownload(manifest, installedManifest);
            var staleFiles = installedManifest != null ? ManifestDiff.ComputeStaleFiles(manifest, installedManifest) : Array.Empty<GameFile>();
            var totalBytes = toDownload.Sum(f => f.SizeBytes);
            var updatedTask = downloadTask with { TotalBytes = totalBytes, Status = DownloadStatus.Downloading };
            await _db.UpsertDownloadTaskAsync(updatedTask);
            OnTaskUpdated?.Invoke(updatedTask);

            // Stage 2: Prepare staging (copy unchanged files from existing install for updates)
            progress?.Report(new InstallProgress(game.Id, InstallStage.Downloading, 0));
            await UpdateStageAsync(downloadTask.Id, InstallStage.Downloading);
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            if (installedManifest != null && Directory.Exists(finalDir))
            {
                foreach (var file in installedManifest.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (toDownload.Any(f => ManifestDiff.IsSameFile(f, file))) continue;
                    if (staleFiles.Any(f => ManifestDiff.IsSameFile(f, file))) continue;

                    var source = Path.Combine(finalDir, file.Path);
                    if (!File.Exists(source)) continue;

                    var target = Path.Combine(stagingDir, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target);
                }
            }

            // Stage 3: Download changed files (parallel)
            var maxParallel = settings.MaxParallelDownloads > 0 ? settings.MaxParallelDownloads : 2;
            var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
            var downloadedBytes = 0L;
            var startTime = DateTime.UtcNow;

            await Task.WhenAll(toDownload.Select(async file =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var localPath = Path.Combine(stagingDir, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    if (File.Exists(localPath)) File.Delete(localPath);

                    await _webDav.DownloadFileAsync(config.GetFileUrl(file.Path), localPath, downloadTask.Id, null, ct);

                    var done = Interlocked.Add(ref downloadedBytes, file.SizeBytes);
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = elapsed.TotalSeconds > 0 ? done / elapsed.TotalSeconds : 0;
                    await _db.UpsertDownloadTaskAsync(downloadTask with
                    {
                        DownloadedBytes = done,
                        Status = DownloadStatus.Downloading,
                        InstallStage = InstallStage.Downloading
                    });
                    OnTaskUpdated?.Invoke(downloadTask with { DownloadedBytes = done });
                    OnProgress?.Invoke(new DownloadProgress(downloadTask.Id, done, totalBytes, speed, null));
                }
                finally
                {
                    semaphore.Release();
                }
            }));

            // Stage 4: Verify checksums of downloaded files
            progress?.Report(new InstallProgress(game.Id, InstallStage.Verifying, 0));
            await UpdateStageAsync(downloadTask.Id, InstallStage.Verifying);
            foreach (var file in toDownload)
            {
                ct.ThrowIfCancellationRequested();
                var localPath = Path.Combine(stagingDir, file.Path);
                var sha256 = await ComputeSha256Async(localPath, ct);
                if (!string.Equals(sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Checksum mismatch for {file.Path}");
                }
            }

            // Stage 5: Finalize (move into place, with backup for updates)
            progress?.Report(new InstallProgress(game.Id, InstallStage.Extracting, 0));
            await UpdateStageAsync(downloadTask.Id, InstallStage.Extracting);

            if (Directory.Exists(finalDir))
            {
                var backupDir = finalDir + ".backup";
                if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
                Directory.Move(finalDir, backupDir);
                try
                {
                    Directory.Move(stagingDir, finalDir);
                }
                catch
                {
                    if (Directory.Exists(finalDir)) Directory.Delete(finalDir, true);
                    Directory.Move(backupDir, finalDir);
                    throw;
                }
                Directory.Delete(backupDir, true);
            }
            else
            {
                Directory.Move(stagingDir, finalDir);
            }

            // Stage 6: Complete
            localState = localState with
            {
                Status = InstallStatus.Installed,
                InstalledPath = finalDir,
                InstalledVersion = manifest.Version,
                InstalledManifest = manifest
            };
            await _db.UpsertLocalStateAsync(localState);
            OnGameStateChanged?.Invoke(localState);

            progress?.Report(new InstallProgress(game.Id, InstallStage.Completed, 100));
            await CompleteTaskAsync(downloadTask.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Install cancelled for game {GameId}", game.Id);
            await FailTaskAsync(downloadTask.Id, "Cancelled", cancelled: true);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install failed for game {GameId}", game.Id);
            localState = localState with { Status = InstallStatus.Failed };
            await _db.UpsertLocalStateAsync(localState);
            OnGameStateChanged?.Invoke(localState);
            await FailTaskAsync(downloadTask.Id, ex.Message, cancelled: false);
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    private async Task UpdateStageAsync(string taskId, InstallStage stage)
    {
        await _downloadService.UpdateInstallStageAsync(taskId, stage);
    }

    private async Task CompleteTaskAsync(string taskId)
    {
        var task = await _db.GetDownloadTaskAsync(taskId);
        if (task != null)
        {
            var done = task with
            {
                Status = DownloadStatus.Completed,
                DownloadedBytes = task.TotalBytes,
                CompletedAt = DateTime.UtcNow,
                InstallStage = InstallStage.Completed
            };
            await _db.UpsertDownloadTaskAsync(done);
            OnTaskUpdated?.Invoke(done);
        }
        try
        {
            await _db.DeleteDownloadTaskAsync(taskId);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private async Task FailTaskAsync(string taskId, string error, bool cancelled)
    {
        var task = await _db.GetDownloadTaskAsync(taskId);
        if (task != null)
        {
            var failed = task with
            {
                Status = cancelled ? DownloadStatus.Cancelled : DownloadStatus.Failed,
                Error = error
            };
            await _db.UpsertDownloadTaskAsync(failed);
            OnTaskUpdated?.Invoke(failed);
        }
    }

    public async Task UninstallAsync(string gameId)
    {
        var localState = await _db.GetLocalStateAsync(gameId);
        if (localState?.InstalledPath != null && Directory.Exists(localState.InstalledPath))
        {
            try
            {
                Directory.Delete(localState.InstalledPath, true);
                var parentDir = Path.GetDirectoryName(localState.InstalledPath);
                if (parentDir != null && Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                {
                    Directory.Delete(parentDir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete game files for {GameId}", gameId);
            }
        }

        var newState = localState ?? new GameLocalState(gameId, InstallStatus.NotInstalled);
        newState = newState with { Status = InstallStatus.NotInstalled, InstalledPath = null, InstalledVersion = null, InstalledManifest = null };
        await _db.UpsertLocalStateAsync(newState);
        OnGameStateChanged?.Invoke(newState);
    }

    public async Task<LaunchResult> LaunchAsync(string gameId)
    {
        var localState = await _db.GetLocalStateAsync(gameId);
        if (localState?.InstalledPath == null || !Directory.Exists(localState.InstalledPath))
        {
            return new LaunchResult(false, Error: "Game not installed");
        }

        var game = await _db.GetGameAsync(gameId);
        if (game == null)
        {
            return new LaunchResult(false, Error: "Game metadata not found");
        }

        var config = game.LaunchConfig;
        if (config == null)
        {
            return new LaunchResult(false, Error: "No launch configuration");
        }

        var exePath = Path.Combine(localState.InstalledPath, config.ExecutablePath);
        if (!File.Exists(exePath))
        {
            return new LaunchResult(false, Error: $"Executable not found: {exePath}");
        }

        var workDir = config.WorkingDirectory != null 
            ? Path.Combine(localState.InstalledPath, config.WorkingDirectory) 
            : localState.InstalledPath;

        var args = config.LaunchArgs != null && config.LaunchArgs.Length > 0 
            ? string.Join(" ", config.LaunchArgs) 
            : "";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workDir,
                Arguments = args,
                UseShellExecute = false
            };
            var process = Process.Start(startInfo);

            if (process == null)
            {
                return new LaunchResult(false, Error: "Failed to start process");
            }

            // Track playtime in background
            _ = TrackPlaytimeAsync(gameId, process, localState.PlayTimeSeconds);

            return new LaunchResult(true, process.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch game {GameId}", gameId);
            return new LaunchResult(false, Error: ex.Message);
        }
    }

    private async Task TrackPlaytimeAsync(string gameId, Process process, long initialPlaytime)
    {
        var startTime = DateTime.UtcNow;
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
            // Process may have already exited
        }
        
        var sessionTime = (long)(DateTime.UtcNow - startTime).TotalSeconds;
        var totalTime = initialPlaytime + sessionTime;
        
        var localState = await _db.GetLocalStateAsync(gameId);
        if (localState != null)
        {
            var updated = localState with 
            { 
                PlayTimeSeconds = totalTime,
                LastPlayed = DateTime.UtcNow
            };
            await _db.UpsertLocalStateAsync(updated);
            OnGameStateChanged?.Invoke(updated);
        }
    }

    public async Task<GameLocalState?> GetLocalStateAsync(string gameId)
    {
        return await _db.GetLocalStateAsync(gameId);
    }

    public async Task<IReadOnlyList<GameLocalState>> GetAllLocalStatesAsync()
    {
        return await _db.GetAllLocalStatesAsync();
    }

    public async Task<Game?> GetGameAsync(string gameId)
    {
        return await _db.GetGameAsync(gameId);
    }

    public async Task VerifyInstallAsync(string gameId)
    {
        var localState = await _db.GetLocalStateAsync(gameId);
        if (localState?.InstalledManifest == null || localState.InstalledPath == null) return;

        foreach (var file in localState.InstalledManifest.Files)
        {
            var path = Path.Combine(localState.InstalledPath, file.Path);
            if (!File.Exists(path))
            {
                await MarkCorruptAsync(localState, $"Missing file: {file.Path}");
                return;
            }
            var sha256 = await ComputeSha256Async(path);
            if (!string.Equals(sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await MarkCorruptAsync(localState, $"Checksum mismatch: {file.Path}");
                return;
            }
        }

        if (localState.Status != InstallStatus.Installed)
        {
            var ok = localState with { Status = InstallStatus.Installed };
            await _db.UpsertLocalStateAsync(ok);
            OnGameStateChanged?.Invoke(ok);
        }
    }

    private async Task MarkCorruptAsync(GameLocalState state, string reason)
    {
        _logger.LogWarning("Install verification failed for {GameId}: {Reason}", state.GameId, reason);
        var failed = state with { Status = InstallStatus.Failed };
        await _db.UpsertLocalStateAsync(failed);
        OnGameStateChanged?.Invoke(failed);
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            using var sha256 = SHA256.Create();
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var hash = await sha256.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }, ct);
    }

    public async Task<Game[]> GetAllGamesAsync()
    {
        return await _db.GetAllGamesAsync();
    }

    public async Task<int> RefreshFromRemoteAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync();
        if (settings.Nextcloud == null) return 0;

        var config = await _webDav.ResolveConfigAsync(settings.Nextcloud, ct);
        if (config.RootFolder != settings.Nextcloud.RootFolder)
        {
            settings.Nextcloud = config;
            await _db.SaveSettingsAsync(settings);
        }

        var games = await _webDav.DownloadMetadataAsync(config, ct);
        await _db.UpsertGamesAsync(games);
        _logger.LogInformation("Synced {Count} games from Nextcloud", games.Length);
        return games.Length;
    }
}
