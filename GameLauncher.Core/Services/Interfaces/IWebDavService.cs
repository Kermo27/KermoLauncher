namespace GameLauncher.Core.Services.Interfaces;

using GameLauncher.Core.Models;

public interface IWebDavService
{
    Task<Game[]> DownloadMetadataAsync(NextcloudConfig config, CancellationToken ct = default);
    Task<GameManifest> DownloadManifestAsync(string manifestUrl, CancellationToken ct = default, string? username = null, string? password = null);
    Task<NextcloudConfig> ResolveConfigAsync(NextcloudConfig config, CancellationToken ct = default);
    Task DownloadFileAsync(string remoteUrl, string localPath, string taskId, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
    Task CreateDirectoryAsync(string remoteDir, string username, string password, CancellationToken ct = default);
    Task UploadFileAsync(string remoteUrl, string localPath, string username, string password, CancellationToken ct = default);
}