using System.Text;

namespace Muthur.Server.Infrastructure;

/// <summary>Minimal append-only file logger. The server runs detached, so the console is not an option.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long RollAtBytes = 10 * 1024 * 1024;
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    public FileLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && new FileInfo(path).Length > RollAtBytes)
            File.Move(path, path + ".1", overwrite: true);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true,
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() => _writer.Dispose();

    private void Write(string line)
    {
        lock (_gate) _writer.WriteLine(line);
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTimeOffset.UtcNow:O} {logLevel,-11} {category}: {formatter(state, exception)}";
            provider.Write(exception is null ? line : line + Environment.NewLine + exception);
        }
    }
}
