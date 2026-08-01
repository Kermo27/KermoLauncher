using GameLauncher.Core.Models;

namespace GameLauncher.Core.Services.Interfaces;

public interface IGameService
{
    event Action<GameLocalState>? OnGameStateChanged;
    event Action<DownloadTask>? OnTaskUpdated;
    event Action<DownloadProgress>? OnProgress;
    
    Task InstallAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
    Task UpdateAsync(Game game, IProgress<InstallProgress>? progress = null, CancellationToken ct = default);
    Task UninstallAsync(string gameId);
    Task<LaunchResult> LaunchAsync(string gameId);
    Task<GameLocalState?> GetLocalStateAsync(string gameId);
    Task<IReadOnlyList<GameLocalState>> GetAllLocalStatesAsync();
    Task<Game[]> GetAllGamesAsync();
    Task<Game?> GetGameAsync(string gameId);
    Task VerifyInstallAsync(string gameId);
    Task<int> RefreshFromRemoteAsync(CancellationToken ct = default);
}