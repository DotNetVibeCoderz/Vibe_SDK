using PollenRobotics.Net.Core.Geometry;

namespace PollenRobotics.Net.MicroDuck;

/// <summary>
/// The policy slots a MicroDuck loads. Each slot holds an ONNX policy trained in simulation.
/// </summary>
/// <remarks>
/// These are slots, not commands: <c>robotctl policy load walk my-policy.onnx</c> replaces what the
/// duck does when it walks. The names are the wire values and match <c>robotctl policy list</c>.
/// </remarks>
public enum DuckActionSlot
{
    /// <summary>Locomotion. The slot velocity commands drive.</summary>
    Walk,

    /// <summary>Balanced standing.</summary>
    Stand,

    /// <summary>The transition between sitting and standing.</summary>
    SitStand,

    /// <summary>Pick an object off the ground with the beak.</summary>
    GroundPick,

    /// <summary>Kick with the left foot.</summary>
    KickLeft,

    /// <summary>Kick with the right foot.</summary>
    KickRight,

    /// <summary>Roll over and recover - the self-righting move.</summary>
    Roulade,
}

/// <summary>Wire names for <see cref="DuckActionSlot"/>.</summary>
public static class DuckActionSlotExtensions
{
    /// <summary>The slot name the daemon uses.</summary>
    public static string ToWireValue(this DuckActionSlot slot) => slot switch
    {
        DuckActionSlot.Walk => "walk",
        DuckActionSlot.Stand => "stand",
        DuckActionSlot.SitStand => "sitstand",
        DuckActionSlot.GroundPick => "ground_pick",
        DuckActionSlot.KickLeft => "kick_left",
        DuckActionSlot.KickRight => "kick_right",
        DuckActionSlot.Roulade => "roulade",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown action slot."),
    };

    /// <summary>Parses a slot name.</summary>
    public static DuckActionSlot ParseSlot(string wireValue) => wireValue switch
    {
        "walk" => DuckActionSlot.Walk,
        "stand" => DuckActionSlot.Stand,
        "sitstand" => DuckActionSlot.SitStand,
        "ground_pick" => DuckActionSlot.GroundPick,
        "kick_left" => DuckActionSlot.KickLeft,
        "kick_right" => DuckActionSlot.KickRight,
        "roulade" => DuckActionSlot.Roulade,
        _ => throw new ArgumentException($"Unknown action slot '{wireValue}'.", nameof(wireValue)),
    };
}

/// <summary>
/// A locomotion command: how fast to walk and how fast to turn.
/// </summary>
/// <remarks>
/// This is a velocity intent, not a trajectory. The walk policy decides the gait; the duck holds
/// the last command until a new one arrives, so the caller has to keep sending - a control loop
/// that stops sending is a duck that keeps walking.
/// </remarks>
/// <param name="ForwardMetersPerSecond">Positive walks forward.</param>
/// <param name="LateralMetersPerSecond">Positive strafes to the duck's left.</param>
/// <param name="YawRadiansPerSecond">Positive turns left.</param>
public readonly record struct DuckVelocity(
    double ForwardMetersPerSecond,
    double LateralMetersPerSecond,
    double YawRadiansPerSecond)
{
    /// <summary>Stand still.</summary>
    public static DuckVelocity Zero => default;

    /// <summary>Walk straight ahead.</summary>
    public static DuckVelocity Forward(double metersPerSecond) => new(metersPerSecond, 0, 0);

    /// <summary>Turn on the spot.</summary>
    public static DuckVelocity Turn(Angle perSecond) => new(0, 0, perSecond.Radians);

    /// <summary>Clamps each axis into the duck's usable range.</summary>
    /// <remarks>
    /// A 25 cm biped balancing on a learned policy has a much narrower stable envelope than its
    /// servos suggest. These limits are conservative on purpose: past them the policy stops
    /// tracking and the duck falls over, which is not an error anything reports.
    /// </remarks>
    public DuckVelocity Clamped() => new(
        Math.Clamp(ForwardMetersPerSecond, -0.15, 0.25),
        Math.Clamp(LateralMetersPerSecond, -0.10, 0.10),
        Math.Clamp(YawRadiansPerSecond, -1.5, 1.5));
}

