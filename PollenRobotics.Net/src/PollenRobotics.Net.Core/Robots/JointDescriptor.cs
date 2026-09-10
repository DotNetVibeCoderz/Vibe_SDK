using PollenRobotics.Net.Core.Geometry;

namespace PollenRobotics.Net.Core.Robots;

/// <summary>
/// One actuated joint: its name, its index on the wire and the range it is allowed to move in.
/// </summary>
/// <param name="Name">Dotted name matching the Python SDK, e.g. <c>r_arm.shoulder.pitch</c>.</param>
/// <param name="Index">Position in the robot's joint vector as the daemon orders it.</param>
/// <param name="Lower">Lower limit.</param>
/// <param name="Upper">Upper limit.</param>
/// <param name="IsContinuous">True when the joint can rotate without end stops.</param>
public readonly record struct JointDescriptor(
    string Name,
    int Index,
    Angle Lower,
    Angle Upper,
    bool IsContinuous = false)
{
    /// <summary>The midpoint of the joint's range, used as the neutral pose when nothing better is known.</summary>
    public Angle Neutral => Angle.FromRadians((Lower.Radians + Upper.Radians) / 2);

    /// <summary>True when <paramref name="value"/> lies inside the joint's limits.</summary>
    public bool Contains(Angle value) => IsContinuous || (value >= Lower && value <= Upper);

    /// <summary>Clamps <paramref name="value"/> into the joint's limits.</summary>
    public Angle Clamp(Angle value) => IsContinuous ? value : value.Clamp(Lower, Upper);

    /// <summary>Builds a descriptor from degrees, which is how the hardware documentation states limits.</summary>
    public static JointDescriptor Degrees(string name, int index, double lowerDeg, double upperDeg) =>
        new(name, index, Angle.FromDegrees(lowerDeg), Angle.FromDegrees(upperDeg));
}
