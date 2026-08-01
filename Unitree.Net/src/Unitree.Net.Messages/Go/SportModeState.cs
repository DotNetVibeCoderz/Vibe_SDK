using System.Numerics;
using System.Runtime.InteropServices;
using Unitree.Net.Core;
using Unitree.Net.Messages.Cdr;

namespace Unitree.Net.Messages.Go;

/// <summary>
/// A DDS timestamp, matching <c>builtin_interfaces::msg::dds_::Time_</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TimeSpec
{
    /// <summary>Seconds since the robot's epoch.</summary>
    public int Seconds;

    /// <summary>Nanosecond component.</summary>
    public uint Nanoseconds;

    /// <summary>Gets the timestamp as a <see cref="TimeSpan"/> since the robot's epoch.</summary>
    public readonly TimeSpan ToTimeSpan() =>
        TimeSpan.FromSeconds(Seconds) + TimeSpan.FromTicks(Nanoseconds / 100);

    /// <summary>Writes this value in CDR form.</summary>
    public readonly void Write(ref CdrWriter writer)
    {
        writer.WriteInt32(Seconds);
        writer.WriteUInt32(Nanoseconds);
    }

    /// <summary>Reads a value from CDR form.</summary>
    public static TimeSpec Read(ref CdrReader reader) => new()
    {
        Seconds = reader.ReadInt32(),
        Nanoseconds = reader.ReadUInt32(),
    };
}

/// <summary>
/// Gait types accepted by the sport-mode controller.
/// </summary>
public enum GaitType : byte
{
    /// <summary>Idle — no gait.</summary>
    Idle = 0,

    /// <summary>Trot, the default walking gait.</summary>
    Trot = 1,

    /// <summary>Trot running, for higher speeds.</summary>
    TrotRunning = 2,

    /// <summary>Forward climbing, for stairs and obstacles.</summary>
    ClimbForward = 3,

    /// <summary>Reverse climbing, for descending stairs.</summary>
    ClimbBackward = 4,
}

/// <summary>
/// Sport-mode controller states reported in <see cref="SportModeState.Mode"/>.
/// </summary>
public enum SportMode : byte
{
    /// <summary>Idle / damping.</summary>
    Idle = 0,

    /// <summary>Balanced standing; accepts velocity commands.</summary>
    BalanceStand = 1,

    /// <summary>Executing a scripted pose.</summary>
    Pose = 2,

    /// <summary>Locomoting.</summary>
    Locomotion = 3,

    /// <summary>Lying down.</summary>
    Lie = 5,

    /// <summary>Joint-space lock.</summary>
    JointLock = 6,

    /// <summary>Damping — motors resist motion without holding position.</summary>
    Damping = 7,

    /// <summary>Recovery stand in progress.</summary>
    RecoveryStand = 8,

    /// <summary>Sitting.</summary>
    Sit = 10,
}

/// <summary>
/// High-level locomotion state published on <c>rt/sportmodestate</c>, matching
/// <c>unitree_go::msg::dds_::SportModeState_</c>.
/// </summary>
/// <remarks>Encoded body size is 236 bytes.</remarks>
[StructLayout(LayoutKind.Sequential)]
public struct SportModeState : ICdrSerializable<SportModeState>
{
    /// <summary>Encoded body size in bytes, excluding the CDR encapsulation header.</summary>
    public const int BodySize = 236;

    /// <summary>Publication timestamp.</summary>
    public TimeSpec Stamp;

    /// <summary>Controller error code; zero means healthy.</summary>
    public uint ErrorCode;

    /// <summary>Body IMU state.</summary>
    public ImuState ImuState;

    /// <summary>Current controller mode; see <see cref="SportMode"/>.</summary>
    public byte Mode;

    /// <summary>Progress through the current scripted motion, 0–1.</summary>
    public float Progress;

    /// <summary>Active gait; cast to <see cref="Go.GaitType"/>.</summary>
    public byte GaitType;

    /// <summary>Commanded swing height, metres.</summary>
    public float FootRaiseHeight;

    /// <summary>
    /// Odometry position in the world frame, metres.
    /// </summary>
    /// <remarks>
    /// This is dead-reckoned from leg odometry and IMU. It drifts — typically a few percent of distance
    /// travelled — and resets when the robot power-cycles. Do not treat it as a global fix.
    /// </remarks>
    public Float3 Position;

