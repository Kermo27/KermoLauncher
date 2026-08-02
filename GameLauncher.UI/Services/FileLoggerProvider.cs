using Microsoft.Extensions.Logging;

namespace GameLauncher.UI.Services;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _lock = new();

    public FileLoggerProvider(string path)
    {
        _path = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch
        {
            // Logging must never crash the app
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    internal void Write(string message)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_path, message + Environment.NewLine);
            }
            catch
            {
                // Logging must never crash the app
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
