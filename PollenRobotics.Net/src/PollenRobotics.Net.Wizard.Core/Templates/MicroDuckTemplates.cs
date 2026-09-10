using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Wizard.Core.Projects;

namespace PollenRobotics.Net.Wizard.Core.Templates;

/// <summary>Templates that drive MicroDuck.</summary>
internal static class MicroDuckTemplates
{
    private const string Preamble = """
        using PollenRobotics.Net.Core.Geometry;
        using PollenRobotics.Net.Core.Realtime;
        using PollenRobotics.Net.MicroDuck;
        using PollenRobotics.Net.MicroDuck.Transports;
        using PollenRobotics.Net.Simulation;
        using PollenRobotics.Net.Simulation.Robots;
        using PollenRobotics.Net.Simulation.Transports;

        bool useSimulator = args.Contains("--sim");

        IMicroDuckTransport transport;
        SimulationEngine? engine = null;

        if (useSimulator)
        {
            var model = new SimulatedMicroDuck();
            engine = new SimulationEngine(model);
            await engine.StartAsync();
            transport = new SimulatedMicroDuckTransport(model, engine);
        }
        else
        {
            transport = new MicroDuckDaemonTransport(MicroDuckOptions.Default);
        }

        await using var duck = new MicroDuckClient(transport, ownsTransport: true);

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        """;

    private const string Epilogue = """

        static async Task ParkAsync(MicroDuckClient duck, SimulationEngine? engine)
        {
            // The daemon holds the last velocity indefinitely. Not stopping here means the duck
            // keeps walking after the program exits.
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await duck.StopAsync(shutdown.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not stop the duck: {ex.Message}");
            }

            if (engine is not null)
            {
                await engine.DisposeAsync();
            }
        }
        """;

