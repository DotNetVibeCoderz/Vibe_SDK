using System.Numerics;
using Shouldly;
using Unitree.Net.Core;
using Unitree.Net.Messages.Cdr;

namespace Unitree.Net.Tests;

public sealed class UnitreeCrc32Tests
{
    [Fact]
    public void EmptyInputReturnsTheSeed()
    {
        UnitreeCrc32.Compute(ReadOnlySpan<uint>.Empty).ShouldBe(0xFFFFFFFFu);
    }

    [Fact]
    public void ComputationIsDeterministic()
    {
        uint[] words = [0x01020304, 0xDEADBEEF, 0x00000000, 0xFFFFFFFF];

        uint first = UnitreeCrc32.Compute(words);
        uint second = UnitreeCrc32.Compute(words);

        first.ShouldBe(second);
    }

    [Fact]
    public void SingleBitChangeChangesTheChecksum()
    {
        uint[] original = [0x01020304, 0xDEADBEEF];
        uint[] mutated = [0x01020305, 0xDEADBEEF];

        UnitreeCrc32.Compute(original).ShouldNotBe(UnitreeCrc32.Compute(mutated));
    }

    [Fact]
    public void ByteOverloadRejectsUnalignedLength()
    {
        byte[] unaligned = new byte[7];

        Should.Throw<ArgumentException>(() => UnitreeCrc32.Compute(unaligned.AsSpan()));
    }

    [Fact]
    public void ByteAndWordOverloadsAgree()
    {
        uint[] words = [0x11223344, 0x55667788];
        byte[] bytes = new byte[8];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()).CopyTo(bytes);

        UnitreeCrc32.Compute(bytes.AsSpan()).ShouldBe(UnitreeCrc32.Compute(words));
    }
}

public sealed class RobotMathTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(-1f, -1f)]
    public void WrapAngleLeavesInRangeValuesUntouched(float input, float expected)
    {
        RobotMath.WrapAngle(input).ShouldBe(expected, 1e-5f);
    }

    [Fact]
    public void WrapAngleFoldsValuesAbovePi()
    {
        RobotMath.WrapAngle(MathF.PI + 0.5f).ShouldBe(-MathF.PI + 0.5f, 1e-5f);
    }

    [Fact]
    public void WrapAngleFoldsValuesBelowNegativePi()
    {
        RobotMath.WrapAngle(-MathF.PI - 0.5f).ShouldBe(MathF.PI - 0.5f, 1e-5f);
    }

    [Fact]
    public void WrapAngleHandlesMultipleRevolutions()
    {
        RobotMath.WrapAngle((MathF.PI * 6f) + 0.25f).ShouldBe(0.25f, 1e-4f);
    }

    /// <summary>
    /// The case that motivates wrapping at all: a robot at +179° turning to -179°.
    /// </summary>
    [Fact]
    public void AngleDifferenceTakesTheShortWayRound()
    {
        float from = float.DegreesToRadians(179f);
        float to = float.DegreesToRadians(-179f);

        float difference = RobotMath.AngleDifference(from, to);

        float.RadiansToDegrees(difference).ShouldBe(2f, 0.01f);
    }

    [Fact]
    public void EulerAndQuaternionConversionsAreInverses()
    {
        var original = new EulerAngles(0.3f, -0.2f, 1.1f);

        Quaternion quaternion = RobotMath.ToQuaternion(original);
        EulerAngles recovered = RobotMath.ToEuler(quaternion);

        recovered.Roll.ShouldBe(original.Roll, 1e-4f);
        recovered.Pitch.ShouldBe(original.Pitch, 1e-4f);
        recovered.Yaw.ShouldBe(original.Yaw, 1e-4f);
    }

    /// <summary>
    /// Gimbal-lock input must not produce NaN — a NaN here silently poisons every downstream control value.
    /// </summary>
    [Fact]
    public void ToEulerClampsAtGimbalLockInsteadOfProducingNaN()
    {
        Quaternion straightUp = RobotMath.ToQuaternion(new EulerAngles(0f, MathF.PI / 2f, 0f));

        EulerAngles result = RobotMath.ToEuler(straightUp);

        float.IsNaN(result.Pitch).ShouldBeFalse();
        MathF.Abs(result.Pitch).ShouldBe(MathF.PI / 2f, 1e-3f);
    }

    [Fact]
    public void WorldToBodyRotatesIntoTheRobotFrame()
    {
        // Facing 90°, a vector pointing along world +Y is straight ahead in the body frame.
        Vector2 body = RobotMath.WorldToBody(new Vector2(0f, 1f), MathF.PI / 2f);

        body.X.ShouldBe(1f, 1e-5f);
        body.Y.ShouldBe(0f, 1e-5f);
    }

    [Fact]
    public void RateLimitCapsTheStepSize()
    {
        RobotMath.RateLimit(0f, 10f, 0.5f).ShouldBe(0.5f);
        RobotMath.RateLimit(0f, -10f, 0.5f).ShouldBe(-0.5f);
    }

    [Fact]
    public void RateLimitPassesSmallStepsThrough()
    {
        RobotMath.RateLimit(1f, 1.2f, 0.5f).ShouldBe(1.2f, 1e-6f);
    }
}

