using System.Text;
using Microsoft.Extensions.Logging;

namespace GameLauncher.UI.Services;

/// <summary>
/// Logger plikowy trzymający jeden otwarty strumień. Wcześniej każda linia otwierała
/// i zamykała plik pod globalną blokadą, co przy pobieraniu potrafiło zdławić wątki.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private readonly string _path;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private bool _disposed;

    public FileLoggerProvider(string path)
    {
        _path = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RotateIfNeeded();
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
        catch
        {
            // Logowanie nie może wywrócić aplikacji.
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // Nic sensownego nie da się już zrobić.
            }
            _writer = null;
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxFileBytes) return;

        var archive = _path + ".1";
        if (File.Exists(archive)) File.Delete(archive);
        File.Move(_path, archive);
    }

    internal void Write(string message)
    {
        lock (_lock)
        {
            if (_disposed || _writer == null) return;
            try
            {
                _writer.WriteLine(message);
            }
            catch
            {
                // Logowanie nie może wywrócić aplikacji.
            }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception != null)
            {
                message += " | EX: " + exception;
            }
            _provider.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] [{_category}] {message}");
        }
    }
}
