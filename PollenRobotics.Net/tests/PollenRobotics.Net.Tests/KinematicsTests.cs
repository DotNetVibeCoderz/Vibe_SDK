using System.Numerics;
using PollenRobotics.Net.Core;
using Xunit;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Kinematics;
using Shouldly;

namespace PollenRobotics.Net.Tests;

/// <summary>Round-trip and convention checks for the pose type.</summary>
public class PoseTests
{
    [Fact]
    public void RowMajorRoundTripsThroughTheWireFormat()
    {
        Pose original = Pose.FromRpy(0.012, -0.004, 0.058,
            Angle.FromDegrees(12), Angle.FromDegrees(-25), Angle.FromDegrees(40));

        Pose recovered = Pose.FromRowMajor(original.ToRowMajor());

        recovered.Position.X.ShouldBe(original.Position.X, 1e-5);
        recovered.Position.Y.ShouldBe(original.Position.Y, 1e-5);
        recovered.Position.Z.ShouldBe(original.Position.Z, 1e-5);

        (Angle roll, Angle pitch, Angle yaw) = recovered.Rpy;
        roll.Degrees.ShouldBe(12, 0.01);
        pitch.Degrees.ShouldBe(-25, 0.01);
        yaw.Degrees.ShouldBe(40, 0.01);
    }

    /// <summary>
    /// The translation must land in the fourth column, not the fourth row.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Numerics.Matrix4x4"/> stores translation in M41..M43, which is the
    /// transpose of the robotics convention the daemons use. Getting this backwards produces a
    /// matrix that round-trips through this SDK perfectly and moves the robot somewhere else.
    /// </remarks>
    [Fact]
    public void TranslationSitsInTheFourthColumn()
    {
        Pose pose = Pose.FromRpy(0.1, 0.2, 0.3, Angle.Zero, Angle.Zero, Angle.Zero);
        double[] matrix = pose.ToRowMajor();

        matrix[3].ShouldBe(0.1, 1e-6);
        matrix[7].ShouldBe(0.2, 1e-6);
        matrix[11].ShouldBe(0.3, 1e-6);
        matrix[15].ShouldBe(1.0, 1e-6);
    }

    [Fact]
    public void ComposingWithTheInverseGivesIdentity()
    {
        Pose pose = Pose.FromRpy(0.05, -0.02, 0.03,
            Angle.FromDegrees(15), Angle.FromDegrees(30), Angle.FromDegrees(-45));

        Pose identity = pose.Compose(pose.Inverse());

        identity.Position.Length().ShouldBeLessThan(1e-5f);
        (Angle roll, Angle pitch, Angle yaw) = identity.Rpy;
        Math.Abs(roll.Degrees).ShouldBeLessThan(0.01);
        Math.Abs(pitch.Degrees).ShouldBeLessThan(0.01);
        Math.Abs(yaw.Degrees).ShouldBeLessThan(0.01);
    }

    /// <summary>At the pitch singularity the decomposition must stay finite.</summary>
    [Fact]
    public void RpyIsDefinedAtThePitchSingularity()
    {
        Pose pose = Pose.FromRpy(0, 0, 0, Angle.Zero, Angle.FromDegrees(90), Angle.Zero);
        (Angle _, Angle pitch, Angle _) = pose.Rpy;

        double.IsNaN(pitch.Radians).ShouldBeFalse();
        Math.Abs(pitch.Degrees).ShouldBe(90, 0.1);
    }
}

/// <summary>Checks on the Reachy Mini neck solver.</summary>
public class StewartPlatformTests
{
    private readonly StewartPlatform _neck = new(StewartGeometry.ReachyMiniApproximation);