public sealed class SafetyLimitTests
{
    [Fact]
    public void VelocityCommandsAreClampedToTheEnvelope()
    {
        var limits = new VelocityLimits(1f, 0.5f, 1f);
        var requested = new VelocityCommand(5f, -3f, 9f);

        VelocityCommand clamped = requested.Clamp(limits);

        clamped.Forward.ShouldBe(1f);
        clamped.Lateral.ShouldBe(-0.5f);
        clamped.YawRate.ShouldBe(1f);
    }

    [Fact]
    public void ExcessiveTorqueIsRejected()
    {
        JointSafetyLimits limits = JointSafetyLimits.Go2Default;

        SafetyViolationException exception = Should.Throw<SafetyViolationException>(
            () => limits.Validate(0, 0f, 0f, 1000f, 50f, 2f));

        exception.LimitName.ShouldContain("Torque");
        exception.Requested.ShouldBe(1000f);
        exception.Limit.ShouldBe(limits.MaxTorque);
    }

    /// <summary>
    /// A NaN setpoint must be rejected outright: it propagates through the impedance law and produces
    /// undefined motor behaviour rather than an obvious failure.
    /// </summary>
    [Fact]
    public void NonFiniteCommandsAreRejected()
    {
        JointSafetyLimits limits = JointSafetyLimits.Go2Default;

        Should.Throw<SafetyViolationException>(() => limits.Validate(0, float.NaN, 0f, 0f, 50f, 2f));
        Should.Throw<SafetyViolationException>(() => limits.Validate(0, 0f, float.PositiveInfinity, 0f, 50f, 2f));
    }

    [Fact]
    public void ClampReplacesNonFiniteValuesWithZero()
    {
        JointSafetyLimits limits = JointSafetyLimits.Go2Default;

        float position = float.NaN;
        float velocity = 0f;
        float torque = float.PositiveInfinity;
        float kp = 1e9f;
        float kd = -5f;

        limits.Clamp(ref position, ref velocity, ref torque, ref kp, ref kd);

        position.ShouldBe(0f);
        torque.ShouldBe(0f);
        kp.ShouldBe(limits.MaxKp);
        kd.ShouldBe(0f);
    }

    [Fact]
    public void NegativeGainsAreRejected()
    {
        JointSafetyLimits limits = JointSafetyLimits.Go2Default;

        Should.Throw<SafetyViolationException>(() => limits.Validate(0, 0f, 0f, 0f, -1f, 2f));
    }
}

public sealed class UnitreeOptionsTests
{
    [Fact]
    public void UnknownModelIsRejected()
    {
        var options = new UnitreeOptions { Model = RobotModel.Unknown };

        Should.Throw<OptionsValidationFailure>(options.Validate);
    }

    [Fact]
    public void NonMulticastAddressIsRejectedForTheMulticastTransport()
    {
        var options = new UnitreeOptions
        {
            Model = RobotModel.Go2,
            Transport = DdsTransportKind.ManagedMulticast,
            MulticastAddress = "192.168.1.10",
        };

        OptionsValidationFailure failure = Should.Throw<OptionsValidationFailure>(options.Validate);
        failure.Message.ShouldContain("multicast range");
    }

    [Fact]
    public void ControlFrequencyDefaultsToTheModelRecommendation()
    {
        var options = new UnitreeOptions { Model = RobotModel.Go2, ControlFrequencyHz = 0 };

        options.GetEffectiveControlFrequencyHz().ShouldBe(500);
    }

    [Fact]
    public void ExplicitControlFrequencyOverridesTheDefault()
    {
        var options = new UnitreeOptions { Model = RobotModel.Go2, ControlFrequencyHz = 200 };

        options.GetEffectiveControlFrequencyHz().ShouldBe(200);
    }

    [Fact]
    public void OutOfRangeControlFrequencyIsRejected()
    {
        var options = new UnitreeOptions
        {
            Model = RobotModel.Go2,
            Transport = DdsTransportKind.Loopback,
            ControlFrequencyHz = 5000,
        };

        Should.Throw<OptionsValidationFailure>(options.Validate);
    }
}

public sealed class RobotModelInfoTests
{
    [Theory]
    [InlineData(RobotModel.Go2, IdlFamily.Go, 20, 12)]
    [InlineData(RobotModel.B2, IdlFamily.Go, 20, 12)]
    [InlineData(RobotModel.G1, IdlFamily.Hg, 35, 29)]
    [InlineData(RobotModel.H1, IdlFamily.Hg, 35, 19)]
    public void ModelFactsAreConsistent(RobotModel model, IdlFamily family, int slots, int actuated)
    {
        RobotModelInfo.GetIdlFamily(model).ShouldBe(family);
        RobotModelInfo.GetMotorSlotCount(model).ShouldBe(slots);
        RobotModelInfo.GetActuatedJointCount(model).ShouldBe(actuated);
    }

