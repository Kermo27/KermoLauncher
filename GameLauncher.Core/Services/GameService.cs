namespace GameLauncher.Core.Services;

using System.Diagnostics;
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

    public async Task InstallAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var localState = await _db.GetLocalStateAsync(game.Id) ?? new GameLocalState(game.Id, InstallStatus.NotInstalled);
        
        try
        {
            // Stage 1: Download
            progress?.Report(new InstallProgress(game.Id, InstallStage.Downloading, 0));
            localState = localState with { Status = InstallStatus.Downloading };
            await _db.UpsertLocalStateAsync(localState);
            OnGameStateChanged?.Invoke(localState);

            var downloadTask = await _downloadService.QueueDownloadAsync(game);
            
            // Wait for download to complete
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var task = await _downloadService.GetTaskAsync(downloadTask.Id);
                if (task == null) throw new InvalidOperationException("Download task disappeared");
                
                if (task.Status == DownloadStatus.Completed) break;
                if (task.Status == DownloadStatus.Failed) throw new Exception(task.Error ?? "Download failed");
                if (task.Status == DownloadStatus.Cancelled) throw new OperationCanceledException("Download cancelled");
                
                progress?.Report(new InstallProgress(game.Id, InstallStage.Downloading, 
                    task.TotalBytes > 0 ? (double)task.DownloadedBytes / task.TotalBytes * 100 : 0));
                
                await Task.Delay(500, ct);
            }

            // Stage 2: Verify
            progress?.Report(new InstallProgress(game.Id, InstallStage.Verifying, 0));
            await _downloadService.UpdateInstallStageAsync(downloadTask.Id, InstallStage.Verifying);
            localState = localState with { Status = InstallStatus.Installing };
            await _db.UpsertLocalStateAsync(localState);
            OnGameStateChanged?.Invoke(localState);

            var zipPath = downloadTask.LocalPath;
            await ZipHelper.VerifyChecksumAsync(zipPath, game.Sha256, ct);

            // Stage 3: Extract
            progress?.Report(new InstallProgress(game.Id, InstallStage.Extracting, 0));
            await _downloadService.UpdateInstallStageAsync(downloadTask.Id, InstallStage.Extracting);
            var settings = await _db.GetSettingsAsync();
            var installRoot = string.IsNullOrWhiteSpace(settings.InstallFolder)
                ? Path.GetDirectoryName(zipPath)!
                : settings.InstallFolder;
            // Use sanitized game name for folder (readable, e.g. "Shift At Midnight")
            var safeName = string.Join("", game.Name.Split(Path.GetInvalidFileNameChars()));
            var extractDir = Path.Combine(installRoot, safeName);
            Directory.CreateDirectory(extractDir);
            
            var extractProgress = new Progress<double>(p => 
                progress?.Report(new InstallProgress(game.Id, InstallStage.Extracting, p * 100)));
            await ZipHelper.ExtractAsync(zipPath, extractDir, extractProgress, ct);

            // Clean up zip
            File.Delete(zipPath);

            // Stage 5: Complete
            localState = localState with 
            { 
                Status = InstallStatus.Installed,
                InstalledPath = extractDir
            };
            await _db.UpsertLocalStateAsync(localState);
            OnGameStateChanged?.Invoke(localState);

            progress?.Report(new InstallProgress(game.Id, InstallStage.Completed, 100));
            await _downloadService.UpdateInstallStageAsync(downloadTask.Id, InstallStage.Completed);

            // Auto-remove download task after successful install
            try
            {
                await _downloadService.RemoveAsync(downloadTask.Id);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install failed for game {GameId}", game.Id);
            localState = localState with { Status = InstallStatus.Failed };
            await _db.UpsertLocalStateAsync(localState);
            OnGameStateChanged?.Invoke(localState);
            throw;
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
        newState = newState with { Status = InstallStatus.NotInstalled, InstalledPath = null };
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
        var game = await _db.GetGameAsync(gameId);
        
        if (localState?.InstalledPath == null || game == null) return;
        
        // Could verify checksums of extracted files here
        // For now just update last verified
        await Task.CompletedTask;
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