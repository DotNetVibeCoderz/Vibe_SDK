namespace Unitree.Net.Core;

/// <summary>
/// Motor slot indices for quadruped robots using the <c>unitree_go</c> IDL.
/// </summary>
/// <remarks>
/// Leg prefixes are front/rear + right/left: FR, FL, RR, RL. Each leg has hip (abduction),
/// thigh (hip flexion) and calf (knee) joints, in that order.
/// </remarks>
public static class GoJoint
{
    /// <summary>Front-right hip (abduction/adduction).</summary>
    public const int FrontRightHip = 0;

    /// <summary>Front-right thigh (hip flexion/extension).</summary>
    public const int FrontRightThigh = 1;

    /// <summary>Front-right calf (knee).</summary>
    public const int FrontRightCalf = 2;

    /// <summary>Front-left hip (abduction/adduction).</summary>
    public const int FrontLeftHip = 3;

    /// <summary>Front-left thigh (hip flexion/extension).</summary>
    public const int FrontLeftThigh = 4;

    /// <summary>Front-left calf (knee).</summary>
    public const int FrontLeftCalf = 5;

    /// <summary>Rear-right hip (abduction/adduction).</summary>
    public const int RearRightHip = 6;

    /// <summary>Rear-right thigh (hip flexion/extension).</summary>
    public const int RearRightThigh = 7;

    /// <summary>Rear-right calf (knee).</summary>
    public const int RearRightCalf = 8;

    /// <summary>Rear-left hip (abduction/adduction).</summary>
    public const int RearLeftHip = 9;

    /// <summary>Rear-left thigh (hip flexion/extension).</summary>
    public const int RearLeftThigh = 10;

    /// <summary>Rear-left calf (knee).</summary>
    public const int RearLeftCalf = 11;

    /// <summary>Number of actuated leg joints.</summary>
    public const int Count = 12;

    /// <summary>Human-readable names indexed by joint id.</summary>
    private static readonly string[] NamesById =
    [
        "FR_hip", "FR_thigh", "FR_calf",
        "FL_hip", "FL_thigh", "FL_calf",
        "RR_hip", "RR_thigh", "RR_calf",
        "RL_hip", "RL_thigh", "RL_calf",
    ];

    /// <summary>Gets the canonical joint name for <paramref name="jointIndex"/>.</summary>
    public static string GetName(int jointIndex) =>
        (uint)jointIndex < (uint)NamesById.Length ? NamesById[jointIndex] : $"joint_{jointIndex}";

    /// <summary>Gets the leg (0 = FR, 1 = FL, 2 = RR, 3 = RL) that owns <paramref name="jointIndex"/>.</summary>
    public static int GetLeg(int jointIndex) => jointIndex / 3;
}

/// <summary>
/// Motor slot indices for the G1 humanoid using the <c>unitree_hg</c> IDL.
/// </summary>
public static class G1Joint
{
    /// <summary>Left hip pitch.</summary>
    public const int LeftHipPitch = 0;

    /// <summary>Left hip roll.</summary>
    public const int LeftHipRoll = 1;

    /// <summary>Left hip yaw.</summary>
    public const int LeftHipYaw = 2;

    /// <summary>Left knee.</summary>
    public const int LeftKnee = 3;

    /// <summary>Left ankle pitch.</summary>
    public const int LeftAnklePitch = 4;

    /// <summary>Left ankle roll.</summary>
    public const int LeftAnkleRoll = 5;

    /// <summary>Right hip pitch.</summary>
    public const int RightHipPitch = 6;

    /// <summary>Right hip roll.</summary>
    public const int RightHipRoll = 7;

    /// <summary>Right hip yaw.</summary>
    public const int RightHipYaw = 8;

    /// <summary>Right knee.</summary>
    public const int RightKnee = 9;

    /// <summary>Right ankle pitch.</summary>
    public const int RightAnklePitch = 10;

    /// <summary>Right ankle roll.</summary>
    public const int RightAnkleRoll = 11;

