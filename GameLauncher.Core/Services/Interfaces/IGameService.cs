using GameLauncher.Core.Models;

namespace GameLauncher.Core.Services.Interfaces;

public interface IGameService
{
    event Action<GameLocalState>? OnGameStateChanged;
    event Action<DownloadTask>? OnTaskUpdated;
    event Action<DownloadProgress>? OnProgress;
    
    Task InstallAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
    Task UpdateAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Stops the transfer but keeps downloaded files, so ResumeInstallAsync can continue.</summary>
    Task PauseInstallAsync(string gameId);

    Task ResumeInstallAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
    Task CancelInstallAsync(string gameId);
    Task UninstallAsync(string gameId);
    Task<LaunchResult> LaunchAsync(string gameId);
    /// <summary>
    /// Stores per-game Proton version / prefix overrides. Empty values inherit Settings.
    /// Survives uninstall so a reinstall keeps the same launch setup.
    /// </summary>
    Task SaveCompatOverridesAsync(string gameId, string? protonVersion, string? compatPrefix);
    Task<GameLocalState?> GetLocalStateAsync(string gameId);
    Task<IReadOnlyList<GameLocalState>> GetAllLocalStatesAsync();
    Task<Game[]> GetAllGamesAsync();
    Task<Game?> GetGameAsync(string gameId);
    /// <summary>
    /// Re-hashes installed files against the stored manifest.
    /// Returns true when the install is intact (and repairs a Failed status back to Installed).
    /// </summary>
    Task<bool> VerifyInstallAsync(string gameId);
    Task<int> RefreshFromRemoteAsync(CancellationToken ct = default);
}