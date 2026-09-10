using System.Numerics;
using PollenRobotics.Net.Core;
using PollenRobotics.Net.Core.Geometry;

namespace PollenRobotics.Net.Kinematics;

/// <summary>
/// The geometry of a six-branch parallel neck: where each branch is anchored on the base and on the
/// moving platform, and how long the links are.
/// </summary>
/// <param name="BaseAnchors">Six base anchor points, in the base frame, metres.</param>
/// <param name="PlatformAnchors">Six platform anchor points, in the platform frame, metres.</param>
/// <param name="CrankLength">Length of the servo horn, metres.</param>
/// <param name="RodLength">Length of the push rod, metres.</param>
/// <param name="HomeHeight">Platform height above the base at the neutral pose, metres.</param>
/// <param name="CrankAxes">Unit vector of each servo rotation axis, base frame.</param>
/// <param name="CrankZeroDirections">Unit vector each horn points along at zero, base frame.</param>
public sealed record StewartGeometry(
    IReadOnlyList<Vector3> BaseAnchors,
    IReadOnlyList<Vector3> PlatformAnchors,
    double CrankLength,
    double RodLength,
    double HomeHeight,
    IReadOnlyList<Vector3> CrankAxes,
    IReadOnlyList<Vector3> CrankZeroDirections)
{
    /// <summary>Number of branches. Six, always - the name of the mechanism says so.</summary>
    public const int BranchCount = 6;

    /// <summary>
    /// A rotary Stewart platform sized to match Reachy Mini's neck.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pollen publishes the mechanism (six branches driven by rotary servos) and the pose envelope
    /// (+/-40 degrees of pitch and roll) but not the link geometry, so the radii and heights below
    /// are chosen to be the right scale for a desk robot, and the rod and horn are then
    /// <b>derived</b> from the envelope by <see cref="ForEnvelope"/> rather than guessed. Guessing
    /// them produced a mechanism that could not reach its own documented limits.
    /// </para>
    /// <para>
    /// Good enough to drive the simulator and to sanity-check a pose before sending it. <b>Not</b>
    /// the numbers to use if you are commanding branch angles directly on hardware: the daemon does
    /// its own kinematics and takes a pose, which is the supported path. See
    /// <c>docs/kinematics.md</c>.
    /// </para>
    /// </remarks>
    public static StewartGeometry ReachyMiniApproximation { get; } = ForEnvelope(
        baseRadius: 0.045,
        platformRadius: 0.022,
        basePairSeparation: Angle.FromDegrees(26),
        platformPairSeparation: Angle.FromDegrees(20),
        homeHeight: 0.055,
        maxTilt: Angle.FromDegrees(40),
        maxYaw: Angle.FromDegrees(25),
        maxLift: 0.012);

    /// <summary>
    /// Builds a geometry whose rod and horn are sized to cover a pose envelope.
    /// </summary>
    /// <param name="baseRadius">Radius of the base anchor circle, metres.</param>
    /// <param name="platformRadius">Radius of the platform anchor circle, metres.</param>
    /// <param name="basePairSeparation">How far each base pair straddles its axis.</param>
    /// <param name="platformPairSeparation">How far each platform pair straddles its axis.</param>
    /// <param name="homeHeight">Platform height above the base at rest, metres.</param>
    /// <param name="maxTilt">Largest combined pitch/roll tilt the neck must reach.</param>
    /// <param name="maxYaw">Largest yaw the neck must reach.</param>
    /// <param name="maxLift">Largest vertical offset, metres.</param>
    /// <param name="margin">How much headroom to leave on the horn beyond the measured swing.</param>
    /// <remarks>
    /// <para>
    /// A rotary branch changes its leg distance by at most the horn length, so the horn has to be
    /// at least half the swing in leg distance across the envelope, and the rod wants to sit in the
    /// middle of that range. Measuring the swing and sizing from it is the only way to get a
    /// mechanism that provably covers its envelope.
    /// </para>
    /// <para>
    /// The dominant cost is tilt, not yaw: tilting by <c>t</c> moves an anchor vertically by
    /// <c>platformRadius * sin(t)</c>, so the horn length scales with the platform radius. That is
    /// why a compact neck has a small platform triangle.
    /// </para>
    /// </remarks>
    public static StewartGeometry ForEnvelope(
        double baseRadius,
        double platformRadius,
        Angle basePairSeparation,
        Angle platformPairSeparation,
        double homeHeight,
        Angle maxTilt,
        Angle maxYaw,
        double maxLift,
        double margin = 1.2)
    {
        // A probe with arbitrary links: only the anchor positions matter for measuring the swing.
        StewartGeometry probe = Build(
            baseRadius, platformRadius, basePairSeparation, platformPairSeparation,
            crankLength: 0.02, rodLength: 0.08, homeHeight);

        double minimum = double.MaxValue;
        double maximum = 0;
        var home = new Vector3(0, 0, (float)homeHeight);

        for (int bearing = 0; bearing < 360; bearing += 15)
        {
            double radians = bearing * Math.PI / 180;

            for (int step = 0; step <= 4; step++)
            {
                Angle tilt = maxTilt * (step / 4.0);

                foreach (double yawScale in new[] { -1.0, 0.0, 1.0 })
                foreach (double lift in new[] { -maxLift, 0, maxLift })
                {
                    Pose pose = Pose.FromRpy(0, 0, lift,
                        Angle.FromRadians(tilt.Radians * Math.Cos(radians)),
                        Angle.FromRadians(tilt.Radians * Math.Sin(radians)),
                        maxYaw * yawScale);

                    for (int branch = 0; branch < BranchCount; branch++)
                    {
                        Vector3 anchor = home + pose.Position + Vector3.Transform(probe.PlatformAnchors[branch], pose.Orientation);
                        double reach = (anchor - probe.BaseAnchors[branch]).Length();

                        minimum = Math.Min(minimum, reach);
                        maximum = Math.Max(maximum, reach);
                    }
                }
            }
        }

        return Build(
            baseRadius,
            platformRadius,
            basePairSeparation,
            platformPairSeparation,
            crankLength: (maximum - minimum) / 2 * margin,
            rodLength: (minimum + maximum) / 2,
            homeHeight);
    }

    /// <summary>
    /// Builds a symmetric six-branch geometry.
    /// </summary>
    /// <param name="baseRadius">Radius of the base anchor circle, metres.</param>
    /// <param name="platformRadius">Radius of the platform anchor circle, metres.</param>
    /// <param name="basePairSeparation">How far each base pair straddles its 120-degree axis.</param>
    /// <param name="platformPairSeparation">How far each platform pair straddles its axis.</param>
    /// <param name="crankLength">Servo horn length, metres.</param>
    /// <param name="rodLength">Push rod length, metres.</param>
    /// <param name="homeHeight">Platform height above the base at rest, metres.</param>
    /// <param name="platformPhase">
    /// How far the platform anchor triad is rotated against the base triad.
    /// </param>
    /// <remarks>
    /// <para>
    /// Anchors sit in three pairs 120 degrees apart, each pair straddling its axis by the given
    /// separation. Branch <c>i</c> on the base pairs with branch <c>i</c> on the platform.
    /// </para>
    /// <para>
    /// <paramref name="platformPhase"/> is the dimension that matters most and the one that is
    /// easiest to get wrong. Textbook 6-UPS Stewart platforms rotate the platform triad 60 degrees
    /// against the base, which is right when every leg is a linear actuator that can change length
    /// freely. A rotary platform cannot: its horn changes the leg distance by at most its own
    /// length. With a 60-degree phase the anchors swing so far during a tilt that the required leg
    /// distance ranges over 80 mm, which would need a horn longer than the platform is wide. A
    /// small phase keeps each platform anchor near its base anchor and the swing down to something
    /// a short horn can absorb.
    /// </para>
    /// </remarks>
    public static StewartGeometry Build(
        double baseRadius,
        double platformRadius,
        Angle basePairSeparation,
        Angle platformPairSeparation,
        double crankLength,
        double rodLength,
        double homeHeight,
        Angle? platformPhase = null)
    {
        double phase = (platformPhase ?? Angle.FromDegrees(12)).Radians;

        var baseAnchors = new Vector3[BranchCount];
        var platformAnchors = new Vector3[BranchCount];
        var crankAxes = new Vector3[BranchCount];
        var crankZero = new Vector3[BranchCount];

        for (int pair = 0; pair < 3; pair++)
        {
            double pairAngle = pair * 2 * Math.PI / 3;

            for (int side = 0; side < 2; side++)
            {
                int i = (pair * 2) + side;
                double sign = side == 0 ? -1 : 1;

                double baseAngle = pairAngle + (sign * basePairSeparation.Radians);
                baseAnchors[i] = new Vector3(
                    (float)(baseRadius * Math.Cos(baseAngle)),
                    (float)(baseRadius * Math.Sin(baseAngle)),
                    0);

                double platformAngle = pairAngle + phase + (sign * platformPairSeparation.Radians);
                platformAnchors[i] = new Vector3(
                    (float)(platformRadius * Math.Cos(platformAngle)),
                    (float)(platformRadius * Math.Sin(platformAngle)),
                    0);

                // The horn sweeps in the vertical plane tangent to the base circle, so its axis is
                // the radial direction and it points along the tangent at zero. Alternating the
                // tangent direction per side mirrors the two branches of a pair, which is what
                // keeps the mechanism symmetric in roll.
                crankAxes[i] = new Vector3((float)Math.Cos(baseAngle), (float)Math.Sin(baseAngle), 0);
                crankZero[i] = new Vector3((float)(-sign * Math.Sin(baseAngle)), (float)(sign * Math.Cos(baseAngle)), 0);
            }
        }

        return new StewartGeometry(baseAnchors, platformAnchors, crankLength, rodLength, homeHeight, crankAxes, crankZero);
    }
}

