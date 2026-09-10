using PollenRobotics.Net.Core;
using PollenRobotics.Net.Core.Geometry;
using Reachy.Part;
using Reachy.Part.Mobile.Base.Mobility;
using Reachy.Part.Mobile.Base.Utility;

namespace PollenRobotics.Net.Reachy2;

/// <summary>
/// The omnidirectional mobile base, when the robot has one.
/// </summary>
/// <remarks>
/// <para>
/// The base is holonomic - it can translate in any direction while rotating independently - so a
/// velocity command has three free components rather than the forward/turn pair a differential
/// drive would take.
/// </para>
/// <para>
/// The drive mode matters. <see cref="ZuuuModePossiblities.CmdVel"/> tracks velocity commands,
/// <see cref="ZuuuModePossiblities.Brake"/> holds position against a push, and
/// <see cref="ZuuuModePossiblities.FreeWheel"/> lets the base be pushed by hand. A velocity command
/// sent while the base is braked is accepted and does nothing.
/// </para>
/// </remarks>
public sealed class Reachy2MobileBase
{
    private readonly MobileBaseUtilityService.MobileBaseUtilityServiceClient _utility;
    private readonly MobileBaseMobilityService.MobileBaseMobilityServiceClient _mobility;
    private readonly PartId _partId;
    private readonly TimeSpan _timeout;

    /// <summary>The part name the robot knows the base by.</summary>
    public string Name => _partId.Name;

    internal Reachy2MobileBase(
        MobileBaseUtilityService.MobileBaseUtilityServiceClient utility,
        MobileBaseMobilityService.MobileBaseMobilityServiceClient mobility,
        PartId partId,
        TimeSpan timeout)
    {
        _utility = utility;
        _mobility = mobility;
        _partId = partId;
        _timeout = timeout;
    }

    /// <summary>Energises the drive.</summary>
    public async Task TurnOnAsync(CancellationToken cancellationToken = default) =>
        await _utility.TurnOnAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

    /// <summary>De-energises the drive.</summary>
    public async Task TurnOffAsync(CancellationToken cancellationToken = default) =>
        await _utility.TurnOffAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

    /// <summary>Switches the drive mode.</summary>
    public async Task SetDriveModeAsync(ZuuuModePossiblities mode, CancellationToken cancellationToken = default)
    {
        MobilityServiceAck ack = await _utility.SetZuuuModeAsync(
            new ZuuuModeCommand { Id = _partId, Mode = mode }, deadline: Deadline(), cancellationToken: cancellationToken);

        Ensure(ack, nameof(SetDriveModeAsync));
    }

