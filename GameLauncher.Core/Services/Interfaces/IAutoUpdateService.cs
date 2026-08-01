namespace GameLauncher.Core.Services.Interfaces;

public interface IAutoUpdateService
{
    event Action<UpdateInfo>? OnUpdateAvailable;
    event Action<double>? OnUpdateDownloadProgress;
    
    Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default);
    Task DownloadAndInstallUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default);
    Task<bool> IsUpdatePendingAsync();
}

public record UpdateInfo(
    string Version,
    string ReleaseNotes,
    string DownloadUrl,
    string Sha256,
    bool IsMandatory
);