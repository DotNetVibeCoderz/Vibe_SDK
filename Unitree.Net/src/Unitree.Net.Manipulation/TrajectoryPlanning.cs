using Unitree.Net.Core;

namespace Unitree.Net.Manipulation;

/// <summary>
/// One sampled point along a joint-space trajectory.
/// </summary>
/// <param name="TimeSeconds">Time from the start of the trajectory.</param>
/// <param name="Positions">Joint positions, radians.</param>
/// <param name="Velocities">Joint velocities, rad/s.</param>
/// <param name="Accelerations">Joint accelerations, rad/s².</param>
public sealed record TrajectoryPoint(
    double TimeSeconds,
    float[] Positions,
    float[] Velocities,
    float[] Accelerations);

/// <summary>
/// Kinematic ceilings used when timing a trajectory.
/// </summary>
/// <param name="MaxVelocity">Maximum joint speed, rad/s.</param>
/// <param name="MaxAcceleration">Maximum joint acceleration, rad/s².</param>
public readonly record struct TrajectoryLimits(float MaxVelocity, float MaxAcceleration)
{
    /// <summary>Conservative defaults suitable for a G1 arm carrying no payload.</summary>
    public static TrajectoryLimits Default => new(1.5f, 3.0f);
}

/// <summary>
/// Plans smooth point-to-point motion in joint space.
/// </summary>
/// <remarks>
/// <para>
/// Trajectories are generated with a quintic polynomial, which gives zero velocity <em>and</em> zero
/// acceleration at both endpoints. The zero-acceleration endpoints are the point: a trapezoidal or
/// cubic profile leaves a step in acceleration at the boundaries, and on a geared arm that step is what
/// you hear as a knock and feel as backlash.
/// </para>
/// <para>
/// All joints are synchronised onto a single duration, so the arm moves along a straight line in joint
/// space and every joint starts and stops together.
/// </para>
/// </remarks>
public static class TrajectoryPlanner
{
    /// <summary>
    /// Computes the shortest duration that respects <paramref name="limits"/> for every joint.
    /// </summary>
    /// <param name="start">Start positions, radians.</param>
    /// <param name="goal">Goal positions, radians.</param>
    /// <param name="limits">Velocity and acceleration ceilings.</param>
    /// <returns>Duration in seconds; zero when start and goal coincide.</returns>
    /// <remarks>
    /// For a quintic profile the peak velocity is 1.875·Δ/T and the peak acceleration is 5.7735·Δ/T².
    /// Inverting both and taking the larger requirement gives the fastest duration that violates neither.
    /// </remarks>
    public static double ComputeDuration(
        ReadOnlySpan<float> start,
        ReadOnlySpan<float> goal,
        TrajectoryLimits limits)
    {
        ValidateDimensions(start, goal);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limits.MaxVelocity, 0f);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limits.MaxAcceleration, 0f);

        const double PeakVelocityFactor = 1.875;
        const double PeakAccelerationFactor = 5.7735;

        double duration = 0;

        for (int i = 0; i < start.Length; i++)
        {
            double delta = Math.Abs(goal[i] - start[i]);

            if (delta < 1e-9)
            {
                continue;
            }

            double velocityLimited = PeakVelocityFactor * delta / limits.MaxVelocity;
            double accelerationLimited = Math.Sqrt(PeakAccelerationFactor * delta / limits.MaxAcceleration);

            duration = Math.Max(duration, Math.Max(velocityLimited, accelerationLimited));
        }

        return duration;
    }

    /// <summary>
    /// Samples a joint-space trajectory at a fixed rate.
    /// </summary>
    /// <param name="start">Start positions, radians.</param>
    /// <param name="goal">Goal positions, radians.</param>
    /// <param name="durationSeconds">Total duration. Must be positive.</param>
    /// <param name="sampleRateHz">Samples per second.</param>
    /// <returns>The sampled trajectory, ending exactly at <paramref name="goal"/>.</returns>
    public static IReadOnlyList<TrajectoryPoint> Plan(
        ReadOnlySpan<float> start,
        ReadOnlySpan<float> goal,
        double durationSeconds,
        int sampleRateHz = 500)
    {
        ValidateDimensions(start, goal);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(durationSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRateHz, 1);

        int jointCount = start.Length;
        int sampleCount = (int)Math.Ceiling(durationSeconds * sampleRateHz) + 1;
        var points = new List<TrajectoryPoint>(sampleCount);

        for (int sample = 0; sample < sampleCount; sample++)
        {
            double time = Math.Min(sample / (double)sampleRateHz, durationSeconds);
            double normalised = time / durationSeconds;

            (double s, double sDot, double sDoubleDot) = EvaluateQuintic(normalised, durationSeconds);

            var positions = new float[jointCount];
            var velocities = new float[jointCount];
            var accelerations = new float[jointCount];

            for (int joint = 0; joint < jointCount; joint++)
            {
                double delta = goal[joint] - start[joint];
                positions[joint] = (float)(start[joint] + (delta * s));
                velocities[joint] = (float)(delta * sDot);
                accelerations[joint] = (float)(delta * sDoubleDot);
            }

            points.Add(new TrajectoryPoint(time, positions, velocities, accelerations));
        }

        return points;
    }

    /// <summary>
    /// Plans a trajectory whose duration is derived from <paramref name="limits"/>.
    /// </summary>
    public static IReadOnlyList<TrajectoryPoint> Plan(
        ReadOnlySpan<float> start,
        ReadOnlySpan<float> goal,
        TrajectoryLimits limits,
        int sampleRateHz = 500)
    {
        double duration = ComputeDuration(start, goal, limits);

        if (duration <= 0)
        {
            // Start and goal coincide. Returning a single point rather than an empty list means callers
            // can execute the result unconditionally without a special case.
            return [new TrajectoryPoint(0, start.ToArray(), new float[start.Length], new float[start.Length])];
        }

        return Plan(start, goal, duration, sampleRateHz);
    }

    /// <summary>
    /// Interpolates a trajectory at an arbitrary time, clamping outside its bounds.
    /// </summary>
    /// <remarks>
    /// Useful when the execution loop runs at a different rate from the planning rate — a trajectory
    /// planned at 100 Hz can be replayed on the 500 Hz control loop without stepping.
    /// </remarks>
    public static TrajectoryPoint Sample(IReadOnlyList<TrajectoryPoint> trajectory, double timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(trajectory);

        if (trajectory.Count == 0)
        {
            throw new ArgumentException("Trajectory is empty.", nameof(trajectory));
        }

        if (timeSeconds <= trajectory[0].TimeSeconds)
        {
            return trajectory[0];
        }

        if (timeSeconds >= trajectory[^1].TimeSeconds)
        {
            return trajectory[^1];
        }

        int high = trajectory.Count - 1;
        int low = 0;

        while (high - low > 1)
        {
            int mid = (low + high) / 2;

            if (trajectory[mid].TimeSeconds <= timeSeconds)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        TrajectoryPoint before = trajectory[low];
        TrajectoryPoint after = trajectory[high];

        double span = after.TimeSeconds - before.TimeSeconds;
        float t = span <= 0 ? 0f : (float)((timeSeconds - before.TimeSeconds) / span);

        int jointCount = before.Positions.Length;
        var positions = new float[jointCount];
        var velocities = new float[jointCount];
        var accelerations = new float[jointCount];

        for (int i = 0; i < jointCount; i++)
        {
            positions[i] = RobotMath.Lerp(before.Positions[i], after.Positions[i], t);
            velocities[i] = RobotMath.Lerp(before.Velocities[i], after.Velocities[i], t);
            accelerations[i] = RobotMath.Lerp(before.Accelerations[i], after.Accelerations[i], t);
        }

        return new TrajectoryPoint(timeSeconds, positions, velocities, accelerations);
    }

    /// <summary>
    /// Evaluates the normalised quintic scaling function and its first two derivatives.
    /// </summary>
    /// <param name="u">Normalised time in [0, 1].</param>
    /// <param name="duration">Total duration, used to rescale the derivatives into real time.</param>
    private static (double Position, double Velocity, double Acceleration) EvaluateQuintic(double u, double duration)
    {
        // s(u) = 10u³ − 15u⁴ + 6u⁵ — the unique quintic with s(0)=0, s(1)=1 and zero first and second
        // derivatives at both ends.
        double u2 = u * u;
        double u3 = u2 * u;
        double u4 = u3 * u;
        double u5 = u4 * u;

        double s = (10 * u3) - (15 * u4) + (6 * u5);
        double dsDu = (30 * u2) - (60 * u3) + (30 * u4);
        double d2sDu2 = (60 * u) - (180 * u2) + (120 * u3);

        return (s, dsDu / duration, d2sDu2 / (duration * duration));
    }

    private static void ValidateDimensions(ReadOnlySpan<float> start, ReadOnlySpan<float> goal)
    {
        if (start.Length == 0)
        {
            throw new ArgumentException("Trajectory must cover at least one joint.", nameof(start));
        }

        if (start.Length != goal.Length)
        {
            throw new ArgumentException(
                $"Start has {start.Length} joints but goal has {goal.Length}.",
                nameof(goal));
        }
    }
}
