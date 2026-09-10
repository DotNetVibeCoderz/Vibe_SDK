using System.Numerics;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Robots;

namespace PollenRobotics.Net.Kinematics;

/// <summary>Which arm.</summary>
public enum ArmSide
{
    /// <summary>The right arm, <c>r_arm</c>.</summary>
    Right,

    /// <summary>The left arm, <c>l_arm</c>.</summary>
    Left,
}

/// <summary>
/// Kinematic chains for Reachy 2's arms and neck.
/// </summary>
/// <remarks>
/// <para>
/// The link lengths here reproduce Reachy 2's published reach and proportions but are not taken
/// from Pollen's URDF, which is not part of this repository. That makes these chains right for the
/// simulator, for previewing a trajectory and for reasoning about reachability, and wrong as the
/// last word on a real robot: on hardware, prefer the robot's own
/// <c>inverse_kinematics</c> service, which runs against the true model. The same caveat is
/// recorded in <c>docs/kinematics.md</c> and in PROGRESS.md.
/// </para>
/// <para>
/// The joint order matches <see cref="RobotCatalog.Reachy2"/>: shoulder pitch, shoulder roll,
/// elbow yaw, elbow pitch, wrist roll, wrist pitch, wrist yaw.
/// </para>
/// </remarks>
public static class ReachyArmChains
{
    // Reachy 2 proportions, metres. Upper arm and forearm are the numbers that set total reach.
    private const double ShoulderOffsetY = 0.19;
    private const double ShoulderOffsetZ = 0.0;
    private const double UpperArmLength = 0.28;
    private const double ForearmLength = 0.28;
    private const double WristToTip = 0.10;

    /// <summary>Builds the seven-axis chain for one arm.</summary>
    public static SerialChain Arm(ArmSide side)
    {
        double sign = side == ArmSide.Right ? -1 : 1;
        RobotDescription description = RobotCatalog.Reachy2;
        string prefix = side == ArmSide.Right ? "r_arm" : "l_arm";

        JointDescriptor Limit(string name) => description[$"{prefix}.{name}"];

        // The shoulder sits out to the side of the torso origin; the chain then alternates twist
        // axes so that pitch, roll and yaw land on the axes their names claim.
        var basePose = new Pose(new Vector3(0, (float)(sign * ShoulderOffsetY), (float)ShoulderOffsetZ), Quaternion.Identity);

        DhLink[] links =
        [
            Link("shoulder.pitch", alpha: 0, a: 0, d: 0, thetaOffset: 0, Limit("shoulder.pitch")),
            Link("shoulder.roll", alpha: Math.PI / 2, a: 0, d: 0, thetaOffset: Math.PI / 2, Limit("shoulder.roll")),
            Link("elbow.yaw", alpha: Math.PI / 2, a: 0, d: UpperArmLength, thetaOffset: Math.PI / 2, Limit("elbow.yaw")),
            Link("elbow.pitch", alpha: Math.PI / 2, a: 0, d: 0, thetaOffset: 0, Limit("elbow.pitch")),
            Link("wrist.roll", alpha: -Math.PI / 2, a: 0, d: ForearmLength, thetaOffset: 0, Limit("wrist.roll")),
            Link("wrist.pitch", alpha: Math.PI / 2, a: 0, d: 0, thetaOffset: 0, Limit("wrist.pitch")),
            Link("wrist.yaw", alpha: -Math.PI / 2, a: 0, d: 0, thetaOffset: 0, Limit("wrist.yaw")),
        ];

        var tool = new Pose(new Vector3(0, 0, (float)WristToTip), Quaternion.Identity);
        return new SerialChain(links, basePose, tool);

        static DhLink Link(string name, double alpha, double a, double d, double thetaOffset, JointDescriptor limits) =>
            new(name, alpha, a, d, thetaOffset, limits.Lower, limits.Upper);
    }

    /// <summary>
    /// The three-axis Orbita neck, as a chain, so the head can be posed by target orientation.
    /// </summary>
    public static SerialChain Neck()
    {
        RobotDescription description = RobotCatalog.Reachy2;
        var basePose = new Pose(new Vector3(0, 0, 0.28f), Quaternion.Identity);

        DhLink[] links =
        [
            new("neck.roll", 0, 0, 0, 0, description["head.neck.roll"].Lower, description["head.neck.roll"].Upper),
            new("neck.pitch", Math.PI / 2, 0, 0, Math.PI / 2, description["head.neck.pitch"].Lower, description["head.neck.pitch"].Upper),
            new("neck.yaw", Math.PI / 2, 0, 0.08, 0, description["head.neck.yaw"].Lower, description["head.neck.yaw"].Upper),
        ];

        return new SerialChain(links, basePose);
    }

    /// <summary>
    /// The neck angles that point the head at a world point.
    /// </summary>
    /// <remarks>
    /// Reachy 2's neck is a three-axis wrist with no translation, so this is a direct aim rather
    /// than an iterative solve. Roll is left at zero: a level horizon is what a look-at is
    /// normally after, and the third axis is better spent elsewhere.
    /// </remarks>
    public static (Angle Roll, Angle Pitch, Angle Yaw) LookAt(Vector3 targetInRobotFrame, Vector3? headOrigin = null)
    {
        Vector3 origin = headOrigin ?? new Vector3(0, 0, 0.28f);
        Vector3 direction = targetInRobotFrame - origin;

        if (direction.LengthSquared() < 1e-9)
        {
            return (Angle.Zero, Angle.Zero, Angle.Zero);
        }

        direction = Vector3.Normalize(direction);

        double yaw = Math.Atan2(direction.Y, direction.X);
        double horizontal = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y));
        double pitch = -Math.Atan2(direction.Z, horizontal);

        return (Angle.Zero, Angle.FromRadians(pitch), Angle.FromRadians(yaw));
    }
}
