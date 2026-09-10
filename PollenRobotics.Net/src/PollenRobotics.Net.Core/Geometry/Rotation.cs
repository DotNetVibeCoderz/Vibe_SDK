using System.Numerics;

namespace PollenRobotics.Net.Core.Geometry;

/// <summary>
/// Euler-angle conversions in the convention the Pollen SDKs use.
/// </summary>
/// <remarks>
/// <c>create_head_pose</c> and the JavaScript SDK's <c>rpyToMatrix</c> both build the rotation as
/// R = Rz(yaw) * Ry(pitch) * Rx(roll) - an intrinsic XYZ sequence, which is the same thing as an
/// extrinsic ZYX one. Getting this backwards produces a rotation that looks right for small angles
/// and drifts visibly past about 20 degrees, so it is worth stating explicitly.
/// </remarks>
public static class Rotation
{
    /// <summary>Builds a quaternion from roll/pitch/yaw using the intrinsic XYZ convention.</summary>
    public static Quaternion FromRpy(Angle roll, Angle pitch, Angle yaw)
    {
        double cr = Math.Cos(roll.Radians * 0.5), sr = Math.Sin(roll.Radians * 0.5);
        double cp = Math.Cos(pitch.Radians * 0.5), sp = Math.Sin(pitch.Radians * 0.5);
        double cy = Math.Cos(yaw.Radians * 0.5), sy = Math.Sin(yaw.Radians * 0.5);

        return Quaternion.Normalize(new Quaternion(
            (float)(sr * cp * cy - cr * sp * sy),
            (float)(cr * sp * cy + sr * cp * sy),
            (float)(cr * cp * sy - sr * sp * cy),
            (float)(cr * cp * cy + sr * sp * sy)));
    }

    /// <summary>Decomposes a quaternion into roll/pitch/yaw using the intrinsic XYZ convention.</summary>
    public static (Angle Roll, Angle Pitch, Angle Yaw) ToRpy(Quaternion q)
    {
        q = Quaternion.Normalize(q);

        double sinRollCosPitch = 2 * ((q.W * q.X) + (q.Y * q.Z));
        double cosRollCosPitch = 1 - (2 * ((q.X * q.X) + (q.Y * q.Y)));
        double roll = Math.Atan2(sinRollCosPitch, cosRollCosPitch);

        // At |pitch| = 90 degrees roll and yaw describe the same rotation and atan2 loses the
        // distinction. Clamping the sine keeps asin defined and pins the answer to the pole rather
        // than returning NaN, which is what a caller sweeping through the singularity wants.
        double sinPitch = 2 * ((q.W * q.Y) - (q.Z * q.X));
        double pitch = Math.Abs(sinPitch) >= 1
            ? Math.CopySign(Math.PI / 2, sinPitch)
            : Math.Asin(sinPitch);

        double sinYawCosPitch = 2 * ((q.W * q.Z) + (q.X * q.Y));
        double cosYawCosPitch = 1 - (2 * ((q.Y * q.Y) + (q.Z * q.Z)));
        double yaw = Math.Atan2(sinYawCosPitch, cosYawCosPitch);

        return (Angle.FromRadians(roll), Angle.FromRadians(pitch), Angle.FromRadians(yaw));
    }

    /// <summary>Builds a rotation matrix from roll/pitch/yaw, as a 16-element row-major array.</summary>
    public static double[] RpyToMatrix(Angle roll, Angle pitch, Angle yaw) =>
        new Pose(Vector3.Zero, FromRpy(roll, pitch, yaw)).ToRowMajor();
}
