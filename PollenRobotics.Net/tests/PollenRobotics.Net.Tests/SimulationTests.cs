using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.MicroDuck;
using PollenRobotics.Net.ReachyMini;
using PollenRobotics.Net.Simulation;
using PollenRobotics.Net.Simulation.Robots;
using PollenRobotics.Net.Simulation.Transports;
using Shouldly;
using Xunit;

namespace PollenRobotics.Net.Tests;

/// <summary>Checks on the simulated Reachy Mini.</summary>
public class SimulatedReachyMiniTests
{
    /// <summary>
    /// The idle breath must not accumulate into the commanded pose.
    /// </summary>
    /// <remarks>
    /// This is a regression test for a bug found by watching the simulator: the wobble was written
    /// back into the head pose every tick rather than applied as an offset, so the head climbed a
    /// few millimetres a second until the pose left the neck workspace. The inverse solver then
    /// threw on every tick, the catch reported all six branches as zero, and the status panel went
    /// on saying "moving" while nothing moved.
    /// </remarks>
    [Fact]
    public void TheIdleBreathDoesNotDriftTheHeadPose()
    {
        var robot = new SimulatedReachyMini();
        robot.SetMotorMode(MotorMode.Enabled);
        robot.SetWobbling(true);

        // Twenty seconds at 50 Hz - long enough for a drift of even a millimetre a second to leave
        // the workspace.
        for (int tick = 0; tick < 1000; tick++)
        {
            robot.Tick(TimeSpan.FromMilliseconds(20));
        }

        ReachyMiniState state = robot.State();

        // The breath is a few millimetres. Anything past a centimetre means it is integrating.
        Math.Abs(state.HeadPose.Position.Z).ShouldBeLessThan(0.01f);
    }

    /// <summary>The breath must actually reach the branch angles, not just the pose.</summary>
    [Fact]
    public void TheIdleBreathMovesTheNeckBranches()
    {
        var robot = new SimulatedReachyMini();
        robot.SetMotorMode(MotorMode.Enabled);
        robot.SetWobbling(true);

        double largest = 0;

        for (int tick = 0; tick < 200; tick++)
        {
            robot.Tick(TimeSpan.FromMilliseconds(20));

            SimulationSnapshot snapshot = robot.Snapshot();

            for (int branch = 1; branch <= 6; branch++)
            {
                largest = Math.Max(largest, Math.Abs(snapshot.JointPositions[branch]));
            }
        }

        // Any non-trivial motion at all. Zero here means the solver is failing and being swallowed.
        (largest * 180 / Math.PI).ShouldBeGreaterThan(1);
    }

    /// <summary>
    /// A large head yaw must pull the body round rather than being silently clamped.
    /// </summary>
    /// <remarks>
    /// The robot allows at most 65 degrees between head yaw and body yaw. With automatic body yaw
    /// on, the body absorbs the excess; with it off, the head stops short - and an application
    /// written against a simulator that ignored the constraint would meet it for the first time on
    /// hardware.
    /// </remarks>
    [Fact]
    public void AutomaticBodyYawAbsorbsTheExcess()
    {
        var robot = new SimulatedReachyMini();
        robot.SetMotorMode(MotorMode.Enabled);
        robot.AutomaticBodyYaw = true;

        robot.SetTarget(ReachyMiniTarget.ForHead(HeadPose.Create(yaw: 120)));

        ReachyMiniState state = robot.State();
        (Angle _, Angle _, Angle headYaw) = state.HeadPose.Rpy;

        double delta = Math.Abs((headYaw - state.BodyYaw).Normalized().Degrees);

        delta.ShouldBeLessThanOrEqualTo(65.5);
        Math.Abs(state.BodyYaw.Degrees).ShouldBeGreaterThan(40);
    }

    [Fact]
    public void WithAutomaticBodyYawOffTheHeadIsClampedInstead()
    {
        var robot = new SimulatedReachyMini();
        robot.SetMotorMode(MotorMode.Enabled);
        robot.AutomaticBodyYaw = false;

        robot.SetTarget(ReachyMiniTarget.ForHead(HeadPose.Create(yaw: 120)));

        ReachyMiniState state = robot.State();
        (Angle _, Angle _, Angle headYaw) = state.HeadPose.Rpy;

        Math.Abs(state.BodyYaw.Degrees).ShouldBeLessThan(0.001);
        Math.Abs(headYaw.Degrees).ShouldBe(65, 0.5);
    }