/// <summary>
/// Inverse and forward kinematics for a rotary Stewart platform.
/// </summary>
/// <remarks>
/// The inverse problem has a closed form and is what the simulator calls every tick. The forward
/// problem does not, and <see cref="SolveForward"/> iterates - it is there for reading a rig back,
/// not for a control loop.
/// </remarks>
public sealed class StewartPlatform
{
    private readonly StewartGeometry _geometry;
    private readonly double[] _homeAngles = new double[StewartGeometry.BranchCount];

    /// <summary>The geometry this solver was built for.</summary>
    public StewartGeometry Geometry => _geometry;

    /// <summary>
    /// The raw horn angles at the home pose, which this solver reports as zero.
    /// </summary>
    /// <remarks>
    /// A rotary Stewart platform has no reason for its horn angles to be zero when the platform is
    /// level - that depends entirely on the link lengths. Real hardware defines servo zero at the
    /// home pose and reports everything relative to it, and so does this solver. Without the offset
    /// a neutral head reads as a hundred degrees of branch travel, which looks like a robot in
    /// trouble.
    /// </remarks>
    public IReadOnlyList<double> HomeAngles => _homeAngles;

    /// <summary>Creates a solver for a geometry.</summary>
    public StewartPlatform(StewartGeometry geometry)
    {
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));

        // Calibrate: solve the home pose once with no offset applied, and treat the result as zero.
        SolveRaw(Pose.Identity, _homeAngles);
    }

    /// <summary>
    /// Solves the six servo angles that place the platform at <paramref name="pose"/>.
    /// </summary>
    /// <param name="pose">Platform pose relative to its home position.</param>
    /// <param name="angles">Receives six angles in radians.</param>
    /// <exception cref="ArgumentException"><paramref name="angles"/> is not six long.</exception>
    /// <exception cref="PollenRoboticsException">The pose is outside the mechanism's reach.</exception>
    public void SolveInverse(Pose pose, Span<double> angles)
    {
        if (angles.Length != StewartGeometry.BranchCount)
        {
            throw new ArgumentException($"Expected {StewartGeometry.BranchCount} angles, got {angles.Length}.", nameof(angles));
        }

        SolveRaw(pose, angles);

        for (int i = 0; i < angles.Length; i++)
        {
            angles[i] -= _homeAngles[i];
        }
    }

    /// <summary>Solves the branch angles without applying the home offset.</summary>
    private void SolveRaw(Pose pose, Span<double> angles)
    {
        var home = new Vector3(0, 0, (float)_geometry.HomeHeight);

        for (int i = 0; i < StewartGeometry.BranchCount; i++)
        {
            Vector3 baseAnchor = _geometry.BaseAnchors[i];

            // Where the platform anchor ends up once the requested pose is applied.
            Vector3 platformAnchor = home + pose.Position + Vector3.Transform(_geometry.PlatformAnchors[i], pose.Orientation);
            Vector3 reach = platformAnchor - baseAnchor;

            angles[i] = SolveBranch(reach, _geometry.CrankAxes[i], _geometry.CrankZeroDirections[i], i);
        }
    }

    /// <summary>Solves the six servo angles, returning a fresh array.</summary>
    public double[] SolveInverse(Pose pose)
    {
        double[] angles = new double[StewartGeometry.BranchCount];
        SolveInverse(pose, angles);
        return angles;
    }

    /// <summary>True when <paramref name="pose"/> is reachable, without throwing if it is not.</summary>
    public bool IsReachable(Pose pose)
    {
        try
        {
            Span<double> scratch = stackalloc double[StewartGeometry.BranchCount];
            SolveInverse(pose, scratch);
            return true;
        }
        catch (PollenRoboticsException)
        {
            return false;
        }
    }

    /// <summary>
    /// Recovers the platform pose from six servo angles by iterating the inverse solution.
    /// </summary>
    /// <remarks>
    /// A rotary Stewart platform has no closed-form forward solution, so this runs a damped
    /// Gauss-Newton search on the six-dimensional pose. It converges in a handful of iterations
    /// from a sensible seed and is not fast enough for a 500 Hz loop - read the pose from the
    /// daemon instead, which is what <c>get_current_head_pose</c> is for.
    /// </remarks>
    public Pose SolveForward(ReadOnlySpan<double> angles, Pose? seed = null, int maxIterations = 40, double tolerance = 1e-7)
    {
        if (angles.Length != StewartGeometry.BranchCount)
        {
            throw new ArgumentException($"Expected {StewartGeometry.BranchCount} angles, got {angles.Length}.", nameof(angles));
        }

        // Six pose parameters: x, y, z, roll, pitch, yaw.
        Span<double> x = stackalloc double[6];
        if (seed is { } s)
        {
            (Angle roll, Angle pitch, Angle yaw) = s.Rpy;
            x[0] = s.Position.X;
            x[1] = s.Position.Y;
            x[2] = s.Position.Z;
            x[3] = roll.Radians;
            x[4] = pitch.Radians;
            x[5] = yaw.Radians;
        }

        Span<double> residual = stackalloc double[6];
        Span<double> perturbed = stackalloc double[6];
        Span<double> jacobian = stackalloc double[36];
        Span<double> step = stackalloc double[6];
        const double Epsilon = 1e-6;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            if (!TryResidual(x, angles, residual))
            {
                // Wandered outside the workspace - shrink back toward the seed and retry.
                for (int i = 0; i < 6; i++)
                {
                    x[i] *= 0.5;
                }

                continue;
            }

            double error = 0;
            for (int i = 0; i < 6; i++)
            {
                error += residual[i] * residual[i];
            }

            if (error < tolerance * tolerance)
            {
                break;
            }

            for (int column = 0; column < 6; column++)
            {
                double original = x[column];
                x[column] = original + Epsilon;
                if (!TryResidual(x, angles, perturbed))
                {
                    x[column] = original - Epsilon;
                    if (!TryResidual(x, angles, perturbed))
                    {
                        x[column] = original;
                        for (int row = 0; row < 6; row++)
                        {
                            jacobian[(row * 6) + column] = 0;
                        }

                        continue;
                    }

                    x[column] = original;
                    for (int row = 0; row < 6; row++)
                    {
                        jacobian[(row * 6) + column] = (residual[row] - perturbed[row]) / Epsilon;
                    }

                    continue;
                }

                x[column] = original;
                for (int row = 0; row < 6; row++)
                {
                    jacobian[(row * 6) + column] = (perturbed[row] - residual[row]) / Epsilon;
                }
            }

            if (!LinearAlgebra.SolveDamped(jacobian, residual, step, damping: 1e-6))
            {
                break;
            }

            for (int i = 0; i < 6; i++)
            {
                x[i] -= step[i];
            }
        }

        return Pose.FromRpy(x[0], x[1], x[2], Angle.FromRadians(x[3]), Angle.FromRadians(x[4]), Angle.FromRadians(x[5]));
    }

    private bool TryResidual(ReadOnlySpan<double> x, ReadOnlySpan<double> target, Span<double> residual)
    {
        Pose pose = Pose.FromRpy(x[0], x[1], x[2], Angle.FromRadians(x[3]), Angle.FromRadians(x[4]), Angle.FromRadians(x[5]));
        try
        {
            Span<double> solved = stackalloc double[StewartGeometry.BranchCount];
            SolveInverse(pose, solved);
            for (int i = 0; i < 6; i++)
            {
                residual[i] = solved[i] - target[i];
            }

            return true;
        }
        catch (PollenRoboticsException)
        {
            return false;
        }
    }

    /// <summary>
    /// The classic rotary-Stewart branch equation.
    /// </summary>
    /// <remarks>
    /// With the horn tip constrained to a circle of radius <c>crank</c> about the servo axis and the
    /// rod a fixed length, the required horn angle satisfies
    /// <c>L^2 = |reach|^2 + crank^2 - 2*crank*(reach . dir(angle))</c>, which reduces to
    /// <c>A sin(a) + B cos(a) = C</c> and solves through the auxiliary-angle identity. No solution
    /// means the platform anchor is closer than <c>rod - crank</c> or further than <c>rod + crank</c>
    /// from the base anchor: the pose is simply outside the mechanism.
    /// </remarks>
    private double SolveBranch(Vector3 reach, Vector3 axis, Vector3 zeroDirection, int branch)
    {
        double crank = _geometry.CrankLength;
        double rod = _geometry.RodLength;

        // The horn sweeps in the plane normal to the servo axis; zeroDirection and this cross
        // product span it.
        Vector3 quadrature = Vector3.Cross(axis, zeroDirection);

        double a = 2 * crank * Vector3.Dot(reach, quadrature);
        double b = 2 * crank * Vector3.Dot(reach, zeroDirection);
        double c = (rod * rod) - reach.LengthSquared() - (crank * crank);

        double magnitude = Math.Sqrt((a * a) + (b * b));
        if (magnitude < 1e-12)
        {
            throw new PollenRoboticsException(
                $"Stewart branch {branch} is degenerate: the platform anchor sits on the servo axis.");
        }

        double ratio = c / magnitude;
        if (Math.Abs(ratio) > 1)
        {
            double distance = reach.Length();
            throw new PollenRoboticsException(
                $"Pose is outside the neck workspace: branch {branch} needs a rod reach of {distance * 1000:0.#} mm, " +
                $"but the mechanism spans {(rod - crank) * 1000:0.#} to {(rod + crank) * 1000:0.#} mm.");
        }

        // The auxiliary-angle identity gives two solutions - the horn can reach the same rod length
        // swung either way. Taking whichever asin returns picks between them arbitrarily, and the
        // arbitrary choice flips as the pose moves, which shows up as a branch angle jumping by
        // most of a revolution mid-trajectory. Prefer the solution nearer zero: that is the elbow
        // the mechanism is actually assembled with.
        double phase = Math.Atan2(b, a);
        double first = Normalize(Math.Asin(ratio) - phase);
        double second = Normalize(Math.PI - Math.Asin(ratio) - phase);

        return Math.Abs(first) <= Math.Abs(second) ? first : second;
    }

    /// <summary>Wraps an angle into (-pi, pi].</summary>
    private static double Normalize(double radians)
    {
        double wrapped = Math.IEEERemainder(radians, 2 * Math.PI);
        return wrapped <= -Math.PI ? wrapped + (2 * Math.PI) : wrapped;
    }
}
