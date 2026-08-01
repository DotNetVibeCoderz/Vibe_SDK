using Shouldly;
using Unitree.Net.Core;
using Unitree.Net.Manipulation;
using Unitree.Net.Messages.Go;
using Unitree.Net.Ros2;

namespace Unitree.Net.Tests;

public sealed class TrajectoryPlannerTests
{
    [Fact]
    public void QuinticProfileStartsAndEndsAtRest()
    {
        float[] start = [0f, 0f];
        float[] goal = [1f, -0.5f];

        IReadOnlyList<TrajectoryPoint> trajectory = TrajectoryPlanner.Plan(start, goal, 2.0, 100);

        // The whole point of a quintic over a cubic: zero acceleration at the endpoints, not just zero
        // velocity. A residual acceleration step is what a geared arm reports as a knock.
        trajectory[0].Velocities.ShouldAllBe(v => MathF.Abs(v) < 1e-4f);
        trajectory[0].Accelerations.ShouldAllBe(a => MathF.Abs(a) < 1e-4f);
        trajectory[^1].Velocities.ShouldAllBe(v => MathF.Abs(v) < 1e-4f);
        trajectory[^1].Accelerations.ShouldAllBe(a => MathF.Abs(a) < 1e-4f);
    }

    [Fact]
    public void TrajectoryLandsExactlyOnTheGoal()
    {
        float[] start = [0.2f, -1.1f, 0.7f];
        float[] goal = [1.4f, 0.3f, -0.9f];

        IReadOnlyList<TrajectoryPoint> trajectory = TrajectoryPlanner.Plan(start, goal, 1.5, 200);

        for (int i = 0; i < goal.Length; i++)
        {
            trajectory[^1].Positions[i].ShouldBe(goal[i], 1e-4f);
        }
    }

    [Fact]
    public void TrajectoryIsMonotonicForASingleJointMove()
    {
        float[] start = [0f];
        float[] goal = [1f];

        IReadOnlyList<TrajectoryPoint> trajectory = TrajectoryPlanner.Plan(start, goal, 1.0, 100);

        for (int i = 1; i < trajectory.Count; i++)
        {
            trajectory[i].Positions[0].ShouldBeGreaterThanOrEqualTo(trajectory[i - 1].Positions[0] - 1e-5f);
        }
    }

    [Fact]
    public void ComputedDurationRespectsTheVelocityLimit()
    {
        float[] start = [0f];
        float[] goal = [10f];
        var limits = new TrajectoryLimits(MaxVelocity: 1f, MaxAcceleration: 1000f);

        double duration = TrajectoryPlanner.ComputeDuration(start, goal, limits);
        IReadOnlyList<TrajectoryPoint> trajectory = TrajectoryPlanner.Plan(start, goal, duration, 500);

        float peakVelocity = trajectory.Max(p => MathF.Abs(p.Velocities[0]));

        peakVelocity.ShouldBeLessThanOrEqualTo(limits.MaxVelocity * 1.02f);
    }

    [Fact]
    public void ComputedDurationRespectsTheAccelerationLimit()
    {
        float[] start = [0f];
        float[] goal = [10f];
        var limits = new TrajectoryLimits(MaxVelocity: 1000f, MaxAcceleration: 2f);

        double duration = TrajectoryPlanner.ComputeDuration(start, goal, limits);
        IReadOnlyList<TrajectoryPoint> trajectory = TrajectoryPlanner.Plan(start, goal, duration, 500);

        float peakAcceleration = trajectory.Max(p => MathF.Abs(p.Accelerations[0]));

        peakAcceleration.ShouldBeLessThanOrEqualTo(limits.MaxAcceleration * 1.05f);
    }

    [Fact]
    public void AllJointsFinishTogether()
    {
        float[] start = [0f, 0f];
        float[] goal = [2f, 0.1f];

        IReadOnlyList<TrajectoryPoint> trajectory =
            TrajectoryPlanner.Plan(start, goal, TrajectoryLimits.Default, 200);

        // Synchronised timing: the small move must not finish early and leave the arm twisting.
        int firstSettled = trajectory.ToList().FindIndex(p => MathF.Abs(p.Positions[1] - goal[1]) < 1e-4f);
        int secondSettled = trajectory.ToList().FindIndex(p => MathF.Abs(p.Positions[0] - goal[0]) < 1e-3f);

        firstSettled.ShouldBeGreaterThan((int)(trajectory.Count * 0.8));
        secondSettled.ShouldBeGreaterThan((int)(trajectory.Count * 0.8));
    }

