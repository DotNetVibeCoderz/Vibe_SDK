using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shouldly;
using Unitree.Net.Core;
using Unitree.Net.Messages;
using Unitree.Net.Messages.Api;
using Unitree.Net.Messages.Cdr;
using Unitree.Net.Messages.Go;

namespace Unitree.Net.Tests;

/// <summary>
/// Verifies the wire format against Unitree's IDL.
/// </summary>
/// <remarks>
/// These are the highest-value tests in the suite. Everything else can be debugged by reading logs;
/// a wire-format error produces a robot that silently ignores commands, with no diagnostic anywhere.
/// </remarks>
public sealed class MessageWireFormatTests
{
    [Theory]
    [InlineData(typeof(MotorCmd), 36)]
    [InlineData(typeof(MotorState), 48)]
    [InlineData(typeof(ImuState), 56)]
    [InlineData(typeof(BmsState), 44)]
    [InlineData(typeof(BmsCmd), 4)]
    public void StructSizesMatchIdl(Type type, int expectedSize)
    {
        int actual = type switch
        {
            _ when type == typeof(MotorCmd) => Unsafe.SizeOf<MotorCmd>(),
            _ when type == typeof(MotorState) => Unsafe.SizeOf<MotorState>(),
            _ when type == typeof(ImuState) => Unsafe.SizeOf<ImuState>(),
            _ when type == typeof(BmsState) => Unsafe.SizeOf<BmsState>(),
            _ when type == typeof(BmsCmd) => Unsafe.SizeOf<BmsCmd>(),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        actual.ShouldBe(expectedSize, $"{type.Name} must match the unitree_go IDL layout exactly.");
    }

    [Fact]
    public void LowCmdStructSizeMatchesDeclaredBodySize()
    {
        Unsafe.SizeOf<LowCmd>().ShouldBe(LowCmd.BodySize);
    }

    [Fact]
    public void LowStateStructSizeMatchesDeclaredBodySize()
    {
        Unsafe.SizeOf<LowState>().ShouldBe(LowState.BodySize);
    }

    [Fact]
    public void SportModeStateStructSizeMatchesDeclaredBodySize()
    {
        Unsafe.SizeOf<SportModeState>().ShouldBe(SportModeState.BodySize);
    }

    /// <summary>
    /// The decisive wire-format check.
    /// </summary>
    /// <remarks>
    /// Unitree computes the CRC over the C++ struct's raw memory. This SDK computes it over the same
    /// struct and then serialises to CDR separately. Those two only agree if the CDR encoding is
    /// byte-identical to the struct layout — so this test is what makes the CRC trustworthy at all.
    /// </remarks>
    [Fact]
    public void LowCmdCdrBodyIsByteIdenticalToStructMemory()
    {
        LowCmd command = LowCmd.CreateIdle();

        for (int i = 0; i < RobotModelInfo.GoMotorSlots; i++)
        {
            command.MotorCmd[i] = MotorCmd.Position(0.1f * i, 40f + i, 2f, -0.5f * i);
        }

        command.Sn[0] = 0xDEADBEEF;
        command.Version[1] = 7;
        command.Bandwidth = 1234;
        command.Led[3] = 0x77;
        command.Gpio = 0x5A;
        command.WirelessRemote[39] = 0xC3;
        command.UpdateCrc();

        var buffer = new byte[LowCmd.MaxSerializedSize];
        int written = command.Serialize(buffer);

        written.ShouldBe(LowCmd.MaxSerializedSize);

        ReadOnlySpan<byte> structBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<LowCmd>(in command));
        ReadOnlySpan<byte> cdrBody = buffer.AsSpan(CdrConstants.EncapsulationHeaderSize);

        cdrBody.SequenceEqual(structBytes).ShouldBeTrue(
            "the CDR body must equal the struct's raw memory, or the CRC we compute is not the CRC the robot computes.");
    }

