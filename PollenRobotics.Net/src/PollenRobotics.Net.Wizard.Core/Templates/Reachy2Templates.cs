using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Wizard.Core.Projects;

namespace PollenRobotics.Net.Wizard.Core.Templates;

/// <summary>Templates that drive Reachy 2.</summary>
/// <remarks>
/// Reachy 2 talks gRPC to its own SDK server, and this SDK has no in-process simulator for it, so
/// these templates connect to a host rather than offering a <c>--sim</c> switch. Point them at
/// <c>localhost</c> when running against Pollen's own simulation stack.
/// </remarks>
internal static class Reachy2Templates
{
    private const string Preamble = """
        using PollenRobotics.Net.Core.Geometry;
        using PollenRobotics.Net.Reachy2;
        using Reachy;

        string host = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "localhost";

        await using var reachy = Reachy2Client.Connect(Reachy2Options.ForHost(host));

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        """;

    public static IEnumerable<RobotTemplate> All()
    {
        yield return new RobotTemplate
        {
            Id = "reachy2.hello",
            Name = "Hello Reachy 2",
            Description = "Connects, reports every part the robot has, and waves the right arm.",
            Robot = RobotKind.Reachy2,
            Category = TemplateCategory.GettingStarted,
            Tags = ["hello", "first", "parts", "wave"],
            Difficulty = 1,
            Generate = name => Build(name, "reachy2.hello", """
                try
                {
                    await reachy.ConnectAsync(stopping.Token);

                    Console.WriteLine($"{reachy.RobotName} at {reachy.Endpoint}");
                    Console.WriteLine($"  hardware {reachy.Info?.VersionHard}, software {reachy.Info?.VersionSoft}");
                    Console.WriteLine($"  r_arm       {(reachy.RightArm is not null ? "yes" : "no")}");
                    Console.WriteLine($"  l_arm       {(reachy.LeftArm is not null ? "yes" : "no")}");
                    Console.WriteLine($"  head        {(reachy.Head is not null ? "yes" : "no")}");
                    Console.WriteLine($"  grippers    {(reachy.RightGripper is not null ? "yes" : "no")}");
                    Console.WriteLine($"  mobile base {(reachy.MobileBase is not null ? "yes" : "no")}");

                    await reachy.TurnOnAsync(stopping.Token);

                    if (reachy.RightArm is not { } arm)
                    {
                        Console.Error.WriteLine("This robot has no right arm to wave with.");
                        return;
                    }

                    // Joint order is shoulder pitch, shoulder roll, elbow yaw, elbow pitch,
                    // wrist roll, wrist pitch, wrist yaw - all in radians.
                    double[] raised = [-1.4, -0.2, 0.0, -1.2, 0.0, 0.0, 0.0];
                    double[] out_ = [-1.4, -0.5, 0.0, -1.2, 0.0, 0.0, 0.0];
                    double[] in_ = [-1.4, 0.0, 0.0, -1.2, 0.0, 0.0, 0.0];

                    // Each goto returns a handle as soon as it is queued; awaiting the handle is
                    // what actually waits for the arm to arrive.
                    await (await arm.GotoJointsAsync(raised, TimeSpan.FromSeconds(2), cancellationToken: stopping.Token))
                        .WaitAsync(cancellationToken: stopping.Token);

                    for (int i = 0; i < 3; i++)
                    {
                        await (await arm.GotoJointsAsync(out_, TimeSpan.FromSeconds(0.5), cancellationToken: stopping.Token))
                            .WaitAsync(cancellationToken: stopping.Token);

                        await (await arm.GotoJointsAsync(in_, TimeSpan.FromSeconds(0.5), cancellationToken: stopping.Token))
                            .WaitAsync(cancellationToken: stopping.Token);
                    }

                    double[] rest = new double[7];
                    await (await arm.GotoJointsAsync(rest, TimeSpan.FromSeconds(2), cancellationToken: stopping.Token))
                        .WaitAsync(cancellationToken: stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "reachy2.pick-place",
            Name = "Pick and Place",
            Description = "Reaches to a Cartesian pose, closes the gripper, moves and releases - the manipulation loop.",
            Robot = RobotKind.Reachy2,
            Category = TemplateCategory.Manipulation,
            Tags = ["pick", "place", "gripper", "cartesian", "ik"],
            Difficulty = 3,
            Generate = name => Build(name, "reachy2.pick-place", """
                try
                {
                    await reachy.ConnectAsync(stopping.Token);
                    await reachy.TurnOnAsync(stopping.Token);

                    if (reachy.RightArm is not { } arm || reachy.RightGripper is not { } gripper)
                    {
                        Console.Error.WriteLine("This routine needs a right arm and a right gripper.");
                        return;
                    }

                    // Poses in the robot frame: x forward, y left, z up, metres.
                    Pose above = Pose.FromRpy(0.38, -0.20, -0.10, 0.Degrees(), 90.Degrees(), 0.Degrees());
                    Pose grasp = Pose.FromRpy(0.38, -0.20, -0.22, 0.Degrees(), 90.Degrees(), 0.Degrees());
                    Pose dropAbove = Pose.FromRpy(0.38, 0.10, -0.10, 0.Degrees(), 90.Degrees(), 0.Degrees());
                    Pose drop = Pose.FromRpy(0.38, 0.10, -0.20, 0.Degrees(), 90.Degrees(), 0.Degrees());

                    // Check reachability before moving. The robot solves against its true model, so
                    // this is a real answer rather than an estimate.
                    foreach ((string label, Pose pose) in new[] { ("above", above), ("grasp", grasp), ("drop", drop) })
                    {
                        if (!await arm.IsReachableAsync(pose, stopping.Token))
                        {
                            Console.Error.WriteLine($"The '{label}' pose is out of reach. Move the target closer to the robot.");
                            return;
                        }
                    }

                    await gripper.OpenAsync(stopping.Token);

                    await MoveAsync(arm, above, 2.0, stopping.Token);
                    await MoveAsync(arm, grasp, 1.5, stopping.Token);

                    await gripper.CloseAsync(stopping.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(600), stopping.Token);

                    if (!await gripper.IsHoldingAsync(stopping.Token))
                    {
                        // Carrying on with an empty gripper wastes the rest of the routine and
                        // looks, from across the room, exactly like a successful pick.
                        Console.Error.WriteLine("The gripper closed on nothing. Stopping.");
                        await MoveAsync(arm, above, 1.5, stopping.Token);
                        return;
                    }

                    Console.WriteLine("Got it.");

                    await MoveAsync(arm, above, 1.5, stopping.Token);
                    await MoveAsync(arm, dropAbove, 2.0, stopping.Token);
                    await MoveAsync(arm, drop, 1.5, stopping.Token);

                    await gripper.OpenAsync(stopping.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(400), stopping.Token);

                    await MoveAsync(arm, dropAbove, 1.5, stopping.Token);
                    Console.WriteLine("Placed.");
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }

                static async Task MoveAsync(Reachy2Arm arm, Pose pose, double seconds, CancellationToken token)
                {
                    // Continuous mode keeps successive solutions on the same branch, so the elbow
                    // does not flip between two nearby waypoints.
                    Reachy2GotoHandle handle = await arm.GotoPoseAsync(
                        pose,
                        TimeSpan.FromSeconds(seconds),
                        cancellationToken: token);

                    GotoOutcome outcome = await handle.WaitAsync(cancellationToken: token);

                    if (outcome != GotoOutcome.Succeeded)
                    {
                        throw new InvalidOperationException($"The move ended as {outcome}.");
                    }
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "reachy2.mobile-patrol",
            Name = "Mobile Base Patrol",
            Description = "Drives the omnidirectional base around a set of waypoints, watching the battery.",
            Robot = RobotKind.Reachy2,
            Category = TemplateCategory.Navigation,
            Tags = ["mobile base", "navigation", "odometry", "waypoints", "zuuu"],
            Difficulty = 2,
            Generate = name => Build(name, "reachy2.mobile-patrol", """
                try
                {
                    await reachy.ConnectAsync(stopping.Token);

                    if (reachy.MobileBase is not { } mobileBase)
                    {
                        Console.Error.WriteLine("This robot has no mobile base.");
                        return;
                    }

                    await mobileBase.TurnOnAsync(stopping.Token);

                    // The base ignores velocity commands unless it is in a mode that tracks them.
                    await mobileBase.SetDriveModeAsync(
                        Reachy.Part.Mobile.Base.Utility.ZuuuModePossiblities.CmdGoto,
                        stopping.Token);

                    await mobileBase.ResetOdometryAsync(stopping.Token);

                    // Waypoints in the odometry frame, which was just zeroed at the current pose.
                    (double X, double Y, double HeadingDegrees)[] waypoints =
                    [
                        (1.0, 0.0, 0),
                        (1.0, 1.0, 90),
                        (0.0, 1.0, 180),
                        (0.0, 0.0, 270),
                    ];

                    while (!stopping.IsCancellationRequested)
                    {
                        foreach ((double x, double y, double heading) in waypoints)
                        {
                            double battery = await mobileBase.GetBatteryLevelAsync(stopping.Token);

                            if (battery is > 0 and < 20)
                            {
                                Console.Error.WriteLine($"Battery at {battery:0}% - stopping the patrol.");
                                await mobileBase.BrakeAsync(stopping.Token);
                                return;
                            }

                            Console.WriteLine($"-> ({x:0.##}, {y:0.##}) facing {heading:0} deg, battery {battery:0}%");

                            await mobileBase.GotoAsync(x, y, heading.Degrees(), stopping.Token);

                            if (!await mobileBase.WaitForArrivalAsync(0.08, TimeSpan.FromSeconds(45), stopping.Token))
                            {
                                Console.Error.WriteLine("Did not arrive in time. Something is in the way.");
                                await mobileBase.BrakeAsync(stopping.Token);
                                return;
                            }

                            (double odomX, double odomY, Angle theta) = await mobileBase.GetOdometryAsync(stopping.Token);
                            Console.WriteLine($"   arrived at ({odomX:0.##}, {odomY:0.##}) facing {theta.Degrees:0} deg");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    if (reachy.MobileBase is { } baseToStop)
                    {
                        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                        try
                        {
                            await baseToStop.BrakeAsync(shutdown.Token);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Could not brake the base: {ex.Message}");
                        }
                    }
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "reachy2.head-gaze",
            Name = "Head Gaze",
            Description = "Sweeps the neck through a set of look-at targets and reports the resulting angles.",
            Robot = RobotKind.Reachy2,
            Category = TemplateCategory.Perception,
            Tags = ["head", "neck", "gaze", "lookat", "orbita"],
            Difficulty = 1,
            Generate = name => Build(name, "reachy2.head-gaze", """
                try
                {
                    await reachy.ConnectAsync(stopping.Token);

                    if (reachy.Head is not { } head)
                    {
                        Console.Error.WriteLine("This robot has no head.");
                        return;
                    }

                    await head.TurnOnAsync(stopping.Token);

                    (string Label, double X, double Y, double Z)[] targets =
                    [
                        ("ahead",       1.2, 0.0, 0.30),
                        ("up left",     1.0, 0.6, 0.80),
                        ("down right",  0.8, -0.5, -0.10),
                        ("the table",   0.5, 0.0, -0.20),
                        ("ahead",       1.2, 0.0, 0.30),
                    ];

                    foreach ((string label, double x, double y, double z) in targets)
                    {
                        Console.WriteLine($"Looking at {label}.");

                        Reachy2GotoHandle handle = await head.LookAtAsync(x, y, z, TimeSpan.FromSeconds(1.5), stopping.Token);
                        await handle.WaitAsync(cancellationToken: stopping.Token);

                        (Angle roll, Angle pitch, Angle yaw) = await head.GetOrientationAsync(stopping.Token);
                        Console.WriteLine($"  -> roll {roll.Degrees:0.#}, pitch {pitch.Degrees:0.#}, yaw {yaw.Degrees:0.#} deg");

                        await Task.Delay(TimeSpan.FromMilliseconds(600), stopping.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "reachy2.bimanual",
            Name = "Bimanual Coordination",
            Description = "Moves both arms together through a mirrored trajectory, queued so they stay in step.",
            Robot = RobotKind.Reachy2,
            Category = TemplateCategory.Manipulation,
            Tags = ["bimanual", "both arms", "coordination", "trajectory"],
            Difficulty = 3,
            Generate = name => Build(name, "reachy2.bimanual", """
                try
                {
                    await reachy.ConnectAsync(stopping.Token);
                    await reachy.TurnOnAsync(stopping.Token);

                    if (reachy.RightArm is not { } right || reachy.LeftArm is not { } left)
                    {
                        Console.Error.WriteLine("This routine needs both arms.");
                        return;
                    }

                    // Mirrored joint vectors. Shoulder roll is the axis that flips between sides -
                    // its limits are the mirror of each other, so the same value on both arms sends
                    // one of them straight into a limit.
                    (double[] Right, double[] Left)[] waypoints =
                    [
                        ([-0.9, -0.3, 0.0, -1.0, 0.0, 0.0, 0.0], [-0.9, 0.3, 0.0, -1.0, 0.0, 0.0, 0.0]),
                        ([-1.3, -0.6, 0.3, -1.4, 0.0, 0.2, 0.0], [-1.3, 0.6, -0.3, -1.4, 0.0, 0.2, 0.0]),
                        ([-0.6, -0.2, 0.0, -0.8, 0.0, -0.2, 0.0], [-0.6, 0.2, 0.0, -0.8, 0.0, -0.2, 0.0]),
                        ([0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0], [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]),
                    ];

                    foreach ((double[] rightGoal, double[] leftGoal) in waypoints)
                    {
                        // Queue both, then wait for both. Awaiting the right arm before queueing the
                        // left would make the arms take turns rather than move together.
                        Reachy2GotoHandle rightMove = await right.GotoJointsAsync(
                            rightGoal, TimeSpan.FromSeconds(1.5), cancellationToken: stopping.Token);

                        Reachy2GotoHandle leftMove = await left.GotoJointsAsync(
                            leftGoal, TimeSpan.FromSeconds(1.5), cancellationToken: stopping.Token);

                        await Task.WhenAll(
                            rightMove.WaitAsync(cancellationToken: stopping.Token),
                            leftMove.WaitAsync(cancellationToken: stopping.Token));

                        Console.WriteLine("Waypoint reached by both arms.");
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "reachy2.telemetry",
            Name = "Telemetry Dashboard",
            Description = "Streams robot state and prints a live console dashboard of joints and temperatures.",
            Robot = RobotKind.Reachy2,
            Category = TemplateCategory.Tooling,
            Tags = ["telemetry", "stream", "monitor", "diagnostics", "temperature"],
            Difficulty = 2,
            Generate = name => Build(name, "reachy2.telemetry", """
                try
                {
                    await reachy.ConnectAsync(stopping.Token);
                    Console.WriteLine($"Streaming from {reachy.RobotName}. Ctrl+C to stop.");

                    // Streaming beats polling for anything mirroring the robot in real time. Keep
                    // the loop body cheap: a slow consumer applies backpressure to the whole channel.
                    await foreach (ReachyState state in reachy.StreamStateAsync(stopping.Token))
                    {
                        Console.Clear();
                        Console.WriteLine($"{reachy.RobotName}  {DateTimeOffset.Now:HH:mm:ss}");
                        Console.WriteLine();

                        if (reachy.RightArm is { } right)
                        {
                            double[] joints = await right.GetJointPositionsAsync(stopping.Token);
                            Console.WriteLine($"r_arm  {string.Join("  ", joints.Select(j => $"{j * 180 / Math.PI,7:0.#}"))}");

                            IReadOnlyList<double> temperatures = await right.GetTemperaturesAsync(stopping.Token);
                            Console.WriteLine($"       temps {string.Join("  ", temperatures.Select(t => $"{t,5:0.#}C"))}");
                        }

                        if (reachy.Head is { } head)
                        {
                            (Angle roll, Angle pitch, Angle yaw) = await head.GetOrientationAsync(stopping.Token);
                            Console.WriteLine($"head   roll {roll.Degrees,6:0.#}  pitch {pitch.Degrees,6:0.#}  yaw {yaw.Degrees,6:0.#}");
                        }

                        if (reachy.MobileBase is { } mobileBase)
                        {
                            double battery = await mobileBase.GetBatteryLevelAsync(stopping.Token);
                            (double x, double y, Angle theta) = await mobileBase.GetOdometryAsync(stopping.Token);
                            Console.WriteLine($"base   battery {battery,3:0}%  at ({x:0.##}, {y:0.##}) facing {theta.Degrees:0}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                """),
        };
    }

    private static IReadOnlyList<ProjectFile> Build(string projectName, string templateId, string body) =>
    [
        ProjectScaffold.CsProj(projectName, RobotKind.Reachy2, ProjectKind.Console),
        new ProjectFile("Program.cs", Preamble + body),
        ProjectScaffold.Readme(projectName, TemplateCatalog.ById(templateId)),
    ];
}
