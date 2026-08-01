using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Messages;
using Unitree.Net.Messages.Go;

namespace Unitree.Net.Sensors;

/// <summary>
/// Battery health derived from the pack's own reporting.
/// </summary>
/// <param name="StateOfChargePercent">Remaining charge, percent.</param>
/// <param name="PackVoltage">Pack voltage, volts.</param>
/// <param name="CurrentAmps">Pack current, amps. Negative means discharging.</param>
/// <param name="CycleCount">Charge cycles completed.</param>
/// <param name="MaxTemperatureCelsius">Hottest reported pack temperature.</param>
/// <param name="CellImbalanceMillivolts">Spread between the highest and lowest cell.</param>
public readonly record struct BatteryStatus(
    int StateOfChargePercent,
    float PackVoltage,
    float CurrentAmps,
    int CycleCount,
    int MaxTemperatureCelsius,
    int CellImbalanceMillivolts)
{
    /// <summary>Whether the pack is currently being charged.</summary>
    public bool IsCharging => CurrentAmps > 0.1f;

    /// <summary>
    /// Whether the cell spread is wide enough to warrant attention.
    /// </summary>
    /// <remarks>
    /// A healthy pack stays under about 50 mV. Widening imbalance is the earliest warning of a failing
    /// cell — it shows up long before the reported state of charge starts behaving oddly.
    /// </remarks>
    public bool HasCellImbalanceWarning => CellImbalanceMillivolts > 50;

    /// <summary>Estimates remaining runtime from the present draw.</summary>
    /// <param name="capacityAmpHours">Pack capacity in amp-hours.</param>
    /// <returns>Estimated remaining time, or <see langword="null"/> when charging or idle.</returns>
    public TimeSpan? EstimateRemaining(float capacityAmpHours = 8f)
    {
        float draw = -CurrentAmps;

        if (draw <= 0.05f)
        {
            return null;
        }

        float remainingAmpHours = capacityAmpHours * (StateOfChargePercent / 100f);
        return TimeSpan.FromHours(remainingAmpHours / draw);
    }
}

/// <summary>
/// Which feet are currently loaded.
/// </summary>
/// <param name="FrontRight">Front-right foot force.</param>
/// <param name="FrontLeft">Front-left foot force.</param>
/// <param name="RearRight">Rear-right foot force.</param>
/// <param name="RearLeft">Rear-left foot force.</param>
public readonly record struct FootContactState(short FrontRight, short FrontLeft, short RearRight, short RearLeft)
{
    /// <summary>Force threshold above which a foot counts as in contact.</summary>
    public const short ContactThreshold = 20;

    /// <summary>Number of feet currently in contact.</summary>
    public int ContactCount =>
        (FrontRight > ContactThreshold ? 1 : 0) +
        (FrontLeft > ContactThreshold ? 1 : 0) +
        (RearRight > ContactThreshold ? 1 : 0) +
        (RearLeft > ContactThreshold ? 1 : 0);

    /// <summary>Whether all four feet are loaded, i.e. the robot is standing rather than stepping.</summary>
    public bool IsFullStance => ContactCount == 4;

    /// <summary>Whether no foot is loaded, which during locomotion means a flight phase.</summary>
    public bool IsAirborne => ContactCount == 0;
}

/// <summary>
/// A consistent view of the robot at one instant.
/// </summary>
/// <param name="Timestamp">When the snapshot was taken.</param>
/// <param name="Orientation">Body orientation.</param>
/// <param name="AngularVelocity">Body angular rates, rad/s.</param>
/// <param name="LinearAcceleration">Body linear acceleration, m/s².</param>
/// <param name="Battery">Battery health.</param>
/// <param name="FootContact">Foot contact state.</param>
/// <param name="MaxMotorTemperatureCelsius">Hottest actuated motor.</param>
/// <param name="OdometryPosition">Dead-reckoned position, metres.</param>
/// <param name="BodyHeight">Body height above ground, metres.</param>
/// <param name="Velocity">World-frame velocity, m/s.</param>
public readonly record struct TelemetrySnapshot(
    DateTimeOffset Timestamp,
    EulerAngles Orientation,
    Vector3 AngularVelocity,
    Vector3 LinearAcceleration,
    BatteryStatus Battery,
    FootContactState FootContact,
    int MaxMotorTemperatureCelsius,
    Vector3 OdometryPosition,
    float BodyHeight,
    Vector3 Velocity);

/// <summary>
/// Aggregates the robot's telemetry topics into one place.
/// </summary>
/// <remarks>
/// Reads from the same subscriptions the control layer uses rather than opening its own, so a dashboard
/// observing the robot adds no extra DDS endpoints and no extra load on the link.
/// </remarks>
public sealed class TelemetryHub : IDisposable
{
    private readonly IDdsSubscriber<LowState> _lowState;
    private readonly IDdsSubscriber<SportModeState> _sportState;
    private readonly IDdsSubscriber<Messages.Go.LowState>? _ownedLowState;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Creates a hub that opens its own subscriptions on <paramref name="participant"/>.</summary>
    public TelemetryHub(IDdsParticipant participant, int queueCapacity = 64, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(participant);

        _logger = logger ?? NullLogger.Instance;
        _lowState = participant.CreateSubscriber<LowState>(Topics.LowState, queueCapacity);
        _sportState = participant.CreateSubscriber<SportModeState>(Topics.SportModeState, queueCapacity);
        _ownedLowState = _lowState;
    }

