using PollenRobotics.Net.Core.Geometry;

namespace PollenRobotics.Net.ReachyMini;

/// <summary>How the neck and antenna motors are behaving.</summary>
public enum MotorMode
{
    /// <summary>Position control. The robot holds whatever it was told to hold.</summary>
    Enabled,

    /// <summary>No power. The head flops.</summary>
    Disabled,

    /// <summary>
    /// Torque compensates gravity only, so the head can be moved by hand and stays put.
    /// </summary>
    /// <remarks>This is the mode to record a move in, and it requires the Placo kinematics backend.</remarks>
    GravityCompensation,
}

/// <summary>Wire-name conversions for <see cref="MotorMode"/>.</summary>
public static class MotorModeExtensions
{
    /// <summary>The spelling the daemon uses.</summary>
    public static string ToWireValue(this MotorMode mode) => mode switch
    {
        MotorMode.Enabled => "enabled",
        MotorMode.Disabled => "disabled",
        MotorMode.GravityCompensation => "gravity_compensation",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown motor mode."),
    };

    /// <summary>Parses the daemon spelling, defaulting to <see cref="MotorMode.Disabled"/> when unknown.</summary>
    public static MotorMode ParseMotorMode(string? wireValue) => wireValue switch
    {
        "enabled" => MotorMode.Enabled,
        "gravity_compensation" => MotorMode.GravityCompensation,
        _ => MotorMode.Disabled,
    };

    /// <summary>
    /// True when the robot counts as awake.
    /// </summary>
    /// <remarks>
    /// Gravity compensation counts. The head is movable but the robot is powered and responsive,
    /// which is what "awake" means to an application deciding whether to play a wake-up animation.
    /// </remarks>
    public static bool IsAwake(this MotorMode mode) => mode is MotorMode.Enabled or MotorMode.GravityCompensation;
}

/// <summary>
/// A snapshot of what Reachy Mini is doing.
/// </summary>
/// <param name="HeadPose">Head pose in the world frame.</param>
/// <param name="Antennas">Antenna angles, right then left.</param>
/// <param name="BodyYaw">Body rotation.</param>
/// <param name="HeadJointPositions">
/// Per-motor angles in radians: body yaw at index 0, the six Stewart branches at 1..6.
/// </param>
/// <param name="MotorMode">Current motor mode.</param>
/// <param name="IsMoveRunning">True while the daemon is playing a recorded move.</param>
/// <param name="Timestamp">When the snapshot was taken locally.</param>
public readonly record struct ReachyMiniState(
    Pose HeadPose,
    (Angle Right, Angle Left) Antennas,
    Angle BodyYaw,
    IReadOnlyList<double> HeadJointPositions,
    MotorMode MotorMode,
    bool IsMoveRunning,
    DateTimeOffset Timestamp)
{
    /// <summary>A zeroed snapshot, used before the first state arrives.</summary>
    public static ReachyMiniState Empty { get; } = new(
        Pose.Identity,
        (Angle.Zero, Angle.Zero),
        Angle.Zero,
        Array.Empty<double>(),
        MotorMode.Disabled,
        IsMoveRunning: false,
        DateTimeOffset.MinValue);

    /// <summary>True when this is <see cref="Empty"/> rather than a real reading.</summary>
    public bool IsEmpty => Timestamp == DateTimeOffset.MinValue;
}

/// <summary>
/// Where the daemon's face tracker last saw a face.
/// </summary>
/// <param name="Detected">False when nothing is in view; the other fields are then stale.</param>
/// <param name="X">Horizontal position in [-1, 1], negative to the robot's left.</param>
/// <param name="Y">Vertical position in [-1, 1], negative upward.</param>
/// <param name="Roll">Head tilt of the tracked face.</param>
public readonly record struct FaceTarget(bool Detected, double X, double Y, Angle Roll)
{
    /// <summary>Nothing in view.</summary>
    public static FaceTarget None { get; } = new(false, 0, 0, Angle.Zero);
}

/// <summary>Inertial reading from the wireless model. The Lite has no IMU.</summary>
/// <param name="AccelerometerMetersPerSecondSquared">Proper acceleration, x/y/z.</param>
/// <param name="GyroscopeRadiansPerSecond">Angular rate, x/y/z.</param>
/// <param name="Quaternion">Fused orientation as w/x/y/z.</param>
/// <param name="TemperatureCelsius">Die temperature.</param>
public readonly record struct ImuReading(
    (double X, double Y, double Z) AccelerometerMetersPerSecondSquared,
    (double X, double Y, double Z) GyroscopeRadiansPerSecond,
    (double W, double X, double Y, double Z) Quaternion,
    double TemperatureCelsius);
