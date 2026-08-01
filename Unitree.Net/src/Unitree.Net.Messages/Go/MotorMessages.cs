using System.Runtime.InteropServices;
using Unitree.Net.Messages.Cdr;

namespace Unitree.Net.Messages.Go;

/// <summary>
/// Motor control modes accepted in <see cref="MotorCmd.Mode"/>.
/// </summary>
public static class MotorMode
{
    /// <summary>Motor idle — no torque is produced. Also called "damping off".</summary>
    public const byte Idle = 0x00;

    /// <summary>Servo mode: the motor tracks the position/velocity/torque command.</summary>
    public const byte Servo = 0x01;

    /// <summary>Foc-calibrating. Reported by the motor; not commanded.</summary>
    public const byte Calibrate = 0x02;
}

/// <summary>
/// A single motor command, matching <c>unitree_go::msg::dds_::MotorCmd_</c>.
/// </summary>
/// <remarks>
/// <para>
/// The robot applies the impedance law
/// <c>τ = τ_ff + Kp·(q_des − q) + Kd·(dq_des − dq)</c>
/// on the motor controller itself. That means <see cref="Kp"/> and <see cref="Kd"/> are not optional
/// tuning knobs — with both at zero the joint only ever sees <see cref="Tau"/> and behaves as a pure
/// torque source.
/// </para>
/// <para>Encoded size is 36 bytes.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct MotorCmd
{
    /// <summary>Encoded size in bytes.</summary>
    public const int EncodedSize = 36;

    /// <summary>Control mode; see <see cref="MotorMode"/>.</summary>
    public byte Mode;

    /// <summary>Desired joint position, radians.</summary>
    public float Q;

    /// <summary>Desired joint velocity, rad/s.</summary>
    public float Dq;

    /// <summary>Feed-forward torque, N·m.</summary>
    public float Tau;

    /// <summary>Position gain.</summary>
    public float Kp;

    /// <summary>Velocity (damping) gain.</summary>
    public float Kd;

    /// <summary>Reserved; must be zero.</summary>
    public UInt32x3 Reserve;

    /// <summary>
    /// A command that leaves the joint limp: zero gains and zero torque.
    /// </summary>
    /// <remarks>
    /// This is the correct neutral value for unused motor slots. Sending a zeroed struct with
    /// <see cref="Mode"/> at <see cref="MotorMode.Servo"/> and a zero <see cref="Q"/> would instead
    /// command the joint to the zero position, which on a standing robot means it collapses.
    /// </remarks>
    public static MotorCmd Idle => new() { Mode = MotorMode.Idle };

    /// <summary>Creates a position-tracking command.</summary>
    /// <param name="position">Target position, radians.</param>
    /// <param name="kp">Position gain.</param>
    /// <param name="kd">Damping gain.</param>
    /// <param name="feedForwardTorque">Optional feed-forward torque, N·m.</param>
    public static MotorCmd Position(float position, float kp, float kd, float feedForwardTorque = 0f) => new()
    {
        Mode = MotorMode.Servo,
        Q = position,
        Dq = 0f,
        Tau = feedForwardTorque,
        Kp = kp,
        Kd = kd,
    };

    /// <summary>Creates a pure-torque command with no position or velocity tracking.</summary>
    public static MotorCmd Torque(float torque) => new()
    {
        Mode = MotorMode.Servo,
        Tau = torque,
    };

    /// <summary>Creates a damping-only command, which resists motion without holding a position.</summary>
    /// <param name="kd">Damping gain.</param>
    public static MotorCmd Damping(float kd) => new()
    {
        Mode = MotorMode.Servo,
        Kd = kd,
    };

    /// <summary>Writes this command in CDR form.</summary>
    public readonly void Write(ref CdrWriter writer)
    {
        // A CDR struct is aligned to its most-aligned member — four bytes here, because of the floats.
        // The first member is a single byte, so nothing else would trigger that alignment, and the whole
        // 20-element array would land two bytes early. Every field after it would then be off by two.
        writer.Align(4);

        writer.WriteByte(Mode);
        writer.WriteSingle(Q);
        writer.WriteSingle(Dq);
        writer.WriteSingle(Tau);
        writer.WriteSingle(Kp);
        writer.WriteSingle(Kd);
        writer.WriteUInt32Array(Reserve);
    }

    /// <summary>Reads a command from CDR form.</summary>
    public static MotorCmd Read(ref CdrReader reader)
    {
        reader.Align(4);

        MotorCmd cmd = default;
        cmd.Mode = reader.ReadByte();
        cmd.Q = reader.ReadSingle();
        cmd.Dq = reader.ReadSingle();
        cmd.Tau = reader.ReadSingle();
        cmd.Kp = reader.ReadSingle();
        cmd.Kd = reader.ReadSingle();
        reader.ReadUInt32Array(cmd.Reserve);
        return cmd;
    }
}