    /// <summary>Creates a hub over subscriptions owned by someone else.</summary>
    /// <remarks>Nothing is disposed by this hub when constructed this way.</remarks>
    public TelemetryHub(
        IDdsSubscriber<LowState> lowState,
        IDdsSubscriber<SportModeState> sportState,
        ILogger? logger = null)
    {
        _lowState = lowState ?? throw new ArgumentNullException(nameof(lowState));
        _sportState = sportState ?? throw new ArgumentNullException(nameof(sportState));
        _logger = logger ?? NullLogger.Instance;
        _ownedLowState = null;
    }

    /// <summary>Number of low-level state messages received.</summary>
    public long LowStateCount => _lowState.ReceivedCount;

    /// <summary>Number of locomotion state messages received.</summary>
    public long SportStateCount => _sportState.ReceivedCount;

    /// <summary>When low-level state last arrived.</summary>
    public DateTimeOffset? LastLowStateAt => _lowState.LastReceivedAt;

    /// <summary>Gets battery health, if low-level state has arrived.</summary>
    public BatteryStatus? GetBattery()
    {
        if (!_lowState.TryGetLatest(out LowState state))
        {
            return null;
        }

        return ToBatteryStatus(in state);
    }

    /// <summary>Gets foot contact state, if low-level state has arrived.</summary>
    public FootContactState? GetFootContact()
    {
        if (!_lowState.TryGetLatest(out LowState state))
        {
            return null;
        }

        return new FootContactState(
            state.FootForce[0],
            state.FootForce[1],
            state.FootForce[2],
            state.FootForce[3]);
    }

    /// <summary>Gets body orientation, if low-level state has arrived.</summary>
    public EulerAngles? GetOrientation() =>
        _lowState.TryGetLatest(out LowState state) ? state.ImuState.ToEuler() : null;

    /// <summary>
    /// Builds a full snapshot, combining low-level and locomotion state.
    /// </summary>
    /// <returns><see langword="null"/> when no low-level state has arrived yet.</returns>
    /// <remarks>
    /// The two source topics are sampled independently and are not synchronised to the same instant.
    /// They arrive within a few milliseconds of each other, which is well inside the timescale of
    /// anything a snapshot is used for, but it is not a hardware-triggered simultaneous capture.
    /// </remarks>
    public TelemetrySnapshot? GetSnapshot()
    {
        if (!_lowState.TryGetLatest(out LowState low))
        {
            return null;
        }

        _sportState.TryGetLatest(out SportModeState sport);

        return new TelemetrySnapshot(
            DateTimeOffset.UtcNow,
            low.ImuState.ToEuler(),
            new Vector3(low.ImuState.Gyroscope[0], low.ImuState.Gyroscope[1], low.ImuState.Gyroscope[2]),
            new Vector3(low.ImuState.Accelerometer[0], low.ImuState.Accelerometer[1], low.ImuState.Accelerometer[2]),
            ToBatteryStatus(in low),
            new FootContactState(low.FootForce[0], low.FootForce[1], low.FootForce[2], low.FootForce[3]),
            low.GetMaxMotorTemperature(),
            sport.GetPosition(),
            sport.BodyHeight,
            sport.GetVelocity());
    }

    /// <summary>Streams low-level state as it arrives.</summary>
    public IAsyncEnumerable<LowState> StreamLowStateAsync(CancellationToken cancellationToken = default) =>
        _lowState.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Streams locomotion state as it arrives.</summary>
    public IAsyncEnumerable<SportModeState> StreamSportStateAsync(CancellationToken cancellationToken = default) =>
        _sportState.Reader.ReadAllAsync(cancellationToken);

    private static BatteryStatus ToBatteryStatus(in LowState state)
    {
        BmsState bms = state.BmsState;

        int maxTemperature = int.MinValue;

        for (int i = 0; i < 2; i++)
        {
            maxTemperature = Math.Max(maxTemperature, bms.BqNtc[i]);
            maxTemperature = Math.Max(maxTemperature, bms.McuNtc[i]);
        }

        return new BatteryStatus(
            bms.Soc,
            bms.GetPackVoltage(),
            bms.Current / 1000f,
            bms.Cycle,
            maxTemperature == int.MinValue ? 0 : maxTemperature,
            bms.GetCellImbalanceMillivolts());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Only tear down subscriptions this hub created; borrowed ones belong to their owner.
        if (_ownedLowState is not null)
        {
            _lowState.Dispose();
            _sportState.Dispose();
        }
    }
}
