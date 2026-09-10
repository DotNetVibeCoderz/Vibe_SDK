namespace PollenRobotics.Net.Core.Geometry;

/// <summary>
/// An angle held in radians. The Pollen daemons all speak radians on the wire while the
/// documentation, the UI and most user code speak degrees, so the conversion is a named type
/// rather than a scattering of <c>* Math.PI / 180</c>.
/// </summary>
public readonly record struct Angle : IComparable<Angle>
{
    /// <summary>The angle in radians.</summary>
    public double Radians { get; }

    private Angle(double radians) => Radians = radians;

    /// <summary>The angle in degrees.</summary>
    public double Degrees => Radians * (180.0 / Math.PI);

    /// <summary>Zero.</summary>
    public static Angle Zero => default;

    /// <summary>Creates an angle from radians.</summary>
    public static Angle FromRadians(double radians) => new(radians);

    /// <summary>Creates an angle from degrees.</summary>
    public static Angle FromDegrees(double degrees) => new(degrees * (Math.PI / 180.0));

    /// <summary>Wraps the angle into (-pi, pi].</summary>
    public Angle Normalized()
    {
        double r = Math.IEEERemainder(Radians, 2 * Math.PI);
        // IEEERemainder returns a value in [-pi, pi]; -pi and +pi are both representable, and we
        // want a single canonical form so that equality comparisons behave.
        if (r <= -Math.PI)
        {
            r += 2 * Math.PI;
        }

        return new Angle(r);
    }

    /// <summary>Clamps the angle between two bounds.</summary>
    public Angle Clamp(Angle min, Angle max) => new(Math.Clamp(Radians, min.Radians, max.Radians));

    public int CompareTo(Angle other) => Radians.CompareTo(other.Radians);

    public static Angle operator +(Angle a, Angle b) => new(a.Radians + b.Radians);
    public static Angle operator -(Angle a, Angle b) => new(a.Radians - b.Radians);
    public static Angle operator -(Angle a) => new(-a.Radians);
    public static Angle operator *(Angle a, double scale) => new(a.Radians * scale);
    public static Angle operator /(Angle a, double divisor) => new(a.Radians / divisor);
    public static bool operator <(Angle a, Angle b) => a.Radians < b.Radians;
    public static bool operator >(Angle a, Angle b) => a.Radians > b.Radians;
    public static bool operator <=(Angle a, Angle b) => a.Radians <= b.Radians;
    public static bool operator >=(Angle a, Angle b) => a.Radians >= b.Radians;

    public override string ToString() => $"{Degrees:0.##}deg";
}

/// <summary>Degree/radian shorthands for readable call sites.</summary>
public static class AngleExtensions
{
    /// <summary>Reads the number as degrees.</summary>
    public static Angle Degrees(this double value) => Angle.FromDegrees(value);

    /// <summary>Reads the number as degrees.</summary>
    public static Angle Degrees(this int value) => Angle.FromDegrees(value);

    /// <summary>Reads the number as radians.</summary>
    public static Angle Radians(this double value) => Angle.FromRadians(value);

    /// <summary>Reads the number as radians.</summary>
    public static Angle Radians(this int value) => Angle.FromRadians(value);
}
