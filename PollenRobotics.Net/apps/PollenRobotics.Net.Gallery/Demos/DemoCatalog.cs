using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Kinematics;
using PollenRobotics.Net.MicroDuck;
using PollenRobotics.Net.ReachyMini;
using PollenRobotics.Net.Simulation.Robots;
using PollenRobotics.Net.Simulation.Transports;

namespace PollenRobotics.Net.Gallery.Demos;

/// <summary>
/// Every demo the gallery offers.
/// </summary>
/// <remarks>
/// Each one drives the simulation through the same client class an application would use, so what
/// runs here is what would run on hardware. The transports are the in-process ones; nothing else
/// changes.
/// </remarks>
public static class DemoCatalog
{
    /// <summary>All demos, in display order.</summary>
    public static IReadOnlyList<GalleryDemo> All { get; } =
    [
        .. ReachyMiniDemos(),
        .. MicroDuckDemos(),
        .. Reachy2Demos(),
    ];

    /// <summary>The groups, in the order the sidebar lists them.</summary>
    public static IReadOnlyList<string> Groups { get; } =
        [.. All.Select(d => d.Group).Distinct(StringComparer.Ordinal)];

    /// <summary>Demos for one robot.</summary>
    public static IReadOnlyList<GalleryDemo> For(RobotKind robot) => [.. All.Where(d => d.Robot == robot)];

