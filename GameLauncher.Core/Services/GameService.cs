namespace GameLauncher.Core.Services;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
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
    private readonly object _installLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _activeInstalls = new();

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

    public Task CancelInstallAsync(string gameId)
    {
        CancellationTokenSource? cts;
        lock (_installLock)
        {
            _activeInstalls.TryGetValue(gameId, out cts);
        }

        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The install already finished.
            }
        }
        return Task.CompletedTask;
    }

    private async Task InstallOrUpdateAsync(Game game, bool isUpdate, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var localState = await _db.GetLocalStateAsync(game.Id) ?? new GameLocalState(game.Id, InstallStatus.NotInstalled);
        var initialState = localState;
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

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linkedCts.Token;
        lock (_installLock) _activeInstalls[game.Id] = linkedCts;

        try
        {
            localState = localState with { Status = InstallStatus.Downloading };
            await _db.UpsertLocalStateAsync(localState);
            OnGameStateChanged?.Invoke(localState);

            // Stage 1: Fetch manifest
            progress?.Report(new InstallProgress(game.Id, InstallStage.Preparing, 0));
            await UpdateStageAsync(downloadTask.Id, InstallStage.Preparing);
            var manifest = await _webDav.DownloadManifestAsync(downloadTask.RemoteUrl, token);

            var toDownload = ManifestDiff.ComputeFilesToDownload(manifest, installedManifest);
            var staleFiles = installedManifest != null ? ManifestDiff.ComputeStaleFiles(manifest, installedManifest) : Array.Empty<GameFile>();

            if (installedManifest != null &&
                installedManifest.Version == manifest.Version &&
                toDownload.Length == 0 &&
                staleFiles.Length == 0)
            {
                // Already up to date
                await CompleteTaskAsync(downloadTask.Id);
                return;
            }

            var totalBytes = toDownload.Sum(f => f.SizeBytes);
            var updatedTask = downloadTask with { TotalBytes = totalBytes, Status = DownloadStatus.Downloading };
            await _db.UpsertDownloadTaskAsync(updatedTask);
            OnTaskUpdated?.Invoke(updatedTask);

            var gameFolder = Path.GetDirectoryName(game.ManifestUrl.Replace('\\', '/'))?.TrimEnd('/') ?? "";
            var fileUrl = (GameFile file) => GameUrl.GetFileUrl(config, game.ManifestUrl, file.Path);

            // Stage 2: Prepare staging (copy unchanged files from existing install for updates)
            progress?.Report(new InstallProgress(game.Id, InstallStage.Downloading, 0));
            await UpdateStageAsync(downloadTask.Id, InstallStage.Downloading);
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            if (installedManifest != null && Directory.Exists(finalDir))
            {
                foreach (var file in installedManifest.Files)
                {
                    token.ThrowIfCancellationRequested();
                    if (toDownload.Any(f => ManifestDiff.IsSameFile(f, file))) continue;
                    if (staleFiles.Any(f => ManifestDiff.IsSameFile(f, file))) continue;

                    var source = GamePaths.Combine(finalDir, file.Path);
                    if (!File.Exists(source)) continue;

                    var target = GamePaths.Combine(stagingDir, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target);
                }
            }

            // Stage 3: Download changed files (parallel)
            var maxParallel = settings.MaxParallelDownloads > 0 ? settings.MaxParallelDownloads : 2;
            var completedBytes = 0L;

            // Bytes of in-flight files are tracked separately so the total stays correct
            // no matter what order the parallel downloads report progress in.
            var inFlight = new ConcurrentDictionary<string, long>();
            long CurrentBytes() => Volatile.Read(ref completedBytes) + inFlight.Values.Sum();

            await using var pump = new ProgressPump(
                _db,
                updatedTask with { InstallStage = InstallStage.Downloading },
                OnTaskUpdated,
                OnProgress);

            await Parallel.ForEachAsync(
                toDownload,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = token },
                async (file, fileToken) =>
                {
                    var localPath = GamePaths.Combine(stagingDir, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    if (File.Exists(localPath)) File.Delete(localPath);

                    var progress = new DelegatedProgress(p =>
                    {
                        inFlight[file.Path] = p.BytesReceived;
                        pump.Report(CurrentBytes());
                    });

                    await _webDav.DownloadFileAsync(fileUrl(file), localPath, downloadTask.Id, progress, fileToken);

                    // Order matters: drop from in-flight before adding to the total, so the
                    // reported sum can dip for a moment but never exceed the real one.
                    inFlight.TryRemove(file.Path, out _);
                    Interlocked.Add(ref completedBytes, file.SizeBytes);
                    pump.Report(CurrentBytes());
                });

            await pump.FlushAsync(totalBytes);

            // Stage 4: Verify checksums of downloaded files
            progress?.Report(new InstallProgress(game.Id, InstallStage.Verifying, 0));
            await UpdateStageAsync(downloadTask.Id, InstallStage.Verifying);
            foreach (var file in toDownload)
            {
                token.ThrowIfCancellationRequested();
                var localPath = GamePaths.Combine(stagingDir, file.Path);
                var sha256 = await ComputeSha256Async(localPath, token);
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

            // WebDAV does not preserve Unix mode bits; native Linux game binaries need +x.
            if (game.LaunchConfig != null)
            {
                GamePaths.TryMakeExecutable(GamePaths.Combine(finalDir, game.LaunchConfig.ExecutablePath));
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
            localState = localState with { Status = initialState.Status };
            await _db.UpsertLocalStateAsync(localState);
            OnGameStateChanged?.Invoke(localState);
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
            lock (_installLock) _activeInstalls.Remove(game.Id);
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

        var exePath = GamePaths.Combine(localState.InstalledPath, config.ExecutablePath);
        if (!File.Exists(exePath))
        {
            return new LaunchResult(false, Error: $"Executable not found: {exePath}");
        }

        GamePaths.TryMakeExecutable(exePath);

        var workDir = config.WorkingDirectory != null
            ? GamePaths.Combine(localState.InstalledPath, config.WorkingDirectory)
            : localState.InstalledPath;

        try
        {
            if (OperatingSystem.IsLinux() &&
                GameLaunchHelper.LooksLikeOnlineFix(workDir, exePath) &&
                !GameLaunchHelper.IsSteamRunning())
            {
                return new LaunchResult(
                    false,
                    Error: "Steam must be running to launch Online-Fix games (Steam Overlay / AppID 480).");
            }

            var settings = await _db.GetSettingsAsync();
            var startInfo = GameLaunchHelper.Build(exePath, workDir, config.LaunchArgs, settings);
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
        long ElapsedSeconds() => initialPlaytime + (long)(DateTime.UtcNow - startTime).TotalSeconds;

        try
        {
            using (process)
            using (var timer = new PeriodicTimer(TimeSpan.FromSeconds(60)))
            using (var exitCts = new CancellationTokenSource())
            {
                var exited = process.WaitForExitAsync(exitCts.Token);

                // One timer for the whole session instead of a fresh Task.Delay per iteration.
                while (!process.HasExited)
                {
                    if (!await timer.WaitForNextTickAsync(CancellationToken.None)) break;
                    if (process.HasExited) break;
                    await PersistPlaytimeAsync(gameId, ElapsedSeconds(), updateLastPlayed: false);
                }

                await exitCts.CancelAsync();
                try
                {
                    await exited;
                }
                catch (OperationCanceledException)
                {
                    // The process exited while we were waiting.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playtime tracking stopped early for {GameId}", gameId);
        }

        await PersistPlaytimeAsync(gameId, ElapsedSeconds(), updateLastPlayed: true);
    }

    private async Task PersistPlaytimeAsync(string gameId, long totalTime, bool updateLastPlayed)
    {
        var localState = await _db.GetLocalStateAsync(gameId);
        if (localState == null) return;

        var updated = updateLastPlayed
            ? localState with { PlayTimeSeconds = totalTime, LastPlayed = DateTime.UtcNow }
            : localState with { PlayTimeSeconds = totalTime };
        await _db.UpsertLocalStateAsync(updated);
        OnGameStateChanged?.Invoke(updated);
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
            var path = GamePaths.Combine(localState.InstalledPath, file.Path);
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
        if (games.Length > 0)
        {
            await _db.RemoveGamesNotInAsync(games.Select(g => g.Id).ToArray());
        }
        _logger.LogInformation("Synced {Count} games from Nextcloud", games.Length);
        return games.Length;
    }

    private sealed class DelegatedProgress : IProgress<DownloadProgress>
    {
        private readonly Action<DownloadProgress> _handler;

        public DelegatedProgress(Action<DownloadProgress> handler)
        {
            _handler = handler;
        }

        public void Report(DownloadProgress value)
        {
            _handler(value);
        }
    }

    /// <summary>
    /// Collects progress reports from parallel downloads and handles them in a single consumer,
    /// so events are raised from one thread and database writes are throttled to one per 500 ms
    /// and actually awaited instead of being fired and forgotten.
    /// </summary>
    private sealed class ProgressPump : IAsyncDisposable
    {
        private static readonly TimeSpan PersistInterval = TimeSpan.FromMilliseconds(500);

        private readonly ILocalDbService _db;
        private readonly DownloadTask _template;
        private readonly Action<DownloadTask>? _onTaskUpdated;
        private readonly Action<DownloadProgress>? _onProgress;
        private readonly Channel<long> _channel =
            Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private readonly Task _consumer;
        private DateTime _lastPersistedAt = DateTime.MinValue;

        public ProgressPump(
            ILocalDbService db,
            DownloadTask template,
            Action<DownloadTask>? onTaskUpdated,
            Action<DownloadProgress>? onProgress)
        {
            _db = db;
            _template = template;
            _onTaskUpdated = onTaskUpdated;
            _onProgress = onProgress;
            _consumer = Task.Run(ConsumeAsync);
        }

        public void Report(long bytes) => _channel.Writer.TryWrite(bytes);

        private async Task ConsumeAsync()
        {
            await foreach (var bytes in _channel.Reader.ReadAllAsync())
            {
                var elapsed = DateTime.UtcNow - _startedAt;
                var speed = elapsed.TotalSeconds > 0 ? bytes / elapsed.TotalSeconds : 0;
                TimeSpan? remaining = speed > 0 && _template.TotalBytes > bytes
                    ? TimeSpan.FromSeconds((_template.TotalBytes - bytes) / speed)
                    : null;

                _onProgress?.Invoke(new DownloadProgress(_template.Id, bytes, _template.TotalBytes, speed, remaining));

                if (DateTime.UtcNow - _lastPersistedAt >= PersistInterval)
                {
                    _lastPersistedAt = DateTime.UtcNow;
                    await PersistAsync(bytes);
                }
            }
        }

        private async Task PersistAsync(long bytes)
        {
            var snapshot = _template with { DownloadedBytes = bytes };
            await _db.UpsertDownloadTaskAsync(snapshot);
            _onTaskUpdated?.Invoke(snapshot);
        }

        /// <summary>Closes the channel and guarantees one final event with the total byte count.</summary>
        public async Task FlushAsync(long finalBytes)
        {
            _channel.Writer.TryComplete();
            await _consumer;
            await PersistAsync(finalBytes);
        }

        public async ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            try
            {
                await _consumer;
            }
            catch (Exception)
            {
                // Error path: the real install exception propagates from FlushAsync.
            }
        }
    }
}