    /// <summary>Body height above the ground, metres.</summary>
    public float BodyHeight;

    /// <summary>Body velocity in the world frame, m/s.</summary>
    public Float3 Velocity;

    /// <summary>Yaw rate, rad/s.</summary>
    public float YawSpeed;

    /// <summary>Ultrasonic obstacle ranges, metres.</summary>
    public Float4 RangeObstacle;

    /// <summary>Measured foot contact force per leg.</summary>
    public Int16x4 FootForce;

    /// <summary>Foot positions in the body frame, four feet by x/y/z.</summary>
    public Float12 FootPositionBody;

    /// <summary>Foot velocities in the body frame, four feet by x/y/z.</summary>
    public Float12 FootSpeedBody;

    /// <inheritdoc />
    public static string DdsTypeName => "unitree_go::msg::dds_::SportModeState_";

    /// <inheritdoc />
    public static int MaxSerializedSize => CdrConstants.EncapsulationHeaderSize + BodySize;

    /// <summary>Gets the odometry position as a vector.</summary>
    public readonly Vector3 GetPosition() => new(Position[0], Position[1], Position[2]);

    /// <summary>Gets the world-frame velocity as a vector.</summary>
    public readonly Vector3 GetVelocity() => new(Velocity[0], Velocity[1], Velocity[2]);

    /// <summary>Gets the robot's odometry pose.</summary>
    public readonly Pose GetPose() => new(GetPosition(), ImuState.ToQuaternion());

    /// <summary>Gets the foot position of one leg in the body frame.</summary>
    /// <param name="leg">Leg index: 0 = front-right, 1 = front-left, 2 = rear-right, 3 = rear-left.</param>
    public readonly Vector3 GetFootPosition(int leg)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)leg, 4u);
        int offset = leg * 3;
        return new Vector3(FootPositionBody[offset], FootPositionBody[offset + 1], FootPositionBody[offset + 2]);
    }

    /// <summary>Gets whether the controller is in a mode that accepts velocity commands.</summary>
    public readonly bool AcceptsVelocityCommands() =>
        (SportMode)Mode is SportMode.BalanceStand or SportMode.Locomotion;

    /// <inheritdoc />
    public readonly int Serialize(Span<byte> destination)
    {
        var writer = new CdrWriter(destination);
        Stamp.Write(ref writer);
        writer.WriteUInt32(ErrorCode);
        ImuState.Write(ref writer);
        writer.WriteByte(Mode);
        writer.WriteSingle(Progress);
        writer.WriteByte(GaitType);
        writer.WriteSingle(FootRaiseHeight);
        writer.WriteSingleArray(Position);
        writer.WriteSingle(BodyHeight);
        writer.WriteSingleArray(Velocity);
        writer.WriteSingle(YawSpeed);
        writer.WriteSingleArray(RangeObstacle);
        writer.WriteInt16Array(FootForce);
        writer.WriteSingleArray(FootPositionBody);
        writer.WriteSingleArray(FootSpeedBody);

        return writer.BytesWritten;
    }

    /// <inheritdoc />
    public static SportModeState Deserialize(ReadOnlySpan<byte> source)
    {
        var reader = new CdrReader(source);
        SportModeState state = default;

        state.Stamp = TimeSpec.Read(ref reader);
        state.ErrorCode = reader.ReadUInt32();
        state.ImuState = Go.ImuState.Read(ref reader);
        state.Mode = reader.ReadByte();
        state.Progress = reader.ReadSingle();
        state.GaitType = reader.ReadByte();
        state.FootRaiseHeight = reader.ReadSingle();
        reader.ReadSingleArray(state.Position);
        state.BodyHeight = reader.ReadSingle();
        reader.ReadSingleArray(state.Velocity);
        state.YawSpeed = reader.ReadSingle();
        reader.ReadSingleArray(state.RangeObstacle);
        reader.ReadInt16Array(state.FootForce);
        reader.ReadSingleArray(state.FootPositionBody);
        reader.ReadSingleArray(state.FootSpeedBody);

        return state;
    }
}
