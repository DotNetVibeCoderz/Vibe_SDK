namespace Unitree.Net.Core;

/// <summary>
/// Unitree robot platforms supported by the SDK.
/// </summary>
/// <remarks>
/// The model determines the DDS IDL family (<c>unitree_go</c> for quadrupeds,
/// <c>unitree_hg</c> for humanoids), the joint count, and which control services are available.
/// </remarks>
public enum RobotModel
{
    /// <summary>Unspecified model. Most APIs reject this value.</summary>
    Unknown = 0,

    /// <summary>Go2 quadruped (12 actuated joints, <c>unitree_go</c> IDL).</summary>
    Go2,

    /// <summary>Go2-W wheeled quadruped (12 leg joints + 4 wheels).</summary>
    Go2W,

    /// <summary>B2 industrial quadruped.</summary>
    B2,

    /// <summary>B2-W wheeled industrial quadruped.</summary>
    B2W,

    /// <summary>G1 humanoid (29 actuated joints, <c>unitree_hg</c> IDL).</summary>
    G1,

    /// <summary>H1 humanoid (19 actuated joints, <c>unitree_hg</c> IDL).</summary>
    H1,

    /// <summary>H1-2 humanoid (27 actuated joints, <c>unitree_hg</c> IDL).</summary>
    H12,

    /// <summary>R1 humanoid with dual-arm manipulation.</summary>
    R1,
}

/// <summary>
/// The DDS interface definition family a robot speaks.
/// </summary>
public enum IdlFamily
{
    /// <summary><c>unitree_go</c> — quadruped message set (20 motor slots).</summary>
    Go,

    /// <summary><c>unitree_hg</c> — humanoid message set (35 motor slots).</summary>
    Hg,
}

/// <summary>
/// Static, per-model capability and geometry facts.
/// </summary>
/// <remarks>
/// Everything here is a compile-time constant lookup — no allocation, safe to call from control loops.
/// </remarks>
public static class RobotModelInfo
{
    /// <summary>Number of motor slots reserved in the <c>unitree_go</c> low-level messages.</summary>
    public const int GoMotorSlots = 20;

    /// <summary>Number of motor slots reserved in the <c>unitree_hg</c> low-level messages.</summary>
    public const int HgMotorSlots = 35;

    /// <summary>Gets the IDL family used by <paramref name="model"/>.</summary>
    public static IdlFamily GetIdlFamily(RobotModel model) => model switch
    {
        RobotModel.Go2 or RobotModel.Go2W or RobotModel.B2 or RobotModel.B2W => IdlFamily.Go,
        RobotModel.G1 or RobotModel.H1 or RobotModel.H12 or RobotModel.R1 => IdlFamily.Hg,
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown robot model."),
    };

    /// <summary>Gets the number of motor slots present in the low-level message for <paramref name="model"/>.</summary>
    public static int GetMotorSlotCount(RobotModel model) =>
        GetIdlFamily(model) == IdlFamily.Go ? GoMotorSlots : HgMotorSlots;

    /// <summary>Gets the number of joints the robot actually actuates (excludes reserved slots).</summary>
    public static int GetActuatedJointCount(RobotModel model) => model switch
    {
        RobotModel.Go2 or RobotModel.B2 => 12,
        RobotModel.Go2W or RobotModel.B2W => 16,
        RobotModel.G1 => 29,
        RobotModel.H1 => 19,
        RobotModel.H12 => 27,
        RobotModel.R1 => 26,
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown robot model."),
    };

    /// <summary>Whether <paramref name="model"/> is a legged quadruped.</summary>
    public static bool IsQuadruped(RobotModel model) => GetIdlFamily(model) == IdlFamily.Go;

    /// <summary>Whether <paramref name="model"/> is a humanoid.</summary>
    public static bool IsHumanoid(RobotModel model) => GetIdlFamily(model) == IdlFamily.Hg;

    /// <summary>Whether <paramref name="model"/> exposes arm/manipulator control.</summary>
    public static bool HasArms(RobotModel model) =>
        model is RobotModel.G1 or RobotModel.H1 or RobotModel.H12 or RobotModel.R1;

    /// <summary>
    /// Gets the recommended low-level control frequency in hertz.
    /// </summary>
    /// <remarks>
    /// Quadrupeds accept 500 Hz on <c>rt/lowcmd</c>; humanoids run their whole-body controller
    /// at 500 Hz as well but tolerate 1 kHz over a wired link.
    /// </remarks>
    public static int GetControlFrequencyHz(RobotModel model) =>
        GetIdlFamily(model) == IdlFamily.Go ? 500 : 500;

    /// <summary>Gets the nominal battery pack cell count, used to scale voltage health checks.</summary>
    public static int GetBatteryCellCount(RobotModel model) => model switch
    {
        RobotModel.Go2 or RobotModel.Go2W => 15,
        RobotModel.B2 or RobotModel.B2W => 15,
        RobotModel.G1 or RobotModel.R1 => 13,
        RobotModel.H1 or RobotModel.H12 => 15,
        _ => 15,
    };
}
