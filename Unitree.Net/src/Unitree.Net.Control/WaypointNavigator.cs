using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;
using Unitree.Net.Messages.Go;

namespace Unitree.Net.Control;

/// <summary>
/// A navigation target in the robot's odometry frame.
/// </summary>
/// <param name="Position">Target position, metres. Only X and Y are used.</param>
/// <param name="ToleranceMetres">How close counts as arrived.</param>
/// <param name="FinalHeading">Heading to face on arrival, radians, or <see langword="null"/> for any.</param>
public readonly record struct Waypoint(Vector3 Position, float ToleranceMetres = 0.15f, float? FinalHeading = null)
{
    /// <summary>Creates a waypoint from planar coordinates.</summary>
    public static Waypoint At(float x, float y, float toleranceMetres = 0.15f) =>
        new(new Vector3(x, y, 0f), toleranceMetres);
}

/// <summary>
/// Tuning for <see cref="WaypointNavigator"/>.
/// </summary>
public sealed class NavigationOptions
{
    /// <summary>Proportional gain from distance error to forward speed.</summary>
    public float DistanceGain { get; set; } = 0.8f;

    /// <summary>Proportional gain from heading error to yaw rate.</summary>
    public float HeadingGain { get; set; } = 1.5f;

    /// <summary>
    /// Heading error beyond which the robot turns in place instead of driving forward.
    /// </summary>
    /// <remarks>
    /// Driving forward while badly misaligned produces a long arc and, in a corridor, a collision.
    /// Turning first is slower but predictable.
    /// </remarks>
    public float TurnInPlaceThreshold { get; set; } = float.DegreesToRadians(45f);

    /// <summary>Heading tolerance when a waypoint specifies a final heading.</summary>
    public float HeadingTolerance { get; set; } = float.DegreesToRadians(8f);

    /// <summary>Control update rate.</summary>
    public int UpdateRateHz { get; set; } = 20;

    /// <summary>
    /// How long the robot may make no progress before the leg is abandoned.
    /// </summary>
    /// <remarks>
    /// Without this, a robot blocked by an obstacle pushes against it indefinitely, because odometry
    /// keeps reporting the same distance error and the controller keeps commanding the same velocity.
    /// </remarks>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Movement below this distance over the stall window counts as no progress, metres.</summary>
    public float StallDistanceThreshold { get; set; } = 0.05f;
}

/// <summary>
/// The outcome of navigating to a waypoint.
/// </summary>
public enum NavigationResult
{
    /// <summary>The robot reached the waypoint within tolerance.</summary>
    Arrived,

    /// <summary>The caller cancelled.</summary>
    Cancelled,

    /// <summary>The robot stopped making progress.</summary>
    Stalled,

    /// <summary>Odometry was unavailable.</summary>
    NoOdometry,
}

/// <summary>
/// Drives the robot to waypoints using odometry feedback.
/// </summary>
/// <remarks>
/// <para>
/// This is a proportional controller over the robot's own dead-reckoned odometry, which drifts by a few
/// percent of distance travelled. It is well suited to short legs, a patrol route with periodic
/// re-localisation, or a demo. It is not a substitute for SLAM over longer distances — see
/// <c>docs/navigation.md</c>.
/// </para>
/// <para>
/// There is no obstacle avoidance here. Enable the robot's own avoidance service, or gate this behind a
/// planner that has a map.
/// </para>
/// </remarks>
public sealed class WaypointNavigator
{
    private readonly UnitreeRobot _robot;
    private readonly NavigationOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates a navigator for <paramref name="robot"/>.</summary>
    public WaypointNavigator(UnitreeRobot robot, NavigationOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(robot);
        _robot = robot;
        _options = options ?? new NavigationOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Drives to <paramref name="waypoint"/> and stops.
    /// </summary>
    public async Task<NavigationResult> GoToAsync(Waypoint waypoint, CancellationToken cancellationToken = default)
    {
        if (!_robot.TryGetSportState(out SportModeState initialState))
        {
            _logger.LogError("Cannot navigate: no locomotion telemetry is available.");
            return NavigationResult.NoOdometry;
        }

        using VelocityStream stream = _robot.Sport.StartVelocityStream(_options.UpdateRateHz);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / _options.UpdateRateHz));

