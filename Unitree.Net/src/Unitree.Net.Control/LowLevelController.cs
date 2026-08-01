using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Messages;
using Unitree.Net.Messages.Go;

namespace Unitree.Net.Control;

/// <summary>
/// Direct joint control at the robot's native command rate.
/// </summary>
/// <remarks>
/// <para>
/// This is the lowest level of control the SDK offers and the only one that can damage hardware. Three
/// things must be true before it does anything useful:
/// </para>
/// <list type="number">
/// <item>The on-board sport service has released the motors — see <see cref="MotionSwitcherClient"/>.</item>
/// <item>Commands are published continuously; the robot treats a gap as a fault.</item>
/// <item>Every command carries a valid CRC, which the publish path handles.</item>
/// </list>
/// <para>
/// The controller owns a <see cref="RealtimeLoop"/> that republishes the current command every tick, so
/// application code sets setpoints at whatever rate it likes and the cadence is maintained underneath.
/// </para>
/// </remarks>
public sealed class LowLevelController : IDisposable
{
    private readonly IDdsPublisher<LowCmd> _publisher;
    private readonly IDdsSubscriber<LowState> _stateSubscriber;
    private readonly RobotSafetyOptions _safety;
    private readonly ILogger _logger;
    private readonly RealtimeLoop _loop;
    private readonly Lock _commandLock = new();
    private readonly float[] _lastCommandedPosition = new float[RobotModelInfo.GoMotorSlots];
    private readonly bool[] _hasCommandedPosition = new bool[RobotModelInfo.GoMotorSlots];

    private LowCmd _command = LowCmd.CreateIdle();
    private DateTimeOffset _lastSetpointUpdate = DateTimeOffset.UtcNow;
    private long _publishFailureCount;
    private bool _emergencyStopped;
    private bool _disposed;

    /// <summary>Creates a controller over <paramref name="participant"/>.</summary>
    /// <param name="participant">The DDS participant.</param>
    /// <param name="options">Connection and safety configuration.</param>
    /// <param name="logger">Logger.</param>
    public LowLevelController(IDdsParticipant participant, UnitreeOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(options);

        if (RobotModelInfo.GetIdlFamily(options.Model) != IdlFamily.Go)
        {
            throw new NotSupportedException(
                $"{nameof(LowLevelController)} implements the unitree_go message set. " +
                $"Model {options.Model} uses unitree_hg, which needs the humanoid controller.");
        }

        _safety = options.Safety;
        _logger = logger ?? NullLogger.Instance;

        _publisher = participant.CreatePublisher<LowCmd>(Topics.LowCommand);
        _stateSubscriber = participant.CreateSubscriber<LowState>(Topics.LowState, options.TelemetryQueueCapacity);

        _loop = new RealtimeLoop(options.GetEffectiveControlFrequencyHz(), OnTick, _logger);
    }

    /// <summary>Whether the control loop is publishing.</summary>
    public bool IsRunning => _loop.IsRunning;

    /// <summary>Whether an emergency stop is latched.</summary>
    public bool IsEmergencyStopped
    {
        get
        {
            lock (_commandLock)
            {
                return _emergencyStopped;
            }
        }
    }

    /// <summary>Timing statistics for the control loop.</summary>
    public LoopStatistics LoopStatistics => _loop.Statistics;

    /// <summary>Number of ticks whose publish attempt threw.</summary>
    public long PublishFailureCount => Interlocked.Read(ref _publishFailureCount);

    /// <summary>Raised once per tick, after the command has been published.</summary>
    /// <remarks>Handlers run on the real-time thread and must not block.</remarks>
    public event Action<LowState>? StateUpdated;

