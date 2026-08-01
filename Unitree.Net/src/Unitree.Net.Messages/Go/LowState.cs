using System.Runtime.InteropServices;
using Unitree.Net.Core;
using Unitree.Net.Messages.Cdr;

namespace Unitree.Net.Messages.Go;

/// <summary>
/// Low-level state published by the robot on <c>rt/lowstate</c>, matching
/// <c>unitree_go::msg::dds_::LowState_</c>.
/// </summary>
/// <remarks>Encoded body size is 1180 bytes; the robot publishes at 500 Hz.</remarks>
[StructLayout(LayoutKind.Sequential)]
public struct LowState : ICdrSerializable<LowState>
{
    /// <summary>Encoded body size in bytes, excluding the CDR encapsulation header.</summary>
    public const int BodySize = 1180;

    /// <summary>Frame header magic.</summary>
    public Byte2 Head;

    /// <summary>Control level indicator.</summary>
    public byte LevelFlag;

    /// <summary>Reserved frame field.</summary>
    public byte FrameReserve;

    /// <summary>Robot serial number.</summary>
    public UInt32x2 Sn;

    /// <summary>Protocol version.</summary>
    public UInt32x2 Version;

    /// <summary>Reported bandwidth.</summary>
    public ushort Bandwidth;

    /// <summary>Body IMU state.</summary>
    public ImuState ImuState;

    /// <summary>Per-motor state.</summary>
    public MotorStateArray MotorState;

    /// <summary>Battery state.</summary>
    public BmsState BmsState;

    /// <summary>Measured foot contact force per leg, in the FR/FL/RR/RL order.</summary>
    public Int16x4 FootForce;

    /// <summary>Estimated foot contact force per leg.</summary>
    public Int16x4 FootForceEst;

    /// <summary>Robot uptime counter in milliseconds.</summary>
    public uint Tick;

    /// <summary>Raw wireless remote payload.</summary>
    public Byte40 WirelessRemote;

    /// <summary>Status bit flags.</summary>
    public byte BitFlag;

    /// <summary>Reel ADC reading.</summary>
    public float AdcReel;

    /// <summary>First NTC thermistor reading, °C.</summary>
    public sbyte TemperatureNtc1;

    /// <summary>Second NTC thermistor reading, °C.</summary>
    public sbyte TemperatureNtc2;

    /// <summary>Main rail voltage, volts.</summary>
    public float PowerV;

    /// <summary>Main rail current, amps.</summary>
    public float PowerA;

    /// <summary>Cooling fan speeds.</summary>
    public UInt16x4 FanFrequency;

    /// <summary>Reserved.</summary>
    public uint Reserve;

    /// <summary>Message checksum.</summary>
    public uint Crc;

    /// <inheritdoc />
    public static string DdsTypeName => "unitree_go::msg::dds_::LowState_";

    /// <inheritdoc />
    public static int MaxSerializedSize => CdrConstants.EncapsulationHeaderSize + BodySize;

    /// <summary>Verifies the received checksum.</summary>
    public readonly bool IsCrcValid()
    {
        LowState copy = this;
        uint received = copy.Crc;
        copy.Crc = 0;
        return UnitreeCrc32.ComputeForMessage(in copy) == received;
    }

    /// <summary>
    /// Recomputes <see cref="Crc"/> over the current contents.
    /// </summary>
    /// <remarks>
    /// The robot fills this in itself, so application code reading telemetry never needs it. It exists
    /// for anything that <em>produces</em> a <see cref="LowState"/> — simulators, replay tools, tests —
    /// which must checksum their output or consumers will reject it.
    /// </remarks>
    public void UpdateCrc()
    {
        Crc = 0;
        Crc = UnitreeCrc32.ComputeForMessage(in this);
    }

    /// <summary>Gets the hottest motor temperature across the actuated joints, °C.</summary>
    /// <param name="jointCount">How many leading motor slots are actuated.</param>
    public readonly int GetMaxMotorTemperature(int jointCount = GoJoint.Count)
    {
        int max = int.MinValue;
        int limit = Math.Min(jointCount, RobotModelInfo.GoMotorSlots);

        for (int i = 0; i < limit; i++)
        {
            int temperature = MotorState[i].Temperature;
            if (temperature > max)
            {
                max = temperature;
            }
        }

        return max == int.MinValue ? 0 : max;
    }

    /// <summary>
    /// Determines whether body attitude indicates the robot has fallen.
    /// </summary>
    /// <param name="thresholdRadians">Absolute roll or pitch beyond which the robot counts as fallen.</param>
    public readonly bool IsFallen(float thresholdRadians)
    {
        EulerAngles rpy = ImuState.ToEuler();
        return MathF.Abs(rpy.Roll) > thresholdRadians || MathF.Abs(rpy.Pitch) > thresholdRadians;
    }

    /// <inheritdoc />
    public readonly int Serialize(Span<byte> destination)
    {
        var writer = new CdrWriter(destination);
        writer.WriteByteArray(Head);
        writer.WriteByte(LevelFlag);
        writer.WriteByte(FrameReserve);
        writer.WriteUInt32Array(Sn);
        writer.WriteUInt32Array(Version);
        writer.WriteUInt16(Bandwidth);
        ImuState.Write(ref writer);

        for (int i = 0; i < RobotModelInfo.GoMotorSlots; i++)
        {
            MotorState[i].Write(ref writer);
        }

        BmsState.Write(ref writer);
        writer.WriteInt16Array(FootForce);
        writer.WriteInt16Array(FootForceEst);
        writer.WriteUInt32(Tick);
        writer.WriteByteArray(WirelessRemote);
        writer.WriteByte(BitFlag);
        writer.WriteSingle(AdcReel);
        writer.WriteSByte(TemperatureNtc1);
        writer.WriteSByte(TemperatureNtc2);
        writer.WriteSingle(PowerV);
        writer.WriteSingle(PowerA);
        writer.WriteUInt16Array(FanFrequency);
        writer.WriteUInt32(Reserve);
        writer.WriteUInt32(Crc);

        return writer.BytesWritten;
    }

    /// <inheritdoc />
    public static LowState Deserialize(ReadOnlySpan<byte> source)
    {
        var reader = new CdrReader(source);
        LowState state = default;

        reader.ReadByteArray(state.Head);
        state.LevelFlag = reader.ReadByte();
        state.FrameReserve = reader.ReadByte();
        reader.ReadUInt32Array(state.Sn);
        reader.ReadUInt32Array(state.Version);
        state.Bandwidth = reader.ReadUInt16();
        state.ImuState = Go.ImuState.Read(ref reader);

        for (int i = 0; i < RobotModelInfo.GoMotorSlots; i++)
        {
            state.MotorState[i] = Go.MotorState.Read(ref reader);
        }

        state.BmsState = Go.BmsState.Read(ref reader);
        reader.ReadInt16Array(state.FootForce);
        reader.ReadInt16Array(state.FootForceEst);
        state.Tick = reader.ReadUInt32();
        reader.ReadByteArray(state.WirelessRemote);
        state.BitFlag = reader.ReadByte();
        state.AdcReel = reader.ReadSingle();
        state.TemperatureNtc1 = reader.ReadSByte();
        state.TemperatureNtc2 = reader.ReadSByte();
        state.PowerV = reader.ReadSingle();
        state.PowerA = reader.ReadSingle();
        reader.ReadUInt16Array(state.FanFrequency);
        state.Reserve = reader.ReadUInt32();
        state.Crc = reader.ReadUInt32();

        return state;
    }
}
