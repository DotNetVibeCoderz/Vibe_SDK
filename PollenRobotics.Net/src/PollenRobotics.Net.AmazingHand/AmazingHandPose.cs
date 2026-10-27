using PollenRobotics.Net.Core.Geometry;

namespace PollenRobotics.Net.AmazingHand;

/// <summary>Which hand a device is. The Amazing Hand is handed, and the two are mirror images.</summary>
public enum HandSide
{
    /// <summary>The right hand. The vendored MuJoCo model this SDK transcribes is this one.</summary>
    Right,

    /// <summary>The left hand. Same mechanism, mirrored, so a given posture needs opposite signs.</summary>
    Left,
}

/// <summary>
/// One finger's posture, in terms a caller can reason about.
/// </summary>
/// <remarks>
/// <para>
/// Each finger is a parallel four-bar driven by two servos. Neither servo is a knuckle: the
/// finger's curl and its sideways splay are both consequences of the <em>pair</em>. Their sum
/// bends the finger and their difference swings it sideways, so commanding a motor directly is
/// almost never what a caller means.
/// </para>
/// <para>
/// This type is the mapping. Work in <see cref="Bend"/> and <see cref="Splay"/>; let
/// <see cref="ToMotors"/> produce the pair.
/// </para>
/// </remarks>
/// <param name="Bend">How far the finger curls in. Zero is straight, positive closes.</param>
/// <param name="Splay">Sideways swing. Zero is in line with the palm.</param>
public readonly record struct FingerPose(Angle Bend, Angle Splay)
{
    /// <summary>A straight, unsplayed finger.</summary>
    public static FingerPose Open { get; } = new(Angle.Zero, Angle.Zero);

    /// <summary>A fully curled finger.</summary>
    public static FingerPose Closed { get; } = new(Angle.FromDegrees(85), Angle.Zero);

    /// <summary>Builds a pose from degrees.</summary>
    public static FingerPose Degrees(double bend, double splay = 0) =>
        new(Angle.FromDegrees(bend), Angle.FromDegrees(splay));

    /// <summary>
    /// Converts the posture into the two motor angles the finger actually has.
    /// </summary>
    /// <remarks>
    /// Bend is the common component and splay the differential one. This is the linkage's
    /// behaviour to a first order, not an exact solution of it: Pollen publishes the geometry but
    /// not a closed-form map, and the real linkage is mildly non-linear toward full curl. It is
    /// accurate enough to drive postures and to animate the simulator; it is not a substitute for
    /// the robot's own calibration.
    /// </remarks>
    public (Angle Motor1, Angle Motor2) ToMotors(HandSide side = HandSide.Right)
    {
        double mirror = side == HandSide.Left ? -1 : 1;

        double motor1 = Bend.Degrees + (mirror * Splay.Degrees);
        double motor2 = Bend.Degrees - (mirror * Splay.Degrees);

        return (Angle.FromDegrees(motor1), Angle.FromDegrees(motor2));
    }

    /// <summary>Recovers a posture from a motor pair. The inverse of <see cref="ToMotors"/>.</summary>
    public static FingerPose FromMotors(Angle motor1, Angle motor2, HandSide side = HandSide.Right)
    {
        double mirror = side == HandSide.Left ? -1 : 1;

        return new FingerPose(
            Angle.FromDegrees((motor1.Degrees + motor2.Degrees) / 2),
            Angle.FromDegrees(mirror * (motor1.Degrees - motor2.Degrees) / 2));
    }
}

/// <summary>
/// The whole hand: four fingers, listed in the order their servos are wired.
/// </summary>
/// <remarks>
/// Finger 1 is the index and finger 4 the little finger, matching the servo ids 1-8 in Pollen's
/// own <c>r_hand.toml</c>. There is no thumb: the Amazing Hand has four equivalent fingers, which
/// is the first thing that surprises people writing grasp code for it.
/// </remarks>
public readonly record struct AmazingHandPose(
    FingerPose Finger1,
    FingerPose Finger2,
    FingerPose Finger3,
    FingerPose Finger4)
{
    /// <summary>All four fingers straight.</summary>
    public static AmazingHandPose Open { get; } = Uniform(FingerPose.Open);

    /// <summary>All four fingers curled.</summary>
    public static AmazingHandPose Fist { get; } = Uniform(FingerPose.Closed);

    /// <summary>The same pose on every finger.</summary>
    public static AmazingHandPose Uniform(FingerPose finger) => new(finger, finger, finger, finger);

    /// <summary>The four fingers in wire order.</summary>
    public FingerPose[] Fingers => [Finger1, Finger2, Finger3, Finger4];

    /// <summary>
    /// Flattens to the eight motor angles, in the catalogue's joint order.
    /// </summary>
    /// <remarks>
    /// The order is finger 1 motor 1, finger 1 motor 2, finger 2 motor 1, and so on - the actuator
    /// order of the MuJoCo model, which is also servo ids 1 through 8.
    /// </remarks>
    public double[] ToJointAngles(HandSide side = HandSide.Right)
    {
        double[] angles = new double[8];
        FingerPose[] fingers = Fingers;

        for (int i = 0; i < fingers.Length; i++)
        {
            (Angle motor1, Angle motor2) = fingers[i].ToMotors(side);
            angles[i * 2] = motor1.Radians;
            angles[(i * 2) + 1] = motor2.Radians;
        }

        return angles;
    }

    /// <summary>Rebuilds a pose from eight motor angles in catalogue order.</summary>
    public static AmazingHandPose FromJointAngles(IReadOnlyList<double> radians, HandSide side = HandSide.Right)
    {
        ArgumentNullException.ThrowIfNull(radians);

        if (radians.Count < 8)
        {
            throw new ArgumentException($"The Amazing Hand has eight motors; got {radians.Count}.", nameof(radians));
        }

        FingerPose Finger(int index) => FingerPose.FromMotors(
            Angle.FromRadians(radians[index * 2]),
            Angle.FromRadians(radians[(index * 2) + 1]),
            side);

        return new AmazingHandPose(Finger(0), Finger(1), Finger(2), Finger(3));
    }
}
