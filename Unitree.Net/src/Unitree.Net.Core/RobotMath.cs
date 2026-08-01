using System.Numerics;
using System.Runtime.CompilerServices;

namespace Unitree.Net.Core;

/// <summary>
/// Roll / pitch / yaw orientation in radians, matching the convention Unitree reports in <c>IMUState.rpy</c>.
/// </summary>
/// <param name="Roll">Rotation about the forward (X) axis, radians.</param>
/// <param name="Pitch">Rotation about the left (Y) axis, radians.</param>
/// <param name="Yaw">Rotation about the up (Z) axis, radians.</param>
public readonly record struct EulerAngles(float Roll, float Pitch, float Yaw)
{
    /// <summary>The identity orientation.</summary>
    public static EulerAngles Zero => default;

    /// <summary>Gets the angles in degrees.</summary>
    public EulerAngles ToDegrees() => new(
        float.RadiansToDegrees(Roll),
        float.RadiansToDegrees(Pitch),
        float.RadiansToDegrees(Yaw));

    /// <summary>Creates an instance from degree values.</summary>
    public static EulerAngles FromDegrees(float roll, float pitch, float yaw) => new(
        float.DegreesToRadians(roll),
        float.DegreesToRadians(pitch),
        float.DegreesToRadians(yaw));
}

