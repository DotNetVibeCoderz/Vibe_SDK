using System.Diagnostics;

namespace PollenRobotics.Net.Core.Realtime;

/// <summary>
/// A fixed-rate loop accurate enough to drive a servo bus.
/// </summary>
/// <remarks>
/// <para>
/// Neither <see cref="Task.Delay(TimeSpan)"/> nor <see cref="PeriodicTimer"/> is usable here. Both
/// are quantised to the OS scheduler tick, which is about 15.6 ms on Windows by default - three
/// quarters of a 50 Hz period and eight times a 500 Hz one. A loop built on them does not run late
/// occasionally, it runs late every single tick.
/// </para>
/// <para>
/// This hybrid-waits instead: it sleeps only while more than one scheduler quantum of slack
/// remains, then spins to the deadline. Spinning burns a core on purpose. That is the trade a
/// control loop makes, and it is why <see cref="RunAsync"/> takes a cancellation token rather than
/// running forever.
/// </para>
/// </remarks>
public sealed class RealtimeLoop
{
    // One Windows scheduler quantum plus a margin. Below this we spin rather than sleep.
    private static readonly TimeSpan SleepThreshold = TimeSpan.FromMilliseconds(20);

    private readonly TimeSpan _period;
    private long _ticks;
    private long _overruns;
    private double _jitterSumMs;
    private double _worstJitterMs;

    /// <summary>The target period.</summary>
    public TimeSpan Period => _period;

    /// <summary>Target frequency in hertz.</summary>
    public double Frequency => 1.0 / _period.TotalSeconds;

    /// <summary>Number of iterations completed.</summary>
    public long Ticks => Volatile.Read(ref _ticks);

    /// <summary>Iterations whose body took longer than the period.</summary>
    public long Overruns => Volatile.Read(ref _overruns);

    /// <summary>Mean absolute deviation from the deadline, in milliseconds.</summary>
    public double MeanJitterMs => Ticks == 0 ? 0 : _jitterSumMs / Ticks;

    /// <summary>Largest observed deviation from the deadline, in milliseconds.</summary>
    public double WorstJitterMs => _worstJitterMs;

    /// <summary>Creates a loop running at <paramref name="frequencyHz"/>.</summary>
    public RealtimeLoop(double frequencyHz)
    {
        if (frequencyHz is <= 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "Expected a rate between 0 and 10 kHz.");
        }

        _period = TimeSpan.FromSeconds(1.0 / frequencyHz);
    }

    /// <summary>
    /// Runs <paramref name="body"/> once per period until cancelled.
    /// </summary>
    /// <param name="body">Receives the elapsed time since the previous tick.</param>
    /// <param name="cancellationToken">Stops the loop. Cancellation is not an error.</param>
    public async Task RunAsync(Func<TimeSpan, CancellationToken, ValueTask> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        long periodTicks = _period.Ticks;
        long start = Stopwatch.GetTimestamp();
        long previous = start;
        long iteration = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            TimeSpan delta = Stopwatch.GetElapsedTime(previous, now);
            previous = now;

            try
            {
                await body(delta, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            iteration++;
            Interlocked.Increment(ref _ticks);

            // Deadlines are computed from the loop start rather than accumulated per tick, so a
            // slow iteration does not push every later deadline out with it.
            TimeSpan target = TimeSpan.FromTicks(periodTicks * iteration);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            TimeSpan remaining = target - elapsed;

            double jitter = Math.Abs(remaining.TotalMilliseconds);
            _jitterSumMs += jitter;
            if (jitter > _worstJitterMs)
            {
                _worstJitterMs = jitter;
            }

            if (remaining <= TimeSpan.Zero)
            {
                Interlocked.Increment(ref _overruns);
                continue;
            }

            await WaitUntilAsync(start, target, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask WaitUntilAsync(long start, TimeSpan target, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan remaining = target - Stopwatch.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining > SleepThreshold)
            {
                // Give the thread pool the bulk of the wait, keeping only the last quantum for the spin.
                try
                {
                    await Task.Delay(remaining - SleepThreshold, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Thread.SpinWait(64);
        }
    }

    /// <summary>Clears the tick, overrun and jitter counters.</summary>
    public void ResetStatistics()
    {
        Volatile.Write(ref _ticks, 0);
        Volatile.Write(ref _overruns, 0);
        _jitterSumMs = 0;
        _worstJitterMs = 0;
    }
}
