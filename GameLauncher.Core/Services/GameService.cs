namespace GameLauncher.Core.Services;

using System.Diagnostics;
using System.Security.Cryptography;
using Models;
using Interfaces;
using Utils;
using Microsoft.Extensions.Logging;

public class GameService : IGameService, IDisposable
{
    private readonly IDownloadService _downloadService;
    private readonly ILocalDbService _db;
    private readonly IWebDavService _webDav;
    private readonly ILogger<GameService> _logger;
    private readonly object _installLock = new();
    private readonly Dictionary<string, ActiveInstall> _activeInstalls = new(StringComparer.Ordinal);

    /// <summary>Games whose running install was stopped by the user rather than cancelled.</summary>
    private readonly HashSet<string> _pausedGames = new(StringComparer.Ordinal);
    private bool _disposed;

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

        // Transfers report through the download service; the install pipeline is the single
        // event source the UI subscribes to.
        _downloadService.OnTaskUpdated += RaiseTaskUpdated;
        _downloadService.OnProgress += RaiseProgress;
    }

    private void RaiseTaskUpdated(DownloadTask task) => OnTaskUpdated?.Invoke(task);

    private void RaiseProgress(DownloadProgress progress) => OnProgress?.Invoke(progress);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _downloadService.OnTaskUpdated -= RaiseTaskUpdated;
        _downloadService.OnProgress -= RaiseProgress;
    }

    /// <summary>An install in flight: its cancellation source and the download task it drives.</summary>
    private sealed record ActiveInstall(CancellationTokenSource Cts, string TaskId);

    public Task InstallAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
        => InstallOrUpdateAsync(game, progress, ct);

    public Task UpdateAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
        => InstallOrUpdateAsync(game, progress, ct);

    public Task ResumeInstallAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
        => InstallOrUpdateAsync(game, progress, ct, resume: true);

    public Task PauseInstallAsync(string gameId)
    {
        ActiveInstall? active;
        lock (_installLock)
        {
            if (!_activeInstalls.TryGetValue(gameId, out active)) return Task.CompletedTask;

            // The install loop reads this in its cancellation handler to tell a pause from a cancel.
            _pausedGames.Add(gameId);
        }

        if (!TryCancel(active))
        {
            lock (_installLock) _pausedGames.Remove(gameId);
        }

        return Task.CompletedTask;
    }

    public async Task CancelInstallAsync(string gameId)
    {
        ActiveInstall? active;
        lock (_installLock)
        {
            _activeInstalls.TryGetValue(gameId, out active);
            _pausedGames.Remove(gameId);
        }

        if (active != null)
        {
            TryCancel(active);
            return;
        }

        // Nothing is running: a paused install is cancelled by throwing its partial files away.
        var localState = await _db.GetLocalStateAsync(gameId);
        if (localState?.Status != InstallStatus.Paused) return;

        DeleteStagingDir(await GetStagingDirAsync(gameId));
        await DiscardTasksForGameAsync(gameId);

        var reset = localState with
        {
            Status = localState.InstalledPath != null && Directory.Exists(localState.InstalledPath)
                ? InstallStatus.Installed
                : InstallStatus.NotInstalled
        };
        await _db.UpsertLocalStateAsync(reset);
        OnGameStateChanged?.Invoke(reset);
    }

    private static bool TryCancel(ActiveInstall active)
    {
        try
        {
            active.Cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The install already finished.
            return false;
        }
    }

    private async Task InstallOrUpdateAsync(
        Game game,
        IProgress<InstallProgress>? progress,
        CancellationToken ct,
        bool resume = false)
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

        // Leftovers from a paused install or an earlier crash would otherwise pile up in the
        // task table and confuse the library view.
        await DiscardTasksForGameAsync(game.Id);

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
        // A paused install keeps the manifest of the version already on disk, so a resumed update
        // still downloads only the files that actually changed.
        var installedManifest = localState.Status is InstallStatus.Installed or InstallStatus.Paused
            ? localState.InstalledManifest
            : null;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linkedCts.Token;
        lock (_installLock) _activeInstalls[game.Id] = new ActiveInstall(linkedCts, taskId);
        var paused = false;

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
            var stagingCopy = installedManifest != null && !resume
                ? Math.Max(0, manifest.TotalBytes - totalBytes)
                : 0;
            InstallFolder.ThrowIfInsufficient(
                installRoot,
                totalBytes + stagingCopy + InstallFolder.DiskSpaceMarginBytes);

            var updatedTask = downloadTask with { TotalBytes = totalBytes, Status = DownloadStatus.Downloading };
            await _db.UpsertDownloadTaskAsync(updatedTask);
            OnTaskUpdated?.Invoke(updatedTask);

            var fileUrl = (GameFile file) => GameUrl.GetFileUrl(config, game.ManifestUrl, file.Path);

            // Stage 2: Prepare staging (copy unchanged files from existing install for updates)
            progress?.Report(new InstallProgress(game.Id, InstallStage.Downloading, 0));
            await UpdateStageAsync(downloadTask.Id, InstallStage.Downloading);

            // A resumed install keeps whatever the previous attempt already wrote.
            if (!resume && Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
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
                    if (File.Exists(target)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target);
                }
            }

            // Stage 3: Download changed files
            var requests = toDownload
                .Select(file => new DownloadFileRequest(
                    file.Path,
                    fileUrl(file),
                    GamePaths.Combine(stagingDir, file.Path),
                    file.SizeBytes))
                .ToArray();

            await _downloadService.DownloadFilesAsync(updatedTask, requests, token);

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
            lock (_installLock) paused = _pausedGames.Remove(game.Id);

            if (paused)
            {
                _logger.LogInformation("Install paused for game {GameId}", game.Id);
                localState = localState with { Status = InstallStatus.Paused };
                await _db.UpsertLocalStateAsync(localState);
                OnGameStateChanged?.Invoke(localState);
                await PauseTaskAsync(downloadTask.Id);

                // Callers treat cancellation as "stopped on purpose"; the paused state is already saved.
                throw;
            }

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
            lock (_installLock)
            {
                _activeInstalls.Remove(game.Id);
                _pausedGames.Remove(game.Id);
            }

            // A paused install keeps its partial files; everything else cleans up after itself.
            if (!paused) DeleteStagingDir(stagingDir);
        }
    }

    private static void DeleteStagingDir(string stagingDir)
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

    /// <summary>Staging folder of a game's pending install, whether or not one is running.</summary>
    private async Task<string> GetStagingDirAsync(string gameId)
    {
        var settings = await _db.GetSettingsAsync();
        var installRoot = string.IsNullOrWhiteSpace(settings.InstallFolder)
            ? Path.Combine(Utils.AppPaths.DataDirectory, "games")
            : settings.InstallFolder;
        return Path.Combine(installRoot, ".update", gameId);
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

    /// <summary>Drops unfinished task rows for a game before a fresh install or resume starts.</summary>
    private async Task DiscardTasksForGameAsync(string gameId)
    {
        var tasks = await _downloadService.GetAllTasksAsync();
        foreach (var task in tasks.Where(t => t.GameId == gameId))
        {
            try
            {
                await _db.DeleteDownloadTaskAsync(task.Id);
            }
            catch
            {
                // Ignore cleanup errors; a stale row is harmless.
            }
        }
    }

    /// <summary>Keeps the task row so the library can offer a resume for the partial download.</summary>
    private async Task PauseTaskAsync(string taskId)
    {
        var task = await _db.GetDownloadTaskAsync(taskId);
        if (task == null) return;

        var paused = task with { Status = DownloadStatus.Paused };
        await _db.UpsertDownloadTaskAsync(paused);
        OnTaskUpdated?.Invoke(paused);
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
            LogLaunchCommand(gameId, startInfo);
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

    /// <summary>
    /// Compatibility problems on Linux come down to which Proton, runtime and environment the game
    /// actually got, so both go into the log instead of only into the terminal.
    /// </summary>
    private void LogLaunchCommand(string gameId, ProcessStartInfo startInfo)
    {
        _logger.LogInformation("Launching {GameId}: {Command} {Arguments}",
            gameId,
            startInfo.FileName,
            string.Join(' ', startInfo.ArgumentList.Select(GameLaunchHelper.Quote)));

        var interesting = new[]
        {
            "STEAM_COMPAT_DATA_PATH", "WINEPREFIX", "WINEDLLOVERRIDES", "LD_PRELOAD",
            "SteamAppId", "STEAM_COMPAT_CLIENT_INSTALL_PATH", "PROTONPATH", "GAMEID"
        };
        var env = interesting
            .Where(key => startInfo.Environment.ContainsKey(key))
            .Select(key => $"{key}={startInfo.Environment[key]}");
        _logger.LogInformation("Launch environment for {GameId}: {Environment}", gameId, string.Join(' ', env));
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

    public async Task<bool> VerifyInstallAsync(string gameId)
    {
        var localState = await _db.GetLocalStateAsync(gameId);
        if (localState?.InstalledManifest == null || localState.InstalledPath == null) return false;

        foreach (var file in localState.InstalledManifest.Files)
        {
            var path = GamePaths.Combine(localState.InstalledPath, file.Path);
            if (!File.Exists(path))
            {
                await MarkCorruptAsync(localState, $"Missing file: {file.Path}");
                return false;
            }
            var sha256 = await ComputeSha256Async(path);
            if (!string.Equals(sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await MarkCorruptAsync(localState, $"Checksum mismatch: {file.Path}");
                return false;
            }
        }

        if (localState.Status != InstallStatus.Installed)
        {
            var ok = localState with { Status = InstallStatus.Installed };
            await _db.UpsertLocalStateAsync(ok);
            OnGameStateChanged?.Invoke(ok);
        }

        return true;
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

}