/// <summary>What the duck reports about itself.</summary>
/// <param name="JointPositions">Fifteen servo angles in radians, in catalogue order.</param>
/// <param name="JointVelocities">Servo rates in radians per second, empty when not reported.</param>
/// <param name="Orientation">Fused body orientation as w/x/y/z.</param>
/// <param name="AngularRateRadiansPerSecond">Gyroscope reading, x/y/z.</param>
/// <param name="AccelerationMetersPerSecondSquared">Accelerometer reading, x/y/z.</param>
/// <param name="ActiveSlot">The policy slot currently running, or null when idle.</param>
/// <param name="IsFallen">True once the daemon has decided the duck is down.</param>
/// <param name="BatteryVolts">Pack voltage, or NaN when not reported.</param>
/// <param name="LoopRateHz">Measured control-loop rate, which should sit at 50.</param>
/// <param name="Timestamp">When the snapshot was taken locally.</param>
public readonly record struct MicroDuckState(
    IReadOnlyList<double> JointPositions,
    IReadOnlyList<double> JointVelocities,
    (double W, double X, double Y, double Z) Orientation,
    (double X, double Y, double Z) AngularRateRadiansPerSecond,
    (double X, double Y, double Z) AccelerationMetersPerSecondSquared,
    DuckActionSlot? ActiveSlot,
    bool IsFallen,
    double BatteryVolts,
    double LoopRateHz,
    DateTimeOffset Timestamp)
{
    /// <summary>A zeroed snapshot, used before the first reading.</summary>
    public static MicroDuckState Empty { get; } = new(
        Array.Empty<double>(),
        Array.Empty<double>(),
        (1, 0, 0, 0),
        (0, 0, 0),
        (0, 0, 0),
        null,
        IsFallen: false,
        double.NaN,
        0,
        DateTimeOffset.MinValue);

    /// <summary>True when this is <see cref="Empty"/> rather than a real reading.</summary>
    public bool IsEmpty => Timestamp == DateTimeOffset.MinValue;

    /// <summary>
    /// Body pitch and roll, derived from the fused orientation.
    /// </summary>
    /// <remarks>Useful for a tip-over guard that reacts before the daemon declares a fall.</remarks>
    public (Angle Roll, Angle Pitch, Angle Yaw) BodyRpy => Rotation.ToRpy(
        new System.Numerics.Quaternion((float)Orientation.X, (float)Orientation.Y, (float)Orientation.Z, (float)Orientation.W));
}

/// <summary>The daemon's own view of whether the robot is fit to move.</summary>
/// <param name="IsHealthy">The overall verdict; the exit code of <c>robotctl health</c> follows it.</param>
/// <param name="FirmwareVersion">Daemon release string.</param>
/// <param name="LoopRateHz">Measured loop rate.</param>
/// <param name="FailedServos">Servo ids that are not responding.</param>
/// <param name="Warnings">Anything the daemon flagged short of a failure.</param>
public readonly record struct MicroDuckHealth(
    bool IsHealthy,
    string FirmwareVersion,
    double LoopRateHz,
    IReadOnlyList<int> FailedServos,
    IReadOnlyList<string> Warnings);

/// <summary>
/// One frame from the head-mounted time-of-flight sensor: an 8x8 grid of distances in metres.
/// </summary>
/// <param name="Distances">Row-major, 64 values. Non-finite where the sensor saw nothing.</param>
/// <param name="Timestamp">When the frame was captured.</param>
public readonly record struct TofFrame(IReadOnlyList<double> Distances, DateTimeOffset Timestamp)
{
    /// <summary>The grid is square and eight on a side.</summary>
    public const int Size = 8;

    /// <summary>Reads one cell.</summary>
    public double this[int row, int column] => Distances[(row * Size) + column];

    /// <summary>The nearest finite reading, or NaN when the frame is empty.</summary>
    public double NearestMeters
    {
        get
        {
            double nearest = double.PositiveInfinity;
            foreach (double distance in Distances)
            {
                if (double.IsFinite(distance) && distance > 0 && distance < nearest)
                {
                    nearest = distance;
                }
            }

            return double.IsInfinity(nearest) ? double.NaN : nearest;
        }
    }
}
