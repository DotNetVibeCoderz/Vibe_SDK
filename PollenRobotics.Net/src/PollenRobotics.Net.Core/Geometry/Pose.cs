using System.Numerics;

namespace PollenRobotics.Net.Core.Geometry;

/// <summary>
/// A rigid transform: a translation in metres plus an orientation.
/// </summary>
/// <remarks>
/// The wire format used by the Reachy Mini daemon is a flat 16-element row-major 4x4 homogeneous
/// matrix, and <see cref="ToRowMajor"/> / <see cref="FromRowMajor"/> are the only sanctioned way to
/// cross that boundary. <see cref="System.Numerics.Matrix4x4"/> is stored row-major with the
/// translation in <c>M41..M43</c>, which is the transpose of the robotics convention, so a direct
/// reinterpret of its memory is wrong. Do not "optimise" the conversion into a memory copy.
/// </remarks>
public readonly record struct Pose
{
    /// <summary>Translation in metres.</summary>
    public Vector3 Position { get; init; }

    /// <summary>Orientation.</summary>
    public Quaternion Orientation { get; init; }

    /// <summary>The identity pose: no translation, no rotation.</summary>
    public static Pose Identity => new() { Position = Vector3.Zero, Orientation = Quaternion.Identity };

    /// <summary>Creates a pose from a translation and an orientation.</summary>
    public Pose(Vector3 position, Quaternion orientation)
    {
        Position = position;
        Orientation = Quaternion.Normalize(orientation);
    }

    /// <summary>
    /// Builds a pose the way the Python SDK's <c>create_head_pose</c> does: intrinsic XYZ Euler
    /// angles (roll about X, then pitch about Y, then yaw about Z) with a translation in metres.
    /// </summary>
    public static Pose FromRpy(double x, double y, double z, Angle roll, Angle pitch, Angle yaw)
    {
        Quaternion q = Rotation.FromRpy(roll, pitch, yaw);
        return new Pose(new Vector3((float)x, (float)y, (float)z), q);
    }

    /// <summary>Builds a pose from translation in millimetres and angles in degrees.</summary>
    public static Pose FromMillimetresDegrees(double xMm, double yMm, double zMm, double rollDeg, double pitchDeg, double yawDeg) =>
        FromRpy(xMm / 1000.0, yMm / 1000.0, zMm / 1000.0,
                Angle.FromDegrees(rollDeg), Angle.FromDegrees(pitchDeg), Angle.FromDegrees(yawDeg));

    /// <summary>The orientation expressed as roll/pitch/yaw (intrinsic XYZ).</summary>
    public (Angle Roll, Angle Pitch, Angle Yaw) Rpy => Rotation.ToRpy(Orientation);

    /// <summary>Composes this pose with <paramref name="other"/>, applying <paramref name="other"/> first.</summary>
    public Pose Compose(Pose other) => new(
        Position + Vector3.Transform(other.Position, Orientation),
        Quaternion.Multiply(Orientation, other.Orientation));

    /// <summary>The inverse transform.</summary>
    public Pose Inverse()
    {
        Quaternion inv = Quaternion.Conjugate(Orientation);
        return new Pose(-Vector3.Transform(Position, inv), inv);
    }

    /// <summary>Linear interpolation of position with spherical interpolation of orientation.</summary>
    public static Pose Lerp(Pose a, Pose b, double t)
    {
        float f = (float)t;
        return new Pose(Vector3.Lerp(a.Position, b.Position, f), Quaternion.Slerp(a.Orientation, b.Orientation, f));
    }

    /// <summary>
    /// Serialises to the 16-element row-major homogeneous matrix the daemons expect.
    /// </summary>
    public double[] ToRowMajor()
    {
        Matrix4x4 m = Matrix4x4.CreateFromQuaternion(Orientation);
        return
        [
            m.M11, m.M21, m.M31, Position.X,
            m.M12, m.M22, m.M32, Position.Y,
            m.M13, m.M23, m.M33, Position.Z,
            0, 0, 0, 1,
        ];
    }

    /// <summary>Reads a 16-element row-major homogeneous matrix.</summary>
    public static Pose FromRowMajor(ReadOnlySpan<double> m)
    {
        if (m.Length != 16)
        {
            throw new ArgumentException($"A homogeneous matrix has 16 elements, got {m.Length}.", nameof(m));
        }

        // Transposed back into Matrix4x4's column-vector layout before extracting the quaternion.
        var rot = new Matrix4x4(
            (float)m[0], (float)m[4], (float)m[8], 0,
            (float)m[1], (float)m[5], (float)m[9], 0,
            (float)m[2], (float)m[6], (float)m[10], 0,
            0, 0, 0, 1);

        return new Pose(
            new Vector3((float)m[3], (float)m[7], (float)m[11]),
            Quaternion.CreateFromRotationMatrix(rot));
    }

    public override string ToString()
    {
        (Angle roll, Angle pitch, Angle yaw) = Rpy;
        return $"pos=({Position.X * 1000:0.#}, {Position.Y * 1000:0.#}, {Position.Z * 1000:0.#})mm " +
               $"rpy=({roll.Degrees:0.#}, {pitch.Degrees:0.#}, {yaw.Degrees:0.#})deg";
    }
}
