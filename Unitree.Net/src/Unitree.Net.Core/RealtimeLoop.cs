using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Unitree.Net.Core;

/// <summary>
/// The callback invoked on every control tick.
/// </summary>
/// <param name="context">Timing information for the current tick.</param>
/// <remarks>
/// The callback runs on the loop's dedicated thread and must not block, allocate, or await.
/// Anything slower than the tick period shows up as jitter in <see cref="RealtimeLoop.Statistics"/>.
/// </remarks>
public delegate void ControlTickCallback(in ControlTickContext context);

/// <summary>
/// Timing state handed to a control tick.
/// </summary>
/// <param name="TickIndex">Monotonically increasing tick counter, starting at zero.</param>
/// <param name="ElapsedSeconds">Seconds since the loop started.</param>
/// <param name="DeltaSeconds">Seconds since the previous tick actually ran.</param>
public readonly record struct ControlTickContext(long TickIndex, double ElapsedSeconds, double DeltaSeconds);

/// <summary>
/// Rolling timing statistics for a <see cref="RealtimeLoop"/>.
/// </summary>
/// <param name="TickCount">Total ticks executed.</param>
/// <param name="OverrunCount">Ticks whose callback took longer than the period.</param>
/// <param name="MissedDeadlineCount">Ticks skipped entirely because the loop fell more than one period behind.</param>
/// <param name="MeanJitterMicroseconds">Mean absolute deviation from the nominal period.</param>
/// <param name="MaxJitterMicroseconds">Worst observed deviation from the nominal period.</param>
public readonly record struct LoopStatistics(
    long TickCount,
    long OverrunCount,
    long MissedDeadlineCount,
    double MeanJitterMicroseconds,
    double MaxJitterMicroseconds);

/// <summary>
/// A fixed-frequency scheduler for real-time control loops.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PeriodicTimer"/> and <c>Task.Delay</c> are driven by the OS timer, whose resolution is
/// ~15.6 ms on stock Windows and ~1 ms on Linux. Neither is usable for the 500 Hz (2 ms) loop the robot
/// expects. This class instead runs a dedicated thread and hybrid-waits: it sleeps while the remaining
/// time is comfortably coarse, then spins for the final fraction of a millisecond.
/// </para>
/// <para>
/// Spinning burns a core. That is the intended trade for a control loop; do not use this class for
/// anything that does not need sub-millisecond accuracy.
/// </para>
/// </remarks>
public sealed class RealtimeLoop : IDisposable
{
    private readonly double _periodSeconds;
    private readonly long _periodTicks;
    private readonly ControlTickCallback _callback;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopSource = new();
    private readonly Thread _thread;

    private long _tickCount;
    private long _overrunCount;
    private long _missedDeadlineCount;
    private double _jitterSumMicroseconds;
    private double _maxJitterMicroseconds;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>
    /// Creates a loop that invokes <paramref name="callback"/> at <paramref name="frequencyHz"/>.
    /// </summary>
    /// <param name="frequencyHz">Target frequency in hertz. Must be positive.</param>
    /// <param name="callback">The per-tick callback.</param>
    /// <param name="logger">Logger for lifecycle and overrun reporting.</param>
    /// <param name="highPriority">
    /// Whether to raise the loop thread's priority. Leave enabled unless the host is shared with
    /// latency-sensitive work that must not be preempted.
    /// </param>
    public RealtimeLoop(
        int frequencyHz,
        ControlTickCallback callback,
        ILogger? logger = null,
        bool highPriority = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frequencyHz, 0);
        ArgumentNullException.ThrowIfNull(callback);

        _periodSeconds = 1.0 / frequencyHz;
        _periodTicks = (long)(Stopwatch.Frequency / (double)frequencyHz);
        _callback = callback;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        FrequencyHz = frequencyHz;

