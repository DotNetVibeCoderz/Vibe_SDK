using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PollenRobotics.Net.Core.Diagnostics;

/// <summary>Severity of a <see cref="RobotLogEntry"/>.</summary>
public enum RobotLogLevel
{
    /// <summary>Fine-grained tracing.</summary>
    Debug,

    /// <summary>Normal operation.</summary>
    Information,

    /// <summary>Something recoverable that the operator should know about.</summary>
    Warning,

    /// <summary>An operation failed.</summary>
    Error,
}

/// <summary>One line in the log panel.</summary>
/// <param name="Timestamp">When the line was produced.</param>
/// <param name="Level">Severity.</param>
/// <param name="Source">Subsystem that produced it, e.g. <c>reachy-mini</c> or <c>simulator</c>.</param>
/// <param name="Message">The text.</param>
public readonly record struct RobotLogEntry(DateTimeOffset Timestamp, RobotLogLevel Level, string Source, string Message)
{
    /// <summary>Renders the entry the way the desktop log panels display it.</summary>
    public override string ToString() =>
        $"{Timestamp:HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant()[..4]}] {Source}: {Message}";
}

/// <summary>
/// A bounded, thread-safe ring of log entries with change notification.
/// </summary>
/// <remarks>
/// <para>
/// The desktop apps have no console anyone will ever look at, so a subsystem failure is otherwise
/// completely silent. Everything - transports, the simulation engine, the wizard build pipeline,
/// JavaScript errors bubbled out of the 3D viewport - funnels into one of these.
/// </para>
/// <para>
/// The ring is capped because a 50 Hz transport logging one line per tick fills a list faster than
/// anyone can read it, and an unbounded log panel is a memory leak with a UI.
/// </para>
/// </remarks>
public sealed class RobotLogSink(int capacity = 2000)
{
    private readonly ConcurrentQueue<RobotLogEntry> _entries = new();
    private readonly int _capacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");

    /// <summary>Raised on the thread that logged the entry. Handlers must marshal to the UI themselves.</summary>
    public event Action<RobotLogEntry>? EntryWritten;

    /// <summary>Number of entries currently retained.</summary>
    public int Count => _entries.Count;

    /// <summary>Appends an entry, evicting the oldest once the ring is full.</summary>
    public void Write(RobotLogLevel level, string source, string message)
    {
        var entry = new RobotLogEntry(DateTimeOffset.Now, level, source, message);
        _entries.Enqueue(entry);

        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
            // Evicting down to capacity. TryDequeue failing means another thread drained it first.
        }

        EntryWritten?.Invoke(entry);
    }

    /// <summary>Appends a debug entry.</summary>
    public void Debug(string source, string message) => Write(RobotLogLevel.Debug, source, message);

    /// <summary>Appends an informational entry.</summary>
    public void Info(string source, string message) => Write(RobotLogLevel.Information, source, message);

    /// <summary>Appends a warning.</summary>
    public void Warn(string source, string message) => Write(RobotLogLevel.Warning, source, message);

    /// <summary>Appends an error.</summary>
    public void Error(string source, string message) => Write(RobotLogLevel.Error, source, message);

    /// <summary>A snapshot of the retained entries, oldest first.</summary>
    public IReadOnlyList<RobotLogEntry> Snapshot() => [.. _entries];

    /// <summary>Drops every retained entry.</summary>
    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
            // Draining.
        }
    }

    /// <summary>Wraps the sink as an <see cref="ILoggerProvider"/> so SDK logging lands in the panel too.</summary>
    public ILoggerProvider AsLoggerProvider() => new SinkLoggerProvider(this);

    private sealed class SinkLoggerProvider(RobotLogSink sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new SinkLogger(sink, ShortenCategory(categoryName));

        public void Dispose()
        {
            // The sink outlives the provider; nothing to release.
        }

        // "PollenRobotics.Net.ReachyMini.ReachyMiniClient" is too wide for a log panel column.
        private static string ShortenCategory(string categoryName)
        {
            int last = categoryName.LastIndexOf('.');
            return last >= 0 && last < categoryName.Length - 1 ? categoryName[(last + 1)..] : categoryName;
        }
    }

    private sealed class SinkLogger(RobotLogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message} ({exception.GetType().Name}: {exception.Message})";
            }

            sink.Write(Map(logLevel), category, message);
        }

        private static RobotLogLevel Map(LogLevel level) => level switch
        {
            LogLevel.Trace or LogLevel.Debug => RobotLogLevel.Debug,
            LogLevel.Information => RobotLogLevel.Information,
            LogLevel.Warning => RobotLogLevel.Warning,
            _ => RobotLogLevel.Error,
        };
    }
}
