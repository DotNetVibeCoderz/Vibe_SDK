using Shouldly;
using Unitree.Net.Core;
using Unitree.Net.Messages.Go;
using Unitree.Net.Simulation;

namespace Unitree.Net.Tests;

/// <summary>
/// Tests for the kinematic stand-in that drives the simulator.
/// </summary>
public sealed class SimulatedRobotTests
{
    /// <summary>Advances a robot in fixed steps, the way the control loop does.</summary>
    private static void Run(SimulatedRobot robot, double seconds, double step = 0.002)
    {
        for (double t = 0; t < seconds; t += step)
        {
            robot.Advance(step);
        }
    }

    [Fact]
    public void StartsResting()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);

        robot.Gait.ShouldBe(SimulatedGait.Resting);
        robot.Capture().Contacts.ShouldAllBe(force => force == 0f);
    }

    [Fact]
    public void MotionIsRefusedWhileResting()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);
        robot.Command = new SimulatedVelocity(1.0f, 0f, 0f);

        Run(robot, 1.0);

        // A resting robot ignores velocity, which mirrors the real thing: the sport service will not
        // accept motion until the robot is standing.
        robot.Gait.ShouldBe(SimulatedGait.Resting);
        robot.Capture().Position.X.ShouldBe(0f, 0.001f);
    }

    [Fact]
    public void StandingThenCommandingVelocityWalks()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);
        robot.StandUp();
        robot.Command = new SimulatedVelocity(0.8f, 0f, 0f);

        Run(robot, 2.0);

        robot.Gait.ShouldBe(SimulatedGait.Walking);

        SimulationSnapshot snapshot = robot.Capture();
        snapshot.Position.X.ShouldBeGreaterThan(1.0f);
        snapshot.Position.X.ShouldBeLessThan(2.0f);
    }

    [Fact]
    public void StandingRisesToTheRigHeight()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);
        robot.StandUp();

        Run(robot, 3.0);

        // The body height comes from the same link lengths that draw the legs, so a standing robot
        // ends up with its feet on the floor rather than near it.
        robot.Capture().Height.ShouldBe(robot.Rig.StandingHeight, 0.005f);
    }

    [Fact]
    public void StandingLoadsEveryFoot()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);
        robot.StandUp();

        Run(robot, 2.0);

        // A stationary robot must read as supported rather than mid-stride, which is what a naive
        // gait phase produces when the robot is not actually moving.
        robot.Capture().Contacts.ShouldAllBe(force => force > 5f);
    }

    [Fact]
    public void TurningChangesHeading()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);
        robot.StandUp();
        robot.Command = new SimulatedVelocity(0f, 0f, 1.0f);

        Run(robot, 1.5);

        robot.Capture().Yaw.ShouldBeGreaterThan(1.0f);
    }

    [Fact]
    public void YawStaysWrappedOverALongTurn()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);
        robot.StandUp();
        robot.Command = new SimulatedVelocity(0f, 0f, 2.0f);

        Run(robot, 30.0);

        // Unwrapped yaw would keep climbing past pi and break every consumer that assumes a heading.
        float yaw = robot.Capture().Yaw;
        yaw.ShouldBeInRange(-MathF.PI, MathF.PI);
    }

    [Fact]
    public void WalkingDrainsTheBatteryFasterThanResting()
    {
        var resting = new SimulatedRobot(RobotModel.Go2);
        var walking = new SimulatedRobot(RobotModel.Go2);

        walking.StandUp();
        walking.Command = new SimulatedVelocity(1.0f, 0f, 0f);

        Run(resting, 60.0, 0.01);
        Run(walking, 60.0, 0.01);

        walking.BatterySoc.ShouldBeLessThan(resting.BatterySoc);
    }

    [Fact]
    public void MotorsHeatWhileWalkingAndSettleBelowTheLimit()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);
        robot.StandUp();
        robot.Command = new SimulatedVelocity(1.0f, 0f, 0f);

        Run(robot, 5.0);
        float early = robot.Capture().MaxMotorTemperature;

        Run(robot, 300.0, 0.01);
        SimulationSnapshot late = robot.Capture();

        late.MaxMotorTemperature.ShouldBeGreaterThan(early);

        // Cooling is proportional to the excess over ambient, so the temperature settles at an
        // equilibrium rather than climbing without bound.
        late.MaxMotorTemperature.ShouldBeLessThan(95f);
    }

    [Fact]
    public void BatteryOverrideIsClamped()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);

        robot.SetBatterySoc(-20f);
        robot.BatterySoc.ShouldBe(0f);

        robot.SetBatterySoc(500f);
        robot.BatterySoc.ShouldBe(100f);
    }

    [Theory]
    [InlineData(RobotModel.Go2)]
    [InlineData(RobotModel.B2W)]
    [InlineData(RobotModel.G1)]
    [InlineData(RobotModel.H1)]
    [InlineData(RobotModel.R1)]
    public void SnapshotShapeMatchesTheRig(RobotModel model)
    {
        var robot = new SimulatedRobot(model);
        robot.StandUp();
        robot.Command = new SimulatedVelocity(0.5f, 0f, 0.2f);

        Run(robot, 2.0);

        SimulationSnapshot snapshot = robot.Capture();

        snapshot.Model.ShouldBe(model);
        snapshot.JointAngles.Count.ShouldBe(robot.Rig.JointCount);
        snapshot.Contacts.Count.ShouldBe(robot.Rig.ContactLinks.Count);
        snapshot.JointAngles.ShouldAllBe(angle => float.IsFinite(angle));
    }

    [Fact]
    public void HumanoidLegsMoveWhenWalking()
    {
        // H1's pitch-only ankle shifts every joint index after the left leg. Hard-coded indices used
        // to leave its right leg motionless while the left walked.
        foreach (RobotModel model in (RobotModel[])[RobotModel.G1, RobotModel.H1, RobotModel.H12, RobotModel.R1])
        {
            var robot = new SimulatedRobot(model);
            robot.StandUp();
            robot.Command = new SimulatedVelocity(0.8f, 0f, 0f);

            Run(robot, 2.0);
            IReadOnlyList<float> angles = robot.Capture().JointAngles;

            RigLink leftKnee = robot.Rig.Links.First(link => link.Name == "left_calf");
            RigLink rightKnee = robot.Rig.Links.First(link => link.Name == "right_calf");

            angles[leftKnee.JointIndex].ShouldNotBe(0f, $"{model} left knee never moved");
            angles[rightKnee.JointIndex].ShouldNotBe(0f, $"{model} right knee never moved");
        }
    }

    [Fact]
    public void LowStateCarriesAValidCrc()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);
        robot.StandUp();
        Run(robot, 1.0);

        LowState state = robot.BuildLowState(1234);

        // The CRC is the whole reason the wire format matters. A simulator that published an invalid
        // one would let a consumer's validation bug go unnoticed until hardware arrived.
        state.IsCrcValid().ShouldBeTrue();
        state.Tick.ShouldBe(1234u);
    }

    [Fact]
    public void SportStateReportsTheCurrentGait()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);

        robot.BuildSportModeState().Mode.ShouldBe((byte)SportMode.Damping);

        robot.StandUp();
        Run(robot, 0.5);
        robot.BuildSportModeState().Mode.ShouldBe((byte)SportMode.BalanceStand);

        robot.Command = new SimulatedVelocity(0.6f, 0f, 0f);
        Run(robot, 0.5);

        SportModeState walking = robot.BuildSportModeState();
        walking.Mode.ShouldBe((byte)SportMode.Locomotion);
        walking.GaitType.ShouldBe((byte)GaitType.Trot);
    }

    [Fact]
    public void StandDownCancelsMotion()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);
        robot.StandUp();
        robot.Command = new SimulatedVelocity(1.0f, 0f, 0f);
        Run(robot, 1.0);

        robot.StandDown();
        Run(robot, 1.0);

        robot.Gait.ShouldBe(SimulatedGait.Resting);
        robot.Command.IsMoving.ShouldBeFalse();
    }

    [Fact]
    public void ZeroAndNegativeTimeStepsAreIgnored()
    {
        var robot = new SimulatedRobot(RobotModel.Go2);
        robot.StandUp();
        Run(robot, 1.0);

        SimulationSnapshot before = robot.Capture();

        robot.Advance(0);
        robot.Advance(-1);

        // The loop can deliver a zero delta on its first tick. Dividing by it would put NaN into every
        // joint velocity and from there into the whole thermal model.
        robot.Capture().ElapsedSeconds.ShouldBe(before.ElapsedSeconds);
    }
}
