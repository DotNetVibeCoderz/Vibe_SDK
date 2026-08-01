using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;

namespace Unitree.Net.Manipulation;

/// <summary>
/// Where an arm controller sends its joint commands.
/// </summary>
/// <remarks>
/// Abstracting the sink keeps trajectory execution independent of whether the joints are reached
/// through the quadruped <c>unitree_go</c> low-level path, the humanoid <c>arm_sdk</c> topic, or a
/// simulator. It also makes the execution logic testable without any transport at all.
/// </remarks>
public interface IJointCommandSink
{
    /// <summary>Number of joints this sink accepts.</summary>
    int JointCount { get; }

    /// <summary>Commands one joint to a position.</summary>
    /// <param name="jointIndex">Index within the sink's joint space.</param>
    /// <param name="position">Target position, radians.</param>
    /// <param name="kp">Position gain.</param>
    /// <param name="kd">Damping gain.</param>
    /// <param name="feedForwardTorque">Feed-forward torque, N·m.</param>
    void SetJointPosition(int jointIndex, float position, float kp, float kd, float feedForwardTorque);

    /// <summary>Reads the measured position of one joint, if available.</summary>
    bool TryGetJointPosition(int jointIndex, out float position);
}

/// <summary>
/// Gains applied while executing an arm trajectory.
/// </summary>
/// <param name="Kp">Position gain.</param>
/// <param name="Kd">Damping gain.</param>
public readonly record struct ArmGains(float Kp, float Kd)
{
    /// <summary>Gains suitable for an unloaded G1 arm.</summary>
    public static ArmGains G1Default => new(60f, 1.5f);

    /// <summary>Softer gains for contact-rich tasks, where a stiff arm would fight the environment.</summary>
    public static ArmGains Compliant => new(20f, 1.0f);
}

/// <summary>
/// Executes joint-space trajectories on one arm.
/// </summary>
/// <remarks>
/// Motions are planned as quintic trajectories and replayed on a wall clock, so execution stays smooth
/// even when the caller's loop rate differs from the planning rate.
/// </remarks>
public sealed class ArmController
{
    private readonly IJointCommandSink _sink;
    private readonly int[] _jointIndices;
    private readonly ILogger _logger;

    /// <summary>Creates a controller over the joints identified by <paramref name="jointIndices"/>.</summary>
    /// <param name="sink">Where commands are sent.</param>
    /// <param name="jointIndices">Sink joint indices forming this arm's chain, base to tip.</param>
    /// <param name="gains">Default gains.</param>
    /// <param name="limits">Default kinematic limits.</param>
    /// <param name="logger">Logger.</param>
    public ArmController(
        IJointCommandSink sink,
        ReadOnlySpan<int> jointIndices,
        ArmGains? gains = null,
        TrajectoryLimits? limits = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        if (jointIndices.Length == 0)
        {
            throw new ArgumentException("An arm must have at least one joint.", nameof(jointIndices));
        }

        foreach (int index in jointIndices)
        {
            if ((uint)index >= (uint)sink.JointCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(jointIndices),
                    index,
                    $"Joint index is outside the sink's range of 0..{sink.JointCount - 1}.");
            }
        }

