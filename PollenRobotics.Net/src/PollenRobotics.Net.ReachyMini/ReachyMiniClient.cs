using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PollenRobotics.Net.Core;
using PollenRobotics.Net.Core.Connectivity;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Core.Safety;
using PollenRobotics.Net.Kinematics;
using PollenRobotics.Net.ReachyMini.Moves;
using PollenRobotics.Net.ReachyMini.Transports;

namespace PollenRobotics.Net.ReachyMini;

/// <summary>
/// Reachy Mini: nine actuated degrees of freedom, a camera, microphones and a speaker.
/// </summary>
/// <remarks>
/// <para>
/// The surface mirrors the Python SDK closely enough that its documentation and examples translate
/// directly - <c>goto_target</c> is <see cref="GotoTargetAsync"/>, <c>set_target</c> is
/// <see cref="SetTargetAsync"/>, and the head/antennas/body_yaw split is the same. Where this SDK
/// differs it is deliberate: limits throw rather than clamp by default (see
/// <see cref="RobotSafetyOptions"/>), and everything is asynchronous because a desktop UI cannot
/// afford a blocking call on its dispatcher thread.
/// </para>
/// <para>
/// Typical use:
/// </para>
/// <code>
/// await using var mini = ReachyMiniClient.Connect();
/// await mini.ConnectAsync();
/// await mini.WakeUpAsync();
/// await mini.GotoTargetAsync(
///     head: HeadPose.Create(z: 10, mm: true),
///     antennas: (45.Degrees(), 45.Degrees()),
///     duration: TimeSpan.FromSeconds(2));
/// </code>
/// </remarks>
public sealed class ReachyMiniClient : IRobotClient
{
    private readonly IReachyMiniTransport _transport;
    private readonly bool _ownsTransport;
    private readonly ILogger _logger;
    private readonly JointLimitGuard _guard;
    private readonly StewartPlatform _neck;

    // The last values we commanded, as opposed to the last we were told. Incremental motion has to
    // build on the former: telemetry lags by a round trip, and deltas accumulated against a lagging
    // baseline stall the moment the user moves faster than the state stream.
    private Pose _commandedHead = Pose.Identity;
    private (Angle Right, Angle Left) _commandedAntennas = (Angle.Zero, Angle.Zero);
    private Angle _commandedBodyYaw = Angle.Zero;

    /// <inheritdoc />
    public RobotKind Kind => RobotKind.ReachyMini;

    /// <inheritdoc />
    public RobotDescription Description => RobotCatalog.ReachyMini;

    /// <inheritdoc />
    public ConnectionState State => _transport.State;

    /// <inheritdoc />
    public string Endpoint => _transport.Endpoint;

    /// <inheritdoc />
    public event Action<ConnectionState>? StateChanged;

    /// <summary>Raised whenever a fresh state snapshot arrives.</summary>
    public event Action<ReachyMiniState>? StateUpdated;

    /// <summary>The transport in use, for callers that need something this class does not expose.</summary>
    public IReachyMiniTransport Transport => _transport;

    /// <summary>The most recent state seen, without a round trip.</summary>
    public ReachyMiniState LastState { get; private set; } = ReachyMiniState.Empty;

    /// <summary>
    /// The neck solver, for previewing whether a pose is reachable before commanding it.
    /// </summary>
    /// <remarks>
    /// The daemon does its own kinematics, so this is not in the command path. It uses approximate
    /// link geometry - see <see cref="StewartGeometry.ReachyMiniApproximation"/>.
    /// </remarks>
    public StewartPlatform Neck => _neck;

    /// <summary>Wraps an existing transport.</summary>
    /// <param name="transport">The transport to drive.</param>
    /// <param name="safety">Limit policy. Defaults to <see cref="RobotSafetyOptions.Default"/>.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="ownsTransport">Whether disposing this client disposes the transport.</param>
    public ReachyMiniClient(
        IReachyMiniTransport transport,
        RobotSafetyOptions? safety = null,
        ILogger<ReachyMiniClient>? logger = null,
        bool ownsTransport = false)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ownsTransport = ownsTransport;
        _logger = logger ?? NullLogger<ReachyMiniClient>.Instance;
        _guard = new JointLimitGuard(Description, safety ?? RobotSafetyOptions.Default, _logger);
        _neck = new StewartPlatform(StewartGeometry.ReachyMiniApproximation);

