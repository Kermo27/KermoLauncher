namespace GameLauncher.Tests;

using System.Security.Cryptography;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services;
using GameLauncher.Core.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class GameServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "gl-db-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string _installDir = Path.Combine(Path.GetTempPath(), "gl-install-" + Guid.NewGuid().ToString("N"));
    private readonly LocalDbService _db;

    public GameServiceTests()
    {
        _db = new LocalDbService(_dbPath);
        _db.SaveSettingsAsync(new AppSettings
        {
            Nextcloud = new NextcloudConfig("https://example.com/s/abc123", ""),
            InstallFolder = _installDir
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_installDir, true); } catch { }
    }

    private static (GameFile File, byte[] Content) MakeFile(string path, int size)
    {
        var content = new byte[size];
        for (var i = 0; i < size; i++)
        {
            content[i] = (byte)(i % 251);
        }
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return (new GameFile(path, size, sha), content);
    }

    [Fact]
    public async Task InstallAsync_ReportsProgressAndCorrectTotals()
    {
        var (f1, c1) = MakeFile("data.bin", 256 * 1024);
        var (f2, c2) = MakeFile("assets/texture.bin", 512 * 1024);
        var files = new[] { f1, f2 };
        var manifest = new GameManifest("1.0.0", files.Sum(f => f.SizeBytes), files);
        var contents = new Dictionary<string, byte[]>
        {
            [f1.Path] = c1,
            [f2.Path] = c2
        };

        var service = new GameService(
            new FakeDownloadService(),
            _db,
            new FakeWebDav(manifest, contents),
            NullLogger<GameService>.Instance);

        var progressEvents = new List<DownloadProgress>();
        var taskEvents = new List<DownloadTask>();
        service.OnProgress += p => progressEvents.Add(p);
        service.OnTaskUpdated += t => taskEvents.Add(t);

        var game = new Game(
            Id: "g1",
            Name: "Game 1",
            Version: "1.0.0",
            Description: "",
            Tags: [],
            Dependencies: [],
            ScreenshotUrls: [],
            ManifestUrl: "g1/manifest.json",
            SizeBytes: manifest.TotalBytes);

        await _db.UpsertGamesAsync([game]);

        await service.InstallAsync(game);

        var total = manifest.TotalBytes;

        // Progress events fire during the download and always carry the real total
        Assert.Contains(progressEvents, p => p.TotalBytes == total && p.BytesReceived > 0 && p.BytesReceived < total);
        Assert.All(progressEvents, p => Assert.Equal(total, p.TotalBytes));

        // Task events during the Downloading stage carry correct totals (was 0 in the old bug)
        var during = taskEvents
            .Where(t => t.Status == DownloadStatus.Downloading && t.InstallStage == InstallStage.Downloading)
            .ToArray();
        Assert.NotEmpty(during);
        Assert.All(during, t => Assert.Equal(total, t.TotalBytes));
        Assert.All(during, t => Assert.InRange(t.DownloadedBytes, 0, total));
        Assert.Equal(total, during[^1].DownloadedBytes);

        // Final task event reaches exactly the total bytes
        Assert.Equal(total, taskEvents[^1].DownloadedBytes);

        // Task is cleaned up after completion
        Assert.Empty(await _db.GetAllDownloadTasksAsync());

        // Game is installed with files in place
        var state = await _db.GetLocalStateAsync("g1");
        Assert.NotNull(state);
        Assert.Equal(InstallStatus.Installed, state.Status);
        Assert.True(File.Exists(Path.Combine(state.InstalledPath!, "data.bin")));
        Assert.True(File.Exists(Path.Combine(state.InstalledPath!, "assets", "texture.bin")));
    }

    private sealed class FakeDownloadService : IDownloadService
    {
        public event Action<DownloadTask>? OnTaskUpdated { add { } remove { } }
        public event Action<DownloadProgress>? OnProgress { add { } remove { } }

        public Task PauseAsync(string taskId) => Task.CompletedTask;
        public Task ResumeAsync(string taskId) => Task.CompletedTask;
        public Task CancelAsync(string taskId) => Task.CompletedTask;
        public Task RemoveAsync(string taskId) => Task.CompletedTask;
        public Task UpdateInstallStageAsync(string taskId, InstallStage stage) => Task.CompletedTask;
        public Task<IReadOnlyList<DownloadTask>> GetAllTasksAsync() => Task.FromResult<IReadOnlyList<DownloadTask>>([]);
        public Task<DownloadTask?> GetTaskAsync(string taskId) => Task.FromResult<DownloadTask?>(null);
    }

    private sealed class FakeWebDav : IWebDavService
    {
        private readonly GameManifest _manifest;
        private readonly Dictionary<string, byte[]> _contents;

        public FakeWebDav(GameManifest manifest, Dictionary<string, byte[]> contents)
        {
            _manifest = manifest;
            _contents = contents;
        }

        public Task<GameManifest> DownloadManifestAsync(string manifestUrl, CancellationToken ct = default, string? username = null, string? password = null)
            => Task.FromResult(_manifest);

        public async Task DownloadFileAsync(string remoteUrl, string localPath, string taskId, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            var file = _manifest.Files.First(f => remoteUrl.Contains(f.Path));
            var content = _contents[file.Path];

            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var chunk = Math.Max(1, content.Length / 10);
            var bytes = 0L;

            await using (var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                while (bytes < content.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = (int)Math.Min(chunk, content.Length - bytes);
                    await fs.WriteAsync(content.AsMemory((int)bytes, n), ct);
                    bytes += n;
                    progress?.Report(new DownloadProgress(taskId, bytes, content.Length, 1024 * 1024, null));
                    await Task.Delay(1, ct);
                }
            }
        }

        public Task<Game[]> DownloadMetadataAsync(NextcloudConfig config, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<Game>());

        public Task<NextcloudConfig> ResolveConfigAsync(NextcloudConfig config, CancellationToken ct = default)
            => Task.FromResult(config);

        public Task CreateDirectoryAsync(string remoteDir, string username, string password, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UploadFileAsync(string remoteUrl, string localPath, string username, string password, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
