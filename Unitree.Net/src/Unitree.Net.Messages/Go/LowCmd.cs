using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unitree.Net.Core;
using Unitree.Net.Messages.Cdr;

namespace Unitree.Net.Messages.Go;

/// <summary>A fixed 20-element array of motor commands, one per <c>unitree_go</c> motor slot.</summary>
[InlineArray(RobotModelInfo.GoMotorSlots)]
public struct MotorCmdArray
{
    private MotorCmd _element0;
}

/// <summary>A fixed 20-element array of motor states, one per <c>unitree_go</c> motor slot.</summary>
[InlineArray(RobotModelInfo.GoMotorSlots)]
public struct MotorStateArray
{
    private MotorState _element0;
}

/// <summary>
/// Low-level command published on <c>rt/lowcmd</c>, matching <c>unitree_go::msg::dds_::LowCmd_</c>.
/// </summary>
/// <remarks>
/// <para>
/// The robot validates <see cref="Crc"/> and silently discards any message that fails. Always call
/// <see cref="UpdateCrc"/> — or use the serialisation path, which does it for you — as the last step
/// before publishing.
/// </para>
/// <para>
/// Publishing on this topic requires the built-in sport service to be stopped first; otherwise the
/// on-board controller and your commands fight over the same motors.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct LowCmd : ICdrSerializable<LowCmd>
{
    /// <summary>Encoded body size in bytes, excluding the CDR encapsulation header.</summary>
    public const int BodySize = 812;

    /// <summary>The magic value Unitree firmware expects in <see cref="Head"/>.</summary>
    public const byte HeadByte0 = 0xFE;

    /// <summary>The second magic value Unitree firmware expects in <see cref="Head"/>.</summary>
    public const byte HeadByte1 = 0xEF;

    /// <summary>Level flag value selecting low-level (direct motor) control.</summary>
    public const byte LowLevelFlag = 0xFF;

    /// <summary>Frame header magic.</summary>
    public Byte2 Head;

    /// <summary>Control level selector; use <see cref="LowLevelFlag"/>.</summary>
    public byte LevelFlag;

    /// <summary>Reserved frame field.</summary>
    public byte FrameReserve;

    /// <summary>Serial number, robot-assigned.</summary>
    public UInt32x2 Sn;

    /// <summary>Protocol version.</summary>
    public UInt32x2 Version;

    /// <summary>Requested bandwidth.</summary>
    public ushort Bandwidth;

    /// <summary>Per-motor commands. Only the first 12 slots are actuated on a Go2.</summary>
    public MotorCmdArray MotorCmd;

    /// <summary>Battery management command.</summary>
    public BmsCmd BmsCmd;

    /// <summary>Raw wireless remote passthrough payload.</summary>
    public Byte40 WirelessRemote;

    /// <summary>LED colour payload.</summary>
    public Byte12 Led;

    /// <summary>Fan control payload.</summary>
    public Byte2 Fan;

    /// <summary>General-purpose output state.</summary>
    public byte Gpio;

    /// <summary>Reserved.</summary>
    public uint Reserve;

    /// <summary>Message checksum; see <see cref="UpdateCrc"/>.</summary>
    public uint Crc;

    /// <inheritdoc />
    public static string DdsTypeName => "unitree_go::msg::dds_::LowCmd_";

    /// <inheritdoc />
    public static int MaxSerializedSize => CdrConstants.EncapsulationHeaderSize + BodySize;

    /// <summary>
    /// Creates a command with the header fields populated and every motor slot idle.
    /// </summary>
    /// <remarks>
    /// Starting from idle rather than <see langword="default"/> matters: a zeroed struct puts every slot
    /// in mode 0 with zero gains, which is also safe, but this factory additionally sets the header magic
    /// the firmware requires. A command without it is dropped before the CRC is even checked.
    /// </remarks>
    public static LowCmd CreateIdle()
    {
        LowCmd cmd = default;
        cmd.Head[0] = HeadByte0;
        cmd.Head[1] = HeadByte1;
        cmd.LevelFlag = LowLevelFlag;

        for (int i = 0; i < RobotModelInfo.GoMotorSlots; i++)
        {
            cmd.MotorCmd[i] = Go.MotorCmd.Idle;
        }

        return cmd;
    }

    /// <summary>
    /// Recomputes <see cref="Crc"/> over the current contents.
    /// </summary>
    /// <remarks>
    /// The checksum covers the struct as 32-bit words up to but excluding the CRC field itself. Because
    /// the in-memory layout of this struct is identical to its CDR encoding — every member is a fixed-size
    /// primitive or array — the checksum can be taken directly over the struct without serialising first.
    /// </remarks>
    public void UpdateCrc()
    {
        Crc = 0;
        Crc = UnitreeCrc32.ComputeForMessage(in this);
    }

    /// <summary>Verifies <see cref="Crc"/> against the current contents.</summary>
    public readonly bool IsCrcValid()
    {
        LowCmd copy = this;
        uint received = copy.Crc;
        copy.UpdateCrc();
        return copy.Crc == received;
    }

    /// <summary>
    /// Sets every motor slot to a damping-only command.
    /// </summary>
    /// <param name="kd">Damping gain. Around 2–5 gives a controlled collapse rather than a drop.</param>
    /// <remarks>
    /// This is the correct emergency stop for a low-level session. Cutting to <see cref="MotorMode.Idle"/>
    /// removes all resistance and the robot falls; damping lets it settle.
    /// </remarks>
    public void SetAllDamping(float kd = 3f)
    {
        for (int i = 0; i < RobotModelInfo.GoMotorSlots; i++)
        {
            MotorCmd[i] = Go.MotorCmd.Damping(kd);
        }
    }

    /// <inheritdoc />
    public readonly int Serialize(Span<byte> destination)
    {
        // The CRC covers the struct including its own zeroed slot, so it must already be current.
        // Serialising a stale CRC is the single most common cause of "the robot ignores my commands".
        LowCmd copy = this;
        copy.UpdateCrc();

        var writer = new CdrWriter(destination);
        writer.WriteByteArray(copy.Head);
        writer.WriteByte(copy.LevelFlag);
        writer.WriteByte(copy.FrameReserve);
        writer.WriteUInt32Array(copy.Sn);
        writer.WriteUInt32Array(copy.Version);
        writer.WriteUInt16(copy.Bandwidth);

        for (int i = 0; i < RobotModelInfo.GoMotorSlots; i++)
        {
            copy.MotorCmd[i].Write(ref writer);
        }

        copy.BmsCmd.Write(ref writer);
        writer.WriteByteArray(copy.WirelessRemote);
        writer.WriteByteArray(copy.Led);
        writer.WriteByteArray(copy.Fan);
        writer.WriteByte(copy.Gpio);
        writer.WriteUInt32(copy.Reserve);
        writer.WriteUInt32(copy.Crc);

        return writer.BytesWritten;
    }

    /// <inheritdoc />
    public static LowCmd Deserialize(ReadOnlySpan<byte> source)
    {
        var reader = new CdrReader(source);
        LowCmd cmd = default;

        reader.ReadByteArray(cmd.Head);
        cmd.LevelFlag = reader.ReadByte();
        cmd.FrameReserve = reader.ReadByte();
        reader.ReadUInt32Array(cmd.Sn);
        reader.ReadUInt32Array(cmd.Version);
        cmd.Bandwidth = reader.ReadUInt16();

        for (int i = 0; i < RobotModelInfo.GoMotorSlots; i++)
        {
            cmd.MotorCmd[i] = Go.MotorCmd.Read(ref reader);
        }

        cmd.BmsCmd = Go.BmsCmd.Read(ref reader);
        reader.ReadByteArray(cmd.WirelessRemote);
        reader.ReadByteArray(cmd.Led);
        reader.ReadByteArray(cmd.Fan);
        cmd.Gpio = reader.ReadByte();
        cmd.Reserve = reader.ReadUInt32();
        cmd.Crc = reader.ReadUInt32();

        return cmd;
    }
}
