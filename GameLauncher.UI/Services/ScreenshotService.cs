using System.Security.Cryptography;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using GameLauncher.Core.Utils;
using Microsoft.Extensions.Logging;

namespace GameLauncher.UI.Services;

public interface IScreenshotService
{
    /// <summary>
    /// Returns raw image bytes rather than a ready bitmap: a bitmap is an unmanaged resource
    /// and has to be owned by one card so that it can be disposed.
    /// </summary>
    Task<byte[]?> LoadCoverAsync(Game game, CancellationToken ct = default);

    /// <summary>Same as the cover path, for any index in <see cref="Game.ScreenshotUrls"/>.</summary>
    Task<byte[]?> LoadAsync(Game game, int index, CancellationToken ct = default);
}

public class ScreenshotService : IScreenshotService
{
    private const int MaxMemoryEntries = 64;
    private const int MaxDiskFiles = 256;

    private readonly ILocalDbService _db;
    private readonly HttpClient _http;
    private readonly ILogger<ScreenshotService> _logger;
    private readonly string? _diskCacheDir;

    private readonly Dictionary<string, byte[]> _cache = new();
    private readonly LinkedList<string> _lru = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ScreenshotService(
        ILocalDbService db,
        HttpClient http,
        ILogger<ScreenshotService> logger,
        string? diskCacheDir = null)
    {
        _db = db;
        _http = http;
        _logger = logger;
        _diskCacheDir = diskCacheDir;

        if (!string.IsNullOrWhiteSpace(_diskCacheDir))
        {
            Directory.CreateDirectory(_diskCacheDir);
        }
    }

    public Task<byte[]?> LoadCoverAsync(Game game, CancellationToken ct = default)
        => LoadAsync(game, 0, ct);

    public async Task<byte[]?> LoadAsync(Game game, int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= game.ScreenshotUrls.Length) return null;

        var settings = await _db.GetSettingsAsync();
        if (settings.Nextcloud == null)
        {
            _logger.LogDebug("No Nextcloud config, skipping screenshot {Index} for {GameId}", index, game.Id);
            return null;
        }

        var url = settings.Nextcloud.GetFileUrl(game.ScreenshotUrls[index]);

        var cached = await TryGetMemoryAsync(url, ct);
        if (cached != null) return cached;

        var fromDisk = await TryReadDiskAsync(url, ct);
        if (fromDisk != null)
        {
            await StoreMemoryAsync(url, fromDisk, ct);
            return fromDisk;
        }

        var bytes = await DownloadAsync(url, ct);

        // Failed downloads are not cached, so refreshing the library retries them.
        if (bytes != null)
        {
            await StoreMemoryAsync(url, bytes, ct);
            await WriteDiskAsync(url, bytes, ct);
        }

        return bytes;
    }

    private async Task<byte[]?> TryGetMemoryAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_cache.TryGetValue(url, out var bytes)) return null;
            Touch(url);
            return bytes;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StoreMemoryAsync(string url, byte[] bytes, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _cache[url] = bytes;
            Touch(url);

            while (_lru.Count > MaxMemoryEntries)
            {
                var oldest = _lru.Last!.Value;
                _lru.RemoveLast();
                _cache.Remove(oldest);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Touch(string url)
    {
        var node = _lru.Find(url);
        if (node != null) _lru.Remove(node);
        _lru.AddFirst(url);
    }

    private async Task<byte[]?> TryReadDiskAsync(string url, CancellationToken ct)
    {
        var path = DiskPath(url);
        if (path == null || !File.Exists(path)) return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Length == 0) return null;
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Screenshot disk cache read failed for {Url}", UrlSanitizer.Mask(url));
            return null;
        }
    }

    private async Task WriteDiskAsync(string url, byte[] bytes, CancellationToken ct)
    {
        var path = DiskPath(url);
        if (path == null) return;

        try
        {
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
            EvictDiskIfNeeded();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Screenshot disk cache write failed for {Url}", UrlSanitizer.Mask(url));
        }
    }

    private void EvictDiskIfNeeded()
    {
        if (_diskCacheDir == null) return;

        try
        {
            var files = new DirectoryInfo(_diskCacheDir).GetFiles();
            if (files.Length <= MaxDiskFiles) return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - MaxDiskFiles))
            {
                try { file.Delete(); } catch { }
            }
        }
        catch
        {
            // Eviction is best-effort; a full disk must not break cover loading.
        }
    }

    private string? DiskPath(string url)
    {
        if (string.IsNullOrWhiteSpace(_diskCacheDir)) return null;
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(_diskCacheDir, hash);
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Screenshot download failed for {Url}: HTTP {Status}", UrlSanitizer.Mask(url), (int)response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Screenshot download failed for {Url}", UrlSanitizer.Mask(url));
            return null;
        }
    }
}
