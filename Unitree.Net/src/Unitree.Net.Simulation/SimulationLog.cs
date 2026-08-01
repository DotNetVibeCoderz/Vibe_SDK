using System.Collections.Concurrent;

namespace Unitree.Net.Simulation;

/// <summary>
/// Severity of a simulator log entry.
/// </summary>
public enum SimulationLogLevel
{
    /// <summary>Routine detail — publish counts, tick statistics.</summary>
    Trace,

    /// <summary>Normal lifecycle: started, stopped, model changed.</summary>
    Info,

    /// <summary>Something worth noticing that did not stop the run.</summary>
    Warning,

    /// <summary>The run failed or a message could not be published.</summary>
    Error,
}

/// <summary>
/// One line in the simulator's log.
/// </summary>
/// <param name="Timestamp">When it happened, in local time.</param>
/// <param name="Level">How much it matters.</param>
/// <param name="Source">Which subsystem emitted it, e.g. "transport" or "loop".</param>
/// <param name="Message">The text.</param>
public readonly record struct SimulationLogEntry(
    DateTimeOffset Timestamp,
    SimulationLogLevel Level,
    string Source,
    string Message);

/// <summary>
/// A bounded, thread-safe log the simulator writes to and the UI reads from.
/// </summary>
/// <remarks>
/// The buffer is bounded because the simulator can emit from a 500 Hz loop: an unbounded log is a
/// memory leak with a UI attached to it. Oldest entries are dropped, and <see cref="DroppedCount"/>
/// records how many, so the panel can say so rather than silently showing a partial history.
/// </remarks>
public sealed class SimulationLog
{
    private readonly ConcurrentQueue<SimulationLogEntry> _entries = new();
    private readonly int _capacity;
    private long _droppedCount;

    /// <summary>Creates a log holding at most <paramref name="capacity"/> entries.</summary>
    /// <param name="capacity">Maximum retained entries.</param>
    public SimulationLog(int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>Raised on the thread that wrote the entry.</summary>
    public event EventHandler<SimulationLogEntry>? EntryWritten;

    /// <summary>How many entries have been dropped to stay within capacity.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>Writes an entry.</summary>
    /// <param name="level">How much it matters.</param>
    /// <param name="source">Which subsystem emitted it.</param>
    /// <param name="message">The text.</param>
    public void Write(SimulationLogLevel level, string source, string message)
    {
        var entry = new SimulationLogEntry(DateTimeOffset.Now, level, source, message);
        _entries.Enqueue(entry);

        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
            Interlocked.Increment(ref _droppedCount);
        }

        EntryWritten?.Invoke(this, entry);
    }

    /// <summary>Writes an informational entry.</summary>
    /// <param name="source">Which subsystem emitted it.</param>
    /// <param name="message">The text.</param>
    public void Info(string source, string message) => Write(SimulationLogLevel.Info, source, message);

    /// <summary>Writes a warning.</summary>
    /// <param name="source">Which subsystem emitted it.</param>
    /// <param name="message">The text.</param>
    public void Warning(string source, string message) => Write(SimulationLogLevel.Warning, source, message);

    /// <summary>Writes an error.</summary>
    /// <param name="source">Which subsystem emitted it.</param>
    /// <param name="message">The text.</param>
    public void Error(string source, string message) => Write(SimulationLogLevel.Error, source, message);

    /// <summary>Takes a snapshot of the retained entries, oldest first.</summary>
    public IReadOnlyList<SimulationLogEntry> Snapshot() => [.. _entries];

    /// <summary>Discards every retained entry and resets the dropped counter.</summary>
    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
            // Drain.
        }

        Interlocked.Exchange(ref _droppedCount, 0);
    }
}
