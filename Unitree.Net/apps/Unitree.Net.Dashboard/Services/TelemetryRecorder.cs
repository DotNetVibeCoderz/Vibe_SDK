using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Unitree.Net.Sensors;

namespace Unitree.Net.Dashboard.Services;

/// <summary>
/// One recorded point in a telemetry history series.
/// </summary>
/// <param name="Timestamp">When the sample was taken.</param>
/// <param name="Value">The recorded value.</param>
public readonly record struct HistoryPoint(DateTimeOffset Timestamp, double Value);

/// <summary>
/// A fixed-capacity ring buffer of telemetry history.
/// </summary>
/// <remarks>
/// Charts need a window of recent history, not the whole session. A ring buffer bounds memory exactly:
/// at one sample per second over a 300-sample window this is five minutes of history for a few kilobytes,
/// and it never grows no matter how long the dashboard runs.
/// </remarks>
public sealed class HistorySeries(int capacity)
{
    private readonly HistoryPoint[] _buffer = new HistoryPoint[capacity];
    private readonly Lock _lock = new();
    private int _count;
    private int _next;

    /// <summary>Maximum retained samples.</summary>
    public int Capacity { get; } = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");

    /// <summary>Number of samples currently retained.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>Appends a sample, evicting the oldest when full.</summary>
    public void Add(DateTimeOffset timestamp, double value)
    {
        lock (_lock)
        {
            _buffer[_next] = new HistoryPoint(timestamp, value);
            _next = (_next + 1) % Capacity;

            if (_count < Capacity)
            {
                _count++;
            }
        }
    }

    /// <summary>Copies the retained samples in chronological order.</summary>
    public HistoryPoint[] Snapshot()
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                return [];
            }

            var result = new HistoryPoint[_count];
            int start = _count == Capacity ? _next : 0;

            for (int i = 0; i < _count; i++)
            {
                result[i] = _buffer[(start + i) % Capacity];
            }

            return result;
        }
    }

    /// <summary>Removes every sample.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _count = 0;
            _next = 0;
        }
    }
}

/// <summary>
/// Samples robot telemetry on a timer and retains it for the dashboard's charts.
/// </summary>
/// <remarks>
/// Deliberately decoupled from the robot's own 500 Hz publish rate. Charts are read by a human at human
/// timescales; recording every message would fill the ring buffer with a few seconds of data and give a
/// chart nobody can interpret.
/// </remarks>
public sealed class TelemetryRecorder(TelemetryHub telemetry, ILogger<TelemetryRecorder> logger)
    : BackgroundService
{
    private const int WindowSamples = 300;

    /// <summary>Interval between recorded samples.</summary>
    public static TimeSpan SampleInterval => TimeSpan.FromSeconds(1);

    /// <summary>Battery state of charge, percent.</summary>
    public HistorySeries BatterySoc { get; } = new(WindowSamples);

    /// <summary>Hottest motor temperature, °C.</summary>
    public HistorySeries MotorTemperature { get; } = new(WindowSamples);

    /// <summary>Body speed, m/s.</summary>
    public HistorySeries Speed { get; } = new(WindowSamples);

    /// <summary>Number of feet loaded.</summary>
    public HistorySeries FootContacts { get; } = new(WindowSamples);

    /// <summary>Total samples recorded since startup.</summary>
    public long SampleCount { get; private set; }

    /// <summary>Raised after each sample, so components can re-render.</summary>
    public event Action? Sampled;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SampleInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                TelemetrySnapshot? snapshot = telemetry.GetSnapshot();

                if (snapshot is null)
                {
                    continue;
                }

                TelemetrySnapshot value = snapshot.Value;
                DateTimeOffset now = value.Timestamp;

                BatterySoc.Add(now, value.Battery.StateOfChargePercent);
                MotorTemperature.Add(now, value.MaxMotorTemperatureCelsius);
                Speed.Add(now, value.Velocity.Length());
                FootContacts.Add(now, value.FootContact.ContactCount);

                SampleCount++;
                Sampled?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telemetry recorder stopped unexpectedly.");
        }
    }
}