    /// <summary>Starts the control loop.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _loop.Start();
        _logger.LogInformation("Low-level control started at {FrequencyHz} Hz.", _loop.FrequencyHz);
    }

    /// <summary>
    /// Stops the control loop, leaving the robot damped.
    /// </summary>
    /// <remarks>
    /// A final damping command is published before the loop stops. Cutting the command stream while the
    /// joints are holding a posture makes the robot collapse.
    /// </remarks>
    public void Stop()
    {
        if (!_loop.IsRunning)
        {
            return;
        }

        SetAllDamping();
        PublishCurrent();
        _loop.Stop();
        _logger.LogInformation("Low-level control stopped; joints left damped.");
    }

    /// <summary>
    /// Latches an emergency stop: every joint is set to damping and further setpoints are refused.
    /// </summary>
    /// <remarks>
    /// The loop keeps publishing so the robot continues to see a valid command stream while it settles.
    /// Clearing the latch requires <see cref="ClearEmergencyStop"/>, which is deliberately explicit.
    /// </remarks>
    public void EmergencyStop(string reason)
    {
        lock (_commandLock)
        {
            _emergencyStopped = true;
            _command.SetAllDamping();
            _lastSetpointUpdate = DateTimeOffset.UtcNow;
        }

        _logger.LogCritical("Emergency stop engaged: {Reason}", reason);
    }

    /// <summary>Clears a latched emergency stop, leaving joints damped until new setpoints arrive.</summary>
    public void ClearEmergencyStop()
    {
        lock (_commandLock)
        {
            _emergencyStopped = false;

            // Position history is stale after a stop; keeping it would let the first new setpoint
            // slew from wherever the joint used to be rather than where it is now.
            Array.Clear(_hasCommandedPosition);
        }

        _logger.LogWarning("Emergency stop cleared. Joints remain damped until new setpoints are applied.");
    }

    /// <summary>
    /// Sets one joint to track a position.
    /// </summary>
    /// <param name="jointIndex">Motor slot index; see <see cref="GoJoint"/>.</param>
    /// <param name="position">Target position, radians.</param>
    /// <param name="kp">Position gain.</param>
    /// <param name="kd">Damping gain.</param>
    /// <param name="feedForwardTorque">Optional feed-forward torque, N·m.</param>
    /// <exception cref="SafetyViolationException">
    /// A limit was exceeded and <see cref="RobotSafetyOptions.ClampInsteadOfThrow"/> is disabled.
    /// </exception>
    public void SetJointPosition(int jointIndex, float position, float kp, float kd, float feedForwardTorque = 0f)
    {
        ValidateJointIndex(jointIndex);

        float velocity = 0f;
        float torque = feedForwardTorque;
        float gainP = kp;
        float gainD = kd;
        float target = position;

        if (_safety.ClampInsteadOfThrow)
        {
            _safety.Joints.Clamp(ref target, ref velocity, ref torque, ref gainP, ref gainD);
        }
        else
        {
            _safety.Joints.Validate(jointIndex, target, velocity, torque, gainP, gainD);
        }

        lock (_commandLock)
        {
            if (_emergencyStopped)
            {
                throw new InvalidOperationException(
                    "Emergency stop is latched. Call ClearEmergencyStop before commanding joints.");
            }

            // Rate limiting converts a large setpoint jump into a ramp. Without it, an application that
            // computes a bad target — or an operator who drags a slider — delivers a step input, and the
            // impedance controller answers a step with a torque spike.
            if (_hasCommandedPosition[jointIndex])
            {
                target = RobotMath.RateLimit(
                    _lastCommandedPosition[jointIndex],
                    target,
                    _safety.Joints.MaxPositionDeltaPerTick);
            }

            _lastCommandedPosition[jointIndex] = target;
            _hasCommandedPosition[jointIndex] = true;

            _command.MotorCmd[jointIndex] = MotorCmd.Position(target, gainP, gainD, torque);
            _lastSetpointUpdate = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Sets one joint to a pure torque command with no position tracking.</summary>
    public void SetJointTorque(int jointIndex, float torque)
    {
        ValidateJointIndex(jointIndex);

        float position = 0f;
        float velocity = 0f;
        float value = torque;
        float kp = 0f;
        float kd = 0f;

        if (_safety.ClampInsteadOfThrow)
        {
            _safety.Joints.Clamp(ref position, ref velocity, ref value, ref kp, ref kd);
        }
        else
        {
            _safety.Joints.Validate(jointIndex, position, velocity, value, kp, kd);
        }

        lock (_commandLock)
        {
            if (_emergencyStopped)
            {
                throw new InvalidOperationException(
                    "Emergency stop is latched. Call ClearEmergencyStop before commanding joints.");
            }

            _command.MotorCmd[jointIndex] = MotorCmd.Torque(value);
            _hasCommandedPosition[jointIndex] = false;
            _lastSetpointUpdate = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Sets every actuated joint from a position vector.
    /// </summary>
    /// <param name="positions">Target positions, radians. Length must equal the actuated joint count.</param>
    /// <param name="kp">Position gain applied to all joints.</param>
    /// <param name="kd">Damping gain applied to all joints.</param>
    public void SetAllJointPositions(ReadOnlySpan<float> positions, float kp, float kd)
    {
        if (positions.Length != GoJoint.Count)
        {
            throw new ArgumentException(
                $"Expected {GoJoint.Count} joint positions but received {positions.Length}.",
                nameof(positions));
        }

        for (int i = 0; i < positions.Length; i++)
        {
            SetJointPosition(i, positions[i], kp, kd);
        }
    }

    /// <summary>Sets every joint to damping-only.</summary>
    /// <param name="kd">Damping gain.</param>
    public void SetAllDamping(float kd = 3f)
    {
        lock (_commandLock)
        {
            _command.SetAllDamping(kd);
            Array.Clear(_hasCommandedPosition);
            _lastSetpointUpdate = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Gets the most recent state, if any has arrived.</summary>
    public bool TryGetState(out LowState state) => _stateSubscriber.TryGetLatest(out state);

    /// <summary>Gets when the last state message arrived.</summary>
    public DateTimeOffset? LastStateReceivedAt => _stateSubscriber.LastReceivedAt;

    private void OnTick(in ControlTickContext context)
    {
        // Order matters: the safety check may latch an emergency stop, and we want that reflected in
        // the command published on this same tick rather than the next one.
        CheckSafety();
        PublishCurrent();

        if (StateUpdated is { } handler && _stateSubscriber.TryGetLatest(out LowState state))
        {
            handler(state);
        }
    }

    private void CheckSafety()
    {
        if (!_stateSubscriber.TryGetLatest(out LowState state))
        {
            return;
        }

        DateTimeOffset? lastReceived = _stateSubscriber.LastReceivedAt;

        if (lastReceived is not null && DateTimeOffset.UtcNow - lastReceived.Value > _safety.StateTimeout)
        {
            if (!IsEmergencyStopped)
            {
                EmergencyStop(
                    $"No low-level state for more than {_safety.StateTimeout.TotalMilliseconds:0} ms — the robot link is stale.");
            }

            return;
        }

        if (IsEmergencyStopped)
        {
            return;
        }

        if (state.IsFallen(_safety.FallDetectionAngle))
        {
            EulerAngles rpy = state.ImuState.ToEuler();
            EmergencyStop(
                $"Fall detected: roll {float.RadiansToDegrees(rpy.Roll):0.#}°, pitch {float.RadiansToDegrees(rpy.Pitch):0.#}°.");
            return;
        }

        int maxTemperature = state.GetMaxMotorTemperature();

        if (maxTemperature > _safety.MaxMotorTemperatureCelsius)
        {
            EmergencyStop($"Motor temperature {maxTemperature} °C exceeds the {_safety.MaxMotorTemperatureCelsius} °C limit.");
            return;
        }

        if (state.BmsState.Soc > 0 && state.BmsState.Soc < _safety.MinBatterySocPercent)
        {
            EmergencyStop($"Battery at {state.BmsState.Soc}%, below the {_safety.MinBatterySocPercent}% floor.");
        }
    }

    private void PublishCurrent()
    {
        LowCmd snapshot;

        lock (_commandLock)
        {
            snapshot = _command;
        }

        try
        {
            _publisher.Publish(snapshot);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _publishFailureCount);

            // Logging every failed tick at 500 Hz would bury everything else, so only the first is loud.
            if (Interlocked.Read(ref _publishFailureCount) == 1)
            {
                _logger.LogError(ex, "Failed to publish a low-level command; suppressing further identical errors.");
            }
        }
    }

    private static void ValidateJointIndex(int jointIndex)
    {
        if ((uint)jointIndex >= RobotModelInfo.GoMotorSlots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jointIndex),
                jointIndex,
                $"Joint index must be between 0 and {RobotModelInfo.GoMotorSlots - 1}.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _loop.Dispose();
        _publisher.Dispose();
        _stateSubscriber.Dispose();
    }
}
