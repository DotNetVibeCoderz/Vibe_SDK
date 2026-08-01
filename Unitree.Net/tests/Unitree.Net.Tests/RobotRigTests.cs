using System.Numerics;
using Shouldly;
using Unitree.Net.Core;
using Unitree.Net.Simulation;

namespace Unitree.Net.Tests;

/// <summary>
/// Tests for the rig descriptions that drive both the simulation and the 3D viewport.
/// </summary>
/// <remarks>
/// The rig is the single description shared by the kinematics and the renderer, which makes it the
/// place a mistake shows up as a limb that never moves or a robot that floats above the floor. These
/// assertions are what caught H1 being modelled with 21 joints when the platform has 19.
/// </remarks>
public sealed class RobotRigTests
{
    public static TheoryData<RobotModel> EveryModel()
    {
        var data = new TheoryData<RobotModel>();

        foreach (RobotModel model in RobotRig.SupportedModels)
        {
            data.Add(model);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void RigBuildsForEverySupportedModel(RobotModel model)
    {
        RobotRig rig = RobotRig.For(model);

        rig.Model.ShouldBe(model);
        rig.DisplayName.ShouldNotBeNullOrWhiteSpace();
        rig.Links.ShouldNotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void JointCountMatchesThePlatform(RobotModel model)
    {
        RobotRig rig = RobotRig.For(model);

        // The rig's own constructor asserts this too. Repeating it here is what turns "the wizard
        // crashed on startup" into a named test failure.
        rig.JointCount.ShouldBe(RobotModelInfo.GetActuatedJointCount(model));
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void EveryJointIndexIsDrivenByExactlyOneLink(RobotModel model)
    {
        RobotRig rig = RobotRig.For(model);
        var seen = new HashSet<int>();

        foreach (RigLink link in rig.Links.Where(link => link.JointIndex >= 0))
        {
            link.JointIndex.ShouldBeLessThan(rig.JointCount);

            seen.Add(link.JointIndex).ShouldBeTrue(
                $"joint {link.JointIndex} is claimed by more than one link — the second would silently win");
        }

        // A gap means a joint the pose array carries that nothing renders: the limb simply never
        // moves, and nothing reports why.
        seen.Count.ShouldBe(rig.JointCount);
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void LinksFormATreeWithParentsAheadOfChildren(RobotModel model)
    {
        RobotRig rig = RobotRig.For(model);
        var defined = new HashSet<string>(StringComparer.Ordinal);

        rig.Links[0].Parent.ShouldBeNull("the first link must be the root");

        foreach (RigLink link in rig.Links)
        {
            if (link.Parent is { } parent)
            {
                // The viewport nests scene-graph nodes in list order, so a forward reference would
                // attach the child to nothing and drop the whole subtree.
                defined.ShouldContain(parent, $"'{link.Name}' names a parent that appears later");
            }

            defined.Add(link.Name).ShouldBeTrue($"'{link.Name}' is defined twice");
        }
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void ContactLinksExist(RobotModel model)
    {
        RobotRig rig = RobotRig.For(model);

        rig.ContactLinks.ShouldNotBeEmpty();

        foreach (string name in rig.ContactLinks)
        {
            rig.Links.ShouldContain(link => link.Name == name);
        }

        rig.ContactLinks.Count.ShouldBe(rig.IsQuadruped ? 4 : 2);
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void RotationAxesAreUnitVectorsOrFixed(RobotModel model)
    {
        RobotRig rig = RobotRig.For(model);

        foreach (RigLink link in rig.Links)
        {
            if (link.JointIndex < 0)
            {
                link.Axis.ShouldBe(Vector3.Zero, $"'{link.Name}' has no joint, so it must not rotate");
                continue;
            }

            // setRotationFromAxisAngle requires a normalised axis; a non-unit one silently scales the
            // rotation and the limb ends up in the wrong place.
            link.Axis.Length().ShouldBe(1f, 0.0001f, $"'{link.Name}' has a non-unit rotation axis");
        }
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void StandingHeightIsPlausible(RobotModel model)
    {
        RobotRig rig = RobotRig.For(model);

        // Derived from the same link lengths that draw the leg, so a robot cannot be drawn floating
        // above the floor or sunk into it.
        rig.StandingHeight.ShouldBeGreaterThan(0.15f);
        rig.StandingHeight.ShouldBeLessThan(1.2f);
    }

    [Fact]
    public void QuadrupedsShareTheGoJointLayout()
    {
        foreach (RobotModel model in RobotRig.SupportedModels.Where(RobotModelInfo.IsQuadruped))
        {
            RobotRig rig = RobotRig.For(model);

            // The first twelve joints are always the leg chain in unitree_go order. The wheeled
            // variants append four drive wheels after it rather than renumbering anything.
            rig.NeutralPose.Count.ShouldBe(rig.JointCount);
            rig.JointCount.ShouldBe(rig.IsWheeled ? GoJoint.Count + 4 : GoJoint.Count);
            rig.ContactLinks.ShouldBe(["FR_foot", "FL_foot", "RR_foot", "RL_foot"]);

            for (int leg = 0; leg < 4; leg++)
            {
                rig.Links.ShouldContain(link => link.JointIndex == (leg * 3) + 1);
            }
        }
    }

    [Theory]
    [InlineData(RobotModel.Go2W)]
    [InlineData(RobotModel.B2W)]
    public void WheeledVariantsDriveTheirWheels(RobotModel model)
    {
        RobotRig rig = RobotRig.For(model);

        rig.IsWheeled.ShouldBeTrue();
        rig.WheelRadius.ShouldBeGreaterThan(0.05f);

        foreach (string name in rig.ContactLinks)
        {
            RigLink wheel = rig.Links.First(link => link.Name == name);

            // A wheel that is not a joint cannot turn, which is how the W variants ended up modelled
            // with twelve joints when the platform has sixteen.
            wheel.JointIndex.ShouldBeGreaterThanOrEqualTo(GoJoint.Count);
            wheel.Axis.ShouldBe(Vector3.UnitY);
        }
    }

    [Theory]
    [InlineData(RobotModel.Go2)]
    [InlineData(RobotModel.B2)]
    public void WalkingVariantsHaveFixedFeet(RobotModel model)
    {
        RobotRig rig = RobotRig.For(model);

        rig.IsWheeled.ShouldBeFalse();
        rig.WheelRadius.ShouldBe(0f);

        foreach (string name in rig.ContactLinks)
        {
            rig.Links.First(link => link.Name == name).JointIndex.ShouldBe(-1);
        }
    }

    [Fact]
    public void H1HasAPitchOnlyAnkleAndNoWrists()
    {
        RobotRig h1 = RobotRig.For(RobotModel.H1);

        // 19 joints only works out if the ankle is pitch-only and the arms stop at the elbow. Getting
        // this wrong is what the joint-count assertion originally caught.
        h1.JointCount.ShouldBe(19);
        h1.Links.ShouldNotContain(link => link.Name == "left_wrist_roll");
        h1.Links.First(link => link.Name == "left_foot").JointIndex.ShouldBe(-1);
    }

    [Fact]
    public void G1HasFullWristsAndAThreeAxisWaist()
    {
        RobotRig g1 = RobotRig.For(RobotModel.G1);

        g1.JointCount.ShouldBe(29);
        g1.Links.ShouldContain(link => link.Name == "left_wrist_yaw");
        g1.Links.ShouldContain(link => link.Name == "waist_roll");
        g1.Links.ShouldContain(link => link.Name == "waist_pitch");
    }

    [Fact]
    public void UnknownModelIsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => RobotRig.For(RobotModel.Unknown));
    }
}
