using System.Numerics;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Safety;
using PollenRobotics.Net.Kinematics;
using Reachy.Kinematics;
using Reachy.Part;
using Reachy.Part.Head;

namespace PollenRobotics.Net.Reachy2;

/// <summary>
/// Reachy 2's head: a three-axis Orbita neck and two antennas.
/// </summary>
/// <remarks>
/// The neck is an Orbita3d - one spherical actuator, not a stack of three servos - so it is
/// commanded as an orientation rather than as three joint angles. Roll, pitch and yaw are how that
/// orientation is written down, not three independent things that can be moved separately.
/// </remarks>
public sealed class Reachy2Head
{
    private readonly HeadService.HeadServiceClient _client;
    private readonly GoToService.GoToServiceClient _goto;
    private readonly PartId _partId;
    private readonly JointLimitGuard _guard;
    private readonly TimeSpan _timeout;

    /// <summary>The part name the robot knows the head by.</summary>
    public string Name => _partId.Name;

    internal Reachy2Head(
        HeadService.HeadServiceClient client,
        GoToService.GoToServiceClient gotoClient,
        PartId partId,
        JointLimitGuard guard,
        TimeSpan timeout)
    {
        _client = client;
        _goto = gotoClient;
        _partId = partId;
        _guard = guard;
        _timeout = timeout;
    }

    /// <summary>Energises the neck and antennas.</summary>
    public async Task TurnOnAsync(CancellationToken cancellationToken = default) =>
        await _client.TurnOnAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

    /// <summary>Makes the head compliant.</summary>
    public async Task TurnOffAsync(CancellationToken cancellationToken = default) =>
        await _client.TurnOffAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

    /// <summary>Reads the neck orientation.</summary>
    public async Task<(Angle Roll, Angle Pitch, Angle Yaw)> GetOrientationAsync(CancellationToken cancellationToken = default)
    {
        Rotation3d rotation = await _client.GetOrientationAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);
        return ProtoConversions.ToRpy(rotation);
    }

    /// <summary>
    /// Reads the neck orientation the robot is currently aiming for.
    /// </summary>
    /// <remarks>
    /// The goal, not the measurement. Comparing it against <see cref="GetOrientationAsync"/> is how
    /// an application tells "the head has not moved yet" from "the head has moved and stopped
    /// short", which look identical from either reading alone.
    /// </remarks>
    public async Task<(Angle Roll, Angle Pitch, Angle Yaw)> GetGoalOrientationAsync(CancellationToken cancellationToken = default)
    {
        Rotation3d rotation = await _client.GetJointGoalPositionAsync(
            _partId, deadline: Deadline(), cancellationToken: cancellationToken);

        return ProtoConversions.ToRpy(rotation);
    }

    /// <summary>True when the head reports itself activated.</summary>
    public async Task<bool> IsOnAsync(CancellationToken cancellationToken = default)
    {
        HeadState state = await _client.GetStateAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);
        return state.Activated;
    }

    /// <summary>
    /// Moves the neck to an orientation, queued behind whatever the head is already doing.
    /// </summary>
    /// <param name="roll">Head tilt.</param>
    /// <param name="pitch">Nod, positive nose-down.</param>
    /// <param name="yaw">Turn, positive to the robot's left.</param>
    /// <param name="duration">Motion time.</param>
    /// <param name="interpolation">Easing.</param>
    /// <param name="cancellationToken">Cancels the call, not the motion.</param>
    /// <returns>A handle on the queued movement.</returns>
    public async Task<Reachy2GotoHandle> GotoOrientationAsync(
        Angle roll,
        Angle pitch,
        Angle yaw,
        TimeSpan? duration = null,
        InterpolationMode interpolation = InterpolationMode.MinimumJerk,
        CancellationToken cancellationToken = default)
    {
        roll = Angle.FromRadians(_guard.Apply("head.neck.roll", roll.Radians));
        pitch = Angle.FromRadians(_guard.Apply("head.neck.pitch", pitch.Radians));
        yaw = Angle.FromRadians(_guard.Apply("head.neck.yaw", yaw.Radians));

        var request = new GoToRequest
        {
            JointsGoal = new JointsGoal
            {
                NeckJointGoal = new NeckJointGoal
                {
                    Id = _partId,
                    JointsGoal = new NeckOrientation { Rotation = ProtoConversions.ToRotation(roll, pitch, yaw) },
                    Duration = ProtoConversions.Wrap(duration?.TotalSeconds),
                },
            },
            InterpolationMode = new GoToInterpolation { InterpolationType = interpolation },
        };

        GoToId id = await _goto.GoToJointsAsync(request, deadline: Deadline(), cancellationToken: cancellationToken);
        return new Reachy2GotoHandle(_goto, id, _timeout);
    }

    /// <summary>Moves the neck to an orientation given in degrees.</summary>
    public Task<Reachy2GotoHandle> GotoRpyDegreesAsync(
        double roll,
        double pitch,
        double yaw,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default) =>
        GotoOrientationAsync(
            Angle.FromDegrees(roll), Angle.FromDegrees(pitch), Angle.FromDegrees(yaw),
            duration, cancellationToken: cancellationToken);

    /// <summary>
    /// Sends a neck orientation straight to the head, bypassing the movement queue.
    /// </summary>
    /// <remarks>Use this to track something continuously; use the goto for a discrete movement.</remarks>
    public async Task SetOrientationAsync(Angle roll, Angle pitch, Angle yaw, CancellationToken cancellationToken = default)
    {
        var goal = new NeckJointGoal
        {
            Id = _partId,
            JointsGoal = new NeckOrientation
            {
                Rotation = ProtoConversions.ToRotation(
                    Angle.FromRadians(_guard.Apply("head.neck.roll", roll.Radians)),
                    Angle.FromRadians(_guard.Apply("head.neck.pitch", pitch.Radians)),
                    Angle.FromRadians(_guard.Apply("head.neck.yaw", yaw.Radians))),
            },
        };

        await _client.SendNeckJointGoalAsync(goal, deadline: Deadline(), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Points the head at a world-frame point.
    /// </summary>
    /// <remarks>
    /// Solved locally rather than through <c>SendNeckCartesianGoal</c> so the resulting angles can
    /// be inspected and clamped before they leave. The neck has no translation, so the aim is exact
    /// - there is no approximation being hidden here.
    /// </remarks>
    public Task<Reachy2GotoHandle> LookAtAsync(
        double x,
        double y,
        double z,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        (Angle roll, Angle pitch, Angle yaw) = ReachyArmChains.LookAt(new Vector3((float)x, (float)y, (float)z));
        return GotoOrientationAsync(roll, pitch, yaw, duration, cancellationToken: cancellationToken);
    }

    /// <summary>Caps neck speed as a percentage of maximum.</summary>
    public async Task SetSpeedLimitAsync(int percent, CancellationToken cancellationToken = default)
    {
        if (percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Speed limit is a percentage.");
        }

        await _client.SetSpeedLimitAsync(
            new SpeedLimitRequest { Id = _partId, Limit = (uint)percent },
            deadline: Deadline(),
            cancellationToken: cancellationToken);
    }

    private DateTime? Deadline() => DateTime.UtcNow.Add(_timeout);
}
