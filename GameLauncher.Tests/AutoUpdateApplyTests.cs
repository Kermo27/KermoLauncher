namespace GameLauncher.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using GameLauncher.Core.Services;
using GameLauncher.Core.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class AutoUpdateSwapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kermo-swap-" + Guid.NewGuid().ToString("N"));

    public AutoUpdateSwapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void SwapExecutable_ReplacesBinaryAndKeepsPreviousAsBackup()
    {
        var current = Path.Combine(_dir, "KermoLauncher");
        var incoming = Path.Combine(_dir, "downloaded");
        File.WriteAllText(current, "old version");
        File.WriteAllText(incoming, "new version");

        AutoUpdateService.SwapExecutable(current, incoming);

        Assert.Equal("new version", File.ReadAllText(current));
        Assert.Equal("old version", File.ReadAllText(current + ".old"));
        Assert.False(File.Exists(incoming));
    }

    [Fact]
    public void SwapExecutable_RestoresPreviousBinaryWhenSwapFails()
    {
        var current = Path.Combine(_dir, "KermoLauncher");
        File.WriteAllText(current, "old version");

        Assert.ThrowsAny<IOException>(
            () => AutoUpdateService.SwapExecutable(current, Path.Combine(_dir, "no-such-file")));

        // Without the rollback the install would be left with no executable at all.
        Assert.True(File.Exists(current));
        Assert.Equal("old version", File.ReadAllText(current));
    }

    [Fact]
    public void SwapExecutable_OverwritesLeftoverBackup()
    {
        var current = Path.Combine(_dir, "KermoLauncher");
        var incoming = Path.Combine(_dir, "downloaded");
        File.WriteAllText(current, "version 2");
        File.WriteAllText(current + ".old", "version 1");
        File.WriteAllText(incoming, "version 3");

        AutoUpdateService.SwapExecutable(current, incoming);

        Assert.Equal("version 3", File.ReadAllText(current));
        Assert.Equal("version 2", File.ReadAllText(current + ".old"));
    }

    [Fact]
    public void SwapExecutable_SetsExecutableBitOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var current = Path.Combine(_dir, "KermoLauncher");
        var incoming = Path.Combine(_dir, "downloaded");
        File.WriteAllText(current, "old version");
        File.WriteAllText(incoming, "new version");
        File.SetUnixFileMode(incoming, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        AutoUpdateService.SwapExecutable(current, incoming);

        Assert.True(File.GetUnixFileMode(current).HasFlag(UnixFileMode.UserExecute));
    }
}

public class AutoUpdateDownloadTests : IDisposable
{
    private const string Payload = "contents of the new launcher version";
    private readonly List<string> _paths = [];

    public void Dispose()
    {
        foreach (var path in _paths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + ".part")) File.Delete(path + ".part"); } catch { }
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_ReusesCachedFileWithMatchingChecksum()
    {
        var (service, handler, update) = NewService(Payload);
        var cached = Track(service.GetCachedDownloadPath(update));
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        await File.WriteAllTextAsync(cached, Payload);

        var path = await service.DownloadUpdateAsync(update);

        Assert.Equal(cached, path);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task DownloadUpdateAsync_DiscardsTruncatedCachedFile()
    {
        var (service, handler, update) = NewService(Payload);
        var cached = Track(service.GetCachedDownloadPath(update));
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        // This is what an interrupted download leaves behind; it used to be swapped in as-is.
        await File.WriteAllTextAsync(cached, Payload[..10]);

        var path = await service.DownloadUpdateAsync(update);

        Assert.Equal(1, handler.Requests);
        Assert.Equal(Payload, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DownloadUpdateAsync_ChecksumMismatchLeavesNoUsableFile()
    {
        var (service, _, update) = NewService("something else entirely", checksumOf: Payload);
        var cached = Track(service.GetCachedDownloadPath(update));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadUpdateAsync(update));

        Assert.False(File.Exists(cached));
        Assert.False(File.Exists(cached + ".part"));
    }

    private string Track(string path)
    {
        _paths.Add(path);
        return path;
    }

    private static (AutoUpdateService Service, CountingHandler Handler, UpdateInfo Update) NewService(
        string served, string? checksumOf = null)
    {
        var handler = new CountingHandler(served);
        var service = new AutoUpdateService(
            new HttpClient(handler),
            NullLogger<AutoUpdateService>.Instance,
            "1.0.0",
            "owner",
            "repo");

        var update = new UpdateInfo(
            Version: "1.0.6",
            ReleaseNotes: "",
            DownloadUrl: $"https://example.com/KermoLauncher-{Guid.NewGuid():N}-linux-x64",
            Sha256: Sha256Of(checksumOf ?? served),
            IsMandatory: false);

        return (service, handler, update);
    }

    private static string Sha256Of(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _body;

        public int Requests { get; private set; }

        public CountingHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8)
            });
        }
    }
}