    [Fact]
    public void ActuatedJointsNeverExceedAvailableSlots()
    {
        foreach (RobotModel model in Enum.GetValues<RobotModel>())
        {
            if (model == RobotModel.Unknown)
            {
                continue;
            }

            RobotModelInfo.GetActuatedJointCount(model)
                .ShouldBeLessThanOrEqualTo(RobotModelInfo.GetMotorSlotCount(model));
        }
    }

    [Fact]
    public void OnlyHumanoidsReportArms()
    {
        RobotModelInfo.HasArms(RobotModel.Go2).ShouldBeFalse();
        RobotModelInfo.HasArms(RobotModel.G1).ShouldBeTrue();
    }
}

public sealed class CdrCodecTests
{
    [Fact]
    public void PrimitivesRoundTripWithCorrectAlignment()
    {
        var buffer = new byte[128];
        var writer = new CdrWriter(buffer);

        writer.WriteByte(0xAB);
        writer.WriteUInt32(0xDEADBEEF);
        writer.WriteInt16(-1234);
        writer.WriteDouble(3.14159);
        writer.WriteSingle(2.5f);
        writer.WriteBool(true);
        writer.WriteInt64(-9_000_000_000L);

        var reader = new CdrReader(buffer);

        reader.ReadByte().ShouldBe((byte)0xAB);
        reader.ReadUInt32().ShouldBe(0xDEADBEEFu);
        reader.ReadInt16().ShouldBe((short)-1234);
        reader.ReadDouble().ShouldBe(3.14159, 1e-9);
        reader.ReadSingle().ShouldBe(2.5f);
        reader.ReadBool().ShouldBeTrue();
        reader.ReadInt64().ShouldBe(-9_000_000_000L);
    }

    /// <summary>
    /// The CDR string length prefix includes the null terminator. A reader that assumes otherwise
    /// truncates the final character of every string it decodes.
    /// </summary>
    [Fact]
    public void StringLengthPrefixIncludesTheTerminator()
    {
        var buffer = new byte[64];
        var writer = new CdrWriter(buffer);
        writer.WriteString("abc");

        var reader = new CdrReader(buffer);
        reader.ReadUInt32().ShouldBe(4u);

        var rereader = new CdrReader(buffer);
        rereader.ReadString().ShouldBe("abc");
    }

    [Fact]
    public void EmptyStringRoundTrips()
    {
        var buffer = new byte[32];
        var writer = new CdrWriter(buffer);
        writer.WriteString(string.Empty);

        var reader = new CdrReader(buffer);
        reader.ReadString().ShouldBe(string.Empty);
    }

    [Fact]
    public void Utf8StringsRoundTrip()
    {
        var buffer = new byte[128];
        var writer = new CdrWriter(buffer);
        writer.WriteString("robot — kontrol tingkat rendah");

        var reader = new CdrReader(buffer);
        reader.ReadString().ShouldBe("robot — kontrol tingkat rendah");
    }

    [Fact]
    public void AlignmentPaddingIsZeroFilled()
    {
        // Non-zero fill, so leftover bytes would be visible if padding were not cleared.
        var buffer = new byte[64];
        Array.Fill(buffer, (byte)0xFF);

        var writer = new CdrWriter(buffer);
        writer.WriteByte(0x01);
        writer.WriteUInt32(0);

        // Header is 4 bytes, then the byte at offset 4, then three padding bytes before the aligned uint.
        buffer[5].ShouldBe((byte)0);
        buffer[6].ShouldBe((byte)0);
        buffer[7].ShouldBe((byte)0);
    }

    [Fact]
    public void OverflowingTheBufferThrows()
    {
        var buffer = new byte[8];
        var writer = new CdrWriter(buffer);

        Should.Throw<CdrFormatException>(() =>
        {
            var local = new CdrWriter(buffer);
            local.WriteDouble(1.0);
            local.WriteDouble(2.0);
        });
    }

    [Fact]
    public void TruncatedPayloadThrows()
    {
        var buffer = new byte[8];
        _ = new CdrWriter(buffer);

        Should.Throw<CdrFormatException>(() =>
        {
            var reader = new CdrReader(buffer);
            reader.ReadInt64();
            reader.ReadInt64();
        });
    }

    [Fact]
    public void BigEndianEncapsulationIsRejectedRatherThanMisread()
    {
        var buffer = new byte[16];
        buffer[0] = 0x00;
        buffer[1] = 0x00; // Plain CDR big-endian.

        Should.Throw<CdrFormatException>(() => new CdrReader(buffer));
    }

    [Fact]
    public void ByteSequenceRoundTrips()
    {
        var buffer = new byte[64];
        var writer = new CdrWriter(buffer);
        writer.WriteByteSequence([1, 2, 3, 4, 5]);

        var reader = new CdrReader(buffer);
        reader.ReadByteSequence().ToArray().ShouldBe(new byte[] { 1, 2, 3, 4, 5 });
    }
}