    /// <summary>
    /// The home pose must read as zero on every branch.
    /// </summary>
    /// <remarks>
    /// Caught by looking at the gallery: a neutral head was showing 118 degrees on three branches
    /// and -33 on the other three, with half the arcs red. A rotary Stewart platform has no reason
    /// for its horn angles to be zero when the platform is level, so the solver calibrates against
    /// the home pose and reports relative to it - which is what the hardware does too.
    /// </remarks>
    [Fact]
    public void HomePoseSolvesToZeroOnEveryBranch()
    {
        double[] angles = _neck.SolveInverse(Pose.Identity);

        angles.Length.ShouldBe(6);

        foreach (double angle in angles)
        {
            Math.Abs(angle * 180 / Math.PI).ShouldBeLessThan(0.001);
        }
    }

    /// <summary>
    /// Every pose inside the published envelope must solve.
    /// </summary>
    /// <remarks>
    /// Pollen documents pitch and roll to +/-40 degrees. If the approximate link geometry cannot
    /// reach that, the approximation is wrong rather than the envelope.
    /// </remarks>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(40, 0, 0)]
    [InlineData(-40, 0, 0)]
    [InlineData(0, 40, 0)]
    [InlineData(0, -40, 0)]
    [InlineData(0, 0, 25)]
    [InlineData(0, 0, -25)]
    [InlineData(28, 28, 20)]
    [InlineData(-28, -28, -20)]
    public void ThePublishedEnvelopeIsReachable(double rollDegrees, double pitchDegrees, double yawDegrees)
    {
        Pose pose = Pose.FromRpy(0, 0, 0,
            Angle.FromDegrees(rollDegrees), Angle.FromDegrees(pitchDegrees), Angle.FromDegrees(yawDegrees));

        Should.NotThrow(() => _neck.SolveInverse(pose));
    }

    /// <summary>
    /// Branch angles must move continuously as the pose sweeps.
    /// </summary>
    /// <remarks>
    /// The branch equation has two solutions, and picking between them arbitrarily produces an
    /// angle that jumps by most of a revolution partway through a trajectory. On hardware that is a
    /// servo slamming from one end of its travel to the other.
    /// </remarks>
    [Fact]
    public void BranchAnglesAreContinuousAcrossASweep()
    {
        double[] previous = _neck.SolveInverse(Pose.Identity);

        for (int degrees = 1; degrees <= 40; degrees++)
        {
            double[] current = _neck.SolveInverse(
                Pose.FromRpy(0, 0, 0, Angle.Zero, Angle.FromDegrees(degrees), Angle.Zero));

            for (int branch = 0; branch < current.Length; branch++)
            {
                double step = Math.Abs(current[branch] - previous[branch]) * 180 / Math.PI;

                step.ShouldBeLessThan(
                    12,
                    $"Branch {branch} jumped {step:0.#} deg between {degrees - 1} and {degrees} deg of pitch.");
            }

            previous = current;
        }
    }

    /// <summary>
    /// A pure lift must move all six branches by a similar amount.
    /// </summary>
    /// <remarks>
    /// Not exactly equal: the platform triad is rotated slightly against the base triad, so the six
    /// branches are congruent rather than identical and their magnitudes differ by about a degree.
    /// Asserting exact equality was wrong about the mechanism, not about the solver.
    /// </remarks>
    [Fact]
    public void LiftingThePlatformMovesEveryBranchTheSameWay()
    {
        double[] raised = _neck.SolveInverse(Pose.FromRpy(0, 0, 0.010, Angle.Zero, Angle.Zero, Angle.Zero));

        double[] magnitudes = [.. raised.Select(Math.Abs)];

        magnitudes.Min().ShouldBeGreaterThan(0.01);
        ((magnitudes.Max() - magnitudes.Min()) * 180 / Math.PI).ShouldBeLessThan(3);

        // Each pair is mirrored, so the branches alternate in sign around the ring.
        for (int i = 0; i < raised.Length; i += 2)
        {
            (raised[i] * raised[i + 1]).ShouldBeLessThan(0, $"Branches {i} and {i + 1} should oppose each other.");
        }
    }

    [Fact]
    public void APoseFarOutsideTheWorkspaceIsRejected()
    {
        Pose unreachable = Pose.FromRpy(0, 0, 0.5, Angle.Zero, Angle.Zero, Angle.Zero);

        Should.Throw<PollenRoboticsException>(() => _neck.SolveInverse(unreachable));
        _neck.IsReachable(unreachable).ShouldBeFalse();
    }

