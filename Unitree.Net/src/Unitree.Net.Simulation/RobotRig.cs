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
    IReadOnlyList<RigShape> Shapes);

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
        RobotModel.G1 => Humanoid(model, "G1", "1.32 m humanoid, 29 joints with dual 7-DoF arms.", HumanoidSpec.G1),
        RobotModel.H1 => Humanoid(model, "H1", "1.80 m humanoid, 19 joints. Unitree's full-size platform.", HumanoidSpec.H1),
        RobotModel.H12 => Humanoid(model, "H1-2", "H1 revision with wrists — 27 joints.", HumanoidSpec.H12),
        RobotModel.R1 => Humanoid(model, "R1", "1.21 m dual-arm humanoid, 26 joints.", HumanoidSpec.R1),
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
        bool Wheeled)
    {
        // Go2 URDF: trunk 0.3762 long, hips at ±0.1934 / ±0.0465, thigh and calf both 0.213.
        // The trunk box is widened here from the URDF's 0.0935 collision width to the shell width a
        // person actually sees — this drives a viewport, not a collision check.
        internal static QuadrupedSpec Go2 { get; } =
            new(new Vector3(0.3762f, 0.190f, 0.114f), 0.1934f, 0.0465f, 0.0955f, 0.213f, 0.213f, 0.024f, 0.022f, false);

        internal static QuadrupedSpec Go2W { get; } = Go2 with { Wheeled = true, FootRadius = 0.070f };

        internal static QuadrupedSpec B2 { get; } =
            new(new Vector3(0.660f, 0.300f, 0.190f), 0.3455f, 0.072f, 0.1200f, 0.350f, 0.350f, 0.040f, 0.037f, false);

        internal static QuadrupedSpec B2W { get; } = B2 with { Wheeled = true, FootRadius = 0.120f };
    }

    private static RobotRig Quadruped(RobotModel model, string name, string summary, QuadrupedSpec spec)
    {
        var links = new List<RigLink>();

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
            ]));

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
                ]));

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
                ]));

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
                ]));

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
                [contact]));
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

    /// <summary>
    /// Proportions and degrees of freedom that distinguish one humanoid from another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lengths are in metres. The three degree-of-freedom counts are what actually separate the
    /// platforms: <c>AnkleDof</c> is 1 for a pitch-only ankle or 2 with roll; <c>WaistDof</c> is 1 for
    /// yaw, 2 to add pitch, 3 to add roll as well; <c>WristDof</c> is 0 for no wrist, 2 for roll and
    /// pitch, 3 to add yaw.
    /// </para>
    /// <para>
    /// <c>JointCount</c> is the platform's published figure, asserted against what the rig actually
    /// builds. A wrong entry fails loudly at construction rather than surfacing later as a limb that
    /// silently never moves.
    /// </para>
    /// </remarks>
    private readonly record struct HumanoidSpec(
        int JointCount,
        float PelvisWidth,
        float ThighLength,
        float CalfLength,
        float AnkleHeight,
        float FootLength,
        float TorsoHeight,
        float ShoulderWidth,
        float UpperArmLength,
        float ForearmLength,
        float HeadRadius,
        float LimbRadius,
        int AnkleDof,
        int WaistDof,
        int WristDof)
    {
        // G1: 1.32 m, 29 joints — legs 2x6, waist 3, arms 2x7.
        internal static HumanoidSpec G1 { get; } =
            new(29, 0.150f, 0.300f, 0.300f, 0.040f, 0.180f, 0.310f, 0.290f, 0.180f, 0.190f, 0.085f, 0.044f, 2, 3, 3);

        // H1: 1.80 m, 19 joints — legs 2x5 (the ankle is pitch-only), waist yaw, arms 2x4 with no wrist.
        internal static HumanoidSpec H1 { get; } =
            new(19, 0.190f, 0.400f, 0.400f, 0.055f, 0.240f, 0.420f, 0.400f, 0.260f, 0.280f, 0.110f, 0.058f, 1, 1, 0);

        // H1-2: the H1 body with an ankle roll and full wrists — legs 2x6, waist yaw, arms 2x7 = 27.
        internal static HumanoidSpec H12 { get; } = H1 with { JointCount = 27, AnkleDof = 2, WristDof = 3 };

        // R1: 1.21 m, 26 joints — legs 2x6, waist 2, arms 2x6.
        internal static HumanoidSpec R1 { get; } =
            new(26, 0.140f, 0.270f, 0.270f, 0.038f, 0.165f, 0.300f, 0.300f, 0.175f, 0.185f, 0.080f, 0.042f, 2, 2, 2);
    }

    private static RobotRig Humanoid(RobotModel model, string name, string summary, HumanoidSpec spec)
    {
        var links = new List<RigLink>();
        int next = 0;

        links.Add(new RigLink(
            "pelvis", null, Vector3.Zero, Vector3.Zero, -1, 1f,
            [
                new RigShape(
                    RigShapeKind.Box,
                    new Vector3(spec.LimbRadius * 3.4f, spec.PelvisWidth * 1.9f, spec.LimbRadius * 3.0f),
                    Vector3.Zero,
                    RigSurface.Shell),
            ]));

        // Legs first, matching the unitree_hg convention: left leg, then right leg, then waist, then
        // arms. The joint indices assigned here are what the pose array is indexed by.
        foreach (float side in (ReadOnlySpan<float>)[1f, -1f])
        {
            string prefix = side > 0 ? "left" : "right";

            int hipPitch = next++;
            int hipRoll = next++;
            int hipYaw = next++;
            int knee = next++;
            int anklePitch = next++;
            int ankleRoll = spec.AnkleDof >= 2 ? next++ : -1;

            links.Add(new RigLink(
                $"{prefix}_hip_pitch", "pelvis",
                new Vector3(0f, side * spec.PelvisWidth * 0.5f, -spec.LimbRadius * 1.2f),
                Vector3.UnitY, hipPitch, 1f,
                [
                    new RigShape(
                        RigShapeKind.Cylinder,
                        new Vector3(spec.LimbRadius * 1.15f, spec.LimbRadius * 1.7f, 0f),
                        Vector3.Zero, RigSurface.Actuator, AxisAlong: 1),
                ]));

            links.Add(new RigLink(
                $"{prefix}_hip_roll", $"{prefix}_hip_pitch",
                Vector3.Zero, Vector3.UnitX, hipRoll, 1f, []));

            links.Add(new RigLink(
                $"{prefix}_thigh", $"{prefix}_hip_roll",
                Vector3.Zero, Vector3.UnitZ, hipYaw, 1f,
                [
                    new RigShape(
                        RigShapeKind.Capsule,
                        new Vector3(spec.LimbRadius, spec.ThighLength * 0.82f, 0f),
                        new Vector3(0f, 0f, -spec.ThighLength * 0.5f),
                        RigSurface.Limb, AxisAlong: 2),
                ]));

            links.Add(new RigLink(
                $"{prefix}_calf", $"{prefix}_thigh",
                new Vector3(0f, 0f, -spec.ThighLength),
                Vector3.UnitY, knee, 1f,
                [
                    new RigShape(
                        RigShapeKind.Capsule,
                        new Vector3(spec.LimbRadius * 0.86f, spec.CalfLength * 0.84f, 0f),
                        new Vector3(0f, 0f, -spec.CalfLength * 0.5f),
                        RigSurface.Limb, AxisAlong: 2),
                ]));

            links.Add(new RigLink(
                $"{prefix}_ankle", $"{prefix}_calf",
                new Vector3(0f, 0f, -spec.CalfLength),
                Vector3.UnitY, anklePitch, 1f, []));

            // H1's ankle is pitch-only, so on that platform the foot is a fixed child of the ankle
            // rather than a rolling joint of its own.
            links.Add(new RigLink(
                $"{prefix}_foot", $"{prefix}_ankle",
                Vector3.Zero,
                ankleRoll >= 0 ? Vector3.UnitX : Vector3.Zero,
                ankleRoll, 1f,
                [
                    new RigShape(
                        RigShapeKind.Box,
                        new Vector3(spec.FootLength, spec.FootLength * 0.42f, spec.AnkleHeight),
                        new Vector3(spec.FootLength * 0.16f, 0f, -spec.AnkleHeight * 0.5f),
                        RigSurface.Contact),
                ]));
        }

        // Waist: yaw on every platform, then roll and pitch as the platform provides them. H1 stops at
        // yaw, R1 adds pitch, G1 carries all three.
        int waistYaw = next++;
        links.Add(new RigLink(
            "waist_yaw", "pelvis",
            new Vector3(0f, 0f, spec.LimbRadius * 1.6f),
            Vector3.UnitZ, waistYaw, 1f, []));

        string torsoParent = "waist_yaw";

        if (spec.WaistDof >= 3)
        {
            links.Add(new RigLink("waist_roll", torsoParent, Vector3.Zero, Vector3.UnitX, next++, 1f, []));
            torsoParent = "waist_roll";
        }

        if (spec.WaistDof >= 2)
        {
            links.Add(new RigLink("waist_pitch", torsoParent, Vector3.Zero, Vector3.UnitY, next++, 1f, []));
            torsoParent = "waist_pitch";
        }

        links.Add(new RigLink(
            "torso", torsoParent, Vector3.Zero, Vector3.Zero, -1, 1f,
            [
                new RigShape(
                    RigShapeKind.Box,
                    new Vector3(spec.LimbRadius * 3.2f, spec.ShoulderWidth * 0.80f, spec.TorsoHeight),
                    new Vector3(0f, 0f, spec.TorsoHeight * 0.5f),
                    RigSurface.Shell),

                // Chest plate, slightly proud of the torso — the visual cue that tells front from back
                // at a glance when the robot is turned away from the camera.
                new RigShape(
                    RigShapeKind.Box,
                    new Vector3(spec.LimbRadius * 0.9f, spec.ShoulderWidth * 0.52f, spec.TorsoHeight * 0.46f),
                    new Vector3(spec.LimbRadius * 1.9f, 0f, spec.TorsoHeight * 0.60f),
                    RigSurface.Shell),
            ]));

        links.Add(new RigLink(
            "head", "torso",
            new Vector3(0f, 0f, spec.TorsoHeight + (spec.HeadRadius * 0.9f)),
            Vector3.Zero, -1, 1f,
            [
                new RigShape(
                    RigShapeKind.Box,
                    new Vector3(spec.HeadRadius * 1.7f, spec.HeadRadius * 1.8f, spec.HeadRadius * 1.9f),
                    Vector3.Zero, RigSurface.Shell),
                new RigShape(
                    RigShapeKind.Box,
                    new Vector3(spec.HeadRadius * 0.28f, spec.HeadRadius * 1.5f, spec.HeadRadius * 0.5f),
                    new Vector3(spec.HeadRadius * 0.86f, 0f, spec.HeadRadius * 0.15f),
                    RigSurface.Sensor),
            ]));

        foreach (float side in (ReadOnlySpan<float>)[1f, -1f])
        {
            string prefix = side > 0 ? "left" : "right";

            int shoulderPitch = next++;
            int shoulderRoll = next++;
            int shoulderYaw = next++;
            int elbow = next++;

            // The shoulder is pushed out past the torso's half-width plus a limb radius, so the upper
            // arm hangs clear of the chest instead of intersecting it.
            float shoulderY = (spec.ShoulderWidth * 0.40f) + (spec.LimbRadius * 1.6f);

            links.Add(new RigLink(
                $"{prefix}_shoulder_pitch", "torso",
                new Vector3(0f, side * shoulderY, spec.TorsoHeight * 0.86f),
                Vector3.UnitY, shoulderPitch, 1f,
                [
                    new RigShape(
                        RigShapeKind.Cylinder,
                        new Vector3(spec.LimbRadius * 1.1f, spec.LimbRadius * 1.6f, 0f),
                        Vector3.Zero, RigSurface.Actuator, AxisAlong: 1),
                ]));

            links.Add(new RigLink(
                $"{prefix}_shoulder_roll", $"{prefix}_shoulder_pitch",
                Vector3.Zero, Vector3.UnitX, shoulderRoll, 1f, []));

            links.Add(new RigLink(
                $"{prefix}_upper_arm", $"{prefix}_shoulder_roll",
                Vector3.Zero, Vector3.UnitZ, shoulderYaw, 1f,
                [
                    new RigShape(
                        RigShapeKind.Capsule,
                        new Vector3(spec.LimbRadius * 0.80f, spec.UpperArmLength * 0.80f, 0f),
                        new Vector3(0f, 0f, -spec.UpperArmLength * 0.5f),
                        RigSurface.Limb, AxisAlong: 2),
                ]));

            links.Add(new RigLink(
                $"{prefix}_forearm", $"{prefix}_upper_arm",
                new Vector3(0f, 0f, -spec.UpperArmLength),
                Vector3.UnitY, elbow, 1f,
                [
                    new RigShape(
                        RigShapeKind.Capsule,
                        new Vector3(spec.LimbRadius * 0.68f, spec.ForearmLength * 0.78f, 0f),
                        new Vector3(0f, 0f, -spec.ForearmLength * 0.5f),
                        RigSurface.Limb, AxisAlong: 2),
                ]));

            // The wrist chain is where the platforms differ most: H1 has none, R1 has roll and pitch,
            // G1 and H1-2 add yaw. Only the first wrist link carries the forearm's length offset.
            string handParent = $"{prefix}_forearm";
            bool hasWrist = spec.WristDof >= 2;

            if (hasWrist)
            {
                links.Add(new RigLink(
                    $"{prefix}_wrist_roll", handParent,
                    new Vector3(0f, 0f, -spec.ForearmLength),
                    Vector3.UnitZ, next++, 1f, []));
                handParent = $"{prefix}_wrist_roll";

                links.Add(new RigLink(
                    $"{prefix}_wrist_pitch", handParent,
                    Vector3.Zero, Vector3.UnitY, next++, 1f, []));
                handParent = $"{prefix}_wrist_pitch";
            }

            if (spec.WristDof >= 3)
            {
                links.Add(new RigLink(
                    $"{prefix}_wrist_yaw", handParent,
                    Vector3.Zero, Vector3.UnitX, next++, 1f, []));
                handParent = $"{prefix}_wrist_yaw";
            }

            links.Add(new RigLink(
                $"{prefix}_hand", handParent,
                hasWrist ? Vector3.Zero : new Vector3(0f, 0f, -spec.ForearmLength),
                Vector3.Zero, -1, 1f,
                [
                    new RigShape(
                        RigShapeKind.Box,
                        new Vector3(spec.LimbRadius * 1.5f, spec.LimbRadius * 0.85f, spec.LimbRadius * 2.2f),
                        new Vector3(0f, 0f, -spec.LimbRadius * 1.1f),
                        RigSurface.Contact),
                ]));
        }

        // The joint layout above must account for exactly the joints the platform claims to have. A
        // mismatch means the pose array and the rig disagree, which shows up as a limb that never moves.
        if (next != spec.JointCount)
        {
            throw new InvalidOperationException(
                $"The {name} rig defines {next} joints but the platform has {spec.JointCount}.");
        }

        var neutral = new float[spec.JointCount];
        float standing = spec.ThighLength + spec.CalfLength + spec.AnkleHeight;

        return new RobotRig(
            model, name, summary, spec.JointCount, standing, links,
            ["left_foot", "right_foot"], neutral);
    }
}