/// <summary>
/// Rigid-body pose: position in metres plus orientation.
/// </summary>
/// <param name="Position">Position in the world frame, metres.</param>
/// <param name="Orientation">Orientation as a unit quaternion.</param>
public readonly record struct Pose(Vector3 Position, Quaternion Orientation)
{
    /// <summary>Origin with identity orientation.</summary>
    public static Pose Identity => new(Vector3.Zero, Quaternion.Identity);

    /// <summary>Gets the orientation as roll/pitch/yaw.</summary>
    public EulerAngles ToEuler() => RobotMath.ToEuler(Orientation);

    /// <summary>Gets the planar (X/Y) distance to <paramref name="other"/> in metres.</summary>
    public float PlanarDistanceTo(in Pose other)
    {
        float dx = other.Position.X - Position.X;
        float dy = other.Position.Y - Position.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}

/// <summary>
/// A planar body-frame velocity command: forward, lateral and yaw rate.
/// </summary>
/// <param name="Forward">Longitudinal velocity, m/s. Positive is forward.</param>
/// <param name="Lateral">Lateral velocity, m/s. Positive is to the robot's left.</param>
/// <param name="YawRate">Turn rate, rad/s. Positive is counter-clockwise seen from above.</param>
public readonly record struct VelocityCommand(float Forward, float Lateral, float YawRate)
{
    /// <summary>A full stop.</summary>
    public static VelocityCommand Stop => default;

    /// <summary>Gets whether every component is zero.</summary>
    public bool IsStop => Forward == 0f && Lateral == 0f && YawRate == 0f;

    /// <summary>
    /// Returns a copy with each component clamped into <paramref name="limits"/>.
    /// </summary>
    public VelocityCommand Clamp(in VelocityLimits limits) => new(
        Math.Clamp(Forward, -limits.MaxForward, limits.MaxForward),
        Math.Clamp(Lateral, -limits.MaxLateral, limits.MaxLateral),
        Math.Clamp(YawRate, -limits.MaxYawRate, limits.MaxYawRate));
}

/// <summary>
/// Per-axis velocity ceilings applied before any locomotion command reaches the robot.
/// </summary>
/// <param name="MaxForward">Maximum absolute longitudinal speed, m/s.</param>
/// <param name="MaxLateral">Maximum absolute lateral speed, m/s.</param>
/// <param name="MaxYawRate">Maximum absolute turn rate, rad/s.</param>
public readonly record struct VelocityLimits(float MaxForward, float MaxLateral, float MaxYawRate)
{
    /// <summary>Conservative defaults suitable for indoor operation with people nearby.</summary>
    public static VelocityLimits Conservative => new(0.6f, 0.4f, 0.8f);

    /// <summary>Go2 factory envelope.</summary>
    public static VelocityLimits Go2Default => new(2.5f, 1.0f, 2.0f);

    /// <summary>G1 humanoid envelope.</summary>
    public static VelocityLimits G1Default => new(1.2f, 0.5f, 1.5f);

    /// <summary>Gets the default envelope for <paramref name="model"/>.</summary>
    public static VelocityLimits ForModel(RobotModel model) => model switch
    {
        RobotModel.Go2 or RobotModel.Go2W => Go2Default,
        RobotModel.B2 or RobotModel.B2W => new VelocityLimits(3.5f, 1.2f, 2.0f),
        RobotModel.G1 or RobotModel.R1 => G1Default,
        RobotModel.H1 or RobotModel.H12 => new VelocityLimits(1.5f, 0.5f, 1.5f),
        _ => Conservative,
    };
}

/// <summary>
/// Conversions and helpers for the frames and conventions Unitree uses.
/// </summary>
public static class RobotMath
{
    /// <summary>
    /// Converts a quaternion to roll/pitch/yaw using the Z-Y-X intrinsic convention Unitree reports.
    /// </summary>
    /// <param name="q">A unit quaternion. Non-normalised input yields undefined results.</param>
    public static EulerAngles ToEuler(in Quaternion q)
    {
        // Roll (X axis).
        float sinrCosp = 2f * ((q.W * q.X) + (q.Y * q.Z));
        float cosrCosp = 1f - (2f * ((q.X * q.X) + (q.Y * q.Y)));
        float roll = MathF.Atan2(sinrCosp, cosrCosp);

        // Pitch (Y axis). Clamped because values slightly outside [-1, 1] appear with accumulated error,
        // and Asin of those is NaN — which would silently poison every downstream control calculation.
        float sinp = 2f * ((q.W * q.Y) - (q.Z * q.X));
        float pitch = MathF.Abs(sinp) >= 1f
            ? MathF.CopySign(MathF.PI / 2f, sinp)
            : MathF.Asin(sinp);

        // Yaw (Z axis).
        float sinyCosp = 2f * ((q.W * q.Z) + (q.X * q.Y));
        float cosyCosp = 1f - (2f * ((q.Y * q.Y) + (q.Z * q.Z)));
        float yaw = MathF.Atan2(sinyCosp, cosyCosp);

        return new EulerAngles(roll, pitch, yaw);
    }

    /// <summary>
    /// Converts roll/pitch/yaw to a unit quaternion using the Z-Y-X intrinsic convention.
    /// </summary>
    public static Quaternion ToQuaternion(in EulerAngles euler)
    {
        (float sr, float cr) = MathF.SinCos(euler.Roll * 0.5f);
        (float sp, float cp) = MathF.SinCos(euler.Pitch * 0.5f);
        (float sy, float cy) = MathF.SinCos(euler.Yaw * 0.5f);

        return new Quaternion(
            (sr * cp * cy) - (cr * sp * sy),
            (cr * sp * cy) + (sr * cp * sy),
            (cr * cp * sy) - (sr * sp * cy),
            (cr * cp * cy) + (sr * sp * sy));
    }

    /// <summary>
    /// Wraps an angle into <c>[-π, π)</c>.
    /// </summary>
    /// <remarks>
    /// Yaw error must be wrapped before it feeds a heading controller, otherwise a robot at +179°
    /// chasing -179° turns the long way round.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float WrapAngle(float radians)
    {
        const float TwoPi = MathF.PI * 2f;
        float wrapped = radians % TwoPi;

        if (wrapped >= MathF.PI)
        {
            wrapped -= TwoPi;
        }
        else if (wrapped < -MathF.PI)
        {
            wrapped += TwoPi;
        }

        return wrapped;
    }

    /// <summary>
    /// Gets the shortest signed rotation from <paramref name="from"/> to <paramref name="to"/>, in radians.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AngleDifference(float from, float to) => WrapAngle(to - from);

    /// <summary>
    /// Rotates a world-frame planar vector into the body frame of a robot facing <paramref name="yaw"/>.
    /// </summary>
    public static Vector2 WorldToBody(Vector2 world, float yaw)
    {
        (float sin, float cos) = MathF.SinCos(yaw);
        return new Vector2(
            (world.X * cos) + (world.Y * sin),
            (-world.X * sin) + (world.Y * cos));
    }

    /// <summary>
    /// Linearly interpolates without clamping <paramref name="t"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    /// <summary>
    /// Limits how far <paramref name="target"/> may move from <paramref name="current"/> in one control tick.
    /// </summary>
    /// <param name="current">The present value.</param>
    /// <param name="target">The requested value.</param>
    /// <param name="maxDelta">Maximum permitted change, in the same units.</param>
    /// <remarks>
    /// Rate limiting is what keeps a large setpoint jump from becoming a torque spike. Every command path
    /// in this SDK that can be driven by a human or an LLM runs through this.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float RateLimit(float current, float target, float maxDelta)
    {
        float delta = Math.Clamp(target - current, -maxDelta, maxDelta);
        return current + delta;
    }
}