    /// <summary>Reads the current drive mode.</summary>
    public async Task<ZuuuModePossiblities> GetDriveModeAsync(CancellationToken cancellationToken = default)
    {
        ZuuuModeCommand mode = await _utility.GetZuuuModeAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);
        return mode.Mode;
    }

    /// <summary>Holds position against being pushed.</summary>
    public Task BrakeAsync(CancellationToken cancellationToken = default) =>
        SetDriveModeAsync(ZuuuModePossiblities.Brake, cancellationToken);

    /// <summary>Lets the base be pushed by hand.</summary>
    public Task FreeWheelAsync(CancellationToken cancellationToken = default) =>
        SetDriveModeAsync(ZuuuModePossiblities.FreeWheel, cancellationToken);

    /// <summary>Cuts drive immediately.</summary>
    public Task EmergencyStopAsync(CancellationToken cancellationToken = default) =>
        SetDriveModeAsync(ZuuuModePossiblities.EmergencyStop, cancellationToken);

    /// <summary>
    /// Drives at a velocity for a bounded time.
    /// </summary>
    /// <param name="xVelocity">Forward speed, m/s.</param>
    /// <param name="yVelocity">Left speed, m/s.</param>
    /// <param name="rotationVelocity">Turn rate, positive to the left.</param>
    /// <param name="duration">
    /// How long to hold it. The API takes this on the command itself, so unlike the MicroDuck there
    /// is no need to keep resending - and no risk of the base running on if the caller dies.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task SetSpeedAsync(
        double xVelocity,
        double yVelocity,
        Angle rotationVelocity,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        var command = new SetSpeedVector
        {
            Id = _partId,
            XVel = ProtoConversions.Wrap(xVelocity),
            YVel = ProtoConversions.Wrap(yVelocity),
            RotVel = ProtoConversions.Wrap(rotationVelocity.Radians),
            Duration = ProtoConversions.Wrap(duration?.TotalSeconds ?? 1.0),
        };

        MobilityServiceAck ack = await _mobility.SendSetSpeedAsync(command, deadline: Deadline(), cancellationToken: cancellationToken);
        Ensure(ack, nameof(SetSpeedAsync));
    }

    /// <summary>Drives to a pose in the odometry frame.</summary>
    /// <param name="x">Target x, metres.</param>
    /// <param name="y">Target y, metres.</param>
    /// <param name="theta">Target heading.</param>
    /// <param name="cancellationToken">Cancels the call, not the motion.</param>
    public async Task GotoAsync(double x, double y, Angle theta, CancellationToken cancellationToken = default)
    {
        var command = new GoToVector
        {
            Id = _partId,
            XGoal = ProtoConversions.Wrap(x),
            YGoal = ProtoConversions.Wrap(y),
            ThetaGoal = ProtoConversions.Wrap(theta.Radians),
        };

        MobilityServiceAck ack = await _mobility.SendGoToAsync(command, deadline: Deadline(), cancellationToken: cancellationToken);
        Ensure(ack, nameof(GotoAsync));
    }

    /// <summary>How far the base still is from its goto target.</summary>
    public async Task<(double DeltaX, double DeltaY, Angle DeltaTheta, double Distance)> GetDistanceToGoalAsync(
        CancellationToken cancellationToken = default)
    {
        DistanceToGoalVector distance = await _mobility.DistanceToGoalAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

        return (
            ProtoConversions.Unwrap(distance.DeltaX),
            ProtoConversions.Unwrap(distance.DeltaY),
            Angle.FromRadians(ProtoConversions.Unwrap(distance.DeltaTheta)),
            ProtoConversions.Unwrap(distance.Distance));
    }

    /// <summary>Waits until the base has arrived, or the timeout expires.</summary>
    /// <param name="tolerance">Distance in metres that counts as arrived.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>True when the base arrived, false on timeout.</returns>
    public async Task<bool> WaitForArrivalAsync(
        double tolerance = 0.05,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTimeOffset.UtcNow < deadline)
        {
            (_, _, _, double distance) = await GetDistanceToGoalAsync(cancellationToken).ConfigureAwait(false);
            if (distance <= tolerance)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Reads the odometry pose.</summary>
    public async Task<(double X, double Y, Angle Theta)> GetOdometryAsync(CancellationToken cancellationToken = default)
    {
        OdometryVector odometry = await _utility.GetOdometryAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

        return (
            ProtoConversions.Unwrap(odometry.X),
            ProtoConversions.Unwrap(odometry.Y),
            Angle.FromRadians(ProtoConversions.Unwrap(odometry.Theta)));
    }

    /// <summary>Zeroes the odometry frame at the base's current pose.</summary>
    public async Task ResetOdometryAsync(CancellationToken cancellationToken = default)
    {
        MobilityServiceAck ack = await _utility.ResetOdometryAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);
        Ensure(ack, nameof(ResetOdometryAsync));
    }

    /// <summary>Reads the battery level.</summary>
    public async Task<double> GetBatteryLevelAsync(CancellationToken cancellationToken = default)
    {
        BatteryLevel level = await _utility.GetBatteryLevelAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);
        return ProtoConversions.Unwrap(level.Level);
    }

    /// <summary>
    /// The API answers with an ack rather than a status code, so a rejected command otherwise
    /// looks exactly like a successful one.
    /// </summary>
    private static void Ensure(MobilityServiceAck ack, string operation)
    {
        if (ack.Success != true)
        {
            throw new RobotCommandException($"The mobile base refused {operation}.", operation);
        }
    }

    private DateTime? Deadline() => DateTime.UtcNow.Add(_timeout);
}
