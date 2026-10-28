using System.Numerics;
using Unitree.Net.Core;

namespace Unitree.Net.Simulation;

/// <summary>
/// The primitive a rig link is drawn with.
/// </summary>
public enum RigShapeKind
{
    /// <summary>A rectangular box.</summary>
    Box,

    /// <summary>A capsule — a cylinder with hemispherical caps. Used for limb segments.</summary>
    Capsule,

    /// <summary>A cylinder. Used for actuator housings and wheels.</summary>
    Cylinder,

    /// <summary>A sphere. Used for foot contact pads.</summary>
    Sphere,
}

/// <summary>
/// Which surface treatment a link is drawn with.
/// </summary>
/// <remarks>
/// These are roles rather than colours. The viewport maps them to the active theme, so a rig does not
/// have to know whether it is being drawn light or dark.
/// </remarks>
public enum RigSurface
{
    /// <summary>Outer body shell — the large painted panels.</summary>
    Shell,

    /// <summary>Structural limb segment.</summary>
    Limb,

    /// <summary>An actuator housing, drawn as an accent so the joints read at a glance.</summary>
    Actuator,

    /// <summary>Ground-contact material — foot pads and tyres.</summary>
    Contact,

    /// <summary>A sensor pod: LiDAR, depth camera.</summary>
    Sensor,
}

/// <summary>
/// The drawable primitive attached to a link.
/// </summary>
/// <param name="Kind">Which primitive to build.</param>
/// <param name="Size">
/// For <see cref="RigShapeKind.Box"/>, the full extents in metres. For
/// <see cref="RigShapeKind.Capsule"/> and <see cref="RigShapeKind.Cylinder"/>, X is the radius and Y
/// the length. For <see cref="RigShapeKind.Sphere"/>, X is the radius.
/// </param>
/// <param name="Center">Centre of the primitive in the link's own frame, in metres.</param>
/// <param name="Surface">Which surface treatment to draw it with.</param>
/// <param name="AxisAlong">
/// Which local axis a capsule or cylinder runs along: 0 = X, 1 = Y, 2 = Z. Ignored for other kinds.
/// </param>
public readonly record struct RigShape(
    RigShapeKind Kind,
    Vector3 Size,
    Vector3 Center,
    RigSurface Surface,
    int AxisAlong = 1);

/// <summary>
/// A manufacturer mesh drawn for a link in place of its primitives.
/// </summary>
/// <param name="Asset">
/// Path of the GLB file relative to the viewer's model root, e.g. <c>go2/hip.glb</c>. The file holds
/// the URDF visual converted by <c>tools/meshes/convert_meshes.py</c>, in the link frame, Z up, metres.
/// </param>
/// <param name="Offset">The URDF visual origin's translation within the link frame, in metres.</param>
/// <param name="Rotation">The URDF visual origin's roll, pitch and yaw, in radians.</param>
/// <remarks>
/// A mesh is decoration, not description. The link's <see cref="RigLink.Shapes"/> stay the rig's
/// geometry and are what a viewer falls back to when the file cannot be loaded, so a missing asset
/// costs fidelity rather than a robot.
/// </remarks>
public readonly record struct RigMesh(string Asset, Vector3 Offset, Vector3 Rotation)
{
    /// <summary>A mesh drawn at the link origin with the given URDF roll/pitch/yaw.</summary>
    /// <param name="asset">Path of the GLB file relative to the viewer's model root.</param>
    /// <param name="roll">Rotation about X, in radians.</param>
    /// <param name="pitch">Rotation about Y, in radians.</param>
    /// <param name="yaw">Rotation about Z, in radians.</param>
    public static RigMesh At(string asset, float roll = 0f, float pitch = 0f, float yaw = 0f) =>
        new(asset, Vector3.Zero, new Vector3(roll, pitch, yaw));
}

/// <summary>
/// One rigid body in a robot rig.
/// </summary>
/// <param name="Name">Unique link name.</param>
/// <param name="Parent">Parent link name, or <see langword="null"/> for the root.</param>
/// <param name="Offset">Translation from the parent's origin, in metres.</param>
/// <param name="Axis">Rotation axis in the link's own frame. <see cref="Vector3.Zero"/> means fixed.</param>
/// <param name="JointIndex">Index into the joint-angle array driving this link, or -1 if fixed.</param>
/// <param name="Sign">Maps joint angle sign to rotation about <paramref name="Axis"/>.</param>
/// <param name="Shapes">Primitives drawn in this link's frame.</param>
/// <remarks>
/// A rig is a plain tree, so nesting the links as scene-graph nodes gives forward kinematics for free —
/// the viewport never computes a transform itself. That is deliberate: the geometry the renderer draws
/// and the geometry the simulation moves are the same description, so they cannot drift apart.
/// </remarks>
public sealed record RigLink(
    string Name,
    string? Parent,
    Vector3 Offset,
    Vector3 Axis,
    int JointIndex,
    float Sign,
    IReadOnlyList<RigShape> Shapes)
{
    /// <summary>
    /// The manufacturer mesh drawn for this link, or <see langword="null"/> to draw
    /// <see cref="Shapes"/> only.
    /// </summary>
    public RigMesh? Mesh { get; init; }

    /// <summary>
    /// Fixed roll, pitch and yaw of this link's frame relative to its parent, in radians, applied
    /// before the joint's own rotation about <see cref="Axis"/>.
    /// </summary>
    /// <remarks>
    /// This is a URDF joint origin's <c>rpy</c>. Most joints have none; G1's hip roll, knee and
    /// shoulders do, and without it every mesh below them hangs at the wrong angle.
    /// </remarks>
    public Vector3 Rotation { get; init; }
}

/// <summary>
/// A complete kinematic and visual description of one robot platform.
/// </summary>
/// <remarks>
/// <para>
/// Dimensions follow Unitree's published URDFs closely enough to be recognisable, and are rounded to
/// the millimetre. They are <em>not</em> a substitute for the real URDF: this drives a viewport and a
/// kinematic stand-in, not a dynamics solver or a collision check.
/// </para>
/// </remarks>
public sealed class RobotRig
{
    private readonly Dictionary<string, RigLink> _byName;