/// <summary>
/// A single motor's reported state, matching <c>unitree_go::msg::dds_::MotorState_</c>.
/// </summary>
/// <remarks>Encoded size is 48 bytes.</remarks>
[StructLayout(LayoutKind.Sequential)]
public struct MotorState
{
    /// <summary>Encoded size in bytes.</summary>
    public const int EncodedSize = 48;

    /// <summary>Reported control mode.</summary>
    public byte Mode;

    /// <summary>Measured position, radians.</summary>
    public float Q;

    /// <summary>Measured velocity, rad/s.</summary>
    public float Dq;

    /// <summary>Measured acceleration, rad/s².</summary>
    public float Ddq;

    /// <summary>Estimated output torque, N·m.</summary>
    public float TauEst;

    /// <summary>Unfiltered position reading.</summary>
    public float QRaw;

    /// <summary>Unfiltered velocity reading.</summary>
    public float DqRaw;

    /// <summary>Unfiltered acceleration reading.</summary>
    public float DdqRaw;

    /// <summary>Motor temperature, °C.</summary>
    public sbyte Temperature;

    /// <summary>
    /// Count of lost communication frames to this motor.
    /// </summary>
    /// <remarks>A rising value on one joint usually means a failing motor cable, not a network problem.</remarks>
    public uint Lost;

    /// <summary>Reserved.</summary>
    public UInt32x2 Reserve;

    /// <summary>Writes this state in CDR form.</summary>
    public readonly void Write(ref CdrWriter writer)
    {
        writer.Align(4);

        writer.WriteByte(Mode);
        writer.WriteSingle(Q);
        writer.WriteSingle(Dq);
        writer.WriteSingle(Ddq);
        writer.WriteSingle(TauEst);
        writer.WriteSingle(QRaw);
        writer.WriteSingle(DqRaw);
        writer.WriteSingle(DdqRaw);
        writer.WriteSByte(Temperature);
        writer.WriteUInt32(Lost);
        writer.WriteUInt32Array(Reserve);
    }

    /// <summary>Reads a state from CDR form.</summary>
    public static MotorState Read(ref CdrReader reader)
    {
        reader.Align(4);

        MotorState state = default;
        state.Mode = reader.ReadByte();
        state.Q = reader.ReadSingle();
        state.Dq = reader.ReadSingle();
        state.Ddq = reader.ReadSingle();
        state.TauEst = reader.ReadSingle();
        state.QRaw = reader.ReadSingle();
        state.DqRaw = reader.ReadSingle();
        state.DdqRaw = reader.ReadSingle();
        state.Temperature = reader.ReadSByte();
        state.Lost = reader.ReadUInt32();
        reader.ReadUInt32Array(state.Reserve);
        return state;
    }
}

/// <summary>
/// Inertial measurement unit state, matching <c>unitree_go::msg::dds_::IMUState_</c>.
/// </summary>
/// <remarks>Encoded size is 56 bytes including trailing alignment padding.</remarks>
[StructLayout(LayoutKind.Sequential)]
public struct ImuState
{
    /// <summary>Encoded size in bytes.</summary>
    public const int EncodedSize = 56;

    /// <summary>Orientation quaternion in <c>w, x, y, z</c> order.</summary>
    /// <remarks>
    /// Note the ordering: Unitree puts the scalar first, whereas <see cref="System.Numerics.Quaternion"/>
    /// puts it last. <see cref="ToQuaternion"/> handles the swap.
    /// </remarks>
    public Float4 Quaternion;

    /// <summary>Angular velocity about x, y, z, rad/s.</summary>
    public Float3 Gyroscope;

    /// <summary>Linear acceleration along x, y, z, m/s².</summary>
    public Float3 Accelerometer;

    /// <summary>Roll, pitch and yaw, radians.</summary>
    public Float3 Rpy;

    /// <summary>IMU die temperature, °C.</summary>
    public sbyte Temperature;

    /// <summary>Converts <see cref="Quaternion"/> to the <c>x, y, z, w</c> ordering used by System.Numerics.</summary>
    public readonly System.Numerics.Quaternion ToQuaternion() =>
        new(Quaternion[1], Quaternion[2], Quaternion[3], Quaternion[0]);

    /// <summary>Gets orientation as roll/pitch/yaw in radians.</summary>
    public readonly Core.EulerAngles ToEuler() => new(Rpy[0], Rpy[1], Rpy[2]);

    /// <summary>Writes this state in CDR form.</summary>
    public readonly void Write(ref CdrWriter writer)
    {
        writer.Align(4);
        writer.WriteSingleArray(Quaternion);
        writer.WriteSingleArray(Gyroscope);
        writer.WriteSingleArray(Accelerometer);
        writer.WriteSingleArray(Rpy);
        writer.WriteSByte(Temperature);

        // The IDL struct is 4-byte aligned, so the trailing int8 is followed by three padding bytes
        // that are part of the wire format and must be emitted.
        writer.Align(4);
    }