    [Fact]
    public void ZeroLengthMoveProducesASinglePoint()
    {
        float[] pose = [1f, 2f, 3f];

        IReadOnlyList<TrajectoryPoint> trajectory =
            TrajectoryPlanner.Plan(pose, pose, TrajectoryLimits.Default);

        trajectory.Count.ShouldBe(1);
        trajectory[0].Positions.ShouldBe(pose);
    }

    [Fact]
    public void SamplingClampsOutsideTheTrajectoryBounds()
    {
        float[] start = [0f];
        float[] goal = [1f];

        IReadOnlyList<TrajectoryPoint> trajectory = TrajectoryPlanner.Plan(start, goal, 1.0, 50);

        TrajectoryPlanner.Sample(trajectory, -5).Positions[0].ShouldBe(0f, 1e-5f);
        TrajectoryPlanner.Sample(trajectory, 99).Positions[0].ShouldBe(1f, 1e-5f);
    }

    [Fact]
    public void SamplingInterpolatesBetweenPlannedPoints()
    {
        float[] start = [0f];
        float[] goal = [1f];

        IReadOnlyList<TrajectoryPoint> trajectory = TrajectoryPlanner.Plan(start, goal, 1.0, 10);
        TrajectoryPoint midpoint = TrajectoryPlanner.Sample(trajectory, 0.5);

        // s(0.5) = 0.5 for the quintic profile, by symmetry.
        midpoint.Positions[0].ShouldBe(0.5f, 1e-3f);
    }

    [Fact]
    public void MismatchedDimensionsAreRejected()
    {
        Should.Throw<ArgumentException>(() =>
            TrajectoryPlanner.Plan(new float[] { 0f }, new float[] { 0f, 1f }, 1.0));
    }
}

public sealed class ArmControllerTests
{
    [Fact]
    public async Task MoveToDrivesEveryJointToItsGoal()
    {
        var sink = new RecordingJointSink(8);

        for (int i = 0; i < 8; i++)
        {
            sink.SeedPosition(i, 0f);
        }

        var arm = new ArmController(sink, [0, 1, 2], limits: new TrajectoryLimits(5f, 20f));

        await arm.MoveToAsync([0.5f, -0.3f, 0.8f]);

        sink.TryGetJointPosition(0, out float first).ShouldBeTrue();
        sink.TryGetJointPosition(1, out float second).ShouldBeTrue();
        sink.TryGetJointPosition(2, out float third).ShouldBeTrue();

        first.ShouldBe(0.5f, 1e-4f);
        second.ShouldBe(-0.3f, 1e-4f);
        third.ShouldBe(0.8f, 1e-4f);
    }

    [Fact]
    public async Task MoveToWithoutFeedbackFailsRatherThanGuessing()
    {
        // No seeded positions: the sink reports no measurement, so there is no valid start pose.
        var sink = new RecordingJointSink(4);
        var arm = new ArmController(sink, [0, 1]);

        await Should.ThrowAsync<UnitreeException>(() => arm.MoveToAsync([1f, 1f]));
    }

    [Fact]
    public void JointIndexOutsideTheSinkIsRejected()
    {
        var sink = new RecordingJointSink(4);

        Should.Throw<ArgumentOutOfRangeException>(() => new ArmController(sink, [0, 99]));
    }

    [Fact]
    public void GoalOfTheWrongLengthIsRejected()
    {
        var sink = new RecordingJointSink(4);
        var arm = new ArmController(sink, [0, 1]);

        Should.Throw<ArgumentException>(() => arm.MoveToAsync([1f, 2f, 3f]).GetAwaiter().GetResult());
    }

    [Fact]
    public void RelaxSendsZeroStiffnessToEveryJoint()
    {
        var sink = new RecordingJointSink(4);
        var arm = new ArmController(sink, [0, 1, 2]);

        arm.Relax(kd: 1.5f);

        sink.History.Count.ShouldBe(3);
        sink.History.ShouldAllBe(record => record.Kp == 0f && record.Kd == 1.5f);
    }

    [Fact]
    public async Task DualArmMotionsShareOneDuration()
    {
        var sink = new RecordingJointSink(8);

        for (int i = 0; i < 8; i++)
        {
            sink.SeedPosition(i, 0f);
        }

        var left = new ArmController(sink, [0, 1], limits: new TrajectoryLimits(5f, 20f));
        var right = new ArmController(sink, [4, 5], limits: new TrajectoryLimits(5f, 20f));
        var coordinator = new DualArmCoordinator(left, right);

        // The left arm's move is much larger, so it sets the shared pace.
        await coordinator.MoveBothAsync([1.5f, 1.5f], [0.05f, 0.05f]);

        sink.TryGetJointPosition(0, out float leftFinal).ShouldBeTrue();
        sink.TryGetJointPosition(4, out float rightFinal).ShouldBeTrue();

        leftFinal.ShouldBe(1.5f, 1e-3f);
        rightFinal.ShouldBe(0.05f, 1e-3f);
    }
}

