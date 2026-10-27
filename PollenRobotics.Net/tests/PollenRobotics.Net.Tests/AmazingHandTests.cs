using PollenRobotics.Net.AmazingHand;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Simulation;
using PollenRobotics.Net.Simulation.Robots;
using PollenRobotics.Net.Simulation.Transports;
using Shouldly;
using Xunit;

namespace PollenRobotics.Net.Tests;

/// <summary>Checks on the Amazing Hand's catalogue entry and posture model.</summary>
public class AmazingHandTests
{
    /// <summary>Eight motors, in the actuator order of Pollen's model and the servo ids on the bus.</summary>
    [Fact]
    public void TheJointOrderIsTwoMotorsPerFinger()
    {
        string[] names = [.. RobotCatalog.AmazingHand.Joints.Select(j => j.Name)];

        names.ShouldBe([
            "finger1_motor1", "finger1_motor2", "finger2_motor1", "finger2_motor2",
            "finger3_motor1", "finger3_motor2", "finger4_motor1", "finger4_motor2",
        ]);
    }

    /// <summary>
    /// Bend is the motors' common component, splay their differential one.
    /// </summary>
    /// <remarks>
    /// This is the whole reason <c>FingerPose</c> exists. A caller who sets `motor1` alone gets a
    /// finger that both curls and swings sideways, which is almost never what they meant.
    /// </remarks>
    [Fact]
    public void BendMovesBothMotorsTogetherAndSplayOpposesThem()
    {
        (Angle bend1, Angle bend2) = FingerPose.Degrees(bend: 40).ToMotors();
        bend1.Degrees.ShouldBe(40, 1e-6);
        bend2.Degrees.ShouldBe(40, 1e-6);

        (Angle splay1, Angle splay2) = FingerPose.Degrees(bend: 0, splay: 15).ToMotors();
        splay1.Degrees.ShouldBe(15, 1e-6);
        splay2.Degrees.ShouldBe(-15, 1e-6);
    }

    /// <summary>A left hand takes the opposite splay for the same posture.</summary>
    [Fact]
    public void SplayMirrorsBetweenHands()
    {
        FingerPose pose = FingerPose.Degrees(bend: 20, splay: 10);

        (Angle right1, _) = pose.ToMotors(HandSide.Right);
        (Angle left1, _) = pose.ToMotors(HandSide.Left);

        right1.Degrees.ShouldBe(30, 1e-6);
        left1.Degrees.ShouldBe(10, 1e-6);
    }

    /// <summary>The motor mapping round-trips.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(45, 0)]
    [InlineData(30, -20)]
    [InlineData(-15, 25)]
    public void APostureSurvivesARoundTripThroughTheMotors(double bend, double splay)
    {
        FingerPose original = FingerPose.Degrees(bend, splay);
        (Angle motor1, Angle motor2) = original.ToMotors();

        FingerPose recovered = FingerPose.FromMotors(motor1, motor2);

        recovered.Bend.Degrees.ShouldBe(bend, 1e-6);
        recovered.Splay.Degrees.ShouldBe(splay, 1e-6);
    }

    /// <summary>The simulated hand starts unpowered, like the contract requires.</summary>
    [Fact]
    public async Task AFreshHandHoldsNothingUntilItIsTurnedOn()
    {
        var model = new SimulatedAmazingHand();
        await using var engine = new SimulationEngine(model);
        await engine.StartAsync(TestContext.Current.CancellationToken);

        await using var hand = new AmazingHandClient(
            new SimulatedAmazingHandTransport(model, engine), ownsTransport: true);

        await hand.ConnectAsync(TestContext.Current.CancellationToken);
        await hand.SetPoseAsync(AmazingHandPose.Fist, TestContext.Current.CancellationToken);
        await Task.Delay(250, TestContext.Current.CancellationToken);

        // Unpowered: the command is accepted and the fingers do not follow it.
        AmazingHandPose idle = await hand.GetPoseAsync(TestContext.Current.CancellationToken);
        idle.Finger1.Bend.Degrees.ShouldBeLessThan(5);

        await hand.TurnOnAsync(TestContext.Current.CancellationToken);
        await hand.SetPoseAsync(AmazingHandPose.Fist, TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        AmazingHandPose closed = await hand.GetPoseAsync(TestContext.Current.CancellationToken);
        closed.Finger1.Bend.Degrees.ShouldBeGreaterThan(40);
    }
}
