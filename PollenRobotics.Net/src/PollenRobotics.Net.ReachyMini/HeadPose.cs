using PollenRobotics.Net.Core.Geometry;

namespace PollenRobotics.Net.ReachyMini;

/// <summary>
/// Builds head poses the way the Python SDK's <c>create_head_pose</c> does.
/// </summary>
/// <remarks>
/// The defaults match Python: angles in degrees, translation in metres unless <c>mm</c> is set.
/// Keeping the same defaults means a snippet copied out of Pollen's documentation produces the same
/// motion here, which is worth more than a tidier signature.
/// </remarks>
public static class HeadPose
{
    /// <summary>The neutral head pose: no offset, level, facing forward.</summary>
    public static Pose Neutral => Pose.Identity;

    /// <summary>
    /// Builds a head pose.
    /// </summary>
    /// <param name="x">Forward offset.</param>
    /// <param name="y">Left offset.</param>
    /// <param name="z">Up offset.</param>
    /// <param name="roll">Roll.</param>
    /// <param name="pitch">Pitch, positive nose-down.</param>
    /// <param name="yaw">Yaw, positive turning left.</param>
    /// <param name="mm">Read the translation as millimetres instead of metres.</param>
    /// <param name="degrees">Read the angles as degrees. True by default, as in Python.</param>
    public static Pose Create(
        double x = 0,
        double y = 0,
        double z = 0,
        double roll = 0,
        double pitch = 0,
        double yaw = 0,
        bool mm = false,
        bool degrees = true)
    {
        double scale = mm ? 0.001 : 1.0;

        Angle ToAngle(double value) => degrees ? Angle.FromDegrees(value) : Angle.FromRadians(value);

        return Pose.FromRpy(x * scale, y * scale, z * scale, ToAngle(roll), ToAngle(pitch), ToAngle(yaw));
    }

    /// <summary>A pose with orientation only, in degrees.</summary>
    public static Pose FromRpyDegrees(double roll, double pitch, double yaw) =>
        Pose.FromRpy(0, 0, 0, Angle.FromDegrees(roll), Angle.FromDegrees(pitch), Angle.FromDegrees(yaw));

    /// <summary>A pose translated in millimetres, level and facing forward.</summary>
    public static Pose FromOffsetMillimetres(double x, double y, double z) =>
        Pose.FromRpy(x / 1000.0, y / 1000.0, z / 1000.0, Angle.Zero, Angle.Zero, Angle.Zero);
}