    public static IEnumerable<RobotTemplate> All()
    {
        yield return new RobotTemplate
        {
            Id = "duck.hello",
            Name = "Hello MicroDuck",
            Description = "Initialises the duck, walks a short square and quacks at each corner.",
            Robot = RobotKind.MicroDuck,
            Category = TemplateCategory.GettingStarted,
            Tags = ["hello", "first", "walk", "basic"],
            Difficulty = 1,
            Generate = name => Build(name, "duck.hello", """
                try
                {
                    await duck.ConnectAsync(stopping.Token);

                    // Nothing moves until the servos are powered and homed.
                    await duck.InitAsync(stopping.Token);
                    Console.WriteLine($"Connected to {duck.Endpoint}.");

                    for (int side = 0; side < 4; side++)
                    {
                        Console.WriteLine($"Side {side + 1} of 4.");

                        // DriveForAsync keeps resending for the duration and stops afterwards. A
                        // single DriveAsync would walk forever.
                        await duck.DriveForAsync(DuckVelocity.Forward(0.12), TimeSpan.FromSeconds(2.5), stopping.Token);
                        await duck.QuackAsync(stopping.Token);

                        await duck.DriveForAsync(
                            DuckVelocity.Turn(90.Degrees()),
                            TimeSpan.FromSeconds(1.0),
                            stopping.Token);
                    }

                    Console.WriteLine("Back where we started, roughly.");
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    await ParkAsync(duck, engine);
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "duck.gamepad",
            Name = "Keyboard Driver",
            Description = "Drives the duck from the keyboard at the 50 Hz control rate, with a stop watchdog.",
            Robot = RobotKind.MicroDuck,
            Category = TemplateCategory.Interface,
            Tags = ["teleop", "drive", "keyboard", "manual", "realtime"],
            Difficulty = 2,
            Generate = name => Build(name, "duck.gamepad", """"
                try
                {
                    await duck.ConnectAsync(stopping.Token);
                    await duck.InitAsync(stopping.Token);

                    Console.WriteLine("""
                        W / S  - forward and back
                        A / D  - strafe
                        Q / E  - turn
                        Space  - stop
                        K      - kick
                        P      - pick up
                        R      - recover from a fall
                        Esc    - quit
                        """);

                    var velocity = DuckVelocity.Zero;
                    var loop = new RealtimeLoop(50);

                    await loop.RunAsync(async (_, token) =>
                    {
                        while (Console.KeyAvailable)
                        {
                            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                            switch (key.Key)
                            {
                                case ConsoleKey.W: velocity = velocity with { ForwardMetersPerSecond = 0.15 }; break;
                                case ConsoleKey.S: velocity = velocity with { ForwardMetersPerSecond = -0.10 }; break;
                                case ConsoleKey.A: velocity = velocity with { LateralMetersPerSecond = 0.07 }; break;
                                case ConsoleKey.D: velocity = velocity with { LateralMetersPerSecond = -0.07 }; break;
                                case ConsoleKey.Q: velocity = velocity with { YawRadiansPerSecond = 1.0 }; break;
                                case ConsoleKey.E: velocity = velocity with { YawRadiansPerSecond = -1.0 }; break;
                                case ConsoleKey.Spacebar: velocity = DuckVelocity.Zero; break;

                                case ConsoleKey.K:
                                    // Actions run to completion, so zero the velocity first or the
                                    // duck tries to walk out of its own kick.
                                    velocity = DuckVelocity.Zero;
                                    await duck.StopAsync(token);
                                    await duck.KickAsync(leftFoot: true, token);
                                    break;

                                case ConsoleKey.P:
                                    velocity = DuckVelocity.Zero;
                                    await duck.StopAsync(token);
                                    await duck.PickAsync(token);
                                    break;

                                case ConsoleKey.R:
                                    velocity = DuckVelocity.Zero;
                                    await duck.RecoverAsync(token);
                                    await duck.StandUpAsync(token);
                                    break;

                                case ConsoleKey.Escape:
                                    await stopping.CancelAsync();
                                    return;

                                default:
                                    break;
                            }
                        }

                        await duck.DriveAsync(velocity, token);
                    }, stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    await ParkAsync(duck, engine);
                }
                """"),
        };

        yield return new RobotTemplate
        {
            Id = "duck.obstacle-avoid",
            Name = "Obstacle Avoider",
            Description = "Walks forward and turns away when the time-of-flight sensor sees something close.",
            Robot = RobotKind.MicroDuck,
            Category = TemplateCategory.Navigation,
            Tags = ["tof", "obstacle", "sensor", "avoidance", "autonomous"],
            Difficulty = 2,
            Generate = name => Build(name, "duck.obstacle-avoid", """
                const double StopDistanceMeters = 0.25;

                try
                {
                    await duck.ConnectAsync(stopping.Token);
                    await duck.InitAsync(stopping.Token);

                    // A fallen duck ignores velocity commands, so watch for that in the background.
                    _ = duck.RunFallRecoveryAsync(stopping.Token);

                    Console.WriteLine("Wandering. Ctrl+C to stop.");

                    // 10 Hz is plenty: the sensor is the limit here, not the loop.
                    var loop = new RealtimeLoop(10);
                    var random = new Random();

                    await loop.RunAsync(async (_, token) =>
                    {
                        TofFrame? frame = await duck.ReadTimeOfFlightAsync(token);

                        if (frame is not { } depth)
                        {
                            // No depth sensor on this duck. Walk on rather than stopping dead.
                            await duck.DriveAsync(DuckVelocity.Forward(0.08), token);
                            return;
                        }

                        double nearest = depth.NearestMeters;

                        if (double.IsNaN(nearest) || nearest > StopDistanceMeters)
                        {
                            await duck.DriveAsync(DuckVelocity.Forward(0.12), token);
                            return;
                        }

                        Console.WriteLine($"Obstacle at {nearest * 100:0} cm - turning.");

                        // Turn a random way, so two ducks facing each other do not deadlock.
                        Angle turn = Angle.FromDegrees(random.Next(2) == 0 ? 70 : -70);
                        await duck.DriveForAsync(DuckVelocity.Turn(turn), TimeSpan.FromSeconds(1.2), token);
                    }, stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    await ParkAsync(duck, engine);
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "duck.trick-show",
            Name = "Trick Show",
            Description = "Runs every action slot in sequence: sit, stand, pick, kick, roll and quack.",
            Robot = RobotKind.MicroDuck,
            Category = TemplateCategory.Behaviour,
            Tags = ["actions", "skills", "demo", "show", "slots"],
            Difficulty = 1,
            Generate = name => Build(name, "duck.trick-show", """
                try
                {
                    await duck.ConnectAsync(stopping.Token);
                    await duck.InitAsync(stopping.Token);

                    IReadOnlyList<string> skills = await duck.ListSkillsAsync(stopping.Token);
                    Console.WriteLine($"Loaded skills: {string.Join(", ", skills)}");

                    (string Label, Func<CancellationToken, Task> Run)[] routine =
                    [
                        ("Standing up",  token => duck.StandUpAsync(token)),
                        ("Quacking",     token => duck.QuackAsync(token)),
                        ("Sitting down", token => duck.SitAsync(token)),
                        ("Standing up",  token => duck.StandUpAsync(token)),
                        ("Picking up",   token => duck.PickAsync(token)),
                        ("Left kick",    token => duck.KickAsync(leftFoot: true, token)),
                        ("Right kick",   token => duck.KickAsync(leftFoot: false, token)),
                        ("Rolling",      token => duck.RecoverAsync(token)),
                        ("Standing up",  token => duck.StandUpAsync(token)),
                    ];

                    foreach ((string label, Func<CancellationToken, Task> run) in routine)
                    {
                        Console.WriteLine(label);
                        await run(stopping.Token);
                        await Task.Delay(TimeSpan.FromSeconds(0.7), stopping.Token);
                    }

                    Console.WriteLine("Show over.");
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    await ParkAsync(duck, engine);
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "duck.patrol",
            Name = "Patrol Route",
            Description = "Walks a repeating route with configurable legs, reporting battery and loop health as it goes.",
            Robot = RobotKind.MicroDuck,
            Category = TemplateCategory.Navigation,
            Tags = ["patrol", "route", "autonomous", "telemetry", "battery"],
            Difficulty = 2,
            Generate = name => Build(name, "duck.patrol", """
                // A route is a list of legs: walk this fast for this long, then turn this far.
                (double Speed, double Seconds, double TurnDegrees)[] route =
                [
                    (0.12, 4.0, 90),
                    (0.12, 2.0, 90),
                    (0.12, 4.0, 90),
                    (0.12, 2.0, 90),
                ];

                try
                {
                    await duck.ConnectAsync(stopping.Token);
                    await duck.InitAsync(stopping.Token);
                    _ = duck.RunFallRecoveryAsync(stopping.Token);

                    int lap = 0;

                    while (!stopping.IsCancellationRequested)
                    {
                        lap++;

                        MicroDuckHealth health = await duck.GetHealthAsync(stopping.Token);

                        if (!health.IsHealthy)
                        {
                            // Better to stop a patrol than to keep walking a robot the daemon has
                            // already decided is unwell.
                            Console.Error.WriteLine($"Health check failed: {string.Join("; ", health.Warnings)}");
                            break;
                        }

                        MicroDuckState state = await duck.GetStateAsync(stopping.Token);
                        Console.WriteLine($"Lap {lap}: battery {state.BatteryVolts:0.00} V, loop {state.LoopRateHz:0} Hz.");

                        foreach ((double speed, double seconds, double turn) in route)
                        {
                            await duck.DriveForAsync(DuckVelocity.Forward(speed), TimeSpan.FromSeconds(seconds), stopping.Token);
                            await duck.DriveForAsync(DuckVelocity.Turn(turn.Degrees()), TimeSpan.FromSeconds(1.0), stopping.Token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    await ParkAsync(duck, engine);
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "duck.policy-runner",
            Name = "ONNX Policy Runner",
            Description = "Loads a reinforcement-learning policy and drives the duck from its output at 50 Hz.",
            Robot = RobotKind.MicroDuck,
            Category = TemplateCategory.Ai,
            Tags = ["onnx", "policy", "reinforcement learning", "ml", "inference"],
            Difficulty = 3,
            Generate = name =>
            [
                ProjectScaffold.CsProj(name, RobotKind.MicroDuck, ProjectKind.Console, "PollenRobotics.Net.Ml"),
                new ProjectFile("Program.cs",
                    "using PollenRobotics.Net.Ml;" + Environment.NewLine + Preamble + """
                    string? policyPath = args.FirstOrDefault(a => a.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));

                    if (policyPath is null || !File.Exists(policyPath))
                    {
                        Console.Error.WriteLine("Pass the path to an .onnx policy as an argument.");
                        Console.Error.WriteLine("Export one from your MuJoCo training run, or pull an official one with");
                        Console.Error.WriteLine("  robotctl policy update");
                        return;
                    }

                    using var policy = new OnnxPolicyRunner(policyPath);
                    Console.WriteLine(policy.Describe());

                    try
                    {
                        await duck.ConnectAsync(stopping.Token);
                        await duck.InitAsync(stopping.Token);

                        // Buffers allocated once. At 50 Hz a fresh array per tick is 3,000 a minute.
                        double[] observation = new double[policy.ObservationSize];
                        double[] action = new double[policy.ActionSize];

                        var loop = new RealtimeLoop(50);

                        await loop.RunAsync(async (_, token) =>
                        {
                            MicroDuckState state = await duck.GetStateAsync(token);
                            BuildObservation(state, observation);

                            policy.Evaluate(observation, action);

                            // How the action maps onto a command is a property of the policy you
                            // trained. This assumes the usual three-component velocity intent;
                            // adjust it to match your own action space.
                            var velocity = new DuckVelocity(
                                action.Length > 0 ? action[0] : 0,
                                action.Length > 1 ? action[1] : 0,
                                action.Length > 2 ? action[2] : 0);

                            await duck.DriveAsync(velocity, token);
                        }, stopping.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("Stopped.");
                    }
                    finally
                    {
                        await ParkAsync(duck, engine);
                    }

                    // The observation layout has to match what the policy was trained on, element
                    // for element. Getting it wrong produces a policy that runs happily and walks
                    // the duck into a wall.
                    static void BuildObservation(MicroDuckState state, double[] destination)
                    {
                        Array.Clear(destination);
                        int index = 0;

                        for (int i = 0; i < state.JointPositions.Count && index < destination.Length; i++)
                        {
                            destination[index++] = state.JointPositions[i];
                        }

                        if (index < destination.Length) destination[index++] = state.AngularRateRadiansPerSecond.X;
                        if (index < destination.Length) destination[index++] = state.AngularRateRadiansPerSecond.Y;
                        if (index < destination.Length) destination[index++] = state.AngularRateRadiansPerSecond.Z;
                        if (index < destination.Length) destination[index++] = state.AccelerationMetersPerSecondSquared.X;
                        if (index < destination.Length) destination[index++] = state.AccelerationMetersPerSecondSquared.Y;
                        if (index < destination.Length) destination[index] = state.AccelerationMetersPerSecondSquared.Z;
                    }
                    """ + Epilogue),
                ProjectScaffold.Readme(name, TemplateCatalog.ById("duck.policy-runner")),
            ],
        };
    }

    private static IReadOnlyList<ProjectFile> Build(string projectName, string templateId, string body) =>
    [
        ProjectScaffold.CsProj(projectName, RobotKind.MicroDuck, ProjectKind.Console),
        new ProjectFile("Program.cs", Preamble + body + Epilogue),
        ProjectScaffold.Readme(projectName, TemplateCatalog.ById(templateId)),
    ];
}