    /// <summary>A robot with no torque must not hold its pose.</summary>
    [Fact]
    public void CuttingTorqueLetsTheHeadSag()
    {
        var robot = new SimulatedReachyMini();
        robot.SetMotorMode(MotorMode.Enabled);
        robot.SetTarget(ReachyMiniTarget.ForHead(HeadPose.Create(pitch: 25)));

        robot.SetMotorMode(MotorMode.Disabled);

        for (int tick = 0; tick < 200; tick++)
        {
            robot.Tick(TimeSpan.FromMilliseconds(20));
        }

        (Angle _, Angle pitch, Angle _) = robot.State().HeadPose.Rpy;
        Math.Abs(pitch.Degrees).ShouldBeLessThan(1);
    }
}

/// <summary>Checks on the simulated MicroDuck.</summary>
public class SimulatedMicroDuckTests
{
    /// <summary>A relaxed duck must ignore commands, as the daemon does.</summary>
    [Fact]
    public void AnUninitialisedDuckIgnoresVelocity()
    {
        var duck = new SimulatedMicroDuck();
        duck.SetVelocity(DuckVelocity.Forward(0.15));

        for (int tick = 0; tick < 100; tick++)
        {
            duck.Tick(TimeSpan.FromMilliseconds(20));
        }

        // Only x and y are travel. Z is the standing height, which is non-zero by design.
        System.Numerics.Vector3 position = duck.Snapshot().BodyPose.Position;
        Math.Abs(position.X).ShouldBeLessThan(0.001f);
        Math.Abs(position.Y).ShouldBeLessThan(0.001f);
    }

    [Fact]
    public void WalkingMovesTheDuckForward()
    {
        var duck = new SimulatedMicroDuck();
        duck.Init();
        duck.SetVelocity(DuckVelocity.Forward(0.12));

        for (int tick = 0; tick < 250; tick++)
        {
            duck.Tick(TimeSpan.FromMilliseconds(20));
        }

        // Five seconds at 0.12 m/s is about 0.6 m. Allow generous slack for the gait model.
        duck.Snapshot().BodyPose.Position.X.ShouldBeGreaterThan(0.3f);
    }

    /// <summary>A fallen duck must ignore velocity, which is what makes recovery necessary.</summary>
    [Fact]
    public void AFallenDuckDoesNotWalk()
    {
        var duck = new SimulatedMicroDuck();
        duck.Init();
        duck.Trip();
        duck.SetVelocity(DuckVelocity.Forward(0.15));

        System.Numerics.Vector3 before = duck.Snapshot().BodyPose.Position;

        for (int tick = 0; tick < 100; tick++)
        {
            duck.Tick(TimeSpan.FromMilliseconds(20));
        }

        (duck.Snapshot().BodyPose.Position - before).Length().ShouldBeLessThan(0.001f);
        duck.State().IsFallen.ShouldBeTrue();
    }

    [Fact]
    public void EveryJointStaysInsideItsLimits()
    {
        var duck = new SimulatedMicroDuck();
        duck.Init();
        duck.SetVelocity(new DuckVelocity(0.25, 0.10, 1.5));

        RobotDescription description = RobotCatalog.MicroDuck;

        for (int tick = 0; tick < 500; tick++)
        {
            duck.Tick(TimeSpan.FromMilliseconds(20));

            SimulationSnapshot snapshot = duck.Snapshot();

            for (int i = 0; i < description.JointCount; i++)
            {
                JointDescriptor joint = description.Joints[i];
                Angle value = Angle.FromRadians(snapshot.JointPositions[i]);

                joint.Contains(value).ShouldBeTrue(
                    $"{joint.Name} reached {value.Degrees:0.#} deg, outside [{joint.Lower.Degrees:0.#}, {joint.Upper.Degrees:0.#}].");
            }
        }
    }
}