    [Fact]
    public void ForwardKinematicsRecoversTheCommandedPose()
    {
        Pose commanded = Pose.FromRpy(0, 0, 0.004, Angle.FromDegrees(10), Angle.FromDegrees(-14), Angle.Zero);

        double[] angles = _neck.SolveInverse(commanded);
        Pose recovered = _neck.SolveForward(angles, seed: commanded);

        (Angle roll, Angle pitch, Angle _) = recovered.Rpy;
        (Angle expectedRoll, Angle expectedPitch, Angle _) = commanded.Rpy;

        roll.Degrees.ShouldBe(expectedRoll.Degrees, 1.0);
        pitch.Degrees.ShouldBe(expectedPitch.Degrees, 1.0);
    }
}

/// <summary>Checks on the seven-axis arm chain.</summary>
public class SerialChainTests
{
    [Fact]
    public void ForwardKinematicsPutsTheRightArmOnTheRightSide()
    {
        SerialChain arm = ReachyArmChains.Arm(ArmSide.Right);
        Pose pose = arm.ForwardKinematics(new double[7]);

        // The robot frame has y pointing left, so the right arm sits at negative y.
        pose.Position.Y.ShouldBeLessThan(0);
    }

    [Fact]
    public void InverseKinematicsReachesAPoseInsideTheWorkspace()
    {
        SerialChain arm = ReachyArmChains.Arm(ArmSide.Right);

        double[] seed = [-0.9, -0.3, 0, -1.0, 0, 0, 0];
        Pose start = arm.ForwardKinematics(seed);

        // A target a few centimetres from a known-reachable configuration is certainly reachable.
        var target = new Pose(start.Position + new Vector3(0.03f, 0.02f, -0.04f), start.Orientation);

        double[]? solution = arm.SolveInverse(target, seed);

        solution.ShouldNotBeNull();

        Pose reached = arm.ForwardKinematics(solution);
        (reached.Position - target.Position).Length().ShouldBeLessThan(0.002f);
    }

    /// <summary>
    /// Seeding from the present configuration must keep successive solutions close together.
    /// </summary>
    /// <remarks>
    /// This is the property that makes a Cartesian trajectory produce continuous joint motion. Lose
    /// it and the arm reaches every waypoint correctly while flipping its elbow between them.
    /// </remarks>
    [Fact]
    public void SeededSolutionsStayNearTheSeed()
    {
        SerialChain arm = ReachyArmChains.Arm(ArmSide.Right);

        double[] current = [-0.9, -0.3, 0, -1.0, 0, 0, 0];
        Pose start = arm.ForwardKinematics(current);

        for (int step = 1; step <= 10; step++)
        {
            var target = new Pose(start.Position + new Vector3(0.005f * step, 0, 0), start.Orientation);
            double[]? next = arm.SolveInverse(target, current);

            next.ShouldNotBeNull();

            for (int joint = 0; joint < next.Length; joint++)
            {
                double delta = Math.Abs(next[joint] - current[joint]) * 180 / Math.PI;
                delta.ShouldBeLessThan(25, $"Joint {joint} moved {delta:0.#} deg for a 5 mm step.");
            }

            current = next;
        }
    }

    [Fact]
    public void SolutionsRespectJointLimits()
    {
        SerialChain arm = ReachyArmChains.Arm(ArmSide.Right);

        // Deliberately far away, so the solver runs into the limits rather than converging early.
        var target = new Pose(new Vector3(1.5f, -1.5f, 1.5f), System.Numerics.Quaternion.Identity);
        double[] solution = new double[7];

        arm.TrySolveInverse(target, new double[7], solution);

        for (int i = 0; i < solution.Length; i++)
        {
            DhLink link = arm.Links[i];
            solution[i].ShouldBeGreaterThanOrEqualTo(link.Lower.Radians - 1e-9);
            solution[i].ShouldBeLessThanOrEqualTo(link.Upper.Radians + 1e-9);
        }
    }
}
