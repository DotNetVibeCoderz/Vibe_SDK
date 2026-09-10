using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.Core.Robots;

namespace PollenRobotics.Net.ReachyMini;

/// <summary>
/// One command frame for Reachy Mini. Any subset of the three targets may be set.
/// </summary>
/// <remarks>
/// <para>
/// All three travel in one message on purpose. Sending body yaw on its own rotates the body while
/// the head keeps its commanded world orientation, so the head visibly counter-rotates - correct
/// per the coordinate convention, and almost never what the caller wanted. To turn head and body
/// together, set both in the same frame.
/// </para>
/// <para>
/// The baseline for an incremental head yaw must be the last value you <b>commanded</b>, not the
/// yaw read back from state. Telemetry lags the command by a round trip, and deltas accumulated
/// against it stall under rapid input.
/// </para>
/// </remarks>
public readonly record struct ReachyMiniTarget
{
    /// <summary>Head pose in the world frame, or null to leave it alone.</summary>
    public Pose? Head { get; init; }

    /// <summary>Antenna angles, right then left, or null to leave them alone.</summary>
    public (Angle Right, Angle Left)? Antennas { get; init; }

    /// <summary>Body rotation, or null to leave it alone.</summary>
    public Angle? BodyYaw { get; init; }

    /// <summary>True when the frame would change nothing.</summary>
    public bool IsEmpty => Head is null && Antennas is null && BodyYaw is null;

    /// <summary>A frame carrying only a head pose.</summary>
    public static ReachyMiniTarget ForHead(Pose head) => new() { Head = head };

    /// <summary>A frame carrying only antenna angles.</summary>
    public static ReachyMiniTarget ForAntennas(Angle right, Angle left) => new() { Antennas = (right, left) };

    /// <summary>A frame carrying only a body yaw.</summary>
    public static ReachyMiniTarget ForBodyYaw(Angle yaw) => new() { BodyYaw = yaw };
}

/// <summary>
/// The wire operations a Reachy Mini client needs, independent of how they get to the robot.
/// </summary>
/// <remarks>
/// Two implementations ship: <see cref="Transports.ReachyMiniDaemonTransport"/> talks to the real
/// daemon over REST and a WebSocket, and the simulation package supplies an in-process one. Every
/// sample, template and gallery page is written against this interface, so the same code drives a
/// robot or the simulator with nothing changed but the transport.
/// </remarks>
public interface IReachyMiniTransport : IAsyncDisposable
{
    /// <summary>Where this transport points, for display.</summary>
    string Endpoint { get; }

    /// <summary>Current connection state.</summary>
    ConnectionState State { get; }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event Action<ConnectionState>? StateChanged;

    /// <summary>Raised whenever a fresh state snapshot arrives.</summary>
    event Action<ReachyMiniState>? StateUpdated;

    /// <summary>Opens the link.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the link, leaving the transport reusable.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies a target immediately, bypassing interpolation.</summary>
    Task SetTargetAsync(ReachyMiniTarget target, CancellationToken cancellationToken = default);

    /// <summary>Interpolates to a target over <paramref name="duration"/>, daemon-side.</summary>
    Task GotoTargetAsync(ReachyMiniTarget target, TimeSpan duration, InterpolationMethod method, CancellationToken cancellationToken = default);

    /// <summary>Cancels an in-flight goto or recorded move.</summary>
    Task CancelMoveAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the latest state, fetching a fresh one if the transport polls.</summary>
    Task<ReachyMiniState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Switches the motor mode.</summary>
    Task SetMotorModeAsync(MotorMode mode, CancellationToken cancellationToken = default);

    /// <summary>Enables or disables torque, optionally for specific motor ids.</summary>
    Task SetTorqueAsync(bool enabled, IReadOnlyList<string>? motorIds = null, CancellationToken cancellationToken = default);

    /// <summary>Starts or stops daemon-side face tracking.</summary>
    /// <param name="enabled">Whether the tracker runs.</param>
    /// <param name="weight">
    /// Blend against application motion: 1 lets tracking own the head, 0 keeps the detector warm
    /// while freeing the head, which is cheaper than stopping and restarting it.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task SetHeadTrackingAsync(bool enabled, double weight = 1.0, CancellationToken cancellationToken = default);

    /// <summary>The latest face the tracker saw.</summary>
    Task<FaceTarget> GetTrackedFaceAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns the idle breathing motion on or off.</summary>
    Task SetWobblingAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Lets the daemon rotate the body to follow large head yaws.</summary>
    Task SetAutomaticBodyYawAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Reads the IMU, or null on hardware without one.</summary>
    Task<ImuReading?> GetImuAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts daemon-side motion recording.</summary>
    Task StartRecordingAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops recording and returns the captured frames.</summary>
    Task<IReadOnlyList<Moves.MoveFrame>> StopRecordingAsync(CancellationToken cancellationToken = default);
}