        _transport.StateChanged += OnTransportStateChanged;
        _transport.StateUpdated += OnTransportStateUpdated;
    }

    /// <summary>
    /// Builds a client wired to the real daemon.
    /// </summary>
    /// <remarks>The returned client owns its transport, so disposing it closes the connection.</remarks>
    public static ReachyMiniClient Connect(ReachyMiniOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        ReachyMiniOptions resolved = options ?? ReachyMiniOptions.Default;
        var transport = new ReachyMiniDaemonTransport(resolved, loggerFactory?.CreateLogger<ReachyMiniDaemonTransport>());
        return new ReachyMiniClient(transport, resolved.Safety, loggerFactory?.CreateLogger<ReachyMiniClient>(), ownsTransport: true);
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default) => _transport.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => _transport.DisconnectAsync(cancellationToken);

    /// <summary>
    /// Applies a target immediately, with no interpolation.
    /// </summary>
    /// <remarks>
    /// This is the high-frequency path - a joystick, a generated trajectory, a face follower. It
    /// does not wait for the robot to arrive, because there is nothing to wait for: the next frame
    /// supersedes this one.
    /// </remarks>
    public async Task SetTargetAsync(
        Pose? head = null,
        (Angle Right, Angle Left)? antennas = null,
        Angle? bodyYaw = null,
        CancellationToken cancellationToken = default)
    {
        ReachyMiniTarget target = BuildTarget(head, antennas, bodyYaw);
        if (target.IsEmpty)
        {
            return;
        }

        await _transport.SetTargetAsync(target, cancellationToken).ConfigureAwait(false);
        RememberCommand(target);
    }

    /// <summary>
    /// Interpolates smoothly to a target and waits for the motion to finish.
    /// </summary>
    /// <param name="head">Target head pose, or null to leave the head alone.</param>
    /// <param name="antennas">Target antenna angles, or null.</param>
    /// <param name="bodyYaw">Target body rotation, or null.</param>
    /// <param name="duration">How long the motion should take.</param>
    /// <param name="method">Easing curve. Minimum jerk unless you have a reason.</param>
    /// <param name="waitForCompletion">
    /// Await the motion. The daemon interpolates either way; this only decides whether the call
    /// returns before or after the robot arrives.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait, not the motion - use <see cref="CancelMoveAsync"/> for that.</param>
    public async Task GotoTargetAsync(
        Pose? head = null,
        (Angle Right, Angle Left)? antennas = null,
        Angle? bodyYaw = null,
        TimeSpan? duration = null,
        InterpolationMethod method = InterpolationMethod.MinJerk,
        bool waitForCompletion = true,
        CancellationToken cancellationToken = default)
    {
        ReachyMiniTarget target = BuildTarget(head, antennas, bodyYaw);
        if (target.IsEmpty)
        {
            return;
        }

        TimeSpan span = duration ?? TimeSpan.FromSeconds(1);
        await _transport.GotoTargetAsync(target, span, method, cancellationToken).ConfigureAwait(false);
        RememberCommand(target);

        if (waitForCompletion)
        {
            await Task.Delay(span, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels a running goto or recorded move; the robot stops where it is.</summary>
    public Task CancelMoveAsync(CancellationToken cancellationToken = default) => _transport.CancelMoveAsync(cancellationToken);

    /// <summary>Sets the head orientation in degrees, leaving translation at the neutral position.</summary>
    public Task SetHeadRpyAsync(double rollDeg, double pitchDeg, double yawDeg, CancellationToken cancellationToken = default) =>
        SetTargetAsync(head: HeadPose.Create(roll: rollDeg, pitch: pitchDeg, yaw: yawDeg), cancellationToken: cancellationToken);

    /// <summary>Sets both antennas in degrees, right then left.</summary>
    public Task SetAntennasAsync(double rightDeg, double leftDeg, CancellationToken cancellationToken = default) =>
        SetTargetAsync(antennas: (Angle.FromDegrees(rightDeg), Angle.FromDegrees(leftDeg)), cancellationToken: cancellationToken);

    /// <summary>Sets the body rotation in degrees.</summary>
    public Task SetBodyYawAsync(double yawDeg, CancellationToken cancellationToken = default) =>
        SetTargetAsync(bodyYaw: Angle.FromDegrees(yawDeg), cancellationToken: cancellationToken);

    /// <summary>
    /// Turns head and body together, the way a person turns to look behind them.
    /// </summary>
    /// <remarks>
    /// The head pose is in the world frame, so rotating the body alone leaves the head's commanded
    /// gaze fixed and it appears to counter-rotate. Adding the same delta to both keeps them
    /// together, and the delta has to be measured from the last commanded yaw rather than from
    /// telemetry - see the remarks on <see cref="ReachyMiniTarget"/>.
    /// </remarks>
    public Task TurnAsync(Angle delta, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        (Angle roll, Angle pitch, Angle yaw) = _commandedHead.Rpy;
        Pose head = Pose.FromRpy(
            _commandedHead.Position.X, _commandedHead.Position.Y, _commandedHead.Position.Z,
            roll, pitch, yaw + delta);

        return duration is { } span
            ? GotoTargetAsync(head: head, bodyYaw: _commandedBodyYaw + delta, duration: span, cancellationToken: cancellationToken)
            : SetTargetAsync(head: head, bodyYaw: _commandedBodyYaw + delta, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Points the head at a world-frame point.
    /// </summary>
    /// <param name="x">Forward, metres.</param>
    /// <param name="y">Left, metres.</param>
    /// <param name="z">Up, metres.</param>
    /// <param name="duration">Motion time, or null to snap.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task LookAtAsync(double x, double y, double z, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        var target = new Vector3((float)x, (float)y, (float)z);

        // The head sits above the base; aim from there, not from the origin, or close targets are
        // pointed at from the wrong height and the robot looks past them.
        var headOrigin = new Vector3(0, 0, (float)StewartGeometry.ReachyMiniApproximation.HomeHeight);
        Vector3 direction = target - headOrigin;

        if (direction.LengthSquared() < 1e-9)
        {
            return Task.CompletedTask;
        }

        direction = Vector3.Normalize(direction);
        double yaw = Math.Atan2(direction.Y, direction.X);
        double horizontal = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y));
        double pitch = -Math.Atan2(direction.Z, horizontal);

        Pose head = Pose.FromRpy(0, 0, 0, Angle.Zero, Angle.FromRadians(pitch), Angle.FromRadians(yaw));

        return duration is { } span
            ? GotoTargetAsync(head: head, duration: span, cancellationToken: cancellationToken)
            : SetTargetAsync(head: head, cancellationToken: cancellationToken);
    }

    /// <summary>Plays the wake-up trajectory and leaves the robot in position control.</summary>
    public async Task WakeUpAsync(CancellationToken cancellationToken = default)
    {
        await _transport.SetMotorModeAsync(MotorMode.Enabled, cancellationToken).ConfigureAwait(false);
        await GotoTargetAsync(
            head: HeadPose.Create(z: 12, mm: true, pitch: -8),
            antennas: (Angle.FromDegrees(35), Angle.FromDegrees(-35)),
            duration: TimeSpan.FromSeconds(0.8),
            method: InterpolationMethod.Cartoon,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await GotoTargetAsync(
            head: HeadPose.Neutral,
            antennas: (Angle.Zero, Angle.Zero),
            duration: TimeSpan.FromSeconds(0.6),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Reachy Mini is awake.");
    }

    /// <summary>Folds the robot down and cuts torque.</summary>
    public async Task GotoSleepAsync(CancellationToken cancellationToken = default)
    {
        await GotoTargetAsync(
            head: HeadPose.Create(z: -6, pitch: 18, mm: true),
            antennas: (Angle.FromDegrees(-60), Angle.FromDegrees(60)),
            bodyYaw: Angle.Zero,
            duration: TimeSpan.FromSeconds(1.2),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await _transport.SetMotorModeAsync(MotorMode.Disabled, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Reachy Mini is asleep.");
    }

    /// <summary>
    /// Brings the robot to position control without replaying the wake-up animation if it is already there.
    /// </summary>
    /// <remarks>
    /// Safe to call on every application start. An application that calls <see cref="WakeUpAsync"/>
    /// unconditionally makes the robot perform a greeting every time a user switches between two
    /// apps, which reads as a fault rather than as charm.
    /// </remarks>
    public async Task<bool> EnsureAwakeAsync(CancellationToken cancellationToken = default)
    {
        ReachyMiniState state = await GetStateAsync(cancellationToken).ConfigureAwait(false);

        if (state.MotorMode == MotorMode.Enabled)
        {
            return true;
        }

        if (state.MotorMode == MotorMode.GravityCompensation)
        {
            await _transport.SetMotorModeAsync(MotorMode.Enabled, cancellationToken).ConfigureAwait(false);
            return true;
        }

        await WakeUpAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task EnableMotorsAsync(CancellationToken cancellationToken = default) =>
        _transport.SetMotorModeAsync(MotorMode.Enabled, cancellationToken);

    /// <inheritdoc />
    public Task DisableMotorsAsync(CancellationToken cancellationToken = default) =>
        _transport.SetMotorModeAsync(MotorMode.Disabled, cancellationToken);

    /// <summary>Puts the motors into gravity compensation so the head can be posed by hand.</summary>
    public Task EnableGravityCompensationAsync(CancellationToken cancellationToken = default) =>
        _transport.SetMotorModeAsync(MotorMode.GravityCompensation, cancellationToken);

    /// <summary>Turns the idle breathing motion on or off.</summary>
    public Task SetWobblingAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _transport.SetWobblingAsync(enabled, cancellationToken);

    /// <summary>Starts daemon-side face tracking.</summary>
    /// <param name="weight">1 gives the tracker the head; 0 keeps the detector warm but hands the head back.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task StartHeadTrackingAsync(double weight = 1.0, CancellationToken cancellationToken = default) =>
        _transport.SetHeadTrackingAsync(true, weight, cancellationToken);

    /// <summary>Stops face tracking and releases the CPU it was using.</summary>
    public Task StopHeadTrackingAsync(CancellationToken cancellationToken = default) =>
        _transport.SetHeadTrackingAsync(false, 0, cancellationToken);

    /// <summary>The latest face the tracker saw.</summary>
    public Task<FaceTarget> GetTrackedFaceAsync(CancellationToken cancellationToken = default) =>
        _transport.GetTrackedFaceAsync(cancellationToken);

    /// <summary>Reads the IMU, or null on a Lite.</summary>
    public Task<ImuReading?> GetImuAsync(CancellationToken cancellationToken = default) =>
        _transport.GetImuAsync(cancellationToken);

    /// <summary>Fetches a fresh state snapshot.</summary>
    public async Task<ReachyMiniState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        ReachyMiniState state = await _transport.GetStateAsync(cancellationToken).ConfigureAwait(false);
        LastState = state;
        return state;
    }

    /// <inheritdoc />
    public async Task<double[]> GetJointPositionsAsync(CancellationToken cancellationToken = default)
    {
        ReachyMiniState state = await GetStateAsync(cancellationToken).ConfigureAwait(false);

        // The daemon reports the seven head-chain motors and the two antennas separately; the SDK
        // presents one vector in RobotCatalog order, which is head chain then antennas.
        double[] joints = new double[Description.JointCount];
        for (int i = 0; i < Math.Min(state.HeadJointPositions.Count, 7); i++)
        {
            joints[i] = state.HeadJointPositions[i];
        }

        joints[7] = state.Antennas.Right.Radians;
        joints[8] = state.Antennas.Left.Radians;
        return joints;
    }

    /// <summary>Starts recording motion. Pair with <see cref="StopRecordingAsync"/>.</summary>
    /// <remarks>Put the robot in gravity compensation first if you intend to pose it by hand.</remarks>
    public Task StartRecordingAsync(CancellationToken cancellationToken = default) =>
        _transport.StartRecordingAsync(cancellationToken);

    /// <summary>Stops recording and returns the captured move.</summary>
    public async Task<RecordedMove> StopRecordingAsync(string name = "recording", CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MoveFrame> frames = await _transport.StopRecordingAsync(cancellationToken).ConfigureAwait(false);
        return new RecordedMove { Name = name, Frames = frames };
    }

    /// <summary>
    /// Plays a recorded move by streaming its frames from this process.
    /// </summary>
    /// <param name="move">The move.</param>
    /// <param name="playFrequencyHz">Frame rate to stream at.</param>
    /// <param name="initialGotoDuration">Time to ease into the first frame, avoiding a jump at the start.</param>
    /// <param name="cancellationToken">Stops playback.</param>
    /// <remarks>
    /// Streaming pays a round trip per frame, which is fine on a wired Lite and visibly rough over
    /// Wi-Fi. Anything long, and anything with audio, should be handed to the daemon instead so
    /// motion and sound share one clock.
    /// </remarks>
    public async Task PlayMoveAsync(
        RecordedMove move,
        double playFrequencyHz = 100,
        TimeSpan? initialGotoDuration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(move);

        if (move.Frames.Count == 0)
        {
            return;
        }

        TimeSpan lead = initialGotoDuration ?? TimeSpan.FromSeconds(1);
        ReachyMiniTarget first = move.Frames[0].ToTarget();
        await _transport.GotoTargetAsync(first, lead, InterpolationMethod.MinJerk, cancellationToken).ConfigureAwait(false);
        await Task.Delay(lead, cancellationToken).ConfigureAwait(false);

        var loop = new RealtimeLoop(playFrequencyHz);
        TimeSpan elapsed = TimeSpan.Zero;
        TimeSpan total = move.Duration;

        using var playback = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await loop.RunAsync(async (delta, token) =>
        {
            elapsed += delta;
            if (elapsed >= total)
            {
                await _transport.SetTargetAsync(move.Frames[^1].ToTarget(), token).ConfigureAwait(false);
                await playback.CancelAsync().ConfigureAwait(false);
                return;
            }

            await _transport.SetTargetAsync(move.Sample(elapsed), token).ConfigureAwait(false);
        }, playback.Token).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
    }

    private ReachyMiniTarget BuildTarget(Pose? head, (Angle Right, Angle Left)? antennas, Angle? bodyYaw)
    {
        if (head is { } pose)
        {
            ValidateHeadPose(pose);
        }

        if (antennas is { } a)
        {
            antennas = (
                Angle.FromRadians(_guard.Apply("antenna.right", a.Right.Radians)),
                Angle.FromRadians(_guard.Apply("antenna.left", a.Left.Radians)));
        }

        if (bodyYaw is { } yaw)
        {
            bodyYaw = Angle.FromRadians(_guard.Apply("body.yaw", yaw.Radians));
        }

        return new ReachyMiniTarget { Head = head, Antennas = antennas, BodyYaw = bodyYaw };
    }

    /// <summary>
    /// Checks a head pose against the documented envelope.
    /// </summary>
    /// <remarks>
    /// The published limits are on the pose, not on the branch angles: pitch and roll to +/-40
    /// degrees, yaw unrestricted, and head yaw within 65 degrees of body yaw. The last one is
    /// checked against the commanded body yaw because that is what the daemon will be holding by
    /// the time this command lands.
    /// </remarks>
    private void ValidateHeadPose(Pose pose)
    {
        if (!_guard.Options.EnforceJointLimits)
        {
            return;
        }

        (Angle roll, Angle pitch, Angle yaw) = pose.Rpy;
        const double PitchRollLimitDeg = 40;

        Check("head.roll", roll, PitchRollLimitDeg);
        Check("head.pitch", pitch, PitchRollLimitDeg);

        double yawDelta = Math.Abs((yaw - _commandedBodyYaw).Normalized().Degrees);
        const double YawDeltaLimitDeg = 65;

        if (yawDelta > YawDeltaLimitDeg && !_guard.Options.ClampInsteadOfThrow)
        {
            throw new RobotSafetyException(
                "head.yaw",
                yaw.Radians,
                (_commandedBodyYaw - Angle.FromDegrees(YawDeltaLimitDeg)).Radians,
                (_commandedBodyYaw + Angle.FromDegrees(YawDeltaLimitDeg)).Radians);
        }

        if (yawDelta > YawDeltaLimitDeg)
        {
            _logger.LogWarning(
                "Head yaw is {Delta:0.#} deg from body yaw, past the {Limit} deg limit. " +
                "The daemon will clamp it, or rotate the body if automatic body yaw is on.",
                yawDelta, YawDeltaLimitDeg);
        }

        void Check(string name, Angle value, double limitDeg)
        {
            if (Math.Abs(value.Degrees) <= limitDeg)
            {
                return;
            }

            if (!_guard.Options.ClampInsteadOfThrow)
            {
                throw new RobotSafetyException(name, value.Radians,
                    Angle.FromDegrees(-limitDeg).Radians, Angle.FromDegrees(limitDeg).Radians);
            }

            _logger.LogWarning("{Joint} of {Value:0.#} deg exceeds the {Limit} deg envelope; the daemon will clamp it.",
                name, value.Degrees, limitDeg);
        }
    }

    private void RememberCommand(ReachyMiniTarget target)
    {
        if (target.Head is { } head)
        {
            _commandedHead = head;
        }

        if (target.Antennas is { } antennas)
        {
            _commandedAntennas = antennas;
        }

        if (target.BodyYaw is { } yaw)
        {
            _commandedBodyYaw = yaw;
        }
    }

    /// <summary>The last head pose this client commanded, which is the baseline for relative motion.</summary>
    public Pose CommandedHeadPose => _commandedHead;

    /// <summary>The last antenna angles this client commanded.</summary>
    public (Angle Right, Angle Left) CommandedAntennas => _commandedAntennas;

    /// <summary>The last body yaw this client commanded.</summary>
    public Angle CommandedBodyYaw => _commandedBodyYaw;

    private void OnTransportStateChanged(ConnectionState state) => StateChanged?.Invoke(state);

    private void OnTransportStateUpdated(ReachyMiniState state)
    {
        LastState = state;
        StateUpdated?.Invoke(state);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _transport.StateChanged -= OnTransportStateChanged;
        _transport.StateUpdated -= OnTransportStateUpdated;

        if (_ownsTransport)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
