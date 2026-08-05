namespace GameLauncher.Core.Services;

using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.Core.Utils;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

public class AutoUpdateService : IAutoUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AutoUpdateService> _logger;
    private readonly string _currentVersion;
    private readonly string _repoOwner;
    private readonly string _repoName;

    public event Action<UpdateInfo>? OnUpdateAvailable;
    public event Action<double>? OnUpdateDownloadProgress;

    public AutoUpdateService(
        HttpClient httpClient,
        ILogger<AutoUpdateService> logger,
        string currentVersion,
        string repoOwner,
        string repoName)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"KermoLauncher/{typeof(AutoUpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0"}");
        _logger = logger;
        _currentVersion = currentVersion;
        _repoOwner = repoOwner;
        _repoName = repoName;
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases/latest";
            _logger.LogInformation("Checking for updates at {Url}", url);

            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
            if (release == null) return null;

            var latestVersion = release.TagName.TrimStart('v');
            if (string.IsNullOrEmpty(latestVersion)) return null;

            if (Version.Parse(latestVersion) <= Version.Parse(_currentVersion))
            {
                _logger.LogInformation("No update available. Current: {Current}, Latest: {Latest}", _currentVersion, latestVersion);
                return null;
            }

            var asset = FindMatchingAsset(release.Assets);
            if (asset == null)
            {
                _logger.LogWarning("No matching asset found for current platform");
                return null;
            }

            var updateInfo = new UpdateInfo(
                Version: latestVersion,
                ReleaseNotes: release.Body ?? "",
                DownloadUrl: asset.BrowserDownloadUrl,
                Sha256: "",
                IsMandatory: false
            );

            OnUpdateAvailable?.Invoke(updateInfo);
            return updateInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for updates");
            throw;
        }
    }

    private GitHubAsset? FindMatchingAsset(GitHubAsset[] assets)
    {
        var isWindows = OperatingSystem.IsWindows();
        foreach (var asset in assets)
        {
            var name = asset.Name.ToLowerInvariant();

            if (isWindows)
            {
                if (name.EndsWith(".exe") || name.EndsWith(".msi") || name.Contains("windows"))
                {
                    return asset;
                }
            }
            else if (name.Contains("linux"))
            {
                return asset;
            }
        }
        return null;
    }

    public string GetCachedDownloadPath(UpdateInfo update)
    {
        var fileName = Path.GetFileName(new Uri(update.DownloadUrl).LocalPath);
        return Path.Combine(Path.GetTempPath(), "KermoLauncher_Update", fileName);
    }

    public async Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var downloadPath = GetCachedDownloadPath(update);
        Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);

        _logger.LogInformation("Downloading update from {Url} to {Path}", update.DownloadUrl, downloadPath);

        using var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        var downloadedBytes = 0L;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            downloadedBytes += bytesRead;

            if (totalBytes > 0)
            {
                var pct = (double)downloadedBytes / totalBytes * 100;
                progress?.Report(pct);
                OnUpdateDownloadProgress?.Invoke(pct);
            }
        }

        _logger.LogInformation("Update downloaded to {Path}", downloadPath);

        if (!string.IsNullOrEmpty(update.Sha256))
        {
            await ZipHelper.VerifyChecksumAsync(downloadPath, update.Sha256, ct);
        }

        return downloadPath;
    }

    public async Task ApplyUpdateAsync(string downloadPath, CancellationToken ct = default)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe))
        {
            throw new InvalidOperationException("Cannot determine current executable path");
        }

        var backupPath = currentExe + ".old";
        if (File.Exists(backupPath)) File.Delete(backupPath);
        File.Move(currentExe, backupPath);
        File.Move(downloadPath, currentExe);

        _logger.LogInformation("Update applied, restarting...");
        
        var psi = new ProcessStartInfo(currentExe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(currentExe)
        };
        Process.Start(psi);
        
        Environment.Exit(0);
        await Task.CompletedTask;
    }

    public Task<bool> IsUpdatePendingAsync()
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe)) return Task.FromResult(false);
        
        var backupPath = currentExe + ".old";
        return Task.FromResult(File.Exists(backupPath));
    }

    public Task CleanupPendingUpdateAsync()
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe)) return Task.CompletedTask;

        var backupPath = currentExe + ".old";
        try
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
                _logger.LogInformation("Cleaned up update backup {Path}", backupPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up update backup {Path}", backupPath);
        }
        return Task.CompletedTask;
    }

    private record GitHubRelease(
        string TagName,
        string Body,
        GitHubAsset[] Assets
    );

    private record GitHubAsset(
        string Name,
        string BrowserDownloadUrl
    );
}