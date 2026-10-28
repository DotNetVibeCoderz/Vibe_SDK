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

    [Fact]
    public void Go2IsDrawnWithManufacturerMeshesOnEveryLink()
    {
        RobotRig go2 = RobotRig.For(RobotModel.Go2);

        go2.Links.ShouldAllBe(link => link.Mesh != null);
        go2["FL_thigh"].Mesh!.Value.Asset.ShouldBe("go2/thigh.glb");
        go2["FR_thigh"].Mesh!.Value.Asset.ShouldBe("go2/thigh_mirror.glb");
    }

    [Fact]
    public void Go2HipMeshesAreFlippedTheWayTheUrdfDrawsThem()
    {
        RobotRig go2 = RobotRig.For(RobotModel.Go2);

        // go2_description.urdf: FL rpy 0 0 0, FR pi 0 0, RL 0 pi 0, RR pi pi 0. Getting one wrong puts
        // that hip's motor housing inside the body instead of outside it.
        go2["FL_hip"].Mesh!.Value.Rotation.ShouldBe(Vector3.Zero);
        go2["FR_hip"].Mesh!.Value.Rotation.ShouldBe(new Vector3(MathF.PI, 0f, 0f));
        go2["RL_hip"].Mesh!.Value.Rotation.ShouldBe(new Vector3(0f, MathF.PI, 0f));
        go2["RR_hip"].Mesh!.Value.Rotation.ShouldBe(new Vector3(MathF.PI, MathF.PI, 0f));
    }

    [Fact]
    public void B2FollowsItsUrdfAndDrawsEachLegFromItsOwnMeshes()
    {
        RobotRig b2 = RobotRig.For(RobotModel.B2);

        // b2_description.urdf: hips at ±0.3285 / ±0.072, thigh 0.11973 out.
        b2["RR_hip"].Offset.ShouldBe(new Vector3(-0.3285f, -0.072f, 0f));
        b2["FL_thigh"].Offset.ShouldBe(new Vector3(0f, 0.11973f, 0f));

        b2["RR_hip"].Mesh!.Value.Asset.ShouldBe("b2/RR_hip.glb");
        b2["RR_hip"].Mesh!.Value.Rotation.ShouldBe(Vector3.Zero);
        b2["FR_calf"].Mesh!.Value.Asset.ShouldBe("b2/FR_calf.glb");

        // The calf mesh ends in the foot, so the foot link has no mesh of its own.
        b2["FL_foot"].Mesh.ShouldBeNull();
    }

    [Theory]
    [InlineData(RobotModel.G1, "left_foot", 0f, 0.11851f, -0.75686f)]
    [InlineData(RobotModel.G1, "right_foot", 0f, -0.11851f, -0.75686f)]
    [InlineData(RobotModel.G1, "left_hand", 0.24127f, 0.15165f, 0.09523f)]
    [InlineData(RobotModel.H1, "left_ankle", 0.03947f, 0.20286f, -0.9742f)]
    [InlineData(RobotModel.H1, "right_ankle", 0.03947f, -0.20286f, -0.9742f)]
    [InlineData(RobotModel.H1, "left_forearm", 0.0185f, 0.21353f, 0.10661f)]
    [InlineData(RobotModel.H12, "left_foot", 0f, 0.163f, -0.9832f)]
    [InlineData(RobotModel.H12, "right_foot", 0f, -0.163f, -0.9832f)]
    [InlineData(RobotModel.H12, "left_wrist_roll", 0.127f, 0.2095f, 0.09498f)]
    [InlineData(RobotModel.R1, "left_foot", -0.00211f, 0.08633f, -0.68756f)]
    [InlineData(RobotModel.R1, "right_foot", -0.00211f, -0.08633f, -0.68756f)]
    [InlineData(RobotModel.R1, "left_wrist_roll", 0.16087f, 0.13861f, 0.03096f)]
    [InlineData(RobotModel.R1, "head", 0.02751f, 0f, 0.4205f)]
    public void HumanoidForwardKinematicsMatchTheUrdf(RobotModel model, string link, float x, float y, float z)
    {
        // Reference positions come from each platform's URDF in unitree_ros, chained in Python at the
        // zero pose — independently of the rig. The fixed joint rotations (G1's hip roll and knee,
        // every platform's shoulders, R1's hips) are what make these come out right: drop one and a
        // foot or hand moves by centimetres.
        Vector3 position = WorldAtZero(RobotRig.For(model), link).Translation;

        position.X.ShouldBe(x, 0.0005f);
        position.Y.ShouldBe(y, 0.0005f);
        position.Z.ShouldBe(z, 0.0005f);
    }

    [Theory]
    [InlineData(RobotModel.G1)]
    [InlineData(RobotModel.H1)]
    [InlineData(RobotModel.H12)]
    [InlineData(RobotModel.R1)]
    public void HumanoidsKeepTheLinkNamesTheGaitLooksUp(RobotModel model)
    {
        RobotRig rig = RobotRig.For(model);

        // The gait finds its joints by name. A rig transcribed from a URDF names its links after the
        // URDF's unless told otherwise, and a missing name is a limb that silently never moves.
        foreach (string side in (string[])["left", "right"])
        {
            foreach (string part in (string[])["hip_pitch", "calf", "ankle", "shoulder_pitch", "forearm"])
            {
                rig[$"{side}_{part}"].JointIndex.ShouldBeGreaterThanOrEqualTo(0);
            }

            rig.ContactLinks.ShouldContain($"{side}_foot");
        }
    }

    [Fact]
    public void G1JointsFollowUnitreeHgOrder()
    {
        RobotRig g1 = RobotRig.For(RobotModel.G1);

        // unitree_hg order: legs 0-11, waist 12-14, arms 15-28.
        g1["left_hip_pitch"].JointIndex.ShouldBe(0);
        g1["right_hip_pitch"].JointIndex.ShouldBe(6);
        g1["waist_yaw"].JointIndex.ShouldBe(12);
        g1["left_shoulder_pitch"].JointIndex.ShouldBe(15);
        g1["right_wrist_yaw"].JointIndex.ShouldBe(28);
    }

    [Theory]
    [InlineData(RobotModel.Go2W, "go2w/wheel.glb", 0.2264f, 0.088f)]
    [InlineData(RobotModel.B2W, "b2w/RL_foot.glb", 0.35f, 0.112f)]
    public void WheeledVariantsCarryTheirOwnCalvesAndWheels(RobotModel model, string rearLeftWheel, float axle, float radius)
    {
        RobotRig rig = RobotRig.For(model);

        // The axle sits where the URDF's wheel joint does, and the wheel radius drives how fast the
        // wheels spin for a given ground speed, so both come from the meshes rather than a guess.
        rig["RL_foot"].Offset.ShouldBe(new Vector3(0f, 0f, -axle));
        rig.WheelRadius.ShouldBe(radius);
        rig["RL_foot"].Mesh!.Value.Asset.ShouldBe(rearLeftWheel);
        rig["RL_foot"].JointIndex.ShouldBeGreaterThanOrEqualTo(GoJoint.Count);
    }

    [Fact]
    public void Go2WFlipsItsOneWheelMeshOntoTheLeftSide()
    {
        RobotRig go2w = RobotRig.For(RobotModel.Go2W);

        // unitree_mujoco's go2w.xml draws the left wheels with quat="0 1 0 0": half a turn about X.
        go2w["FL_foot"].Mesh!.Value.Rotation.ShouldBe(new Vector3(MathF.PI, 0f, 0f));
        go2w["FR_foot"].Mesh!.Value.Rotation.ShouldBe(Vector3.Zero);
        go2w["FL_hip"].Mesh!.Value.Asset.ShouldBe("go2/hip.glb");
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void EveryPlatformIsDrawnFromManufacturerMeshes(RobotModel model)
    {
        // Every rig link that has shapes has a mesh, or sits on a link whose mesh draws it — a foot
        // inside B2's calf, a contact point inside H1's ankle, G1's torso inside its waist link.
        RobotRig rig = RobotRig.For(model);

        rig.Links.Count(link => link.Mesh != null).ShouldBeGreaterThan(rig.Links.Count / 2);
        rig.Links[0].Mesh.ShouldNotBeNull();
    }

    /// <summary>Chains a link's transforms at zero joint angles. Row-vector convention throughout.</summary>
    private static Matrix4x4 WorldAtZero(RobotRig rig, string name)
    {
        RigLink link = rig[name];
        Vector3 r = link.Rotation;

        // URDF rpy is Rz(yaw) Ry(pitch) Rx(roll) acting on column vectors, which with row vectors is
        // X, then Y, then Z.
        Matrix4x4 local = Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateRotationZ(r.Z)
            * Matrix4x4.CreateTranslation(link.Offset);

        return link.Parent is null ? local : local * WorldAtZero(rig, link.Parent);
    }

    [Theory]
    [MemberData(nameof(EveryModel))]
    public void EveryMeshTheRigNamesShipsWithTheSimulator(RobotModel model)
    {
        // A missing file degrades quietly to primitives at runtime, which is the right behaviour for a
        // viewer and the wrong one for a build: this is where a typo in an asset name gets caught.
        string root = Path.Combine(RepositoryRoot(), "apps", "Unitree.Net.Simulator", "wwwroot", "models");

        foreach (RigLink link in RobotRig.For(model).Links)
        {
            if (link.Mesh is { } mesh)
            {
                File.Exists(Path.Combine(root, mesh.Asset)).ShouldBeTrue($"{link.Name} names {mesh.Asset}");
            }
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Unitree.Net.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unitree.Net.slnx not found above the test output directory.");
    }
}