public sealed class RealtimeLoopTests
{
    [Fact]
    public void LoopRunsAtApproximatelyTheRequestedRate()
    {
        int tickCount = 0;
        using var loop = new RealtimeLoop(200, (in ControlTickContext _) => Interlocked.Increment(ref tickCount));

        loop.Start();
        Thread.Sleep(500);
        loop.Stop();

        // 200 Hz for ~0.5 s is about 100 ticks. The band is wide because CI machines are noisy; the
        // assertion is that the loop runs at roughly the right order, not that it is jitter-free.
        tickCount.ShouldBeGreaterThan(50);
        tickCount.ShouldBeLessThan(200);
    }

    /// <summary>
    /// A throwing callback must not kill the loop: losing the control thread on a robot is far worse
    /// than one bad tick.
    /// </summary>
    [Fact]
    public void AThrowingCallbackDoesNotStopTheLoop()
    {
        int tickCount = 0;

        using var loop = new RealtimeLoop(200, (in ControlTickContext _) =>
        {
            int current = Interlocked.Increment(ref tickCount);

            if (current == 3)
            {
                throw new InvalidOperationException("simulated tick failure");
            }
        });

        loop.Start();
        Thread.Sleep(300);
        loop.Stop();

        tickCount.ShouldBeGreaterThan(10);
        loop.Statistics.TickCount.ShouldBeGreaterThan(10);
    }

    [Fact]
    public void TickContextReportsMonotonicIndicesAndElapsedTime()
    {
        var indices = new List<long>();
        double lastElapsed = -1;
        bool elapsedIsMonotonic = true;

        using var loop = new RealtimeLoop(100, (in ControlTickContext context) =>
        {
            lock (indices)
            {
                indices.Add(context.TickIndex);

                if (context.ElapsedSeconds < lastElapsed)
                {
                    elapsedIsMonotonic = false;
                }

                lastElapsed = context.ElapsedSeconds;
            }
        });

        loop.Start();
        Thread.Sleep(200);
        loop.Stop();

        lock (indices)
        {
            indices.Count.ShouldBeGreaterThan(3);
            indices[0].ShouldBe(0);
            indices.ShouldBe(indices.OrderBy(i => i).ToList());
            elapsedIsMonotonic.ShouldBeTrue();
        }
    }

    [Fact]
    public void InvalidFrequencyIsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RealtimeLoop(0, (in ControlTickContext _) => { }));
    }

    [Fact]
    public void StartIsIdempotent()
    {
        using var loop = new RealtimeLoop(100, (in ControlTickContext _) => { });

        loop.Start();
        Should.NotThrow(loop.Start);
        loop.IsRunning.ShouldBeTrue();
        loop.Stop();
    }
}

public sealed class Ros2MessageTests
{
    [Fact]
    public void TwistRoundTripsThroughCdr()
    {
        var original = new Ros2Twist
        {
            Linear = new System.Numerics.Vector3(0.5f, -0.2f, 0f),
            Angular = new System.Numerics.Vector3(0f, 0f, 0.8f),
        };

        var buffer = new byte[Ros2Twist.MaxSerializedSize];
        int written = original.Serialize(buffer);

        Ros2Twist decoded = Ros2Twist.Deserialize(buffer.AsSpan(0, written));

        decoded.Linear.X.ShouldBe(0.5f, 1e-6f);
        decoded.Linear.Y.ShouldBe(-0.2f, 1e-6f);
        decoded.Angular.Z.ShouldBe(0.8f, 1e-6f);
    }

    [Fact]
    public void TwistMapsOntoTheBodyFrameVelocityCommand()
    {
        var twist = new Ros2Twist
        {
            Linear = new System.Numerics.Vector3(0.4f, 0.1f, 0f),
            Angular = new System.Numerics.Vector3(0f, 0f, -0.3f),
        };

        VelocityCommand command = twist.ToVelocityCommand();

        command.Forward.ShouldBe(0.4f);
        command.Lateral.ShouldBe(0.1f);
        command.YawRate.ShouldBe(-0.3f);
    }

    [Fact]
    public void VelocityCommandAndTwistAreInverses()
    {
        var command = new VelocityCommand(0.7f, -0.2f, 1.1f);

        VelocityCommand round = Ros2Twist.FromVelocityCommand(command).ToVelocityCommand();

        round.ShouldBe(command);
    }