        Vector3 stallReference = initialState.GetPosition();
        DateTimeOffset stallSince = DateTimeOffset.UtcNow;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_robot.TryGetSportState(out SportModeState state))
                {
                    continue;
                }

                Vector3 position = state.GetPosition();
                float yaw = state.ImuState.ToEuler().Yaw;

                var toTarget = new Vector2(
                    waypoint.Position.X - position.X,
                    waypoint.Position.Y - position.Y);

                float distance = toTarget.Length();

                if (distance <= waypoint.ToleranceMetres)
                {
                    stream.Stop();

                    if (waypoint.FinalHeading is { } heading)
                    {
                        await AlignHeadingAsync(stream, heading, cancellationToken).ConfigureAwait(false);
                    }

                    _logger.LogInformation(
                        "Arrived at ({X:0.##}, {Y:0.##}); final error {Error:0.###} m.",
                        waypoint.Position.X,
                        waypoint.Position.Y,
                        distance);

                    return NavigationResult.Arrived;
                }

                // Stall detection compares against a reference that only advances when real progress
                // happens, so slow-but-steady motion is never mistaken for being stuck.
                if (Vector3.Distance(position, stallReference) > _options.StallDistanceThreshold)
                {
                    stallReference = position;
                    stallSince = DateTimeOffset.UtcNow;
                }
                else if (DateTimeOffset.UtcNow - stallSince > _options.StallTimeout)
                {
                    stream.Stop();
                    _logger.LogWarning(
                        "Stalled {Distance:0.##} m from ({X:0.##}, {Y:0.##}); no progress for {Timeout:0} s.",
                        distance,
                        waypoint.Position.X,
                        waypoint.Position.Y,
                        _options.StallTimeout.TotalSeconds);
                    return NavigationResult.Stalled;
                }

                float desiredHeading = MathF.Atan2(toTarget.Y, toTarget.X);
                float headingError = RobotMath.AngleDifference(yaw, desiredHeading);
                float yawRate = headingError * _options.HeadingGain;

                float forward = MathF.Abs(headingError) > _options.TurnInPlaceThreshold
                    ? 0f
                    : distance * _options.DistanceGain;

                stream.Command = new VelocityCommand(forward, 0f, yawRate);
            }
        }
        catch (OperationCanceledException)
        {
            stream.Stop();
            return NavigationResult.Cancelled;
        }

        return NavigationResult.Cancelled;
    }

    /// <summary>
    /// Drives through a route, stopping at the first failure.
    /// </summary>
    /// <returns>The result of the last waypoint attempted.</returns>
    public async Task<NavigationResult> FollowRouteAsync(
        IEnumerable<Waypoint> route,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);

        NavigationResult result = NavigationResult.Arrived;
        int index = 0;

        foreach (Waypoint waypoint in route)
        {
            _logger.LogInformation(
                "Leg {Index}: heading to ({X:0.##}, {Y:0.##}).",
                index++,
                waypoint.Position.X,
                waypoint.Position.Y);

            result = await GoToAsync(waypoint, cancellationToken).ConfigureAwait(false);

            if (result != NavigationResult.Arrived)
            {
                _logger.LogWarning("Route abandoned at leg {Index} with result {Result}.", index - 1, result);
                return result;
            }
        }

        return result;
    }

    private async Task AlignHeadingAsync(VelocityStream stream, float targetHeading, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / _options.UpdateRateHz));
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTimeOffset.UtcNow < deadline &&
               await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_robot.TryGetSportState(out SportModeState state))
            {
                continue;
            }

            float error = RobotMath.AngleDifference(state.ImuState.ToEuler().Yaw, targetHeading);

            if (MathF.Abs(error) <= _options.HeadingTolerance)
            {
                stream.Stop();
                return;
            }

            stream.Command = new VelocityCommand(0f, 0f, error * _options.HeadingGain);
        }

        stream.Stop();
        _logger.LogWarning("Timed out while aligning to the final heading.");
    }
}
