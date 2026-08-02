namespace GameLauncher.Tests;

using System.Net;
using System.Text;
using GameLauncher.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class AutoUpdateServiceTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v1.1.0",
          "body": "Release notes",
          "assets": [
            { "name": "KermoLauncher-1.0.2-win-x64.exe", "browser_download_url": "https://example.com/dl.exe" },
            { "name": "KermoLauncher-1.0.2-linux-x64", "browser_download_url": "https://example.com/dl-linux" }
          ]
        }
        """;

    [Fact]
    public async Task CheckForUpdatesAsync_NewerVersion_ReturnsUpdateInfo()
    {
        var handler = new StubHandler(ReleaseJson);
        var service = NewService(handler, "1.0.2");

        var update = await service.CheckForUpdatesAsync();

        Assert.Equal("https://api.github.com/repos/owner/repo/releases/latest", handler.LastRequestUrl);
        Assert.NotNull(update);
        Assert.Equal("1.1.0", update!.Version);
        Assert.Equal("Release notes", update.ReleaseNotes);
        Assert.False(string.IsNullOrEmpty(update.DownloadUrl));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SameVersion_ReturnsNull()
    {
        var json = ReleaseJson.Replace("v1.1.0", "v1.0.2");
        var service = NewService(new StubHandler(json), "1.0.2");

        var update = await service.CheckForUpdatesAsync();

        Assert.Null(update);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_HttpError_Throws()
    {
        var handler = new StubHandler("", HttpStatusCode.InternalServerError);
        var service = NewService(handler, "1.0.2");

        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckForUpdatesAsync());
    }

    private static AutoUpdateService NewService(StubHandler handler, string currentVersion)
    {
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new AutoUpdateService(
            client,
            NullLogger<AutoUpdateService>.Instance,
            currentVersion,
            "owner",
            "repo");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _statusCode;

        public string? LastRequestUrl { get; private set; }

        public StubHandler(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _json = json;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }
}
