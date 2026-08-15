namespace GameLauncher.Tests;

using System.Net;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services;
using GameLauncher.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class ScreenshotServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "gl-shot-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly LocalDbService _db;
    private readonly MapHandler _handler = new();
    private readonly ScreenshotService _service;

    public ScreenshotServiceTests()
    {
        _db = new LocalDbService(_dbPath);
        _db.SaveSettingsAsync(new AppSettings
        {
            Nextcloud = new NextcloudConfig("https://example.com/s/abc123", "")
        }).GetAwaiter().GetResult();

        _service = new ScreenshotService(_db, new HttpClient(_handler), NullLogger<ScreenshotService>.Instance);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task LoadAsync_Index1_IsNotTheCover()
    {
        var cover = new byte[] { 1, 2, 3 };
        var second = new byte[] { 9, 9, 9 };
        var game = GameWithShots("a.jpg", "b.jpg");
        var nc = (await _db.GetSettingsAsync()).Nextcloud!;
        _handler.ByUrl[nc.GetFileUrl("a.jpg")] = cover;
        _handler.ByUrl[nc.GetFileUrl("b.jpg")] = second;

        var first = await _service.LoadCoverAsync(game);
        var other = await _service.LoadAsync(game, 1);

        Assert.Equal(cover, first);
        Assert.Equal(second, other);
    }

    [Fact]
    public async Task LoadAsync_OutOfRange_ReturnsNull()
    {
        var game = GameWithShots("a.jpg");

        Assert.Null(await _service.LoadAsync(game, 1));
        Assert.Null(await _service.LoadAsync(game, -1));
        Assert.Equal(0, _handler.Hits);
    }

    [Fact]
    public async Task LoadAsync_CachesByUrl()
    {
        var bytes = new byte[] { 4, 5, 6 };
        var game = GameWithShots("a.jpg");
        var nc = (await _db.GetSettingsAsync()).Nextcloud!;
        _handler.ByUrl[nc.GetFileUrl("a.jpg")] = bytes;

        await _service.LoadAsync(game, 0);
        await _service.LoadCoverAsync(game);

        Assert.Equal(1, _handler.Hits);
    }

    private static Game GameWithShots(params string[] urls) => new(
        "g", "Game", "1.0", "", [], [], urls, "g/manifest.json", 1);

    private sealed class MapHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> ByUrl { get; } = new(StringComparer.Ordinal);
        public int Hits;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hits++;
            var url = request.RequestUri!.ToString();
            if (ByUrl.TryGetValue(url, out var bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
