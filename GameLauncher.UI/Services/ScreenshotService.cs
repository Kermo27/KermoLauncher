using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace GameLauncher.UI.Services;

public interface IScreenshotService
{
    Task<Bitmap?> LoadCoverAsync(Game game, CancellationToken ct = default);
}

public class ScreenshotService : IScreenshotService
{
    private readonly ILocalDbService _db;
    private readonly HttpClient _http;
    private readonly ILogger<ScreenshotService> _logger;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();

    public ScreenshotService(ILocalDbService db, HttpClient http, ILogger<ScreenshotService> logger)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    public async Task<Bitmap?> LoadCoverAsync(Game game, CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync();
        if (settings.Nextcloud == null || game.ScreenshotUrls.Length == 0)
        {
            _logger.LogWarning("No Nextcloud config or no screenshots for game {GameId}", game.Id);
            return null;
        }

        var url = settings.Nextcloud.GetFileUrl(game.ScreenshotUrls[0]);
        return await _cache.GetOrAdd(url, u => LoadAsync(u, ct));
    }

    private async Task<Bitmap?> LoadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Cover download failed for {Url}: HTTP {Status}", url, (int)response.StatusCode);
                return null;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cover download failed for {Url}", url);
            return null;
        }
    }
}