    [Fact]
    public void ImuRoundTripsThroughCdr()
    {
        var original = new Ros2Imu
        {
            Header = Ros2Header.Now("imu_link"),
            Orientation = System.Numerics.Quaternion.Identity,
            AngularVelocity = new System.Numerics.Vector3(0.1f, 0.2f, 0.3f),
            LinearAcceleration = new System.Numerics.Vector3(0f, 0f, 9.81f),
        };

        var buffer = new byte[Ros2Imu.MaxSerializedSize];
        int written = original.Serialize(buffer);

        Ros2Imu decoded = Ros2Imu.Deserialize(buffer.AsSpan(0, written));

        decoded.Header.FrameId.ShouldBe("imu_link");
        decoded.AngularVelocity.Y.ShouldBe(0.2f, 1e-6f);
        decoded.LinearAcceleration.Z.ShouldBe(9.81f, 1e-5f);
    }

    [Fact]
    public void OdometryRoundTripsThroughCdr()
    {
        var original = new Ros2Odometry
        {
            Header = Ros2Header.Now("odom"),
            ChildFrameId = "base_link",
            Pose = new Pose(new System.Numerics.Vector3(1.5f, -0.5f, 0.32f), System.Numerics.Quaternion.Identity),
            Twist = new Ros2Twist { Linear = new System.Numerics.Vector3(0.4f, 0f, 0f) },
        };

        var buffer = new byte[Ros2Odometry.MaxSerializedSize];
        int written = original.Serialize(buffer);

        Ros2Odometry decoded = Ros2Odometry.Deserialize(buffer.AsSpan(0, written));

        decoded.Header.FrameId.ShouldBe("odom");
        decoded.ChildFrameId.ShouldBe("base_link");
        decoded.Pose.Position.X.ShouldBe(1.5f, 1e-6f);
        decoded.Twist.Linear.X.ShouldBe(0.4f, 1e-6f);
    }
}

public sealed class TelemetryDerivationTests
{
    [Fact]
    public void PackVoltageSumsTheCellVoltages()
    {
        BmsState bms = default;

        for (int i = 0; i < 15; i++)
        {
            bms.CellVoltage[i] = 4000;
        }

        bms.GetPackVoltage().ShouldBe(60f, 1e-3f);
    }

    [Fact]
    public void CellImbalanceIgnoresUnpopulatedCells()
    {
        BmsState bms = default;

        for (int i = 0; i < 10; i++)
        {
            bms.CellVoltage[i] = (ushort)(4000 + i);
        }

        // Cells 10–14 stay at zero. Counting them would report a 4 V imbalance on a healthy pack.
        bms.GetCellImbalanceMillivolts().ShouldBe(9);
    }

    [Fact]
    public void CellImbalanceIsZeroWhenNothingIsReported()
    {
        BmsState bms = default;

        bms.GetCellImbalanceMillivolts().ShouldBe(0);
    }

    [Fact]
    public void FallDetectionTriggersOnExcessivePitch()
    {
        LowState state = default;
        state.ImuState.Rpy[1] = float.DegreesToRadians(60f);

        state.IsFallen(float.DegreesToRadians(50f)).ShouldBeTrue();
        state.IsFallen(float.DegreesToRadians(70f)).ShouldBeFalse();
    }

    [Fact]
    public void MaxMotorTemperatureCoversOnlyTheActuatedJoints()
    {
        LowState state = default;
        state.MotorState[3].Temperature = 55;

        // Slot 15 is unactuated on a Go2; a spurious reading there must not be reported.
        state.MotorState[15].Temperature = 99;

        state.GetMaxMotorTemperature(GoJoint.Count).ShouldBe(55);
    }

    [Fact]
    public void IdleMotorCommandProducesNoTorqueOrStiffness()
    {
        MotorCmd idle = MotorCmd.Idle;

        idle.Kp.ShouldBe(0f);
        idle.Kd.ShouldBe(0f);
        idle.Tau.ShouldBe(0f);
        idle.Mode.ShouldBe(MotorMode.Idle);
    }

    [Fact]
    public void DampingCommandResistsMotionWithoutHoldingAPosition()
    {
        MotorCmd damping = MotorCmd.Damping(3f);

        damping.Kp.ShouldBe(0f);
        damping.Kd.ShouldBe(3f);
        damping.Mode.ShouldBe(MotorMode.Servo);
    }

    [Fact]
    public void SetAllDampingCoversEverySlot()
    {
        LowCmd command = LowCmd.CreateIdle();
        command.SetAllDamping(2.5f);

        for (int i = 0; i < RobotModelInfo.GoMotorSlots; i++)
        {
            command.MotorCmd[i].Kd.ShouldBe(2.5f);
            command.MotorCmd[i].Kp.ShouldBe(0f);
        }
    }
}
