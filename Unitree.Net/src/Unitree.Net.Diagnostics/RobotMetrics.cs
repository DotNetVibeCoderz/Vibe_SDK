using System.Diagnostics.Metrics;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Sensors;

namespace Unitree.Net.Diagnostics;

/// <summary>
/// Publishes robot health as <see cref="System.Diagnostics.Metrics"/> instruments.
/// </summary>
/// <remarks>
/// <para>
/// Uses the standard .NET metrics API rather than a bespoke reporting mechanism, so anything that
/// already collects .NET metrics — OpenTelemetry, Prometheus via the OTLP exporter, <c>dotnet-counters</c>
/// — picks these up with no extra integration work.
/// </para>
/// <para>
/// Gauges are observable: values are read from the robot when the collector polls, rather than pushed on
/// every telemetry message. At 500 Hz, pushing would generate far more measurement traffic than any
/// backend wants, and the extra resolution is meaningless for health monitoring.
/// </para>
/// </remarks>
public sealed class RobotMetrics : IDisposable
{
    /// <summary>The meter name to enable in a metrics pipeline.</summary>
    public const string MeterName = "Unitree.Net";

    private readonly Meter _meter;
    private readonly TelemetryHub _telemetry;
    private readonly UnitreeRobot _robot;
    private readonly Counter<long> _emergencyStopCounter;
    private readonly Counter<long> _connectionLossCounter;
    private readonly Histogram<double> _controlJitterMicroseconds;
    private bool _disposed;

    /// <summary>Creates metrics for <paramref name="robot"/>.</summary>
    public RobotMetrics(UnitreeRobot robot, TelemetryHub telemetry)
    {
        ArgumentNullException.ThrowIfNull(robot);
        ArgumentNullException.ThrowIfNull(telemetry);

        _robot = robot;
        _telemetry = telemetry;
        _meter = new Meter(MeterName, "0.1.0");

        _emergencyStopCounter = _meter.CreateCounter<long>(
            "unitree.emergency_stops",
            unit: "{stop}",
            description: "Emergency stops engaged since the process started.");

        _connectionLossCounter = _meter.CreateCounter<long>(
            "unitree.connection_losses",
            unit: "{loss}",
            description: "Transitions out of the connected state.");

        _controlJitterMicroseconds = _meter.CreateHistogram<double>(
            "unitree.control_loop.jitter",
            unit: "us",
            description: "Deviation of control ticks from their nominal period.");

        _meter.CreateObservableGauge(
            "unitree.battery.state_of_charge",
            ObserveBatterySoc,
            unit: "%",
            description: "Battery state of charge.");

        _meter.CreateObservableGauge(
            "unitree.battery.voltage",
            ObserveBatteryVoltage,
            unit: "V",
            description: "Battery pack voltage.");

        _meter.CreateObservableGauge(
            "unitree.battery.cell_imbalance",
            ObserveCellImbalance,
            unit: "mV",
            description: "Spread between the highest and lowest battery cell.");

        _meter.CreateObservableGauge(
            "unitree.motor.max_temperature",
            ObserveMaxMotorTemperature,
            unit: "Cel",
            description: "Hottest actuated motor.");

        _meter.CreateObservableGauge(
            "unitree.telemetry.age",
            ObserveTelemetryAge,
            unit: "ms",
            description: "Age of the most recent low-level state message.");

        _meter.CreateObservableGauge(
            "unitree.foot.contact_count",
            ObserveContactCount,
            unit: "{foot}",
            description: "Number of feet currently loaded.");

        // Wiring the counter to the connection's own event means loss is recorded exactly when it
        // happens, rather than being inferred later from a sampled gauge.
        _robot.StateChanged += OnConnectionStateChanged;
    }

    /// <summary>Records an emergency stop.</summary>
    public void RecordEmergencyStop(string reason) =>
        _emergencyStopCounter.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Records one control tick's timing deviation.</summary>
    public void RecordControlJitter(double microseconds) =>
        _controlJitterMicroseconds.Record(microseconds);

    /// <summary>Samples the control loop and records its jitter.</summary>
    public void SampleControlLoop()
    {
        if (!_robot.LowLevel.IsRunning)
        {
            return;
        }

        LoopStatistics statistics = _robot.LowLevel.LoopStatistics;
        RecordControlJitter(statistics.MeanJitterMicroseconds);
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        if (args.PreviousState == ConnectionState.Connected && args.CurrentState != ConnectionState.Connected)
        {
            _connectionLossCounter.Add(
                1,
                new KeyValuePair<string, object?>("new_state", args.CurrentState.ToString()));
        }
    }

    private IEnumerable<Measurement<int>> ObserveBatterySoc() =>
        _telemetry.GetBattery() is { } battery
            ? [new Measurement<int>(battery.StateOfChargePercent)]
            : [];

    private IEnumerable<Measurement<double>> ObserveBatteryVoltage() =>
        _telemetry.GetBattery() is { } battery
            ? [new Measurement<double>(battery.PackVoltage)]
            : [];

    private IEnumerable<Measurement<int>> ObserveCellImbalance() =>
        _telemetry.GetBattery() is { } battery
            ? [new Measurement<int>(battery.CellImbalanceMillivolts)]
            : [];

    private IEnumerable<Measurement<int>> ObserveMaxMotorTemperature() =>
        _telemetry.GetSnapshot() is { } snapshot
            ? [new Measurement<int>(snapshot.MaxMotorTemperatureCelsius)]
            : [];

    private IEnumerable<Measurement<int>> ObserveContactCount() =>
        _telemetry.GetFootContact() is { } contact
            ? [new Measurement<int>(contact.ContactCount)]
            : [];

    private IEnumerable<Measurement<double>> ObserveTelemetryAge()
    {
        DateTimeOffset? last = _telemetry.LastLowStateAt;

        return last is null
            ? []
            : [new Measurement<double>((DateTimeOffset.UtcNow - last.Value).TotalMilliseconds)];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _robot.StateChanged -= OnConnectionStateChanged;
        _meter.Dispose();
    }
}
