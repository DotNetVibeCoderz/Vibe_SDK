using System.Numerics;
using PollenRobotics.Net.Core.Geometry;

namespace PollenRobotics.Net.Kinematics;

/// <summary>
/// One revolute link, described by modified Denavit-Hartenberg parameters.
/// </summary>
/// <param name="Name">Joint name, matching <see cref="Core.Robots.RobotDescription"/>.</param>
/// <param name="Alpha">Twist of the previous axis, radians.</param>
/// <param name="A">Link length along the previous x axis, metres.</param>
/// <param name="D">Link offset along the current z axis, metres.</param>
/// <param name="ThetaOffset">Constant added to the joint value, radians.</param>
/// <param name="Lower">Lower joint limit.</param>
/// <param name="Upper">Upper joint limit.</param>
public readonly record struct DhLink(
    string Name,
    double Alpha,
    double A,
    double D,
    double ThetaOffset,
    Angle Lower,
    Angle Upper);

/// <summary>
/// Forward kinematics, a geometric Jacobian and damped least-squares inverse kinematics for an
/// open revolute chain.
/// </summary>
/// <remarks>
/// <para>
/// Reachy 2's arm has seven joints and the task is six-dimensional, so the arm is redundant: an
/// infinite family of joint vectors reaches any given pose. The solver resolves that by staying
/// near the seed configuration, which is what makes successive calls along a trajectory produce
/// continuous joint motion instead of jumping between elbow configurations.
/// </para>
/// <para>
/// Seed the solver with the arm's <b>present</b> position, not with zeros. Seeding from zero is how
/// you get a solution that is geometrically correct and physically absurd - the elbow flips through
/// the torso on the way there.
/// </para>
/// </remarks>
public sealed class SerialChain
{
    private readonly DhLink[] _links;
    private readonly Pose _baseTransform;
    private readonly Pose _toolTransform;

    /// <summary>The links, base to tip.</summary>
    public IReadOnlyList<DhLink> Links => _links;

    /// <summary>Number of joints.</summary>
    public int JointCount => _links.Length;

    /// <summary>Creates a chain.</summary>
    /// <param name="links">Links ordered base to tip.</param>
    /// <param name="baseTransform">Where the chain's base sits in the robot frame.</param>
    /// <param name="toolTransform">Offset from the last link to the controlled point.</param>
    public SerialChain(IEnumerable<DhLink> links, Pose? baseTransform = null, Pose? toolTransform = null)
    {
        _links = [.. links];
        if (_links.Length == 0)
        {
            throw new ArgumentException("A chain needs at least one link.", nameof(links));
        }

        _baseTransform = baseTransform ?? Pose.Identity;
        _toolTransform = toolTransform ?? Pose.Identity;
    }

    /// <summary>The tool pose for a joint vector, in the robot frame.</summary>
    public Pose ForwardKinematics(ReadOnlySpan<double> joints)
    {
        RequireJointCount(joints.Length);

        Pose current = _baseTransform;
        for (int i = 0; i < _links.Length; i++)
        {
            current = current.Compose(LinkTransform(_links[i], joints[i]));
        }

        return current.Compose(_toolTransform);
    }

    /// <summary>The tool pose of every intermediate frame, base to tip, for rendering a rig.</summary>
    public Pose[] ForwardKinematicsAll(ReadOnlySpan<double> joints)
    {
        RequireJointCount(joints.Length);

        var poses = new Pose[_links.Length + 1];
        Pose current = _baseTransform;
        poses[0] = current;

        for (int i = 0; i < _links.Length; i++)
        {
            current = current.Compose(LinkTransform(_links[i], joints[i]));
            poses[i + 1] = current;
        }

        poses[^1] = current.Compose(_toolTransform);
        return poses;
    }

    /// <summary>
    /// The 6xN geometric Jacobian at <paramref name="joints"/>, row-major, linear rows first.
    /// </summary>
    public void Jacobian(ReadOnlySpan<double> joints, Span<double> jacobian)
    {
        RequireJointCount(joints.Length);
        if (jacobian.Length < 6 * _links.Length)
        {
            throw new ArgumentException($"Expected room for {6 * _links.Length} values.", nameof(jacobian));
        }

        Pose[] frames = ForwardKinematicsAll(joints);
        Vector3 tip = frames[^1].Position;

        for (int i = 0; i < _links.Length; i++)
        {
            // For a revolute joint the linear column is axis x (tip - origin) and the angular
            // column is the axis itself, both expressed in the base frame.
            Pose frame = frames[i + 1];
            Vector3 axis = Vector3.Transform(Vector3.UnitZ, frame.Orientation);
            Vector3 lever = tip - frame.Position;
            Vector3 linear = Vector3.Cross(axis, lever);

            jacobian[(0 * _links.Length) + i] = linear.X;
            jacobian[(1 * _links.Length) + i] = linear.Y;
            jacobian[(2 * _links.Length) + i] = linear.Z;
            jacobian[(3 * _links.Length) + i] = axis.X;
            jacobian[(4 * _links.Length) + i] = axis.Y;
            jacobian[(5 * _links.Length) + i] = axis.Z;
        }
    }