    private RobotRig(
        RobotModel model,
        string displayName,
        string summary,
        int jointCount,
        float standingHeight,
        IReadOnlyList<RigLink> links,
        IReadOnlyList<string> contactLinks,
        IReadOnlyList<float> neutralPose)
    {
        Model = model;
        DisplayName = displayName;
        Summary = summary;
        JointCount = jointCount;
        StandingHeight = standingHeight;
        Links = links;
        ContactLinks = contactLinks;
        NeutralPose = neutralPose;
        _byName = links.ToDictionary(link => link.Name, StringComparer.Ordinal);
    }

    /// <summary>The platform this rig describes.</summary>
    public RobotModel Model { get; }

    /// <summary>Marketing name, e.g. "Go2".</summary>
    public string DisplayName { get; }

    /// <summary>One-line description shown in the model picker.</summary>
    public string Summary { get; }

    /// <summary>Number of actuated joints the rig expects in a pose array.</summary>
    public int JointCount { get; }

    /// <summary>Height of the body origin above the ground when standing, in metres.</summary>
    public float StandingHeight { get; }

    /// <summary>All links, parents always ahead of their children.</summary>
    public IReadOnlyList<RigLink> Links { get; }

    /// <summary>Names of the links that touch the ground, in foot-index order.</summary>
    public IReadOnlyList<string> ContactLinks { get; }

    /// <summary>Joint angles for the robot's neutral standing pose, in radians.</summary>
    public IReadOnlyList<float> NeutralPose { get; }

    /// <summary>
    /// Drive-wheel radius in metres, or zero on a platform that walks on feet.
    /// </summary>
    /// <remarks>
    /// The simulation turns ground speed into wheel rotation with this, so a rolling robot's wheels
    /// spin at a rate that matches the distance it covers rather than at an arbitrary one.
    /// </remarks>
    public float WheelRadius { get; init; }

    /// <summary>Whether this platform rolls on driven wheels rather than walking on feet.</summary>
    public bool IsWheeled => WheelRadius > 0f;

    /// <summary>Whether this rig walks on four legs.</summary>
    public bool IsQuadruped => RobotModelInfo.IsQuadruped(Model);

    /// <summary>Gets a link by name.</summary>
    /// <param name="name">The link name.</param>
    /// <exception cref="KeyNotFoundException">No link has that name.</exception>
    public RigLink this[string name] => _byName[name];

