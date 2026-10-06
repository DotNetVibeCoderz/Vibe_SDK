using PollenRobotics.Net.Core.Robots;
using Shouldly;
using Xunit;

namespace PollenRobotics.Net.Tests;

/// <summary>
/// Checks the MicroDuck joint table against the source it was transcribed from.
/// </summary>
/// <remarks>
/// The table used to be a guess - fifteen joints in leg-leg-head order with a beak on the end - and
/// nothing in the suite disagreed with it, because every other test took the catalogue as the
/// definition of truth. These assertions restate what
/// <c>src/PollenRobotics.Net.MicroDuck/Reference/robot_walk.xml</c> says, so the catalogue is
/// checked against Pollen's model rather than against itself.
/// </remarks>
public class MicroDuckCatalogueTests
{
    [Fact]
    public void TheJointOrderPutsTheHeadBetweenTheLegs()
    {
        string[] names = [.. RobotCatalog.MicroDuck.Joints.Select(j => j.Name)];

        names.ShouldBe([
            "left_hip_yaw", "left_hip_roll", "left_hip_pitch", "left_knee", "left_ankle",
            "neck_pitch", "head_pitch", "head_yaw", "head_roll",
            "right_hip_yaw", "right_hip_roll", "right_hip_pitch", "right_knee", "right_ankle",
        ]);
    }

    /// <summary>The bill is fixed geometry: the model has a mouth_tip site but no joint.</summary>
    [Fact]
    public void ThereIsNoBeakJoint()
    {
        RobotCatalog.MicroDuck.Joints.ShouldNotContain(j => j.Name.Contains("beak", StringComparison.OrdinalIgnoreCase));
        RobotCatalog.MicroDuck.JointCount.ShouldBe(14);
    }

    /// <summary>Hip yaw is asymmetric, and mirrored between the legs.</summary>
    /// <remarks>
    /// Commanding the same yaw on both legs does not give a symmetric stance - one leg reaches its
    /// limit while the other has travel left. The old table had both at a symmetric +/-45, which
    /// would have let a command through that the robot refuses.
    /// </remarks>
    [Fact]
    public void HipYawMirrorsBetweenTheLegs()
    {
        JointDescriptor left = RobotCatalog.MicroDuck["left_hip_yaw"];
        JointDescriptor right = RobotCatalog.MicroDuck["right_hip_yaw"];

        left.Lower.Degrees.ShouldBe(-25, 0.5);
        left.Upper.Degrees.ShouldBe(30, 0.5);

        right.Lower.Degrees.ShouldBe(-left.Upper.Degrees, 0.5);
        right.Upper.Degrees.ShouldBe(-left.Lower.Degrees, 0.5);
    }

    /// <summary>Head yaw reaches far beyond a neck's usual travel.</summary>
    [Fact]
    public void HeadYawReachesBehindTheDuck()
    {
        JointDescriptor yaw = RobotCatalog.MicroDuck["head_yaw"];

        yaw.Upper.Degrees.ShouldBeGreaterThan(160);
        yaw.Lower.Degrees.ShouldBeLessThan(-160);
    }
}