    private static IEnumerable<GalleryDemo> ReachyMiniDemos()
    {
        yield return new GalleryDemo
        {
            Id = "mini.pose",
            Title = "Head pose",
            Summary = "Move the head through a sequence of poses and watch the neck solve for it.",
            WatchFor = "Six neck arcs move together for one head pose. The Stewart platform has no "
                     + "one-to-one mapping between a pose axis and a motor.",
            Robot = RobotKind.ReachyMini,
            Group = "Reachy Mini",
            Duration = TimeSpan.FromSeconds(10),
            Source = """
                // A pose is translation plus orientation. The daemon does the kinematics; you
                // command where the head should be, not what the six neck motors should do.
                await mini.GotoTargetAsync(
                    head: HeadPose.Create(z: 12, pitch: -14, mm: true),
                    duration: TimeSpan.FromSeconds(1),
                    method: InterpolationMethod.MinJerk);

                await mini.GotoTargetAsync(
                    head: HeadPose.Create(roll: 22, yaw: 18),
                    duration: TimeSpan.FromSeconds(1));

                await mini.GotoTargetAsync(head: HeadPose.Neutral, duration: TimeSpan.FromSeconds(1));
                """,
            RunAsync = async (context, token) =>
            {
                await using ReachyMiniClient mini = await ConnectMiniAsync(context, token);

                (string Label, Pose Pose)[] sequence =
                [
                    ("lift and look up", HeadPose.Create(z: 12, pitch: -14, mm: true)),
                    ("tilt and glance", HeadPose.Create(roll: 22, yaw: 18)),
                    ("lean in", HeadPose.Create(x: 10, pitch: 10, mm: true)),
                    ("look away", HeadPose.Create(yaw: -30, roll: -12)),
                    ("neutral", HeadPose.Neutral),
                ];

                foreach ((string label, Pose pose) in sequence)
                {
                    context.Report(label);
                    await mini.GotoTargetAsync(head: pose, duration: TimeSpan.FromSeconds(1.4), cancellationToken: token);
                }
            },
        };

        yield return new GalleryDemo
        {
            Id = "mini.limits",
            Title = "Safety limits",
            Summary = "Command the head past its envelope and watch the SDK refuse.",
            WatchFor = "The arcs go red as the joint nears an end stop. The SDK throws rather than "
                     + "clamping, so an out-of-range command is a bug report, not a mystery.",
            Robot = RobotKind.ReachyMini,
            Group = "Reachy Mini",
            Duration = TimeSpan.FromSeconds(9),
            Source = """
                // Head pitch and roll stop at 40 degrees. By default the SDK throws rather than
                // quietly clamping - a servo that has been silently clamped for ten minutes looks
                // exactly like one that is tracking correctly.
                try
                {
                    await mini.SetTargetAsync(head: HeadPose.Create(pitch: 65));
                }
                catch (RobotSafetyException ex)
                {
                    Console.WriteLine(ex.Message);
                    // "Joint 'head.pitch' limited to [-40, 40] deg, commanded 65 deg."
                }

                // Ask for clamping explicitly when a REPL-style loop wants it.
                var permissive = new ReachyMiniClient(transport, RobotSafetyOptions.Permissive);
                """,
            RunAsync = async (context, token) =>
            {
                await using ReachyMiniClient mini = await ConnectMiniAsync(context, token);

                context.Report("approaching the pitch limit");

                for (int pitch = 0; pitch <= 38; pitch += 4)
                {
                    await mini.SetTargetAsync(head: HeadPose.Create(pitch: pitch), cancellationToken: token);
                    await Task.Delay(140, token);
                }

                context.Report("commanding 65 degrees, past the 40 degree limit");
                await Task.Delay(500, token);

                try
                {
                    await mini.SetTargetAsync(head: HeadPose.Create(pitch: 65), cancellationToken: token);
                    context.Log.Warn("gallery", "That should have thrown. The limit check is not working.");
                }
                catch (Core.Safety.RobotSafetyException ex)
                {
                    context.Log.Error("gallery", ex.Message);
                    context.Report("refused, as it should be");
                }

                await Task.Delay(900, token);
                await mini.GotoTargetAsync(head: HeadPose.Neutral, duration: TimeSpan.FromSeconds(1), cancellationToken: token);
            },
        };

        yield return new GalleryDemo
        {
            Id = "mini.yaw-coupling",
            Title = "Head and body yaw",
            Summary = "Turn past the 65-degree head-to-body limit and watch the body follow.",
            WatchFor = "Body yaw starts moving only once head yaw hits its 65-degree budget. With "
                     + "automatic body yaw off, the head would simply stop turning.",
            Robot = RobotKind.ReachyMini,
            Group = "Reachy Mini",
            Duration = TimeSpan.FromSeconds(11),
            Source = """
                // Head yaw must stay within 65 degrees of body yaw. With automatic body yaw on, the
                // daemon rotates the body to keep that satisfied instead of clamping the head.
                await mini.Transport.SetAutomaticBodyYawAsync(true);

                for (int yaw = 0; yaw <= 120; yaw += 5)
                {
                    await mini.SetTargetAsync(head: HeadPose.Create(yaw: yaw));
                    await Task.Delay(60);
                }

                // Turning head and body together instead - the tank-style turn. The baseline is the
                // last COMMANDED yaw, not telemetry, which lags by a round trip.
                await mini.TurnAsync(45.Degrees(), TimeSpan.FromSeconds(1));
                """,
            RunAsync = async (context, token) =>
            {
                await using ReachyMiniClient mini = await ConnectMiniAsync(context, token);
                await mini.Transport.SetAutomaticBodyYawAsync(true, token);

                context.Report("sweeping head yaw to 120 degrees");

                // Permissive here on purpose: the whole point is to run into the constraint and see
                // the body absorb it, which a throwing guard would prevent.
                for (int yaw = 0; yaw <= 120; yaw += 5)
                {
                    await mini.Transport.SetTargetAsync(
                        ReachyMiniTarget.ForHead(HeadPose.Create(yaw: yaw)), token);

                    await Task.Delay(70, token);
                }

                context.Report("body has taken up the excess");
                await Task.Delay(900, token);

                await mini.Transport.GotoTargetAsync(
                    new ReachyMiniTarget { Head = HeadPose.Neutral, BodyYaw = Angle.Zero },
                    TimeSpan.FromSeconds(1.5),
                    InterpolationMethod.MinJerk,
                    token);

                await Task.Delay(1500, token);
            },
        };

        yield return new GalleryDemo
        {
            Id = "mini.easing",
            Title = "Interpolation curves",
            Summary = "The same movement played with each of the four easing methods.",
            WatchFor = "Cartoon overshoots and settles; minimum jerk starts and stops without a "
                     + "jolt; linear starts abruptly. Watch the arc marker, not the robot.",
            Robot = RobotKind.ReachyMini,
            Group = "Reachy Mini",
            Duration = TimeSpan.FromSeconds(13),
            Source = """
                // Four curves, all of which satisfy f(0)=0 and f(1)=1, so a trajectory always
                // arrives exactly where it was asked to.
                foreach (InterpolationMethod method in Enum.GetValues<InterpolationMethod>())
                {
                    await mini.GotoTargetAsync(
                        head: HeadPose.Create(yaw: 35),
                        duration: TimeSpan.FromSeconds(1),
                        method: method);

                    await mini.GotoTargetAsync(
                        head: HeadPose.Neutral,
                        duration: TimeSpan.FromSeconds(1),
                        method: method);
                }
                """,
            RunAsync = async (context, token) =>
            {
                await using ReachyMiniClient mini = await ConnectMiniAsync(context, token);

                foreach (InterpolationMethod method in Enum.GetValues<InterpolationMethod>())
                {
                    context.Report($"{method} - {Describe(method)}");

                    await mini.GotoTargetAsync(
                        head: HeadPose.Create(yaw: 35),
                        antennas: (Angle.FromDegrees(45), Angle.FromDegrees(-45)),
                        duration: TimeSpan.FromSeconds(1),
                        method: method,
                        cancellationToken: token);

                    await mini.GotoTargetAsync(
                        head: HeadPose.Neutral,
                        antennas: (Angle.Zero, Angle.Zero),
                        duration: TimeSpan.FromSeconds(1),
                        method: method,
                        cancellationToken: token);
                }

                static string Describe(InterpolationMethod method) => method switch
                {
                    InterpolationMethod.Linear => "constant velocity, abrupt at both ends",
                    InterpolationMethod.MinJerk => "zero velocity and acceleration at both ends",
                    InterpolationMethod.EaseInOut => "smoothstep",
                    _ => "overshoots about ten percent, then settles",
                };
            },
        };

        yield return new GalleryDemo
        {
            Id = "mini.emotion",
            Title = "Expressive poses",
            Summary = "Six emotions built from head pose, antennas and easing.",
            WatchFor = "Personality comes from the easing as much as the pose. The same angles "
                     + "played with minimum jerk read as thoughtful; with cartoon, as delighted.",
            Robot = RobotKind.ReachyMini,
            Group = "Reachy Mini",
            Duration = TimeSpan.FromSeconds(14),
            Source = """
                // Emotions are data, not code, so they can be tuned without touching playback.
                var happy = new[]
                {
                    (HeadPose.Create(z: 14, pitch: -12, mm: true), (70.Degrees(), (-70).Degrees())),
                    (HeadPose.Create(z: 6, pitch: -4, mm: true), (40.Degrees(), (-40).Degrees())),
                };

                foreach ((Pose head, (Angle right, Angle left) antennas) in happy)
                {
                    await mini.GotoTargetAsync(
                        head: head,
                        antennas: antennas,
                        duration: TimeSpan.FromSeconds(0.45),
                        method: InterpolationMethod.Cartoon);
                }
                """,
            RunAsync = async (context, token) =>
            {
                await using ReachyMiniClient mini = await ConnectMiniAsync(context, token);

                (string Name, Pose Head, double Right, double Left, InterpolationMethod Easing)[] emotions =
                [
                    ("curious", HeadPose.Create(roll: 20, yaw: 22, z: 8, mm: true), 45, 10, InterpolationMethod.MinJerk),
                    ("happy", HeadPose.Create(z: 14, pitch: -12, mm: true), 70, -70, InterpolationMethod.Cartoon),
                    ("alert", HeadPose.Create(z: 16, pitch: -18, mm: true), 0, 0, InterpolationMethod.Cartoon),
                    ("sad", HeadPose.Create(z: -8, pitch: 25, mm: true), -70, 70, InterpolationMethod.MinJerk),
                    ("sleepy", HeadPose.Create(z: -6, pitch: 20, roll: 12, mm: true), -40, -50, InterpolationMethod.MinJerk),
                    ("no", HeadPose.Create(yaw: 26), 20, -20, InterpolationMethod.Linear),
                ];

                foreach ((string name, Pose head, double right, double left, InterpolationMethod easing) in emotions)
                {
                    context.Report(name);

                    await mini.GotoTargetAsync(
                        head: head,
                        antennas: (Angle.FromDegrees(right), Angle.FromDegrees(left)),
                        duration: TimeSpan.FromSeconds(0.65),
                        method: easing,
                        cancellationToken: token);

                    await Task.Delay(500, token);

                    await mini.GotoTargetAsync(
                        head: HeadPose.Neutral,
                        antennas: (Angle.Zero, Angle.Zero),
                        duration: TimeSpan.FromSeconds(0.6),
                        cancellationToken: token);
                }
            },
        };
    }

