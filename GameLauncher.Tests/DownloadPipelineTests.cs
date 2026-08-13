namespace GameLauncher.Tests;

using System.Security.Cryptography;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services;
using GameLauncher.Core.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The install pipeline drives a single download executor: pausing keeps partial files for a
/// resume, and the parallel limit is taken from settings on every run.
/// </summary>
public class DownloadPipelineTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "gl-db-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string _installDir = Path.Combine(Path.GetTempPath(), "gl-install-" + Guid.NewGuid().ToString("N"));
    private readonly LocalDbService _db;

    public DownloadPipelineTests()
    {
        _db = new LocalDbService(_dbPath);
        SaveSettings(maxParallel: 2);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_installDir, true); } catch { }
    }

    private void SaveSettings(int maxParallel) =>
        _db.SaveSettingsAsync(new AppSettings
        {
            Nextcloud = new NextcloudConfig("https://example.com/s/abc123", ""),
            InstallFolder = _installDir,
            MaxParallelDownloads = maxParallel
        }).GetAwaiter().GetResult();

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

    private static Game MakeGame(GameManifest manifest) => new(
        Id: "g1",
        Name: "Game 1",
        Version: manifest.Version,
        Description: "",
        Tags: [],
        Dependencies: [],
        ScreenshotUrls: [],
        ManifestUrl: "g1/manifest.json",
        SizeBytes: manifest.TotalBytes);

    [Fact]
    public async Task PauseThenResume_ContinuesPartialFileAndInstalls()
    {
        var (file, content) = MakeFile("data.bin", 512 * 1024);
        var manifest = new GameManifest("1.0.0", file.SizeBytes, [file]);
        var webDav = new ResumableWebDav(manifest, new Dictionary<string, byte[]> { [file.Path] = content })
        {
            StallAfterHalf = true
        };

        using var downloads = new DownloadService(webDav, _db, NullLogger<DownloadService>.Instance);
        using var service = new GameService(downloads, _db, webDav, NullLogger<GameService>.Instance);

        var game = MakeGame(manifest);
        await _db.UpsertGamesAsync([game]);

        var install = service.InstallAsync(game);
        await webDav.HalfWritten.Task.WaitAsync(Timeout);

        await service.PauseInstallAsync(game.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);

        var pausedState = await _db.GetLocalStateAsync(game.Id);
        Assert.Equal(InstallStatus.Paused, pausedState!.Status);

        var partial = Path.Combine(_installDir, ".update", game.Id, file.Path);
        Assert.True(File.Exists(partial), "partial download must survive a pause");
        var partialLength = new FileInfo(partial).Length;
        Assert.InRange(partialLength, 1, file.SizeBytes - 1);

        webDav.StallAfterHalf = false;
        await service.ResumeInstallAsync(game);

        // The resumed transfer asked for the remaining bytes instead of starting over.
        Assert.Equal([0L, partialLength], webDav.RequestedOffsets);

        var state = await _db.GetLocalStateAsync(game.Id);
        Assert.Equal(InstallStatus.Installed, state!.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(state.InstalledPath!, file.Path)));
        Assert.False(Directory.Exists(Path.Combine(_installDir, ".update", game.Id)));
    }

    [Fact]
    public async Task CancelWhilePaused_DiscardsPartialFiles()
    {
        var (file, content) = MakeFile("data.bin", 256 * 1024);
        var manifest = new GameManifest("1.0.0", file.SizeBytes, [file]);
        var webDav = new ResumableWebDav(manifest, new Dictionary<string, byte[]> { [file.Path] = content })
        {
            StallAfterHalf = true
        };

        using var downloads = new DownloadService(webDav, _db, NullLogger<DownloadService>.Instance);
        using var service = new GameService(downloads, _db, webDav, NullLogger<GameService>.Instance);

        var game = MakeGame(manifest);
        await _db.UpsertGamesAsync([game]);

        var install = service.InstallAsync(game);
        await webDav.HalfWritten.Task.WaitAsync(Timeout);
        await service.PauseInstallAsync(game.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);

        await service.CancelInstallAsync(game.Id);

        var state = await _db.GetLocalStateAsync(game.Id);
        Assert.Equal(InstallStatus.NotInstalled, state!.Status);
        Assert.False(Directory.Exists(Path.Combine(_installDir, ".update", game.Id)));
        Assert.Empty(await _db.GetAllDownloadTasksAsync());
    }

    [Fact]
    public async Task CompletedFilesAreNotDownloadedAgain()
    {
        var (file, content) = MakeFile("data.bin", 64 * 1024);
        var manifest = new GameManifest("1.0.0", file.SizeBytes, [file]);
        var webDav = new ResumableWebDav(manifest, new Dictionary<string, byte[]> { [file.Path] = content });

        using var downloads = new DownloadService(webDav, _db, NullLogger<DownloadService>.Instance);
        await _db.UpsertGamesAsync([MakeGame(manifest)]);

        var localPath = Path.Combine(_installDir, "staging", file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllBytesAsync(localPath, content);

        var task = new DownloadTask("t1", "g1", "remote/manifest.json", _installDir,
            file.SizeBytes, 0, DownloadStatus.Queued);
        await _db.UpsertDownloadTaskAsync(task);

        await downloads.DownloadFilesAsync(
            task,
            [new DownloadFileRequest(file.Path, "remote/" + file.Path, localPath, file.SizeBytes)]);

        Assert.Empty(webDav.RequestedOffsets);
        var stored = await _db.GetDownloadTaskAsync("t1");
        Assert.Equal(file.SizeBytes, stored!.DownloadedBytes);
    }

    [Fact]
    public async Task ParallelLimitFollowsSettingsWithoutRestart()
    {
        var files = Enumerable.Range(0, 3)
            .Select(i => MakeFile($"file{i}.bin", 16 * 1024))
            .ToArray();
        var manifest = new GameManifest("1.0.0", files.Sum(f => f.File.SizeBytes), files.Select(f => f.File).ToArray());
        var webDav = new ResumableWebDav(manifest, files.ToDictionary(f => f.File.Path, f => f.Content));

        using var downloads = new DownloadService(webDav, _db, NullLogger<DownloadService>.Instance);
        await _db.UpsertGamesAsync([MakeGame(manifest)]);

        SaveSettings(maxParallel: 1);
        await RunBatchAsync(downloads, webDav, manifest, "run1");
        Assert.Equal(1, webDav.MaxObservedConcurrency);

        // Same service instance, new limit: it must be picked up without recreating anything.
        webDav.Reset();
        SaveSettings(maxParallel: 3);
        webDav.HoldUntilConcurrency = 2;
        await RunBatchAsync(downloads, webDav, manifest, "run2");
        Assert.True(webDav.MaxObservedConcurrency >= 2,
            $"expected concurrent downloads, saw {webDav.MaxObservedConcurrency}");
    }

    private async Task RunBatchAsync(
        IDownloadService downloads,
        ResumableWebDav webDav,
        GameManifest manifest,
        string runId)
    {
        var stagingDir = Path.Combine(_installDir, runId);
        var task = new DownloadTask(runId, "g1", "remote/manifest.json", stagingDir,
            manifest.TotalBytes, 0, DownloadStatus.Queued);
        await _db.UpsertDownloadTaskAsync(task);

        var requests = manifest.Files
            .Select(f => new DownloadFileRequest(f.Path, "remote/" + f.Path, Path.Combine(stagingDir, f.Path), f.SizeBytes))
            .ToArray();

        await downloads.DownloadFilesAsync(task, requests).WaitAsync(Timeout);
        Assert.Equal(manifest.Files.Length, webDav.RequestedOffsets.Count);
    }

    /// <summary>
    /// Appends like the real WebDAV client (Range requests continue a partial file) and can stall
    /// halfway through so a pause has something to interrupt.
    /// </summary>
    private sealed class ResumableWebDav : IWebDavService
    {
        private readonly GameManifest _manifest;
        private readonly Dictionary<string, byte[]> _contents;
        private readonly object _lock = new();
        private readonly List<long> _requestedOffsets = [];
        private int _concurrency;

        public ResumableWebDav(GameManifest manifest, Dictionary<string, byte[]> contents)
        {
            _manifest = manifest;
            _contents = contents;
        }

        /// <summary>When set, a fresh download writes half the file and then waits to be cancelled.</summary>
        public volatile bool StallAfterHalf;

        /// <summary>Transfers wait until this many are running at once (or the wait times out).</summary>
        public int HoldUntilConcurrency;

        public TaskCompletionSource HalfWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxObservedConcurrency { get; private set; }

        public IReadOnlyList<long> RequestedOffsets
        {
            get { lock (_lock) return _requestedOffsets.ToArray(); }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _requestedOffsets.Clear();
                MaxObservedConcurrency = 0;
            }
        }

        public async Task DownloadFileAsync(string remoteUrl, string localPath, string taskId, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            var file = _manifest.Files.First(f => remoteUrl.EndsWith(f.Path, StringComparison.Ordinal));
            var content = _contents[file.Path];

            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var existing = File.Exists(localPath) ? new FileInfo(localPath).Length : 0;

            lock (_lock)
            {
                _requestedOffsets.Add(existing);
                _concurrency++;
                MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, _concurrency);
            }

            try
            {
                await WaitForPeersAsync(ct);

                await using var fs = new FileStream(localPath, FileMode.Append, FileAccess.Write, FileShare.None, 8192, true);
                var written = existing;
                var stopAt = StallAfterHalf ? content.Length / 2 : content.Length;

                while (written < stopAt)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = (int)Math.Min(8192, stopAt - written);
                    await fs.WriteAsync(content.AsMemory((int)written, chunk), ct);
                    written += chunk;
                    progress?.Report(new DownloadProgress(taskId, written, content.Length, 1024 * 1024, null));
                }

                await fs.FlushAsync(ct);

                if (StallAfterHalf)
                {
                    HalfWritten.TrySetResult();
                    await Task.Delay(System.Threading.Timeout.Infinite, ct);
                }
            }
            finally
            {
                lock (_lock) _concurrency--;
            }
        }

        private async Task WaitForPeersAsync(CancellationToken ct)
        {
            if (HoldUntilConcurrency <= 1) return;

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock)
                {
                    if (_concurrency >= HoldUntilConcurrency) return;
                }
                await Task.Delay(10, ct);
            }
        }

        public Task<GameManifest> DownloadManifestAsync(string manifestUrl, CancellationToken ct = default, string? username = null, string? password = null)
            => Task.FromResult(_manifest);

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
