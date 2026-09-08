using System.Text;

namespace MiningFleet.Agent;

/// <summary>
/// Writes the agent's log to a file beside the binary, and never throws while doing it.
///
/// This exists because the Windows event log is not dependable on a mining node. On
/// mks68i7rtx the Event Log service answers "RPC server unavailable", and .NET's EventLog
/// provider turns that into an exception out of ILogger.Log - so the agent died while trying
/// to record a warning, twice, and both times the node became unreachable until somebody
/// visited it in person. A logger that can take the process down is worse than no logger.
///
/// Deliberately minimal: one file, size-capped, one previous generation kept. Anything
/// richer would mean a logging package on a binary that has to be copied to nodes.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();

    public FileLoggerProvider(string path) => _path = path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        // Every failure is swallowed on purpose: see the class remarks.
        try
        {
            lock (_gate)
            {
                Roll();
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private void Roll()
    {
        try
        {
            var file = new FileInfo(_path);
            if (!file.Exists || file.Length < MaxBytes) return;

            var previous = _path + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(_path, previous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            // Namespaces make every line unreadable at this width; the type name is enough.
            _category = category[(category.LastIndexOf('.') + 1)..];
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {Short(logLevel)} {_category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;

            _provider.Write(line);
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "trc",
            LogLevel.Debug => "dbg",
            LogLevel.Information => "inf",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "---",
        };
    }
}