        _thread = new Thread(Run)
        {
            Name = $"unitree-control-{frequencyHz}hz",
            IsBackground = true,
            Priority = highPriority ? ThreadPriority.Highest : ThreadPriority.Normal,
        };
    }

    /// <summary>The configured tick frequency in hertz.</summary>
    public int FrequencyHz { get; }

    /// <summary>The nominal tick period.</summary>
    public TimeSpan Period => TimeSpan.FromSeconds(_periodSeconds);

    /// <summary>Whether the loop thread is currently running.</summary>
    public bool IsRunning => _running;

    /// <summary>Gets a snapshot of timing statistics.</summary>
    public LoopStatistics Statistics
    {
        get
        {
            long ticks = Interlocked.Read(ref _tickCount);
            return new LoopStatistics(
                ticks,
                Interlocked.Read(ref _overrunCount),
                Interlocked.Read(ref _missedDeadlineCount),
                ticks == 0 ? 0 : _jitterSumMicroseconds / ticks,
                _maxJitterMicroseconds);
        }
    }

    /// <summary>Starts the loop. Idempotent.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_running)
        {
            return;
        }

        _running = true;
        _thread.Start();
        _logger.LogInformation("Control loop started at {FrequencyHz} Hz.", FrequencyHz);
    }

    /// <summary>
    /// Signals the loop to stop and waits for the thread to drain.
    /// </summary>
    /// <param name="timeout">How long to wait for the thread to exit. Defaults to two seconds.</param>
    /// <returns><see langword="true"/> if the loop thread exited within the timeout.</returns>
    public bool Stop(TimeSpan? timeout = null)
    {
        if (!_running)
        {
            return true;
        }

        _stopSource.Cancel();
        bool joined = _thread.Join(timeout ?? TimeSpan.FromSeconds(2));
        _running = false;

        LoopStatistics stats = Statistics;
        _logger.LogInformation(
            "Control loop stopped after {TickCount} ticks; {OverrunCount} overruns, {MissedCount} missed deadlines, mean jitter {MeanJitter:0.0} µs, max {MaxJitter:0.0} µs.",
            stats.TickCount,
            stats.OverrunCount,
            stats.MissedDeadlineCount,
            stats.MeanJitterMicroseconds,
            stats.MaxJitterMicroseconds);

        return joined;
    }

    private void Run()
    {
        CancellationToken token = _stopSource.Token;
        long startTimestamp = Stopwatch.GetTimestamp();
        long nextDeadline = startTimestamp + _periodTicks;
        long previousTickTimestamp = startTimestamp;
        long index = 0;

        while (!token.IsCancellationRequested)
        {
            WaitUntil(nextDeadline, token);

            if (token.IsCancellationRequested)
            {
                break;
            }

            long now = Stopwatch.GetTimestamp();
            double jitterMicroseconds = TicksToMicroseconds(now - nextDeadline);
            RecordJitter(jitterMicroseconds);

            var context = new ControlTickContext(
                index,
                TicksToSeconds(now - startTimestamp),
                TicksToSeconds(now - previousTickTimestamp));

            previousTickTimestamp = now;

            try
            {
                _callback(in context);
            }
            catch (Exception ex)
            {
                // A throwing callback must not kill the loop: on a robot, losing the control thread is
                // worse than one bad tick. The loop keeps its cadence and the failure is surfaced.
                _logger.LogError(ex, "Control tick {TickIndex} threw; continuing.", index);
            }

            long afterCallback = Stopwatch.GetTimestamp();
            if (afterCallback > nextDeadline + _periodTicks)
            {
                Interlocked.Increment(ref _overrunCount);
            }

            index++;
            Interlocked.Increment(ref _tickCount);
            nextDeadline += _periodTicks;

            // If the callback blew far past its budget, resynchronise rather than sprint to catch up —
            // a burst of back-to-back ticks would push a jerky command sequence at the motors.
            if (afterCallback > nextDeadline)
            {
                long behind = afterCallback - nextDeadline;
                long skipped = (behind / _periodTicks) + 1;
                Interlocked.Add(ref _missedDeadlineCount, skipped);
                nextDeadline += skipped * _periodTicks;
            }
        }

        _running = false;
    }

    /// <summary>
    /// Hybrid wait: coarse sleep while there is slack, then spin for the last stretch.
    /// </summary>
    private static void WaitUntil(long deadlineTimestamp, CancellationToken token)
    {
        // Sleep only while there is more slack than one OS scheduler quantum.
        //
        // Thread.Sleep is quantised to the system timer: ~15.6 ms on stock Windows, ~1 ms on Linux.
        // Sleeping for a duration shorter than that quantum does not sleep less — it sleeps a whole
        // quantum. A 200 Hz loop asking for a 4 ms sleep therefore gets ~15.6 ms and runs at a third of
        // its requested rate, which is exactly the failure this threshold exists to prevent.
        //
        // Below the threshold we spin. That burns a core, which is the deliberate trade for a control
        // loop: at 500 Hz the 2 ms period is entirely inside the quantum, so the loop spins throughout.
        long sleepThresholdTicks = Stopwatch.Frequency / 50; // 20 ms — comfortably over the Windows quantum.

        while (true)
        {
            long remaining = deadlineTimestamp - Stopwatch.GetTimestamp();

            if (remaining <= 0 || token.IsCancellationRequested)
            {
                return;
            }

            if (remaining > sleepThresholdTicks)
            {
                int sleepMs = (int)(TicksToSeconds(remaining - sleepThresholdTicks) * 1000);

                if (sleepMs > 0)
                {
                    Thread.Sleep(sleepMs);
                    continue;
                }
            }

            // Yield to any thread ready on this core rather than spinning blind. SpinWait escalates to a
            // yield internally, which keeps a single-core host from starving.
            Thread.SpinWait(50);
        }
    }

    private void RecordJitter(double jitterMicroseconds)
    {
        double magnitude = Math.Abs(jitterMicroseconds);
        _jitterSumMicroseconds += magnitude;

        if (magnitude > _maxJitterMicroseconds)
        {
            _maxJitterMicroseconds = magnitude;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double TicksToSeconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double TicksToMicroseconds(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _stopSource.Dispose();
    }
}