    /// <summary>Reads a state from CDR form.</summary>
    public static ImuState Read(ref CdrReader reader)
    {
        reader.Align(4);

        ImuState state = default;
        reader.ReadSingleArray(state.Quaternion);
        reader.ReadSingleArray(state.Gyroscope);
        reader.ReadSingleArray(state.Accelerometer);
        reader.ReadSingleArray(state.Rpy);
        state.Temperature = reader.ReadSByte();
        reader.Align(4);
        return state;
    }
}

/// <summary>
/// Battery management system command, matching <c>unitree_go::msg::dds_::BmsCmd_</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct BmsCmd
{
    /// <summary>Encoded size in bytes.</summary>
    public const int EncodedSize = 4;

    /// <summary>Set to <c>0xA5</c> to request a battery shutdown.</summary>
    public byte Off;

    /// <summary>Reserved.</summary>
    public Byte3 Reserve;

    /// <summary>Writes this command in CDR form.</summary>
    public readonly void Write(ref CdrWriter writer)
    {
        writer.WriteByte(Off);
        writer.WriteByteArray(Reserve);
    }

    /// <summary>Reads a command from CDR form.</summary>
    public static BmsCmd Read(ref CdrReader reader)
    {
        BmsCmd cmd = default;
        cmd.Off = reader.ReadByte();
        reader.ReadByteArray(cmd.Reserve);
        return cmd;
    }
}

/// <summary>
/// Battery management system state, matching <c>unitree_go::msg::dds_::BmsState_</c>.
/// </summary>
/// <remarks>Encoded size is 44 bytes.</remarks>
[StructLayout(LayoutKind.Sequential)]
public struct BmsState
{
    /// <summary>Encoded size in bytes.</summary>
    public const int EncodedSize = 44;

    /// <summary>Firmware major version.</summary>
    public byte VersionHigh;

    /// <summary>Firmware minor version.</summary>
    public byte VersionLow;

    /// <summary>Pack status flags.</summary>
    public byte Status;

    /// <summary>State of charge, percent.</summary>
    public byte Soc;

    /// <summary>Pack current in milliamps. Negative means discharging.</summary>
    public int Current;

    /// <summary>Charge cycle count.</summary>
    public ushort Cycle;

    /// <summary>Battery gauge NTC temperatures, °C.</summary>
    public SByte2 BqNtc;

    /// <summary>Pack controller NTC temperatures, °C.</summary>
    public SByte2 McuNtc;

    /// <summary>Per-cell voltages, millivolts.</summary>
    public UInt16x15 CellVoltage;

    /// <summary>Gets the pack voltage in volts, summed across cells.</summary>
    public readonly float GetPackVoltage()
    {
        int total = 0;
        for (int i = 0; i < 15; i++)
        {
            total += CellVoltage[i];
        }

        return total / 1000f;
    }

    /// <summary>
    /// Gets the spread between the highest and lowest cell voltage, in millivolts.
    /// </summary>
    /// <remarks>
    /// A healthy pack stays under roughly 50 mV. A widening spread is the earliest signal of a failing
    /// cell, well before state of charge starts misbehaving.
    /// </remarks>
    public readonly int GetCellImbalanceMillivolts()
    {
        int min = int.MaxValue;
        int max = int.MinValue;

        for (int i = 0; i < 15; i++)
        {
            int value = CellVoltage[i];

            // Unpopulated cells report zero on packs with fewer than 15 cells; including them would
            // report a permanent imbalance of the full pack voltage.
            if (value == 0)
            {
                continue;
            }

            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }
        }

        return min == int.MaxValue ? 0 : max - min;
    }

    /// <summary>Writes this state in CDR form.</summary>
    public readonly void Write(ref CdrWriter writer)
    {
        writer.Align(4);
        writer.WriteByte(VersionHigh);
        writer.WriteByte(VersionLow);
        writer.WriteByte(Status);
        writer.WriteByte(Soc);
        writer.WriteInt32(Current);
        writer.WriteUInt16(Cycle);

        for (int i = 0; i < 2; i++)
        {
            writer.WriteSByte(BqNtc[i]);
        }

        for (int i = 0; i < 2; i++)
        {
            writer.WriteSByte(McuNtc[i]);
        }

        writer.WriteUInt16Array(CellVoltage);
    }

    /// <summary>Reads a state from CDR form.</summary>
    public static BmsState Read(ref CdrReader reader)
    {
        reader.Align(4);

        BmsState state = default;
        state.VersionHigh = reader.ReadByte();
        state.VersionLow = reader.ReadByte();
        state.Status = reader.ReadByte();
        state.Soc = reader.ReadByte();
        state.Current = reader.ReadInt32();
        state.Cycle = reader.ReadUInt16();

        for (int i = 0; i < 2; i++)
        {
            state.BqNtc[i] = reader.ReadSByte();
        }

        for (int i = 0; i < 2; i++)
        {
            state.McuNtc[i] = reader.ReadSByte();
        }

        reader.ReadUInt16Array(state.CellVoltage);
        return state;
    }
}