    private static IEnumerable<GalleryDemo> MicroDuckDemos()
    {
        yield return new GalleryDemo
        {
            Id = "duck.walk",
            Title = "Walking",
            Summary = "Velocity intents drive a gait; the duck holds the last one until told otherwise.",
            WatchFor = "The leg arcs run half a cycle apart, and step frequency rises with speed. "
                     + "Nothing here commands a joint angle - the policy owns the servos.",
            Robot = RobotKind.MicroDuck,
            Group = "MicroDuck",
            Duration = TimeSpan.FromSeconds(12),
            Source = """
                // Velocity is an intent, not a trajectory. The daemon holds it until replaced, so a
                // one-shot command walks forever - DriveForAsync resends for a bounded window and
                // stops afterwards, even if the caller is cancelled.
                await duck.InitAsync();

                await duck.DriveForAsync(DuckVelocity.Forward(0.15), TimeSpan.FromSeconds(3));
                await duck.DriveForAsync(DuckVelocity.Turn(90.Degrees()), TimeSpan.FromSeconds(1.5));
                await duck.DriveForAsync(new DuckVelocity(0.08, 0.06, 0), TimeSpan.FromSeconds(2));
                """,
            RunAsync = async (context, token) =>
            {
                await using MicroDuckClient duck = await ConnectDuckAsync(context, token);

                context.Report("powering the servos and homing");
                await duck.InitAsync(token);

                (string Label, DuckVelocity Velocity, double Seconds)[] legs =
                [
                    ("walking forward", DuckVelocity.Forward(0.15), 3),
                    ("turning left", DuckVelocity.Turn(Angle.FromDegrees(80)), 1.5),
                    ("strafing", new DuckVelocity(0.06, 0.08, 0), 2),
                    ("backing up", DuckVelocity.Forward(-0.08), 1.5),
                ];

                foreach ((string label, DuckVelocity velocity, double seconds) in legs)
                {
                    context.Report(label);
                    await duck.DriveForAsync(velocity, TimeSpan.FromSeconds(seconds), token);
                }

                context.Report("stopped");
            },
        };

        yield return new GalleryDemo
        {
            Id = "duck.actions",
            Title = "Action slots",
            Summary = "Each slot holds a learned policy that runs to completion.",
            WatchFor = "Actions block until they finish, so consecutive awaits sequence correctly. "
                     + "Sending a velocity mid-action would fight the policy.",
            Robot = RobotKind.MicroDuck,
            Group = "MicroDuck",
            Duration = TimeSpan.FromSeconds(15),
            Source = """
                // Seven slots, each an ONNX policy trained in MuJoCo and loaded onto the robot.
                // robotctl policy load walk my-policy.onnx replaces what "walk" means.
                await duck.InitAsync();

                await duck.SitAsync();       // sitstand
                await duck.StandUpAsync();   // stand
                await duck.PickAsync();      // ground_pick
                await duck.KickAsync(leftFoot: true);
                await duck.RecoverAsync();   // roulade - also the fall-recovery move
                """,
            RunAsync = async (context, token) =>
            {
                await using MicroDuckClient duck = await ConnectDuckAsync(context, token);
                await duck.InitAsync(token);

                (string Label, DuckActionSlot Slot)[] routine =
                [
                    ("sitting down", DuckActionSlot.SitStand),
                    ("standing up", DuckActionSlot.Stand),
                    ("picking up with the beak", DuckActionSlot.GroundPick),
                    ("left kick", DuckActionSlot.KickLeft),
                    ("right kick", DuckActionSlot.KickRight),
                    ("roulade", DuckActionSlot.Roulade),
                ];

                foreach ((string label, DuckActionSlot slot) in routine)
                {
                    context.Report(label);
                    await duck.PerformAsync(slot, token);
                    await Task.Delay(350, token);
                }

                context.Report("routine complete");
            },
        };

        yield return new GalleryDemo
        {
            Id = "duck.recovery",
            Title = "Fall recovery",
            Summary = "Tip the duck over and watch it notice and get back up.",
            WatchFor = "A fallen duck ignores velocity commands. Without a recovery watcher the "
                     + "application looks hung rather than tipped over.",
            Robot = RobotKind.MicroDuck,
            Group = "MicroDuck",
            Duration = TimeSpan.FromSeconds(12),
            Source = """
                // Start the watcher alongside the application's own logic and forget about it.
                _ = duck.RunFallRecoveryAsync(cancellationToken);

                await duck.InitAsync();
                await duck.DriveForAsync(DuckVelocity.Forward(0.15), TimeSpan.FromSeconds(10));

                // Meanwhile, if state.IsFallen goes true the watcher runs:
                //   await duck.RecoverAsync();   // roulade
                //   await duck.StandUpAsync();
                """,
            RunAsync = async (context, token) =>
            {
                await using MicroDuckClient duck = await ConnectDuckAsync(context, token);
                await duck.InitAsync(token);

                context.Report("walking");
                _ = duck.DriveForAsync(DuckVelocity.Forward(0.14), TimeSpan.FromSeconds(3), token);
                await Task.Delay(2600, token);

                context.Report("tipping the duck over");

                if (context.Engine.Robot is SimulatedMicroDuck model)
                {
                    model.Trip();
                }

                context.Log.Warn("gallery", "The duck is down. Velocity commands will now do nothing.");
                await Task.Delay(1600, token);

                context.Report("commanding a walk while fallen - nothing happens");
                await duck.DriveAsync(DuckVelocity.Forward(0.15), token);
                await Task.Delay(1400, token);

                context.Report("running the recovery move");
                await duck.RecoverAsync(token);
                await duck.StandUpAsync(token);

                context.Report("back on its feet");
                await duck.StopAsync(token);
            },
        };
    }