    [Fact]
    public void LowStateCdrBodyIsByteIdenticalToStructMemory()
    {
        LowState state = BuildPopulatedLowState();

        var buffer = new byte[LowState.MaxSerializedSize];
        int written = state.Serialize(buffer);

        written.ShouldBe(LowState.MaxSerializedSize);

        ReadOnlySpan<byte> structBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<LowState>(in state));
        buffer.AsSpan(CdrConstants.EncapsulationHeaderSize).SequenceEqual(structBytes).ShouldBeTrue();
    }

    [Fact]
    public void LowCmdRoundTripsThroughCdr()
    {
        LowCmd original = LowCmd.CreateIdle();
        original.MotorCmd[0] = MotorCmd.Position(0.5f, 60f, 3f, 1.25f);
        original.MotorCmd[11] = MotorCmd.Torque(-2f);
        original.Gpio = 0x5A;

        var buffer = new byte[LowCmd.MaxSerializedSize];
        int written = original.Serialize(buffer);

        LowCmd decoded = LowCmd.Deserialize(buffer.AsSpan(0, written));

        decoded.MotorCmd[0].Q.ShouldBe(0.5f);
        decoded.MotorCmd[0].Kp.ShouldBe(60f);
        decoded.MotorCmd[0].Tau.ShouldBe(1.25f);
        decoded.MotorCmd[11].Tau.ShouldBe(-2f);
        decoded.Gpio.ShouldBe((byte)0x5A);
        decoded.Head[0].ShouldBe(LowCmd.HeadByte0);
        decoded.LevelFlag.ShouldBe(LowCmd.LowLevelFlag);
    }

    [Fact]
    public void SerializedLowCmdCarriesAValidCrc()
    {
        LowCmd command = LowCmd.CreateIdle();
        command.MotorCmd[3] = MotorCmd.Position(1.1f, 50f, 2f);

        var buffer = new byte[LowCmd.MaxSerializedSize];
        int written = command.Serialize(buffer);

        LowCmd decoded = LowCmd.Deserialize(buffer.AsSpan(0, written));

        // Serialize must refresh the CRC even though the caller never called UpdateCrc — a stale CRC is
        // dropped by the firmware without any error being reported.
        decoded.IsCrcValid().ShouldBeTrue();
        decoded.Crc.ShouldNotBe(0u);
    }

    [Fact]
    public void CorruptedPayloadFailsCrcValidation()
    {
        LowCmd command = LowCmd.CreateIdle();
        command.MotorCmd[0] = MotorCmd.Position(0.5f, 60f, 3f);

        var buffer = new byte[LowCmd.MaxSerializedSize];
        int written = command.Serialize(buffer);

        // Flip one bit in the middle of the motor block.
        buffer[200] ^= 0x01;

        LowCmd decoded = LowCmd.Deserialize(buffer.AsSpan(0, written));
        decoded.IsCrcValid().ShouldBeFalse();
    }

    [Fact]
    public void LowStateRoundTripsThroughCdr()
    {
        LowState original = BuildPopulatedLowState();

        var buffer = new byte[LowState.MaxSerializedSize];
        int written = original.Serialize(buffer);

        LowState decoded = LowState.Deserialize(buffer.AsSpan(0, written));

        decoded.MotorState[19].Temperature.ShouldBe((sbyte)49);
        decoded.MotorState[19].Q.ShouldBe(19 * 0.3f);
        decoded.BmsState.Current.ShouldBe(-3200);
        decoded.BmsState.Soc.ShouldBe((byte)88);
        decoded.PowerV.ShouldBe(28.5f);
        decoded.TemperatureNtc2.ShouldBe((sbyte)-5);
        decoded.FanFrequency[3].ShouldBe((ushort)900);
        decoded.Tick.ShouldBe(12345u);
        decoded.ImuState.Rpy[1].ShouldBe(0.25f);
    }

    [Fact]
    public void SportModeStateRoundTripsThroughCdr()
    {
        SportModeState original = default;
        original.Mode = (byte)SportMode.Locomotion;
        original.GaitType = (byte)GaitType.Trot;
        original.BodyHeight = 0.33f;
        original.Position[0] = 1.5f;
        original.Velocity[1] = -0.2f;
        original.FootPositionBody[11] = 9f;
        original.Stamp.Seconds = 99;

        var buffer = new byte[SportModeState.MaxSerializedSize];
        int written = original.Serialize(buffer);

        written.ShouldBe(SportModeState.MaxSerializedSize);

        SportModeState decoded = SportModeState.Deserialize(buffer.AsSpan(0, written));

        decoded.Mode.ShouldBe((byte)SportMode.Locomotion);
        decoded.GaitType.ShouldBe((byte)GaitType.Trot);
        decoded.BodyHeight.ShouldBe(0.33f);
        decoded.FootPositionBody[11].ShouldBe(9f);
        decoded.Stamp.Seconds.ShouldBe(99);
        decoded.GetPosition().X.ShouldBe(1.5f);
    }

    [Fact]
    public void ApiRequestRoundTripsIncludingJsonParameter()
    {
        ApiRequest original = ApiRequest.Create(SportApi.Move, """{"x":0.3,"y":0,"z":0}""");

        var buffer = new byte[ApiRequest.DefaultMaxSize];
        int written = original.Serialize(buffer);

        ApiRequest decoded = ApiRequest.Deserialize(buffer.AsSpan(0, written));

        decoded.Header.Identity.ApiId.ShouldBe(SportApi.Move);
        decoded.Header.Identity.Id.ShouldBe(original.Header.Identity.Id);
        decoded.Parameter.ShouldBe("""{"x":0.3,"y":0,"z":0}""");
    }

    [Fact]
    public void ApiResponseRoundTripsAndReportsStatus()
    {
        var original = new ApiResponse
        {
            Identity = new RequestIdentity(5, SportApi.Move),
            Status = new ResponseStatus(0),
            Data = """{"ok":true}""",
        };

        var buffer = new byte[ApiResponse.DefaultMaxSize];
        int written = original.Serialize(buffer);

        ApiResponse decoded = ApiResponse.Deserialize(buffer.AsSpan(0, written));

        decoded.Data.ShouldBe("""{"ok":true}""");
        decoded.Status.IsSuccess.ShouldBeTrue();
        Should.NotThrow(decoded.EnsureSuccess);
    }

    [Fact]
    public void FailedApiResponseThrowsWithTheApiIdAndCode()
    {
        var response = new ApiResponse
        {
            Identity = new RequestIdentity(1, SportApi.StandUp),
            Status = new ResponseStatus(3103),
        };

        UnitreeServiceException exception = Should.Throw<UnitreeServiceException>(response.EnsureSuccess);

        exception.ApiId.ShouldBe(SportApi.StandUp);
        exception.StatusCode.ShouldBe(3103);
    }

    private static LowState BuildPopulatedLowState()
    {
        LowState state = default;

        for (int i = 0; i < RobotModelInfo.GoMotorSlots; i++)
        {
            state.MotorState[i].Q = i * 0.3f;
            state.MotorState[i].Temperature = (sbyte)(30 + i);
            state.MotorState[i].Lost = (uint)i;
        }

        state.ImuState.Rpy[1] = 0.25f;
        state.ImuState.Temperature = 44;
        state.BmsState.Soc = 88;
        state.BmsState.Current = -3200;
        state.BmsState.CellVoltage[14] = 4110;
        state.PowerV = 28.5f;
        state.Tick = 12345;
        state.FanFrequency[3] = 900;
        state.TemperatureNtc2 = -5;

        return state;
    }
}
