namespace GameLauncher.Core.Services;

using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.Core.Utils;
using Microsoft.Extensions.DependencyInjection;
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

    [ActivatorUtilitiesConstructor]
    public AutoUpdateService(
        HttpClient httpClient,
        ILogger<AutoUpdateService> logger,
        AutoUpdateOptions options)
        : this(httpClient, logger, options.CurrentVersion, options.RepoOwner, options.RepoName)
    {
    }

    public AutoUpdateService(
        HttpClient httpClient,
        ILogger<AutoUpdateService> logger,
        string currentVersion,
        string repoOwner,
        string repoName)
    {
        _httpClient = httpClient;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"KermoLauncher/{typeof(AutoUpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0"}");
        }
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

            var latest = UpdateVersion.Parse(release.TagName);
            var current = UpdateVersion.Parse(_currentVersion);
            if (latest == null || current == null)
            {
                _logger.LogWarning(
                    "Cannot compare versions. Current: {Current}, tag: {Tag}", _currentVersion, release.TagName);
                return null;
            }

            var latestVersion = release.TagName.TrimStart('v', 'V');
            if (latest <= current)
            {
                _logger.LogInformation("No update available. Current: {Current}, Latest: {Latest}", _currentVersion, latestVersion);
                return null;
            }

            var assets = release.Assets
                .Select(a => new ReleaseAsset(a.Name, a.BrowserDownloadUrl))
                .ToArray();

            var rid = UpdateAssetMatcher.CurrentRid;
            var asset = UpdateAssetMatcher.Find(assets, rid);
            if (asset == null)
            {
                _logger.LogWarning("Release {Tag} has no asset for {Rid}", release.TagName, rid);
                return null;
            }

            var updateInfo = new UpdateInfo(
                Version: latestVersion,
                ReleaseNotes: release.Body ?? "",
                DownloadUrl: asset.DownloadUrl,
                Sha256: await TryGetChecksumAsync(assets, asset.Name, ct),
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

    /// <summary>
    /// Checksum read from the SHA256SUMS asset of the release. An empty result means no
    /// verification, which is how releases made before the workflow looked, so it is not fatal.
    /// </summary>
    private async Task<string> TryGetChecksumAsync(
        IReadOnlyList<ReleaseAsset> assets, string assetName, CancellationToken ct)
    {
        var checksums = assets.FirstOrDefault(
            a => a.Name.Equals(UpdateAssetMatcher.ChecksumAssetName, StringComparison.OrdinalIgnoreCase));

        if (checksums == null)
        {
            _logger.LogWarning(
                "Release has no {File}, the download will not be verified", UpdateAssetMatcher.ChecksumAssetName);
            return "";
        }

        try
        {
            var content = await _httpClient.GetStringAsync(checksums.DownloadUrl, ct);
            var hash = ChecksumFile.Find(content, assetName);
            if (hash == null)
            {
                _logger.LogWarning("{File} has no entry for {Asset}", UpdateAssetMatcher.ChecksumAssetName, assetName);
            }
            return hash ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read {File}", UpdateAssetMatcher.ChecksumAssetName);
            return "";
        }
    }

    public string GetCachedDownloadPath(UpdateInfo update)
    {
        var fileName = Path.GetFileName(new Uri(update.DownloadUrl).LocalPath);
        return Path.Combine(Path.GetTempPath(), "KermoLauncher_Update", fileName);
    }

    public async Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var finalPath = GetCachedDownloadPath(update);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        if (File.Exists(finalPath))
        {
            if (await MatchesChecksumAsync(finalPath, update.Sha256, ct))
            {
                _logger.LogInformation("Reusing verified update file {Path}", finalPath);
                progress?.Report(100);
                return finalPath;
            }

            // An interrupted download leaves a truncated file behind. It used to be swapped
            // straight into place, which produced an install that could not start.
            _logger.LogInformation("Discarding unverified cached update file {Path}", finalPath);
            File.Delete(finalPath);
        }

        // The file only takes its final name once it is fully downloaded and verified.
        var partPath = finalPath + ".part";
        try
        {
            await DownloadToFileAsync(update, partPath, progress, ct);

            if (!string.IsNullOrEmpty(update.Sha256))
            {
                await ZipHelper.VerifyChecksumAsync(partPath, update.Sha256, ct);
            }

            File.Move(partPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }

        _logger.LogInformation("Update downloaded to {Path}", finalPath);
        return finalPath;
    }

    private async Task DownloadToFileAsync(
        UpdateInfo update, string path, IProgress<double>? progress, CancellationToken ct)
    {
        _logger.LogInformation("Downloading update from {Url} to {Path}", update.DownloadUrl, path);

        using var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        var downloadedBytes = 0L;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

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
    }

    /// <summary>Without a checksum there is no way to confirm the file is complete, so it is rejected.</summary>
    private static async Task<bool> MatchesChecksumAsync(string path, string expected, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expected)) return false;

        try
        {
            var actual = await ZipHelper.ComputeSha256Async(path, ct);
            return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // It will be overwritten on the next attempt.
        }
    }

    /// <summary>
    /// Swaps the executable and starts a new instance. Shutting this instance down is the
    /// caller's job: Environment.Exit used to cut off in-flight database writes and downloads.
    /// </summary>
    public Task ApplyUpdateAsync(string downloadPath, CancellationToken ct = default)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe))
        {
            throw new InvalidOperationException("Cannot determine current executable path");
        }

        SwapExecutable(currentExe, downloadPath);

        _logger.LogInformation("Update applied, starting new instance");

        var psi = new ProcessStartInfo(currentExe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(currentExe)
        };
        Process.Start(psi);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Replaces the executable, keeping the previous one as .old. If putting the new file in
    /// place fails, the previous one is restored, otherwise the install is left with no binary.
    /// Public because that rollback cannot be tested any other way than on a real pair of files.
    /// </summary>
    public static void SwapExecutable(string currentExe, string newFile)
    {
        var backupPath = currentExe + ".old";
        if (File.Exists(backupPath)) File.Delete(backupPath);

        File.Move(currentExe, backupPath);
        try
        {
            File.Move(newFile, currentExe);
            MakeExecutable(currentExe);
        }
        catch
        {
            File.Move(backupPath, currentExe, overwrite: true);
            throw;
        }
    }

    /// <summary>A downloaded file carries no executable bit, so on Unix the new version would not start.</summary>
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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