    /// <summary>
    /// Solves for a joint vector reaching <paramref name="target"/>, starting from <paramref name="seed"/>.
    /// </summary>
    /// <param name="target">Desired tool pose in the robot frame.</param>
    /// <param name="seed">Starting configuration - use the arm's present joint positions.</param>
    /// <param name="solution">Receives the answer. May alias <paramref name="seed"/>.</param>
    /// <param name="options">Convergence settings.</param>
    /// <returns>Whether the solver converged inside its tolerances.</returns>
    public bool TrySolveInverse(Pose target, ReadOnlySpan<double> seed, Span<double> solution, IkOptions? options = null)
    {
        RequireJointCount(seed.Length);
        RequireJointCount(solution.Length);

        IkOptions opts = options ?? IkOptions.Default;
        seed.CopyTo(solution);

        Span<double> jacobian = stackalloc double[6 * _links.Length];
        Span<double> error = stackalloc double[6];
        Span<double> step = stackalloc double[_links.Length];

        for (int iteration = 0; iteration < opts.MaxIterations; iteration++)
        {
            Pose current = ForwardKinematics(solution);
            PoseError(current, target, error);

            double positionError = Math.Sqrt((error[0] * error[0]) + (error[1] * error[1]) + (error[2] * error[2]));
            double orientationError = Math.Sqrt((error[3] * error[3]) + (error[4] * error[4]) + (error[5] * error[5]));

            if (positionError < opts.PositionTolerance && orientationError < opts.OrientationTolerance)
            {
                return true;
            }

            Jacobian(solution, jacobian);

            if (!LinearAlgebra.SolveDamped(jacobian, error, step, opts.Damping, rows: 6, columns: _links.Length))
            {
                return false;
            }

            for (int i = 0; i < _links.Length; i++)
            {
                double next = solution[i] + (step[i] * opts.StepScale);

                if (opts.RespectJointLimits)
                {
                    next = Math.Clamp(next, _links[i].Lower.Radians, _links[i].Upper.Radians);
                }

                solution[i] = next;
            }
        }

        // One last check: the final iteration may have landed inside tolerance.
        Pose settled = ForwardKinematics(solution);
        PoseError(settled, target, error);
        double finalPosition = Math.Sqrt((error[0] * error[0]) + (error[1] * error[1]) + (error[2] * error[2]));
        double finalOrientation = Math.Sqrt((error[3] * error[3]) + (error[4] * error[4]) + (error[5] * error[5]));
        return finalPosition < opts.PositionTolerance && finalOrientation < opts.OrientationTolerance;
    }

    /// <summary>Solves inverse kinematics, returning a fresh array, or null when it does not converge.</summary>
    public double[]? SolveInverse(Pose target, ReadOnlySpan<double> seed, IkOptions? options = null)
    {
        double[] solution = new double[_links.Length];
        return TrySolveInverse(target, seed, solution, options) ? solution : null;
    }

    /// <summary>
    /// The six-vector taking <paramref name="current"/> to <paramref name="target"/>: translation
    /// then rotation, as an axis-angle vector.
    /// </summary>
    private static void PoseError(Pose current, Pose target, Span<double> error)
    {
        Vector3 translation = target.Position - current.Position;
        error[0] = translation.X;
        error[1] = translation.Y;
        error[2] = translation.Z;

        Quaternion delta = Quaternion.Multiply(target.Orientation, Quaternion.Conjugate(current.Orientation));

        // A quaternion and its negation are the same rotation; picking the positive-w branch keeps
        // the error vector on the short way round instead of taking the 350-degree path.
        if (delta.W < 0)
        {
            delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
        }

        double vectorLength = Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y) + (delta.Z * delta.Z));
        if (vectorLength < 1e-9)
        {
            error[3] = error[4] = error[5] = 0;
            return;
        }

        double angle = 2 * Math.Atan2(vectorLength, delta.W);
        double scale = angle / vectorLength;
        error[3] = delta.X * scale;
        error[4] = delta.Y * scale;
        error[5] = delta.Z * scale;
    }

    private static Pose LinkTransform(DhLink link, double theta)
    {
        double t = theta + link.ThetaOffset;

        // Modified DH: Rx(alpha) then Tx(a) then Rz(theta) then Tz(d).
        Pose twist = new(new Vector3((float)link.A, 0, 0), Rotation.FromRpy(Angle.FromRadians(link.Alpha), Angle.Zero, Angle.Zero));
        Pose rotate = new(new Vector3(0, 0, (float)link.D), Rotation.FromRpy(Angle.Zero, Angle.Zero, Angle.FromRadians(t)));
        return twist.Compose(rotate);
    }

    private void RequireJointCount(int count)
    {
        if (count != _links.Length)
        {
            throw new ArgumentException($"This chain has {_links.Length} joints, got {count}.");
        }
    }
}

/// <summary>Convergence settings for <see cref="SerialChain.TrySolveInverse"/>.</summary>
public sealed record IkOptions
{
    /// <summary>Maximum Gauss-Newton iterations.</summary>
    public int MaxIterations { get; init; } = 120;

    /// <summary>Position tolerance in metres.</summary>
    public double PositionTolerance { get; init; } = 5e-4;

    /// <summary>Orientation tolerance in radians.</summary>
    public double OrientationTolerance { get; init; } = 5e-3;

    /// <summary>Squared damping factor for the least-squares step.</summary>
    public double Damping { get; init; } = 1e-4;

    /// <summary>Fraction of the computed step actually taken, which damps oscillation.</summary>
    public double StepScale { get; init; } = 0.75;

    /// <summary>Clamp each iterate into the joint limits.</summary>
    public bool RespectJointLimits { get; init; } = true;

    /// <summary>The defaults: sub-millimetre position, quarter-degree orientation.</summary>
    public static IkOptions Default { get; } = new();

    /// <summary>Looser and faster, for a preview that updates as the user drags a handle.</summary>
    public static IkOptions Interactive { get; } = new()
    {
        MaxIterations = 40,
        PositionTolerance = 2e-3,
        OrientationTolerance = 2e-2,
    };
}
