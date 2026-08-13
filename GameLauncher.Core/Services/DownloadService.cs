namespace GameLauncher.Core.Services;

using System.Collections.Concurrent;
using System.Threading.Channels;
using GameLauncher.Core.Models;
using GameLauncher.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// The only component that transfers game files: it owns parallelism, progress aggregation and
/// cancellation, while GameService drives the install pipeline (manifest, verify, finalize) around it.
/// </summary>
public class DownloadService : IDownloadService, IDisposable
{
    private const int DefaultMaxParallel = 2;

    private readonly IWebDavService _webDav;
    private readonly ILocalDbService _db;
    private readonly ILogger<DownloadService> _logger;
    private readonly Dictionary<string, CancellationTokenSource> _activeDownloads = new(StringComparer.Ordinal);
    private readonly object _activeLock = new();
    private bool _disposed;

    public event Action<DownloadTask>? OnTaskUpdated;
    public event Action<DownloadProgress>? OnProgress;

    public DownloadService(
        IWebDavService webDav,
        ILocalDbService db,
        ILogger<DownloadService> logger)
    {
        _webDav = webDav;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Downloads every requested file into place, reporting one aggregated progress stream for the
    /// task. Files already complete on disk are skipped and partial ones are resumed, so a paused
    /// or interrupted install continues instead of starting over.
    /// </summary>
    public async Task DownloadFilesAsync(
        DownloadTask task,
        IReadOnlyList<DownloadFileRequest> files,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_activeLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DownloadService));
            _activeDownloads[task.Id] = cts;
        }

        try
        {
            var running = task with
            {
                Status = DownloadStatus.Downloading,
                InstallStage = InstallStage.Downloading
            };
            await _db.UpsertDownloadTaskAsync(running);
            OnTaskUpdated?.Invoke(running);

            // Read per run, so changing the limit in Settings applies to the next download
            // instead of needing a restart.
            var settings = await _db.GetSettingsAsync();
            var maxParallel = settings.MaxParallelDownloads > 0
                ? settings.MaxParallelDownloads
                : DefaultMaxParallel;

            var completedBytes = 0L;

            // Bytes of in-flight files are tracked separately so the total stays correct
            // no matter what order the parallel downloads report progress in.
            var inFlight = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
            long CurrentBytes() => Volatile.Read(ref completedBytes) + inFlight.Values.Sum();

            await using var pump = new ProgressPump(_db, running, OnTaskUpdated, OnProgress);

            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = cts.Token },
                async (file, fileToken) =>
                {
                    var onDisk = PrepareLocalFile(file);
                    if (onDisk == file.SizeBytes && file.SizeBytes > 0)
                    {
                        Interlocked.Add(ref completedBytes, file.SizeBytes);
                        pump.Report(CurrentBytes());
                        return;
                    }

                    inFlight[file.Key] = onDisk;
                    var progress = new DelegatedProgress(p =>
                    {
                        inFlight[file.Key] = p.BytesReceived;
                        pump.Report(CurrentBytes());
                    });

                    await _webDav.DownloadFileAsync(file.RemoteUrl, file.LocalPath, task.Id, progress, fileToken);

                    // Order matters: drop from in-flight before adding to the total, so the
                    // reported sum can dip for a moment but never exceed the real one.
                    inFlight.TryRemove(file.Key, out _);
                    Interlocked.Add(ref completedBytes, file.SizeBytes);
                    pump.Report(CurrentBytes());
                });

            await pump.FlushAsync(task.TotalBytes);
        }
        finally
        {
            lock (_activeLock)
            {
                _activeDownloads.Remove(task.Id);
            }
        }
    }

    /// <summary>
    /// Bytes already on disk for this file. A file longer than the manifest says is treated as
    /// garbage from an aborted write and removed, so the download starts from a known state.
    /// </summary>
    private static long PrepareLocalFile(DownloadFileRequest file)
    {
        var info = new FileInfo(file.LocalPath);
        if (!info.Exists) return 0;

        if (info.Length > file.SizeBytes)
        {
            try
            {
                info.Delete();
            }
            catch (IOException)
            {
                // The download will fail below with a clearer error than we could raise here.
            }
            return 0;
        }

        return info.Length;
    }

    public async Task UpdateInstallStageAsync(string taskId, InstallStage stage)
    {
        var task = await _db.GetDownloadTaskAsync(taskId);
        if (task != null && task.InstallStage != stage)
        {
            var updatedTask = task with { InstallStage = stage };
            await _db.UpsertDownloadTaskAsync(updatedTask);
            OnTaskUpdated?.Invoke(updatedTask);
        }
    }

    public async Task<IReadOnlyList<DownloadTask>> GetAllTasksAsync()
    {
        return await _db.GetAllDownloadTasksAsync();
    }

    public async Task<DownloadTask?> GetTaskAsync(string taskId)
    {
        return await _db.GetDownloadTaskAsync(taskId);
    }

    public void Dispose()
    {
        CancellationTokenSource[] active;
        lock (_activeLock)
        {
            if (_disposed) return;
            _disposed = true;
            active = _activeDownloads.Values.ToArray();
            _activeDownloads.Clear();
        }

        foreach (var cts in active)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The download already finished.
            }
        }

        _logger.LogInformation("Download service disposed, {Count} transfer(s) cancelled", active.Length);
    }

    private sealed class DelegatedProgress : IProgress<DownloadProgress>
    {
        private readonly Action<DownloadProgress> _handler;

        public DelegatedProgress(Action<DownloadProgress> handler)
        {
            _handler = handler;
        }

        public void Report(DownloadProgress value)
        {
            _handler(value);
        }
    }

    /// <summary>
    /// Collects progress reports from parallel downloads and handles them in a single consumer,
    /// so events are raised from one thread and database writes are throttled to one per 500 ms
    /// and actually awaited instead of being fired and forgotten.
    /// </summary>
    private sealed class ProgressPump : IAsyncDisposable
    {
        private static readonly TimeSpan PersistInterval = TimeSpan.FromMilliseconds(500);

        private readonly ILocalDbService _db;
        private readonly DownloadTask _template;
        private readonly Action<DownloadTask>? _onTaskUpdated;
        private readonly Action<DownloadProgress>? _onProgress;
        private readonly Channel<long> _channel =
            Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private readonly Task _consumer;
        private DateTime _lastPersistedAt = DateTime.MinValue;

        public ProgressPump(
            ILocalDbService db,
            DownloadTask template,
            Action<DownloadTask>? onTaskUpdated,
            Action<DownloadProgress>? onProgress)
        {
            _db = db;
            _template = template;
            _onTaskUpdated = onTaskUpdated;
            _onProgress = onProgress;
            _consumer = Task.Run(ConsumeAsync);
        }

        public void Report(long bytes) => _channel.Writer.TryWrite(bytes);

        private async Task ConsumeAsync()
        {
            await foreach (var bytes in _channel.Reader.ReadAllAsync())
            {
                var elapsed = DateTime.UtcNow - _startedAt;
                var speed = elapsed.TotalSeconds > 0 ? bytes / elapsed.TotalSeconds : 0;
                TimeSpan? remaining = speed > 0 && _template.TotalBytes > bytes
                    ? TimeSpan.FromSeconds((_template.TotalBytes - bytes) / speed)
                    : null;

                _onProgress?.Invoke(new DownloadProgress(_template.Id, bytes, _template.TotalBytes, speed, remaining));

                if (DateTime.UtcNow - _lastPersistedAt >= PersistInterval)
                {
                    _lastPersistedAt = DateTime.UtcNow;
                    await PersistAsync(bytes);
                }
            }
        }

        private async Task PersistAsync(long bytes)
        {
            var snapshot = _template with { DownloadedBytes = bytes };
            await _db.UpsertDownloadTaskAsync(snapshot);
            _onTaskUpdated?.Invoke(snapshot);
        }

        /// <summary>Closes the channel and guarantees one final event with the total byte count.</summary>
        public async Task FlushAsync(long finalBytes)
        {
            _channel.Writer.TryComplete();
            await _consumer;
            await PersistAsync(finalBytes);
        }

        public async ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            try
            {
                await _consumer;
            }
            catch (Exception)
            {
                // Error path: the real install exception propagates from FlushAsync.
            }
        }
    }
}