    /// <summary>Waist yaw.</summary>
    public const int WaistYaw = 12;

    /// <summary>Waist roll.</summary>
    public const int WaistRoll = 13;

    /// <summary>Waist pitch.</summary>
    public const int WaistPitch = 14;

    /// <summary>Left shoulder pitch — first joint of the left arm chain.</summary>
    public const int LeftShoulderPitch = 15;

    /// <summary>Left shoulder roll.</summary>
    public const int LeftShoulderRoll = 16;

    /// <summary>Left shoulder yaw.</summary>
    public const int LeftShoulderYaw = 17;

    /// <summary>Left elbow.</summary>
    public const int LeftElbow = 18;

    /// <summary>Left wrist roll.</summary>
    public const int LeftWristRoll = 19;

    /// <summary>Left wrist pitch.</summary>
    public const int LeftWristPitch = 20;

    /// <summary>Left wrist yaw.</summary>
    public const int LeftWristYaw = 21;

    /// <summary>Right shoulder pitch — first joint of the right arm chain.</summary>
    public const int RightShoulderPitch = 22;

    /// <summary>Right shoulder roll.</summary>
    public const int RightShoulderRoll = 23;

    /// <summary>Right shoulder yaw.</summary>
    public const int RightShoulderYaw = 24;

    /// <summary>Right elbow.</summary>
    public const int RightElbow = 25;

    /// <summary>Right wrist roll.</summary>
    public const int RightWristRoll = 26;

    /// <summary>Right wrist pitch.</summary>
    public const int RightWristPitch = 27;

    /// <summary>Right wrist yaw.</summary>
    public const int RightWristYaw = 28;

    /// <summary>Number of actuated joints.</summary>
    public const int Count = 29;

    /// <summary>Number of joints in one arm chain (shoulder 3 + elbow 1 + wrist 3).</summary>
    public const int ArmChainLength = 7;

    /// <summary>Index of the first left-arm joint.</summary>
    public const int LeftArmStart = LeftShoulderPitch;

    /// <summary>Index of the first right-arm joint.</summary>
    public const int RightArmStart = RightShoulderPitch;

    private static readonly string[] NamesById =
    [
        "left_hip_pitch", "left_hip_roll", "left_hip_yaw", "left_knee", "left_ankle_pitch", "left_ankle_roll",
        "right_hip_pitch", "right_hip_roll", "right_hip_yaw", "right_knee", "right_ankle_pitch", "right_ankle_roll",
        "waist_yaw", "waist_roll", "waist_pitch",
        "left_shoulder_pitch", "left_shoulder_roll", "left_shoulder_yaw", "left_elbow",
        "left_wrist_roll", "left_wrist_pitch", "left_wrist_yaw",
        "right_shoulder_pitch", "right_shoulder_roll", "right_shoulder_yaw", "right_elbow",
        "right_wrist_roll", "right_wrist_pitch", "right_wrist_yaw",
    ];

    /// <summary>Gets the canonical joint name for <paramref name="jointIndex"/>.</summary>
    public static string GetName(int jointIndex) =>
        (uint)jointIndex < (uint)NamesById.Length ? NamesById[jointIndex] : $"joint_{jointIndex}";

    /// <summary>Whether <paramref name="jointIndex"/> belongs to either arm chain.</summary>
    public static bool IsArmJoint(int jointIndex) => jointIndex is >= LeftArmStart and <= RightWristYaw;

    /// <summary>Whether <paramref name="jointIndex"/> belongs to either leg chain.</summary>
    public static bool IsLegJoint(int jointIndex) => jointIndex is >= LeftHipPitch and <= RightAnkleRoll;
}

/// <summary>
/// Which arm of a dual-arm robot a command targets.
/// </summary>
public enum ArmSide
{
    /// <summary>The robot's left arm.</summary>
    Left,

    /// <summary>The robot's right arm.</summary>
    Right,
}