        _sink = sink;
        _jointIndices = jointIndices.ToArray();
        _logger = logger ?? NullLogger.Instance;
        Gains = gains ?? ArmGains.G1Default;
        Limits = limits ?? TrajectoryLimits.Default;
    }

    /// <summary>Number of joints in this arm's chain.</summary>
    public int JointCount => _jointIndices.Length;

    /// <summary>Gains used when none are supplied per call.</summary>
    public ArmGains Gains { get; set; }

    /// <summary>Kinematic limits used when a duration is not supplied.</summary>
    public TrajectoryLimits Limits { get; set; }

    /// <summary>
    /// Reads the arm's current joint positions.
    /// </summary>
    /// <returns><see langword="false"/> if any joint has no measurement yet.</returns>
    public bool TryGetCurrentPositions(Span<float> positions)
    {
        if (positions.Length != JointCount)
        {
            throw new ArgumentException(
                $"Expected {JointCount} elements but received {positions.Length}.",
                nameof(positions));
        }

        for (int i = 0; i < _jointIndices.Length; i++)
        {
            if (!_sink.TryGetJointPosition(_jointIndices[i], out float position))
            {
                return false;
            }

            positions[i] = position;
        }

        return true;
    }

    /// <summary>
    /// Moves the arm to <paramref name="goal"/>, timing the motion from the configured limits.
    /// </summary>
    /// <param name="goal">Target joint positions, radians.</param>
    /// <param name="gains">Overrides the default gains.</param>
    /// <param name="cancellationToken">Cancels execution, leaving the arm where it is.</param>
    public async Task MoveToAsync(
        float[] goal,
        ArmGains? gains = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goal);

        if (goal.Length != JointCount)
        {
            throw new ArgumentException(
                $"Expected {JointCount} goal positions but received {goal.Length}.",
                nameof(goal));
        }

        var start = new float[JointCount];

        if (!TryGetCurrentPositions(start))
        {
            throw new UnitreeException(
                "Cannot plan an arm motion: joint feedback is unavailable. Confirm the robot is connected " +
                "and publishing low-level state.");
        }

        IReadOnlyList<TrajectoryPoint> trajectory = TrajectoryPlanner.Plan(start, goal, Limits);
        await ExecuteAsync(trajectory, gains, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replays a planned trajectory in real time.
    /// </summary>
    /// <param name="trajectory">The trajectory to execute.</param>
    /// <param name="gains">Overrides the default gains.</param>
    /// <param name="cancellationToken">Cancels execution.</param>
    /// <remarks>
    /// Execution is driven by a <see cref="Stopwatch"/> and samples the trajectory at the elapsed time
    /// rather than stepping index by index. A late tick therefore resumes at the correct point instead of
    /// stretching the motion.
    /// </remarks>
    public async Task ExecuteAsync(
        IReadOnlyList<TrajectoryPoint> trajectory,
        ArmGains? gains = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trajectory);

        if (trajectory.Count == 0)
        {
            return;
        }

        ArmGains effectiveGains = gains ?? Gains;
        double duration = trajectory[^1].TimeSeconds;

        _logger.LogInformation(
            "Executing a {JointCount}-joint arm trajectory over {Duration:0.00} s.",
            JointCount,
            duration);

        long startTimestamp = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            double elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
            TrajectoryPoint point = TrajectoryPlanner.Sample(trajectory, elapsed);

            ApplyPoint(point, effectiveGains);

            if (elapsed >= duration)
            {
                break;
            }
        }

        // Land exactly on the final setpoint. Sampling at the loop rate would otherwise leave the arm a
        // fraction of a tick short of the goal, and that residual accumulates across chained motions.
        ApplyPoint(trajectory[^1], effectiveGains);
    }

    /// <summary>Holds the arm at its present position with the configured gains.</summary>
    public void Hold(ArmGains? gains = null)
    {
        ArmGains effectiveGains = gains ?? Gains;

        for (int i = 0; i < _jointIndices.Length; i++)
        {
            if (_sink.TryGetJointPosition(_jointIndices[i], out float position))
            {
                _sink.SetJointPosition(_jointIndices[i], position, effectiveGains.Kp, effectiveGains.Kd, 0f);
            }
        }
    }

    /// <summary>Releases the arm to damping-only, so it yields to external force.</summary>
    /// <param name="kd">Damping gain.</param>
    public void Relax(float kd = 1f)
    {
        foreach (int index in _jointIndices)
        {
            _sink.SetJointPosition(index, 0f, 0f, kd, 0f);
        }
    }

    private void ApplyPoint(TrajectoryPoint point, ArmGains gains)
    {
        for (int i = 0; i < _jointIndices.Length; i++)
        {
            _sink.SetJointPosition(_jointIndices[i], point.Positions[i], gains.Kp, gains.Kd, 0f);
        }
    }
}

/// <summary>
/// Runs two arms together, either in lockstep or independently.
/// </summary>
/// <remarks>
/// Synchronised execution matters whenever both arms hold the same object: if one arm finishes its
/// motion before the other, the object is twisted or dropped. <see cref="MoveBothAsync"/> plans one
/// duration covering both arms so they start and finish together.
/// </remarks>
public sealed class DualArmCoordinator(ArmController left, ArmController right, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>The left arm.</summary>
    public ArmController Left { get; } = left ?? throw new ArgumentNullException(nameof(left));

    /// <summary>The right arm.</summary>
    public ArmController Right { get; } = right ?? throw new ArgumentNullException(nameof(right));

    /// <summary>
    /// Moves both arms so that they start and finish simultaneously.
    /// </summary>
    /// <param name="leftGoal">Left arm target positions, radians.</param>
    /// <param name="rightGoal">Right arm target positions, radians.</param>
    /// <param name="cancellationToken">Cancels both motions.</param>
    public async Task MoveBothAsync(
        float[] leftGoal,
        float[] rightGoal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leftGoal);
        ArgumentNullException.ThrowIfNull(rightGoal);

        var leftStart = new float[Left.JointCount];
        var rightStart = new float[Right.JointCount];

        if (!Left.TryGetCurrentPositions(leftStart) || !Right.TryGetCurrentPositions(rightStart))
        {
            throw new UnitreeException("Cannot plan a dual-arm motion: joint feedback is unavailable.");
        }

        // One shared duration — the slower arm sets the pace for both.
        double duration = Math.Max(
            TrajectoryPlanner.ComputeDuration(leftStart, leftGoal, Left.Limits),
            TrajectoryPlanner.ComputeDuration(rightStart, rightGoal, Right.Limits));

        if (duration <= 0)
        {
            return;
        }

        _logger.LogInformation("Executing a synchronised dual-arm motion over {Duration:0.00} s.", duration);

        IReadOnlyList<TrajectoryPoint> leftTrajectory =
            TrajectoryPlanner.Plan(leftStart, leftGoal, duration);
        IReadOnlyList<TrajectoryPoint> rightTrajectory =
            TrajectoryPlanner.Plan(rightStart, rightGoal, duration);

        await Task.WhenAll(
                Left.ExecuteAsync(leftTrajectory, cancellationToken: cancellationToken),
                Right.ExecuteAsync(rightTrajectory, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Holds both arms at their present positions.</summary>
    public void HoldBoth()
    {
        Left.Hold();
        Right.Hold();
    }

    /// <summary>Relaxes both arms to damping-only.</summary>
    public void RelaxBoth(float kd = 1f)
    {
        Left.Relax(kd);
        Right.Relax(kd);
    }
}
