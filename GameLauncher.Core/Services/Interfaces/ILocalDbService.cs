namespace GameLauncher.Core.Services.Interfaces;

using GameLauncher.Core.Models;

public interface ILocalDbService
{
    Task InitializeAsync();
    
    // Games metadata (synced from Nextcloud)
    Task UpsertGamesAsync(Game[] games);
    Task RemoveGamesNotInAsync(IReadOnlyCollection<string> keepIds);
    Task<Game?> GetGameAsync(string gameId);
    Task<Game[]> GetAllGamesAsync();
    Task<Game[]> GetGamesByStatusAsync(InstallStatus status);
    
    // Local game state
    Task UpsertLocalStateAsync(GameLocalState state);
    Task<GameLocalState?> GetLocalStateAsync(string gameId);
    Task<IReadOnlyList<GameLocalState>> GetAllLocalStatesAsync();
    
    // Downloads queue
    Task UpsertDownloadTaskAsync(DownloadTask task);
    Task<DownloadTask?> GetDownloadTaskAsync(string taskId);
    Task<IReadOnlyList<DownloadTask>> GetAllDownloadTasksAsync();
    Task DeleteDownloadTaskAsync(string taskId);
    
    // Settings
    Task<AppSettings> GetSettingsAsync();
    Task SaveSettingsAsync(AppSettings settings);
}