/// <summary>Checks that SDK code runs unchanged against the simulation.</summary>
public class SimulatedTransportTests
{
    [Fact]
    public async Task TheReachyMiniClientDrivesTheSimulation()
    {
        var model = new SimulatedReachyMini();
        var transport = new SimulatedReachyMiniTransport(model);
        await using var mini = new ReachyMiniClient(transport, ownsTransport: true);

        await mini.ConnectAsync(TestContext.Current.CancellationToken);
        await mini.EnableMotorsAsync(TestContext.Current.CancellationToken);
        await mini.SetTargetAsync(head: HeadPose.Create(pitch: 20), cancellationToken: TestContext.Current.CancellationToken);

        (Angle _, Angle pitch, Angle _) = model.State().HeadPose.Rpy;
        pitch.Degrees.ShouldBe(20, 0.5);
    }

    /// <summary>
    /// A limit violation must throw through the whole stack, not just in the guard.
    /// </summary>
    [Fact]
    public async Task ThePitchLimitThrowsThroughTheClient()
    {
        var transport = new SimulatedReachyMiniTransport();
        await using var mini = new ReachyMiniClient(transport, ownsTransport: true);

        await mini.ConnectAsync(TestContext.Current.CancellationToken);
        await mini.EnableMotorsAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<Core.Safety.RobotSafetyException>(
            async () => await mini.SetTargetAsync(head: HeadPose.Create(pitch: 65)));
    }

    [Fact]
    public async Task TheDuckClientNeedsInitBeforeItWalks()
    {
        var model = new SimulatedMicroDuck();
        var transport = new SimulatedMicroDuckTransport(model);
        await using var duck = new MicroDuckClient(transport, ownsTransport: true);

        await duck.ConnectAsync(TestContext.Current.CancellationToken);

        // Commanding before init is accepted and does nothing, exactly as on the robot.
        await duck.DriveAsync(DuckVelocity.Forward(0.15), TestContext.Current.CancellationToken);
        model.IsInitialised.ShouldBeFalse();

        await duck.InitAsync(TestContext.Current.CancellationToken);
        model.IsInitialised.ShouldBeTrue();
    }

    /// <summary>
    /// DriveForAsync must stop the duck even when the caller is cancelled.
    /// </summary>
    /// <remarks>
    /// The daemon holds the last velocity indefinitely, so a cancelled walk that skips its stop
    /// leaves the duck walking into whatever is in front of it.
    /// </remarks>
    [Fact]
    public async Task ADriveThatIsCancelledStillStopsTheDuck()
    {
        var model = new SimulatedMicroDuck();
        var transport = new SimulatedMicroDuckTransport(model);
        await using var duck = new MicroDuckClient(transport, ownsTransport: true);

        await duck.ConnectAsync(TestContext.Current.CancellationToken);
        await duck.InitAsync(TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        try
        {
            await duck.DriveForAsync(DuckVelocity.Forward(0.15), TimeSpan.FromSeconds(30), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        model.State().JointPositions.ShouldNotBeEmpty();

        // Let the model settle and check it is no longer travelling.
        System.Numerics.Vector3 before = model.Snapshot().BodyPose.Position;

        for (int tick = 0; tick < 50; tick++)
        {
            model.Tick(TimeSpan.FromMilliseconds(20));
        }

        (model.Snapshot().BodyPose.Position - before).Length().ShouldBeLessThan(0.005f);
    }
}

/// <summary>Checks on the engine that ticks the models.</summary>
public class SimulationEngineTests
{
    [Fact]
    public async Task TheEngineTicksTheRobot()
    {
        await using var engine = new SimulationEngine(new SimulatedReachyMini(), frequencyHz: 100);

        await engine.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        await engine.StopAsync();

        engine.LatestSnapshot.SimulationTime.TotalMilliseconds.ShouldBeGreaterThan(200);
    }

    /// <summary>Swapping robots must resize the joint vector, not reuse the old shape.</summary>
    [Fact]
    public async Task LoadingAnotherRobotChangesTheJointCount()
    {
        await using var engine = new SimulationEngine(new SimulatedReachyMini());

        engine.Robot.Description.JointCount.ShouldBe(9);

        await engine.LoadRobotAsync(RobotKind.MicroDuck, TestContext.Current.CancellationToken);
        engine.Robot.Description.JointCount.ShouldBe(15);
        engine.LatestSnapshot.JointPositions.Count.ShouldBe(15);

        await engine.LoadRobotAsync(RobotKind.Reachy2, TestContext.Current.CancellationToken);
        engine.Robot.Description.JointCount.ShouldBe(21);
    }
}
