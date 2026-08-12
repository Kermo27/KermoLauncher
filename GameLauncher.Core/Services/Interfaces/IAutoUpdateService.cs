namespace GameLauncher.Core.Services.Interfaces;

public interface IAutoUpdateService
{
    event Action<UpdateInfo>? OnUpdateAvailable;
    event Action<double>? OnUpdateDownloadProgress;
    
    Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default);
    string GetCachedDownloadPath(UpdateInfo update);
    Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default);
    Task ApplyUpdateAsync(string downloadPath, CancellationToken ct = default);
    Task<bool> IsUpdatePendingAsync();
    Task CleanupPendingUpdateAsync();
}

/// <summary>Parametry sprawdzania aktualizacji — wstrzykiwane, żeby serwis dał się zarejestrować jako typed client.</summary>
public record AutoUpdateOptions(
    string CurrentVersion,
    string RepoOwner,
    string RepoName
);

public record UpdateInfo(
    string Version,
    string ReleaseNotes,
    string DownloadUrl,
    string Sha256,
    bool IsMandatory
);