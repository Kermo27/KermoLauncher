namespace GameLauncher.Core.Services;

using System.Net.Http.Headers;
using System.Text.Json;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

public class WebDavService : IWebDavService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebDavService> _logger;

    public WebDavService(HttpClient httpClient, ILogger<WebDavService> logger)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _logger = logger;
    }

    private static string UserAgent =>
        $"KermoLauncher/{typeof(WebDavService).Assembly.GetName().Version?.ToString(3) ?? "1.0"}";

    public async Task<Game[]> DownloadMetadataAsync(NextcloudConfig config, CancellationToken ct = default)
    {
        var url = config.MetadataUrl;
        _logger.LogInformation("Downloading metadata from {Url}", url);

        var response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var games = System.Text.Json.JsonSerializer.Deserialize<Game[]>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];

        _logger.LogInformation("Loaded {Count} games from metadata", games.Length);
        return games;
    }

    /// <summary>
    /// Wykrywa katalog bazowy udostępnienia: sprawdza czy metadata.json leży w korzeniu,
    /// a jeśli nie - w podfolderze "Games". Zwraca konfigurację z ustawionym RootFolder.
    /// </summary>
    public async Task<NextcloudConfig> ResolveConfigAsync(NextcloudConfig config, CancellationToken ct = default)
    {
        if (await MetadataFileExistsAsync(config, "", ct))
        {
            return config with { RootFolder = "" };
        }

        if (await MetadataFileExistsAsync(config, "Games", ct))
        {
            return config with { RootFolder = "Games" };
        }

        return config with { RootFolder = "" };
    }

    private async Task<bool> MetadataFileExistsAsync(NextcloudConfig config, string rootFolder, CancellationToken ct)
    {
        try
        {
            var url = $"{config.ServerBase}/public.php/dav/files/{config.DavToken}" +
                      (rootFolder.Length > 0 ? $"/{rootFolder}" : "") + "/metadata.json";
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task DownloadFileAsync(string remoteUrl, string localPath, string taskId, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Downloading {Url} to {Path}", remoteUrl, localPath);

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        var fileInfo = new FileInfo(localPath);
        long existingBytes = fileInfo.Exists ? fileInfo.Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, remoteUrl);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            _logger.LogWarning("Server rejected resume for {Path}; restarting download from scratch", localPath);
            response.Dispose();
            request.Dispose();
            File.Delete(localPath);
            existingBytes = 0;

            using var retryRequest = new HttpRequestMessage(HttpMethod.Get, remoteUrl);
            using var retryResponse = await _httpClient.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            retryResponse.EnsureSuccessStatusCode();
            await DownloadStreamAsync(retryResponse, localPath, taskId, existingBytes, progress, ct);
            _logger.LogInformation("Download completed: {Path}", localPath);
            return;
        }

        response.EnsureSuccessStatusCode();
        await DownloadStreamAsync(response, localPath, taskId, existingBytes, progress, ct);

        _logger.LogInformation("Download completed: {Path}", localPath);
    }

    private static async Task DownloadStreamAsync(HttpResponseMessage response, string localPath, string taskId, long existingBytes, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var totalBytes = existingBytes + (response.Content.Headers.ContentLength ?? 0);
        var downloadedBytes = existingBytes;
        var startTime = DateTime.UtcNow;
        var lastReportTime = DateTime.UtcNow;
        var lastReportBytes = downloadedBytes;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(localPath, FileMode.Append, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            downloadedBytes += bytesRead;

            var now = DateTime.UtcNow;
            if ((now - lastReportTime).TotalMilliseconds >= 100 || progress != null)
            {
                var elapsed = now - startTime;
                var speed = elapsed.TotalSeconds > 0 ? (downloadedBytes - existingBytes) / elapsed.TotalSeconds : 0;
                TimeSpan? remaining = speed > 0 ? TimeSpan.FromSeconds((totalBytes - downloadedBytes) / speed) : null;

                progress?.Report(new DownloadProgress(
                    taskId,
                    downloadedBytes,
                    totalBytes,
                    speed,
                    remaining
                ));

                lastReportTime = now;
                lastReportBytes = downloadedBytes;
            }
        }
    }

    public async Task<bool> FileExistsAsync(string remoteUrl, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, remoteUrl);
            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<long> GetFileSizeAsync(string remoteUrl, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, remoteUrl);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return response.Content.Headers.ContentLength ?? 0;
    }

    public async Task CreateDirectoryAsync(string remoteDir, string username, string password, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), remoteDir);
        request.Headers.Authorization = BasicAuth(username, password);
        using var response = await _httpClient.SendAsync(request, ct);
        if ((int)response.StatusCode is not (201 or 204 or 405 or 409))
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task UploadFileAsync(string remoteUrl, string localPath, string username, string password, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(localPath);
        await using var stream = File.OpenRead(localPath);
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = fileInfo.Length;

        using var request = new HttpRequestMessage(HttpMethod.Put, remoteUrl);
        request.Headers.Authorization = BasicAuth(username, password);
        request.Content = content;

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Uploaded {Path} ({Bytes} bytes)", localPath, fileInfo.Length);
    }

    private static AuthenticationHeaderValue BasicAuth(string username, string password)
    {
        var credentials = System.Text.Encoding.UTF8.GetBytes($"{username}:{password}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentials));
    }
}