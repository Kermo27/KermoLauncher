namespace GameLauncher.Core.Services.Interfaces;

using GameLauncher.Core.Models;

public interface IWebDavService
{
    Task<Game[]> DownloadMetadataAsync(NextcloudConfig config, CancellationToken ct = default);
    Task<NextcloudConfig> ResolveConfigAsync(NextcloudConfig config, CancellationToken ct = default);
    Task DownloadFileAsync(string remoteUrl, string localPath, string taskId, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
    Task<bool> FileExistsAsync(string remoteUrl, CancellationToken ct = default);
    Task<long> GetFileSizeAsync(string remoteUrl, CancellationToken ct = default);
    Task CreateDirectoryAsync(string remoteDir, string username, string password, CancellationToken ct = default);
    Task UploadFileAsync(string remoteUrl, string localPath, string username, string password, CancellationToken ct = default);
}