    /// <summary>Builds the rig for <paramref name="model"/>.</summary>
    /// <param name="model">The platform to describe.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model"/> has no rig.</exception>
    public static RobotRig For(RobotModel model) => model switch
    {
        RobotModel.Go2 => Quadruped(model, "Go2", "Agile 15 kg quadruped. 12 joints, unitree_go IDL.", QuadrupedSpec.Go2),
        RobotModel.Go2W => Quadruped(model, "Go2-W", "Go2 on wheels. 12 leg joints plus 4 drive wheels.", QuadrupedSpec.Go2W),
        RobotModel.B2 => Quadruped(model, "B2", "60 kg industrial quadruped rated for 40 kg payload.", QuadrupedSpec.B2),
        RobotModel.B2W => Quadruped(model, "B2-W", "B2 on wheels — 6 m/s on hard ground.", QuadrupedSpec.B2W),
        RobotModel.G1 => G1(),
        RobotModel.H1 => H1(),
        RobotModel.H12 => H12(),
        RobotModel.R1 => R1(),
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "No rig is defined for this model."),
    };

    /// <summary>Every model that has a rig, in menu order.</summary>
    public static IReadOnlyList<RobotModel> SupportedModels { get; } =
    [
        RobotModel.Go2,
        RobotModel.Go2W,
        RobotModel.B2,
        RobotModel.B2W,
        RobotModel.G1,
        RobotModel.H1,
        RobotModel.H12,
        RobotModel.R1,
    ];

    // ------------------------------------------------------------------ quadrupeds

    /// <summary>Proportions that distinguish one quadruped from another.</summary>
    private readonly record struct QuadrupedSpec(
        Vector3 Body,
        float HipX,
        float HipY,
        float AbductionY,
        float ThighLength,
        float CalfLength,
        float LimbRadius,
        float FootRadius,
        bool Wheeled,
        string? MeshSet = null,
        bool MeshPerLeg = false,
        string? WheelMeshSet = null)
    {
        // Go2 URDF: trunk 0.3762 long, hips at ±0.1934 / ±0.0465, thigh and calf both 0.213.
        // The trunk box is widened here from the URDF's 0.0935 collision width to the shell width a
        // person actually sees — this drives a viewport, not a collision check.
        //
        // The link frames are the URDF's own (base, hip, thigh, calf, foot at the same origins), which
        // is what lets unitree_ros's visual meshes hang off these links unmodified.
        internal static QuadrupedSpec Go2 { get; } =
            new(new Vector3(0.3762f, 0.190f, 0.114f), 0.1934f, 0.0465f, 0.0955f, 0.213f, 0.213f, 0.024f, 0.022f, false, "go2");

        // Go2-W shares Go2's body, hips and thighs, but its calf ends in a wheel mount whose axle sits
        // 0.2264 below the knee, not 0.213, and the wheel's radius is 0.088. Its calf and wheel meshes
        // come from unitree_mujoco, because go2w_description references DAE files it does not ship.
        internal static QuadrupedSpec Go2W { get; } =
            Go2 with { Wheeled = true, CalfLength = 0.2264f, FootRadius = 0.088f, WheelMeshSet = "go2w" };

        // B2 URDF: hips at ±0.3285 / ±0.072, thigh 0.11973 out, thigh and calf both 0.35. The calf
        // origin also sits 0.087 mm off the leg plane, which is left out. The foot radius is where the
        // calf mesh's sole actually ends, 0.035 below the foot frame, so the meshes stand on the floor.
        // Unitree ships one mesh per leg rather than mirroring, and the calf mesh includes the foot.
        internal static QuadrupedSpec B2 { get; } =
            new(new Vector3(0.660f, 0.300f, 0.190f), 0.3285f, 0.072f, 0.11973f, 0.350f, 0.350f, 0.040f, 0.035f, false, "b2", MeshPerLeg: true);

        // B2-W shares B2's frames and its body, hip and thigh meshes; its calves and 0.112 m wheels are
        // its own. Its URDF body mesh is a 72 MB variant of the same chassis, so B2's is drawn instead.
        internal static QuadrupedSpec B2W { get; } = B2 with { Wheeled = true, FootRadius = 0.112f, WheelMeshSet = "b2w" };
    }

    private static RobotRig Quadruped(RobotModel model, string name, string summary, QuadrupedSpec spec)
    {
        var links = new List<RigLink>();

        // Meshes come from unitree_ros's URDF visuals; see tools/meshes. Null when the platform has none.
        RigMesh? MeshOf(string part, float roll = 0f, float pitch = 0f) =>
            spec.MeshSet is { } set ? RigMesh.At($"{set}/{part}.glb", roll, pitch) : null;

        // A wheeled variant's calves and wheels come from a set of their own.
        RigMesh? WheelMeshOf(string part, float roll = 0f) =>
            spec.WheelMeshSet is { } set ? RigMesh.At($"{set}/{part}.glb", roll) : MeshOf(part);

        links.Add(new RigLink(
            "trunk", null, Vector3.Zero, Vector3.Zero, -1, 1f,
            [
                new RigShape(RigShapeKind.Box, spec.Body, Vector3.Zero, RigSurface.Shell),

                // A narrower deck on top reads as the removable payload plate both platforms carry.
                new RigShape(
                    RigShapeKind.Box,
                    new Vector3(spec.Body.X * 0.62f, spec.Body.Y * 0.72f, spec.Body.Z * 0.42f),
                    new Vector3(-spec.Body.X * 0.04f, 0f, spec.Body.Z * 0.60f),
                    RigSurface.Shell),

                // Head fairing and the LiDAR dome that sits on it.
                new RigShape(
                    RigShapeKind.Box,
                    new Vector3(spec.Body.X * 0.16f, spec.Body.Y * 0.60f, spec.Body.Z * 0.75f),
                    new Vector3((spec.Body.X * 0.5f) + (spec.Body.X * 0.06f), 0f, spec.Body.Z * 0.18f),
                    RigSurface.Shell),
                new RigShape(
                    RigShapeKind.Cylinder,
                    new Vector3(spec.Body.Z * 0.30f, spec.Body.Z * 0.34f, 0f),
                    new Vector3((spec.Body.X * 0.5f) + (spec.Body.X * 0.04f), 0f, spec.Body.Z * 0.72f),
                    RigSurface.Sensor,
                    AxisAlong: 2),
            ])
        {
            Mesh = MeshOf("base"),
        });

        // FR, FL, RR, RL — the order the unitree_go motor array uses.
        ReadOnlySpan<string> legNames = ["FR", "FL", "RR", "RL"];
        ReadOnlySpan<float> frontSign = [1f, 1f, -1f, -1f];
        ReadOnlySpan<float> leftSign = [-1f, 1f, -1f, 1f];

        for (int leg = 0; leg < 4; leg++)
        {
            string prefix = legNames[leg];
            float sx = frontSign[leg];
            float sy = leftSign[leg];

            int hipJoint = leg * 3;
            int thighJoint = hipJoint + 1;
            int calfJoint = hipJoint + 2;

            // Go2's URDF draws one hip mesh four ways — flipped about X on the right, about Y at the
            // rear — and gives the right-hand thigh and calf mirrored meshes of their own. B2's has a
            // separate mesh for every leg and needs no flips.
            bool right = sy < 0f;
            string mirror = right ? "_mirror" : "";
            string hipMesh = spec.MeshPerLeg ? $"{prefix}_hip" : "hip";
            string thighMesh = spec.MeshPerLeg ? $"{prefix}_thigh" : "thigh" + mirror;
            string calfMesh = spec.MeshPerLeg ? $"{prefix}_calf" : "calf" + mirror;

            // Hip: abduction about the body's X axis.
            links.Add(new RigLink(
                $"{prefix}_hip", "trunk",
                new Vector3(sx * spec.HipX, sy * spec.HipY, 0f),
                Vector3.UnitX, hipJoint, 1f,
                [
                    new RigShape(
                        RigShapeKind.Cylinder,
                        new Vector3(spec.LimbRadius * 1.55f, spec.LimbRadius * 2.4f, 0f),
                        new Vector3(0f, sy * spec.LimbRadius * 1.2f, 0f),
                        RigSurface.Actuator,
                        AxisAlong: 1),
                ])
            {
                Mesh = spec.MeshPerLeg
                    ? MeshOf(hipMesh)
                    : MeshOf(hipMesh, right ? MathF.PI : 0f, sx < 0f ? MathF.PI : 0f),
            });

            // Thigh: flexion about Y, hanging down -Z.
            links.Add(new RigLink(
                $"{prefix}_thigh", $"{prefix}_hip",
                new Vector3(0f, sy * spec.AbductionY, 0f),
                Vector3.UnitY, thighJoint, 1f,
                [
                    new RigShape(
                        RigShapeKind.Cylinder,
                        new Vector3(spec.LimbRadius * 1.35f, spec.LimbRadius * 2.0f, 0f),
                        Vector3.Zero,
                        RigSurface.Actuator,
                        AxisAlong: 1),
                    new RigShape(
                        RigShapeKind.Capsule,
                        new Vector3(spec.LimbRadius, spec.ThighLength * 0.86f, 0f),
                        new Vector3(0f, 0f, -spec.ThighLength * 0.5f),
                        RigSurface.Limb,
                        AxisAlong: 2),
                ])
            {
                Mesh = MeshOf(thighMesh),
            });

            // Calf: knee about Y.
            links.Add(new RigLink(
                $"{prefix}_calf", $"{prefix}_thigh",
                new Vector3(0f, 0f, -spec.ThighLength),
                Vector3.UnitY, calfJoint, 1f,
                [
                    new RigShape(
                        RigShapeKind.Capsule,
                        new Vector3(spec.LimbRadius * 0.72f, spec.CalfLength * 0.88f, 0f),
                        new Vector3(0f, 0f, -spec.CalfLength * 0.5f),
                        RigSurface.Limb,
                        AxisAlong: 2),
                ])
            {
                Mesh = WheelMeshOf(calfMesh),
            });

            // Foot, or a wheel on the W variants. The wheel is a driven joint — Go2-W and B2-W have
            // sixteen actuators, not twelve — so it spins about the leg's lateral axis rather than
            // being a fixed contact. It is also why the wheeled variants stand taller: the contact
            // radius is much larger.
            RigShape contact = spec.Wheeled
                ? new RigShape(
                    RigShapeKind.Cylinder,
                    new Vector3(spec.FootRadius, spec.FootRadius * 0.42f, 0f),
                    Vector3.Zero,
                    RigSurface.Contact,
                    AxisAlong: 1)
                : new RigShape(
                    RigShapeKind.Sphere,
                    new Vector3(spec.FootRadius, 0f, 0f),
                    Vector3.Zero,
                    RigSurface.Contact);

            links.Add(new RigLink(
                $"{prefix}_foot", $"{prefix}_calf",
                new Vector3(0f, 0f, -spec.CalfLength),
                spec.Wheeled ? Vector3.UnitY : Vector3.Zero,
                spec.Wheeled ? GoJoint.Count + leg : -1,
                1f,
                [contact])
            {
                // B2's calf mesh already ends in the foot; the viewport hides this link's sphere along
                // with every other primitive once the meshes are in. Go2-W draws one wheel mesh both
                // ways, flipped about X on the left as unitree_mujoco does; B2-W has one per leg.
                Mesh = spec.Wheeled
                    ? WheelMeshOf(spec.MeshPerLeg ? $"{prefix}_foot" : "wheel", spec.MeshPerLeg || right ? 0f : MathF.PI)
                    : spec.MeshPerLeg ? null : MeshOf("foot"),
            });
        }

        // Standing pose: hips level, thighs forward, knees folded back. The wheeled variants keep the
        // legs straighter because the wheel radius already supplies most of the ride height.
        float thigh = spec.Wheeled ? 0.55f : 0.80f;
        float calf = spec.Wheeled ? -1.10f : -1.55f;

        int jointCount = spec.Wheeled ? GoJoint.Count + 4 : GoJoint.Count;
        var neutral = new float[jointCount];

        for (int leg = 0; leg < 4; leg++)
        {
            neutral[(leg * 3) + 1] = thigh;
            neutral[(leg * 3) + 2] = calf;
        }

        float standing = StandingHeightOf(spec, thigh, calf);

        return new RobotRig(
            model, name, summary, jointCount, standing, links,
            ["FR_foot", "FL_foot", "RR_foot", "RL_foot"], neutral)
        {
            WheelRadius = spec.Wheeled ? spec.FootRadius : 0f,
        };
    }

    /// <summary>
    /// Computes how high the trunk origin sits when the legs hold <paramref name="thigh"/> and
    /// <paramref name="calf"/>.
    /// </summary>
    /// <remarks>
    /// Planar two-link kinematics in the sagittal plane. Getting this from the same numbers that draw
    /// the leg is what stops the robot floating above the floor or sinking into it.
    /// </remarks>
    private static float StandingHeightOf(QuadrupedSpec spec, float thigh, float calf)
    {
        float thighDrop = spec.ThighLength * MathF.Cos(thigh);
        float calfDrop = spec.CalfLength * MathF.Cos(thigh + calf);
        return thighDrop + calfDrop + spec.FootRadius;
    }

    // ------------------------------------------------------------------- humanoids
    //
    // Every humanoid is a transcription of its URDF in unitree_ros: each offset and fixed rotation below
    // is that file's joint origin, so its visual meshes hang off these links unmodified. The rows were
    // generated by tools/meshes/urdf_to_rig.py and are pinned by RobotRigTests, which chains each rig
    // at zero and compares feet and arms against positions computed from the URDF independently.
    //
    // Link names are fixed by the gait, which finds its joints by name: <side>_hip_pitch, <side>_calf
    // (knee), <side>_ankle (ankle pitch), <side>_shoulder_pitch, <side>_forearm (elbow), and <side>_foot
    // for ground contact. Joint indices follow URDF order. For G1 that is unitree_hg's order; for the
    // others the SDK has no index table yet, because their low-level state is not implemented.

    /// <summary>G1 from <c>g1_29dof_rev_1_0.urdf</c>.</summary>
    private static RobotRig G1()
    {
        var rig = new UrdfRig("g1");

        rig.Add("pelvis", null, Vector3.Zero, Vector3.Zero, "pelvis",
            [UrdfRig.Box(new Vector3(0.12f, 0.20f, 0.11f), new Vector3(0f, 0f, -0.04f), RigSurface.Shell)]);

        foreach (float side in (ReadOnlySpan<float>)[1f, -1f])
        {
            string p = side > 0 ? "left" : "right";

            rig.Add($"{p}_hip_pitch", "pelvis", new Vector3(0f, side * 0.064452f, -0.1027f), Vector3.UnitY,
                $"{p}_hip_pitch_link", [UrdfRig.Housing(0.04f)]);

            // The roll joint is pitched back by 0.1749 rad and the knee pitched forward by the same,
            // so the thigh slants while the shin hangs straight at zero.
            rig.Add($"{p}_hip_roll", $"{p}_hip_pitch", new Vector3(0f, side * 0.052f, -0.030465f), Vector3.UnitX,
                $"{p}_hip_roll_link", [], rotation: new Vector3(0f, -0.1749f, 0f));
            rig.Add($"{p}_thigh", $"{p}_hip_roll", new Vector3(0.025001f, 0f, -0.12412f), Vector3.UnitZ,
                $"{p}_hip_yaw_link", [UrdfRig.Bone(new Vector3(-0.078273f, 0f, -0.17734f), 0.04f)]);
            rig.Add($"{p}_calf", $"{p}_thigh", new Vector3(-0.078273f, side * 0.0021489f, -0.17734f), Vector3.UnitY,
                $"{p}_knee_link", [UrdfRig.Bone(new Vector3(0f, 0f, -0.30001f))], rotation: new Vector3(0f, 0.1749f, 0f));
            rig.Add($"{p}_ankle", $"{p}_calf", new Vector3(0f, -side * 9.4445e-5f, -0.30001f), Vector3.UnitY,
                $"{p}_ankle_pitch_link", []);
            rig.Add($"{p}_foot", $"{p}_ankle", new Vector3(0f, 0f, -0.017558f), Vector3.UnitX,
                $"{p}_ankle_roll_link", [UrdfRig.Box(new Vector3(0.17f, 0.07f, 0.03f), new Vector3(0.03f, 0f, -0.02f), RigSurface.Contact)]);
        }

        rig.Add("waist_yaw", "pelvis", Vector3.Zero, Vector3.UnitZ, "waist_yaw_link_rev_1_0", []);
        rig.Add("waist_roll", "waist_yaw", new Vector3(-0.0039635f, 0f, 0.044f), Vector3.UnitX, "waist_roll_link_rev_1_0", []);
        rig.Add("waist_pitch", "waist_roll", Vector3.Zero, Vector3.UnitY, "torso_link_rev_1_0", []);

        // The URDF's torso is the waist-pitch link itself. This fixed child shares its frame and
        // carries the fallback shapes, so the shoulders keep the parent name every humanoid rig uses.
        rig.Add("torso", "waist_pitch", Vector3.Zero, Vector3.Zero, null,
            [UrdfRig.Box(new Vector3(0.13f, 0.22f, 0.30f), new Vector3(0f, 0f, 0.17f), RigSurface.Shell)]);
        rig.Add("head", "torso", new Vector3(0.0039635f, 0f, -0.044f), Vector3.Zero, "head",
            [UrdfRig.Box(new Vector3(0.12f, 0.12f, 0.14f), new Vector3(0f, 0f, 0.47f), RigSurface.Shell)]);

        foreach (float side in (ReadOnlySpan<float>)[1f, -1f])
        {
            string p = side > 0 ? "left" : "right";

            // The shoulder pitch frame is rolled outward by 0.2793 rad and the roll frame rolls it
            // back, which is what tilts the shoulder actuator the way the real one sits.
            rig.Add($"{p}_shoulder_pitch", "torso",
                new Vector3(0.0039563f, side > 0 ? 0.10022f : -0.10021f, 0.24778f), Vector3.UnitY,
                $"{p}_shoulder_pitch_link", [UrdfRig.Housing(0.036f)],
                rotation: new Vector3(side * 0.27931f, 5.4949e-5f, -side * 0.00019159f));
            rig.Add($"{p}_shoulder_roll", $"{p}_shoulder_pitch", new Vector3(0f, side * 0.038f, -0.013831f), Vector3.UnitX,
                $"{p}_shoulder_roll_link", [], rotation: new Vector3(-side * 0.27925f, 0f, 0f));
            rig.Add($"{p}_upper_arm", $"{p}_shoulder_roll", new Vector3(0f, side * 0.00624f, -0.1032f), Vector3.UnitZ,
                $"{p}_shoulder_yaw_link", [UrdfRig.Bone(new Vector3(0.015783f, 0f, -0.080518f), 0.032f)]);
            rig.Add($"{p}_forearm", $"{p}_upper_arm", new Vector3(0.015783f, 0f, -0.080518f), Vector3.UnitY,
                $"{p}_elbow_link", [UrdfRig.Bone(new Vector3(0.1f, 0f, -0.01f), 0.028f)]);
            rig.Add($"{p}_wrist_roll", $"{p}_forearm", new Vector3(0.1f, side * 0.00188791f, -0.01f), Vector3.UnitX,
                $"{p}_wrist_roll_link", []);
            rig.Add($"{p}_wrist_pitch", $"{p}_wrist_roll", new Vector3(0.038f, 0f, 0f), Vector3.UnitY,
                $"{p}_wrist_pitch_link", []);
            rig.Add($"{p}_wrist_yaw", $"{p}_wrist_pitch", new Vector3(0.046f, 0f, 0f), Vector3.UnitZ,
                $"{p}_wrist_yaw_link", []);
            rig.Add($"{p}_hand", $"{p}_wrist_yaw", new Vector3(0.0415f, side * 0.003f, 0f), Vector3.Zero,
                $"{p}_rubber_hand", [UrdfRig.Box(new Vector3(0.09f, 0.035f, 0.06f), new Vector3(0.04f, 0f, 0f), RigSurface.Contact)]);
        }

        // Pelvis height with the sole on the floor, from the URDF's foot mesh with the legs in the
        // gait's standing pose (hip -0.10, knee 0.18, ankle -0.044). The same holds for every humanoid.
        return rig.Build(RobotModel.G1, "G1", "1.32 m humanoid, 29 joints with dual 7-DoF arms.", 0.795f);
    }

    /// <summary>H1 from <c>h1_description/urdf/h1.urdf</c>.</summary>
    /// <remarks>
    /// The hip chain runs yaw, roll, pitch — the reverse of G1's — and the ankle is pitch-only, so
    /// the foot is a fixed child of it. The arms have no wrists: 2×5 legs, a waist and 2×4 arms is 19.
    /// </remarks>
    private static RobotRig H1()
    {
        var rig = new UrdfRig("h1");

        rig.Add("pelvis", null, Vector3.Zero, Vector3.Zero, "pelvis",
            [UrdfRig.Box(new Vector3(0.14f, 0.24f, 0.14f), new Vector3(0f, 0f, -0.08f), RigSurface.Shell)]);

        foreach (float side in (ReadOnlySpan<float>)[1f, -1f])
        {
            string p = side > 0 ? "left" : "right";

            rig.Add($"{p}_hip_yaw", "pelvis", new Vector3(0f, side * 0.0875f, -0.1742f), Vector3.UnitZ,
                $"{p}_hip_yaw_link", [UrdfRig.Housing(0.05f)]);
            rig.Add($"{p}_hip_roll", $"{p}_hip_yaw", new Vector3(0.039468f, 0f, 0f), Vector3.UnitX,
                $"{p}_hip_roll_link", [UrdfRig.Bone(new Vector3(0f, side * 0.11536f, 0f), 0.05f)]);
            rig.Add($"{p}_hip_pitch", $"{p}_hip_roll", new Vector3(0f, side * 0.11536f, 0f), Vector3.UnitY,
                $"{p}_hip_pitch_link", [UrdfRig.Bone(new Vector3(0f, 0f, -0.4f), 0.05f)]);
            rig.Add($"{p}_calf", $"{p}_hip_pitch", new Vector3(0f, 0f, -0.4f), Vector3.UnitY,
                $"{p}_knee_link", [UrdfRig.Bone(new Vector3(0f, 0f, -0.4f), 0.04f)]);
            rig.Add($"{p}_ankle", $"{p}_calf", new Vector3(0f, 0f, -0.4f), Vector3.UnitY, $"{p}_ankle_link", []);

            // The ankle link is the foot; this fixed child is only the contact point the gait loads.
            rig.Add($"{p}_foot", $"{p}_ankle", Vector3.Zero, Vector3.Zero, null,
                [UrdfRig.Box(new Vector3(0.22f, 0.08f, 0.05f), new Vector3(0.04f, 0f, -0.045f), RigSurface.Contact)]);
        }

        rig.Add("torso", "pelvis", Vector3.Zero, Vector3.UnitZ, "torso_link",
            [UrdfRig.Box(new Vector3(0.16f, 0.30f, 0.45f), new Vector3(0f, 0f, 0.28f), RigSurface.Shell)]);

        foreach (float side in (ReadOnlySpan<float>)[1f, -1f])
        {
            string p = side > 0 ? "left" : "right";

            rig.Add($"{p}_shoulder_pitch", "torso", new Vector3(0.0055f, side * 0.15535f, 0.42999f), Vector3.UnitY,
                $"{p}_shoulder_pitch_link", [UrdfRig.Housing(0.045f)], rotation: new Vector3(side * 0.43633f, 0f, 0f));
            rig.Add($"{p}_shoulder_roll", $"{p}_shoulder_pitch", new Vector3(-0.0055f, side * 0.0565f, -0.0165f), Vector3.UnitX,
                $"{p}_shoulder_roll_link", [], rotation: new Vector3(-side * 0.43633f, 0f, 0f));
            rig.Add($"{p}_upper_arm", $"{p}_shoulder_roll", new Vector3(0f, 0f, -0.1343f), Vector3.UnitZ,
                $"{p}_shoulder_yaw_link", [UrdfRig.Bone(new Vector3(0.0185f, 0f, -0.198f), 0.04f)]);
            rig.Add($"{p}_forearm", $"{p}_upper_arm", new Vector3(0.0185f, 0f, -0.198f), Vector3.UnitY,
                $"{p}_elbow_link", [UrdfRig.Bone(new Vector3(0.26f, 0f, 0f), 0.035f)]);
        }

        return rig.Build(RobotModel.H1, "H1", "1.80 m humanoid, 19 joints. Unitree's full-size platform.", 1.048f);
    }

    /// <summary>H1-2 from <c>h1_2_description/h1_2_handless.urdf</c>.</summary>
    /// <remarks>
    /// The H1 body revised: the hip chain becomes yaw, pitch, roll, the ankle gains a roll joint, and
    /// the arms gain three-axis wrists — 2×6, a waist and 2×7 is 27. Both wrists share one yaw mesh.
    /// </remarks>
    private static RobotRig H12()
    {
        var rig = new UrdfRig("h12");

        rig.Add("pelvis", null, Vector3.Zero, Vector3.Zero, "pelvis",
            [UrdfRig.Box(new Vector3(0.14f, 0.24f, 0.14f), new Vector3(0f, 0f, -0.08f), RigSurface.Shell)]);

        foreach (float side in (ReadOnlySpan<float>)[1f, -1f])
        {
            string p = side > 0 ? "left" : "right";

            rig.Add($"{p}_hip_yaw", "pelvis", new Vector3(0f, side * 0.0875f, -0.1632f), Vector3.UnitZ,
                $"{p}_hip_yaw_link", [UrdfRig.Bone(new Vector3(0f, side * 0.0755f, 0f), 0.05f)]);
            rig.Add($"{p}_hip_pitch", $"{p}_hip_yaw", new Vector3(0f, side * 0.0755f, 0f), Vector3.UnitY,
                $"{p}_hip_pitch_link", [UrdfRig.Housing(0.05f)]);
            rig.Add($"{p}_hip_roll", $"{p}_hip_pitch", Vector3.Zero, Vector3.UnitX,
                $"{p}_hip_roll_link", [UrdfRig.Bone(new Vector3(0f, 0f, -0.4f), 0.05f)]);
            rig.Add($"{p}_calf", $"{p}_hip_roll", new Vector3(0f, 0f, -0.4f), Vector3.UnitY,
                $"{p}_knee_link", [UrdfRig.Bone(new Vector3(0f, 0f, -0.4f), 0.04f)]);
            rig.Add($"{p}_ankle", $"{p}_calf", new Vector3(0f, 0f, -0.4f), Vector3.UnitY, $"{p}_ankle_pitch_link", []);
            rig.Add($"{p}_foot", $"{p}_ankle", new Vector3(0f, 0f, -0.02f), Vector3.UnitX,
                $"{p}_ankle_roll_link", [UrdfRig.Box(new Vector3(0.22f, 0.08f, 0.04f), new Vector3(0.04f, 0f, -0.025f), RigSurface.Contact)]);
        }

        rig.Add("torso", "pelvis", Vector3.Zero, Vector3.UnitZ, "torso_link",
            [UrdfRig.Box(new Vector3(0.16f, 0.30f, 0.45f), new Vector3(0f, 0f, 0.28f), RigSurface.Shell)]);

        foreach (float side in (ReadOnlySpan<float>)[1f, -1f])
        {
            string p = side > 0 ? "left" : "right";

            rig.Add($"{p}_shoulder_pitch", "torso", new Vector3(0f, side * 0.14806f, 0.42333f), Vector3.UnitY,
                $"{p}_shoulder_pitch_link", [UrdfRig.Housing(0.045f)], rotation: new Vector3(side * 0.2618f, 0f, 0f));
            rig.Add($"{p}_shoulder_roll", $"{p}_shoulder_pitch", new Vector3(0.0342f, side * 0.061999f, -0.0060011f), Vector3.UnitX,
                $"{p}_shoulder_roll_link", [], rotation: new Vector3(-side * 0.2618f, 0f, 0f));
            rig.Add($"{p}_upper_arm", $"{p}_shoulder_roll", new Vector3(-0.0342f, 0f, -0.1456f), Vector3.UnitZ,
                $"{p}_shoulder_yaw_link", [UrdfRig.Bone(new Vector3(0.006f, side * 0.0329f, -0.182f), 0.04f)]);
            rig.Add($"{p}_forearm", $"{p}_upper_arm", new Vector3(0.006f, side * 0.0329f, -0.182f), Vector3.UnitY,
                $"{p}_elbow_link", [UrdfRig.Bone(new Vector3(0.121f, -side * 0.0329f, -0.011f), 0.035f)]);
            rig.Add($"{p}_wrist_roll", $"{p}_forearm", new Vector3(0.121f, -side * 0.0329f, -0.011f), Vector3.UnitX,
                $"{p}_wrist_roll_link", [UrdfRig.Bone(new Vector3(0.087f, 0f, 0f), 0.03f)]);
            rig.Add($"{p}_wrist_pitch", $"{p}_wrist_roll", new Vector3(0.087f, 0f, 0f), Vector3.UnitY, $"{p}_wrist_pitch_link", []);
            rig.Add($"{p}_wrist_yaw", $"{p}_wrist_pitch", new Vector3(0.02f, 0f, 0f), Vector3.UnitZ, "wrist_yaw_link", []);
        }

        return rig.Build(RobotModel.H12, "H1-2", "H1 revision with wrists — 27 joints.", 1.031f);
    }

    /// <summary>R1 from <c>r1_description/R1.urdf</c>.</summary>
    /// <remarks>
    /// <para>
    /// R1 is laid out differently from the others: the waist rolls then yaws, each arm has five joints
    /// ending in a wrist roll, and the head pitches and yaws — 2×6, 2, 2×5 and 2 is 26.
    /// </para>
    /// <para>
    /// The ankle is driven through two push-rod linkages. The URDF models the rods as fixed links, so
    /// here they ride with the calf and the foot and do not follow the ankle exactly as it moves.
    /// </para>
    /// </remarks>
    private static RobotRig R1()
    {
        var rig = new UrdfRig("r1");

        rig.Add("pelvis", null, Vector3.Zero, Vector3.Zero, "pelvis_link",
            [UrdfRig.Box(new Vector3(0.12f, 0.18f, 0.12f), new Vector3(0.03f, 0f, -0.05f), RigSurface.Shell)]);

        foreach (float side in (ReadOnlySpan<float>)[1f, -1f])
        {
            string p = side > 0 ? "left" : "right";

            // The hip pitch frame is rolled 0.4363 rad outward and the roll frame rolls it back: the
            // hip actuators sit splayed, as G1's shoulders do.
            rig.Add($"{p}_hip_pitch", "pelvis", new Vector3(0.0325f, side * 0.0704672f, -0.0902351f), Vector3.UnitY,
                $"{p}_hip_pitch_link", [UrdfRig.Housing(0.04f)], rotation: new Vector3(-side * 0.436332f, 0f, 0f));
            rig.Add($"{p}_hip_roll", $"{p}_hip_pitch", new Vector3(0.0248f, side * 0.045f, -0.053f), Vector3.UnitX,
                $"{p}_hip_roll_link", [], rotation: new Vector3(side * 0.436332f, 0f, 0f));
            rig.Add($"{p}_hip_yaw", $"{p}_hip_roll", new Vector3(-0.0194507f, -side * 0.0006f, -0.0618f), Vector3.UnitZ,
                $"{p}_hip_yaw_link", [UrdfRig.Bone(new Vector3(-0.02315f, side * 0.01866f, -0.159301f), 0.04f)]);
            rig.Add($"{p}_calf", $"{p}_hip_yaw", new Vector3(-0.02315f, side * 0.01866f, -0.159301f), Vector3.UnitY,
                $"{p}_knee_link", [UrdfRig.Bone(new Vector3(-0.0168139f, -side * 0.0205811f, -0.309175f))]);
            rig.Add($"{p}_ankle_rod_a", $"{p}_calf", new Vector3(0.0137908f, -side * 0.0205811f, -0.0965262f), Vector3.Zero,
                $"{p}_ankle_A_link", [], rotation: new Vector3(0f, 0.0893609f, 0f));
            rig.Add($"{p}_ankle_rod_a_end", $"{p}_ankle_rod_a", new Vector3(0.013f, side * 0.0159962f, -0.00404621f), Vector3.Zero,
                $"{p}_ankle_A_rod_link", []);
            rig.Add($"{p}_ankle_rod_b", $"{p}_calf", new Vector3(0.00785625f, -side * 0.0205811f, -0.162761f), Vector3.Zero,
                $"{p}_ankle_B_link", [], rotation: new Vector3(0f, 0.0893609f, 0f));
            rig.Add($"{p}_ankle_rod_b_end", $"{p}_ankle_rod_b", new Vector3(0.013f, -side * 0.0159933f, -0.0040578f), Vector3.Zero,
                $"{p}_ankle_B_rod_link", []);
            rig.Add($"{p}_ankle", $"{p}_calf", new Vector3(-0.0168139f, -side * 0.0205811f, -0.309175f), Vector3.UnitY,
                $"{p}_ankle_pitch_link", []);
            rig.Add($"{p}_foot", $"{p}_ankle", Vector3.Zero, Vector3.UnitX,
                $"{p}_ankle_roll_link", [UrdfRig.Box(new Vector3(0.17f, 0.07f, 0.045f), new Vector3(0.03f, 0f, -0.03f), RigSurface.Contact)]);
            rig.Add($"{p}_ankle_link_a", $"{p}_foot", new Vector3(0.021f, side * 0.014f, -0.0094f), Vector3.Zero,
                $"{p}_ankle_constraint_A_link", []);
            rig.Add($"{p}_ankle_link_b", $"{p}_foot", new Vector3(0.021f, -side * 0.014f, -0.0094f), Vector3.Zero,
                $"{p}_ankle_constraint_B_link", []);
        }

        rig.Add("waist_roll", "pelvis", Vector3.Zero, Vector3.UnitX, "waist_roll_link", [],
            meshOffset: new Vector3(0.0325f, 0f, 0.049f));
        rig.Add("torso", "waist_roll", new Vector3(0.0325f, 0f, 0.049f), Vector3.UnitZ, "waist_yaw_link",
            [UrdfRig.Box(new Vector3(0.12f, 0.20f, 0.26f), new Vector3(0f, 0f, 0.14f), RigSurface.Shell)]);

        foreach (float side in (ReadOnlySpan<float>)[1f, -1f])
        {
            string p = side > 0 ? "left" : "right";

            rig.Add($"{p}_shoulder_pitch", "torso", new Vector3(0f, side * 0.085688f, 0.19749f), Vector3.UnitY,
                $"{p}_shoulder_pitch_link", [UrdfRig.Housing(0.035f)], rotation: new Vector3(side * 0.26187f, 0f, 0f));
            rig.Add($"{p}_shoulder_roll", $"{p}_shoulder_pitch", new Vector3(0.03445f, side * 0.047132f, -0.025693f), Vector3.UnitX,
                $"{p}_shoulder_roll_link", [], rotation: new Vector3(-side * 0.26187f, 0f, 0f));
            rig.Add($"{p}_upper_arm", $"{p}_shoulder_roll", new Vector3(-0.03445f, side * 0.0043f, -0.10835f), Vector3.UnitZ,
                $"{p}_shoulder_yaw_link", [UrdfRig.Bone(new Vector3(0.016191f, side * 0.026461f, -0.082858f), 0.03f)]);
            rig.Add($"{p}_forearm", $"{p}_upper_arm", new Vector3(0.016191f, side * 0.026461f, -0.082858f), Vector3.UnitY,
                $"{p}_elbow_link", [UrdfRig.Bone(new Vector3(0.11218f, -side * 0.03002f, -0.011702f), 0.028f)]);
            rig.Add($"{p}_wrist_roll", $"{p}_forearm", new Vector3(0.11218f, -side * 0.03002f, -0.011702f), Vector3.UnitX,
                $"{p}_wrist_roll_link", [UrdfRig.Box(new Vector3(0.08f, 0.035f, 0.05f), new Vector3(0.04f, 0f, 0f), RigSurface.Contact)]);
        }

        rig.Add("head_pitch", "torso", new Vector3(-0.006f, 0.03155f, 0.255f), Vector3.UnitY, "head_pitch_link", []);
        rig.Add("head", "head_pitch", new Vector3(0.0010126f, -0.03155f, 0.1165f), Vector3.UnitZ, "head_yaw_link",
            [UrdfRig.Box(new Vector3(0.12f, 0.12f, 0.14f), new Vector3(0.02f, 0f, 0f), RigSurface.Shell)]);
        rig.Add("camera", "head", new Vector3(0.066971f, 0f, -0.021637f), Vector3.Zero, "camera_link", [],
            rotation: new Vector3(0f, 0.34907f, 0f));

        return rig.Build(RobotModel.R1, "R1", "1.21 m humanoid, 26 joints with 5-DoF arms and a 2-DoF head.", 0.744f);
    }

    /// <summary>Collects the links of a humanoid transcribed from its URDF.</summary>
    private sealed class UrdfRig(string meshSet)
    {
        // The URDF's zero pose holds every one of these humanoids' forearms out level, elbows at a
        // right angle. The neutral pose lowers them by this much so the arms hang, and the gait's
        // elbow swing is added to it.
        private const float ElbowNeutral = 0.8f;

        private readonly List<RigLink> _links = [];
        private int _joints;

        /// <summary>
        /// Adds a link. A non-zero <paramref name="axis"/> makes it a joint, numbered in the order
        /// added — which is URDF order.
        /// </summary>
        public void Add(
            string name, string? parent, Vector3 offset, Vector3 axis, string? mesh, RigShape[] shapes,
            Vector3 rotation = default, Vector3 meshOffset = default)
        {
            int joint = axis == Vector3.Zero ? -1 : _joints++;
            _links.Add(new RigLink(name, parent, offset, axis, joint, 1f, shapes)
            {
                Mesh = mesh is null ? null : new RigMesh($"{meshSet}/{mesh}.glb", meshOffset, Vector3.Zero),
                Rotation = rotation,
            });
        }

        /// <summary>
        /// A capsule from the link origin to <paramref name="to"/>, so the fallback skeleton follows
        /// the same frames the meshes do. Each segment runs mostly along one axis, which is the one used.
        /// </summary>
        public static RigShape Bone(Vector3 to, float radius = 0.035f)
        {
            Vector3 a = Vector3.Abs(to);
            int axis = a.X >= a.Y && a.X >= a.Z ? 0 : a.Y >= a.Z ? 1 : 2;
            float length = MathF.Max(to.Length() - (2f * radius), 0.001f);
            return new RigShape(RigShapeKind.Capsule, new Vector3(radius, length, 0f), to * 0.5f, RigSurface.Limb, axis);
        }

        /// <summary>An actuator housing across the link's Y axis.</summary>
        public static RigShape Housing(float radius) =>
            new(RigShapeKind.Cylinder, new Vector3(radius, radius * 1.4f, 0f), Vector3.Zero, RigSurface.Actuator, AxisAlong: 1);

        /// <summary>A box, for pelvis, torso, feet and hands.</summary>
        public static RigShape Box(Vector3 size, Vector3 center, RigSurface surface) =>
            new(RigShapeKind.Box, size, center, surface);

        public RobotRig Build(RobotModel model, string name, string summary, float standingHeight)
        {
            // A mismatch means the pose array and the rig disagree, which shows up as a limb that never
            // moves. It fails here, at construction, rather than there.
            int expected = RobotModelInfo.GetActuatedJointCount(model);
            if (_joints != expected)
            {
                throw new InvalidOperationException($"The {name} rig defines {_joints} joints but the platform has {expected}.");
            }

            var neutral = new float[expected];
            foreach (RigLink link in _links)
            {
                if (link.JointIndex >= 0 && link.Name.EndsWith("_forearm", StringComparison.Ordinal))
                {
                    neutral[link.JointIndex] = ElbowNeutral;
                }
            }

            return new RobotRig(model, name, summary, expected, standingHeight, _links, ["left_foot", "right_foot"], neutral);
        }
    }
}