    private static IEnumerable<GalleryDemo> Reachy2Demos()
    {
        yield return new GalleryDemo
        {
            Id = "reachy2.arm",
            Title = "Arm trajectory",
            Summary = "Queued joint movements on a seven-axis arm.",
            WatchFor = "Movements queue per part rather than replacing one another, so a sequence "
                     + "issued back to back plays in order.",
            Robot = RobotKind.Reachy2,
            Group = "Reachy 2",
            Duration = TimeSpan.FromSeconds(11),
            Source = """
                // A goto returns a handle as soon as it is queued; the motion happens later.
                // Awaiting the handle is what waits for the arm to arrive.
                Reachy2GotoHandle move = await arm.GotoJointsAsync(
                    [-1.2, -0.4, 0.0, -1.1, 0.0, 0.2, 0.0],
                    TimeSpan.FromSeconds(1.5));

                GotoOutcome outcome = await move.WaitAsync();

                // Queue two arms, then wait for both, or they take turns instead of moving together.
                await Task.WhenAll(rightMove.WaitAsync(), leftMove.WaitAsync());
                """,
            RunAsync = async (context, token) =>
            {
                if (context.Engine.Robot is not SimulatedReachy2 robot)
                {
                    return;
                }

                robot.TurnOn("r_arm");
                robot.TurnOn("l_arm");

                string[] rightJoints =
                [
                    "r_arm.shoulder.pitch", "r_arm.shoulder.roll", "r_arm.elbow.yaw",
                    "r_arm.elbow.pitch", "r_arm.wrist.roll", "r_arm.wrist.pitch", "r_arm.wrist.yaw",
                ];

                (string Label, double[] Joints)[] waypoints =
                [
                    ("raising the arm", [-1.2, -0.4, 0.0, -1.1, 0.0, 0.2, 0.0]),
                    ("reaching out", [-1.5, -0.7, 0.3, -1.4, 0.0, 0.3, 0.2]),
                    ("bringing it in", [-0.6, -0.2, 0.0, -0.8, 0.0, -0.2, 0.0]),
                    ("back to rest", [0, 0, 0, 0, 0, 0, 0]),
                ];

                foreach ((string label, double[] joints) in waypoints)
                {
                    context.Report(label);
                    robot.QueueJointMove("r_arm", rightJoints, joints, TimeSpan.FromSeconds(1.4));

                    await Task.Delay(1500, token);
                }
            },
        };

        yield return new GalleryDemo
        {
            Id = "reachy2.ik",
            Title = "Inverse kinematics",
            Summary = "Solve a Cartesian pose and show what it costs in joint space.",
            WatchFor = "Seven joints for a six-dimensional task means an infinite family of "
                     + "solutions. Seeding from the present pose is what keeps them continuous.",
            Robot = RobotKind.Reachy2,
            Group = "Reachy 2",
            Duration = TimeSpan.FromSeconds(12),
            Source = """
                // Seed with the arm's PRESENT joints. Seeding from zero gives a geometrically valid
                // solution reached by an unacceptable route - the elbow flips through the torso.
                double[] seed = await arm.GetJointPositionsAsync();

                double[]? solution = await arm.ComputeInverseKinematicsAsync(target, seed);

                if (solution is null)
                {
                    // Out of reach. The robot solves against its true model, so this is a real
                    // answer rather than an estimate.
                    return;
                }

                await (await arm.GotoJointsAsync(solution, TimeSpan.FromSeconds(1.5))).WaitAsync();
                """,
            RunAsync = async (context, token) =>
            {
                if (context.Engine.Robot is not SimulatedReachy2 robot)
                {
                    return;
                }

                robot.TurnOn("r_arm");

                (string Label, double X, double Y, double Z)[] targets =
                [
                    ("in front, chest height", 0.42, -0.18, 0.05),
                    ("out to the side", 0.25, -0.42, 0.00),
                    ("down toward the table", 0.38, -0.20, -0.22),
                    ("up and forward", 0.40, -0.15, 0.28),
                ];

                foreach ((string label, double x, double y, double z) in targets)
                {
                    var pose = new Pose(
                        new System.Numerics.Vector3((float)x, (float)y, (float)z),
                        Rotation.FromRpy(Angle.Zero, Angle.FromDegrees(90), Angle.Zero));

                    int id = robot.QueueArmPose(ArmSide.Right, pose, TimeSpan.FromSeconds(1.5));

                    if (id == 0)
                    {
                        context.Report($"{label} - unreachable");
                        context.Log.Warn("gallery", $"No solution for ({x:0.##}, {y:0.##}, {z:0.##}).");
                        await Task.Delay(900, token);
                        continue;
                    }

                    context.Report(label);
                    await Task.Delay(1700, token);

                    Pose reached = robot.GetArmPose(ArmSide.Right);
                    double error = (reached.Position - pose.Position).Length();
                    context.Log.Info("gallery", $"{label}: reached within {error * 1000:0.#} mm.");
                }

                robot.QueueJointMove(
                    "r_arm",
                    [
                        "r_arm.shoulder.pitch", "r_arm.shoulder.roll", "r_arm.elbow.yaw",
                        "r_arm.elbow.pitch", "r_arm.wrist.roll", "r_arm.wrist.pitch", "r_arm.wrist.yaw",
                    ],
                    new double[7],
                    TimeSpan.FromSeconds(1.5));

                await Task.Delay(1600, token);
            },
        };

        yield return new GalleryDemo
        {
            Id = "reachy2.bimanual",
            Title = "Both arms together",
            Summary = "A mirrored trajectory queued on both arms so they stay in step.",
            WatchFor = "Shoulder roll is the axis that mirrors. The same value on both arms sends "
                     + "one of them straight into a limit - the ranges are mirror images.",
            Robot = RobotKind.Reachy2,
            Group = "Reachy 2",
            Duration = TimeSpan.FromSeconds(10),
            Source = """
                // Queue both, then wait for both. Awaiting the right arm before queueing the left
                // makes the arms take turns rather than move together.
                Reachy2GotoHandle right = await rightArm.GotoJointsAsync(rightGoal, duration);
                Reachy2GotoHandle left = await leftArm.GotoJointsAsync(leftGoal, duration);

                await Task.WhenAll(right.WaitAsync(), left.WaitAsync());
                """,
            RunAsync = async (context, token) =>
            {
                if (context.Engine.Robot is not SimulatedReachy2 robot)
                {
                    return;
                }

                robot.TurnOn("r_arm");
                robot.TurnOn("l_arm");

                string[] rightJoints =
                [
                    "r_arm.shoulder.pitch", "r_arm.shoulder.roll", "r_arm.elbow.yaw",
                    "r_arm.elbow.pitch", "r_arm.wrist.roll", "r_arm.wrist.pitch", "r_arm.wrist.yaw",
                ];

                string[] leftJoints =
                [
                    "l_arm.shoulder.pitch", "l_arm.shoulder.roll", "l_arm.elbow.yaw",
                    "l_arm.elbow.pitch", "l_arm.wrist.roll", "l_arm.wrist.pitch", "l_arm.wrist.yaw",
                ];

                (string Label, double[] Right, double[] Left)[] waypoints =
                [
                    ("arms out", [-0.9, -0.3, 0, -1.0, 0, 0, 0], [-0.9, 0.3, 0, -1.0, 0, 0, 0]),
                    ("reaching up", [-1.4, -0.6, 0.3, -1.4, 0, 0.2, 0], [-1.4, 0.6, -0.3, -1.4, 0, 0.2, 0]),
                    ("bringing in", [-0.5, -0.15, 0, -0.7, 0, -0.2, 0], [-0.5, 0.15, 0, -0.7, 0, -0.2, 0]),
                    ("rest", new double[7], new double[7]),
                ];

                foreach ((string label, double[] right, double[] left) in waypoints)
                {
                    context.Report(label);

                    robot.QueueJointMove("r_arm", rightJoints, right, TimeSpan.FromSeconds(1.3));
                    robot.QueueJointMove("l_arm", leftJoints, left, TimeSpan.FromSeconds(1.3));

                    await Task.Delay(1400, token);
                }
            },
        };
    }

    private static async Task<ReachyMiniClient> ConnectMiniAsync(DemoContext context, CancellationToken token)
    {
        var model = (SimulatedReachyMini)context.Engine.Robot;
        var transport = new SimulatedReachyMiniTransport(model, context.Engine);
        var client = new ReachyMiniClient(transport, ownsTransport: true);

        await client.ConnectAsync(token);
        await client.EnableMotorsAsync(token);
        return client;
    }

    private static async Task<MicroDuckClient> ConnectDuckAsync(DemoContext context, CancellationToken token)
    {
        var model = (SimulatedMicroDuck)context.Engine.Robot;
        var transport = new SimulatedMicroDuckTransport(model, context.Engine);
        var client = new MicroDuckClient(transport, ownsTransport: true);

        await client.ConnectAsync(token);
        return client;
    }
}
