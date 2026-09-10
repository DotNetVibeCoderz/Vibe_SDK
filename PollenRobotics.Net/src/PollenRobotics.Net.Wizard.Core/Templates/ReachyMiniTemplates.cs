using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Wizard.Core.Projects;

namespace PollenRobotics.Net.Wizard.Core.Templates;

/// <summary>Templates that drive Reachy Mini.</summary>
internal static class ReachyMiniTemplates
{
    /// <summary>
    /// The bring-up block every Reachy Mini template opens with.
    /// </summary>
    /// <remarks>
    /// Kept identical across templates on purpose. Someone who has read one template can skip
    /// straight to the interesting part of the next, and the connect/park pattern is the thing
    /// most worth copying.
    /// </remarks>
    private const string Preamble = """
        using PollenRobotics.Net.Core.Geometry;
        using PollenRobotics.Net.Core.Realtime;
        using PollenRobotics.Net.ReachyMini;
        using PollenRobotics.Net.ReachyMini.Transports;
        using PollenRobotics.Net.Simulation;
        using PollenRobotics.Net.Simulation.Robots;
        using PollenRobotics.Net.Simulation.Transports;

        bool useSimulator = args.Contains("--sim");

        IReachyMiniTransport transport;
        SimulationEngine? engine = null;

        if (useSimulator)
        {
            var model = new SimulatedReachyMini();
            engine = new SimulationEngine(model);
            await engine.StartAsync();
            transport = new SimulatedReachyMiniTransport(model, engine);
        }
        else
        {
            transport = new ReachyMiniDaemonTransport(ReachyMiniOptions.Default);
        }

        await using var mini = new ReachyMiniClient(transport, ownsTransport: true);

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Cancel the token instead of dying, so the robot gets parked on the way out.
            e.Cancel = true;
            stopping.Cancel();
        };

        """;

    private const string Epilogue = """

        static async Task ParkAsync(ReachyMiniClient mini, SimulationEngine? engine)
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await mini.GotoSleepAsync(shutdown.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not park the robot: {ex.Message}");
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
            Id = "mini.hello",
            Name = "Hello Reachy Mini",
            Description = "Connects, wakes up, nods hello and waves both antennas.",
            Robot = RobotKind.ReachyMini,
            Category = TemplateCategory.GettingStarted,
            Tags = ["hello", "first", "basic", "goto"],
            Difficulty = 1,
            Generate = name => Build(name, "mini.hello", """
                try
                {
                    await mini.ConnectAsync(stopping.Token);
                    await mini.EnsureAwakeAsync(stopping.Token);
                    Console.WriteLine($"Connected to {mini.Endpoint}.");

                    // Nod twice. Pitch is positive nose-down.
                    for (int i = 0; i < 2; i++)
                    {
                        await mini.GotoTargetAsync(
                            head: HeadPose.Create(pitch: 15),
                            duration: TimeSpan.FromSeconds(0.4),
                            cancellationToken: stopping.Token);

                        await mini.GotoTargetAsync(
                            head: HeadPose.Neutral,
                            duration: TimeSpan.FromSeconds(0.4),
                            cancellationToken: stopping.Token);
                    }

                    // Wave the antennas. They move in opposite directions, which reads as a wave
                    // rather than as a shrug.
                    for (int i = 0; i < 3; i++)
                    {
                        await mini.SetAntennasAsync(50, -50, stopping.Token);
                        await Task.Delay(200, stopping.Token);
                        await mini.SetAntennasAsync(-50, 50, stopping.Token);
                        await Task.Delay(200, stopping.Token);
                    }

                    await mini.SetAntennasAsync(0, 0, stopping.Token);
                    Console.WriteLine("Done.");
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    await ParkAsync(mini, engine);
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "mini.emotions",
            Name = "Emotion Player",
            Description = "A library of expressive poses - curious, happy, sad, alert, sleepy - played by name.",
            Robot = RobotKind.ReachyMini,
            Category = TemplateCategory.Behaviour,
            Tags = ["emotion", "expression", "personality", "animation"],
            Difficulty = 1,
            Generate = name => Build(name, "mini.emotions", """
                // Each emotion is a short sequence of poses. Keeping them as data rather than as
                // code makes them easy to tune without touching the playback loop.
                var emotions = new Dictionary<string, (Pose Head, (Angle Right, Angle Left) Antennas, InterpolationMethod Easing)[]>
                {
                    ["curious"] =
                    [
                        (HeadPose.Create(roll: 18, yaw: 20, z: 8, mm: true), (40.Degrees(), 10.Degrees()), InterpolationMethod.MinJerk),
                        (HeadPose.Create(roll: 12, yaw: 25), (55.Degrees(), 20.Degrees()), InterpolationMethod.EaseInOut),
                    ],
                    ["happy"] =
                    [
                        (HeadPose.Create(z: 14, pitch: -12, mm: true), (70.Degrees(), (-70).Degrees()), InterpolationMethod.Cartoon),
                        (HeadPose.Create(z: 6, pitch: -4, mm: true), (40.Degrees(), (-40).Degrees()), InterpolationMethod.Cartoon),
                        (HeadPose.Create(z: 14, pitch: -12, mm: true), (70.Degrees(), (-70).Degrees()), InterpolationMethod.Cartoon),
                    ],
                    ["sad"] =
                    [
                        (HeadPose.Create(z: -8, pitch: 25, mm: true), ((-70).Degrees(), 70.Degrees()), InterpolationMethod.MinJerk),
                    ],
                    ["alert"] =
                    [
                        (HeadPose.Create(z: 16, pitch: -18, mm: true), (0.Degrees(), 0.Degrees()), InterpolationMethod.Cartoon),
                    ],
                    ["sleepy"] =
                    [
                        (HeadPose.Create(z: -6, pitch: 20, roll: 10, mm: true), ((-40).Degrees(), (-50).Degrees()), InterpolationMethod.MinJerk),
                    ],
                    ["no"] =
                    [
                        (HeadPose.Create(yaw: 25), (20.Degrees(), (-20).Degrees()), InterpolationMethod.Linear),
                        (HeadPose.Create(yaw: -25), ((-20).Degrees(), 20.Degrees()), InterpolationMethod.Linear),
                        (HeadPose.Create(yaw: 25), (20.Degrees(), (-20).Degrees()), InterpolationMethod.Linear),
                    ],
                };

                try
                {
                    await mini.ConnectAsync(stopping.Token);
                    await mini.EnsureAwakeAsync(stopping.Token);

                    string requested = args.FirstOrDefault(a => !a.StartsWith("--")) ?? string.Empty;

                    IEnumerable<string> toPlay = emotions.ContainsKey(requested)
                        ? [requested]
                        : emotions.Keys;

                    if (!emotions.ContainsKey(requested))
                    {
                        Console.WriteLine($"Playing all emotions. Pass one of: {string.Join(", ", emotions.Keys)}");
                    }

                    foreach (string emotion in toPlay)
                    {
                        Console.WriteLine($"-> {emotion}");

                        foreach ((Pose head, (Angle Right, Angle Left) antennas, InterpolationMethod easing) in emotions[emotion])
                        {
                            await mini.GotoTargetAsync(
                                head: head,
                                antennas: antennas,
                                duration: TimeSpan.FromSeconds(0.5),
                                method: easing,
                                cancellationToken: stopping.Token);
                        }

                        await mini.GotoTargetAsync(
                            head: HeadPose.Neutral,
                            antennas: (Angle.Zero, Angle.Zero),
                            duration: TimeSpan.FromSeconds(0.6),
                            cancellationToken: stopping.Token);

                        await Task.Delay(400, stopping.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    await ParkAsync(mini, engine);
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "mini.face-follow",
            Name = "Face Follower",
            Description = "Turns on the daemon's face tracker and reacts when it loses or reacquires a face.",
            Robot = RobotKind.ReachyMini,
            Category = TemplateCategory.Perception,
            Tags = ["face", "tracking", "camera", "vision", "interaction"],
            Difficulty = 2,
            Generate = name => Build(name, "mini.face-follow", """
                try
                {
                    await mini.ConnectAsync(stopping.Token);
                    await mini.EnsureAwakeAsync(stopping.Token);

                    // Weight 1.0 hands the head entirely to the tracker. Lower it to blend tracking
                    // with motion this program commands.
                    await mini.StartHeadTrackingAsync(1.0, stopping.Token);
                    Console.WriteLine("Tracking. Ctrl+C to stop.");

                    bool wasTracking = false;
                    var loop = new RealtimeLoop(10);

                    await loop.RunAsync(async (_, token) =>
                    {
                        FaceTarget face = await mini.GetTrackedFaceAsync(token);

                        if (face.Detected && !wasTracking)
                        {
                            Console.WriteLine("Found a face.");

                            // Perk the antennas up on acquisition. The tracker owns the head, so
                            // this is the only channel left to react on.
                            await mini.SetAntennasAsync(45, -45, token);
                        }
                        else if (!face.Detected && wasTracking)
                        {
                            Console.WriteLine("Lost it.");
                            await mini.SetAntennasAsync(-25, 25, token);
                        }

                        wasTracking = face.Detected;
                    }, stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await mini.StopHeadTrackingAsync(shutdown.Token);
                    await ParkAsync(mini, engine);
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "mini.idle-life",
            Name = "Idle Life",
            Description = "Keeps the robot looking alive: breathing, occasional glances, the odd antenna twitch.",
            Robot = RobotKind.ReachyMini,
            Category = TemplateCategory.Behaviour,
            Tags = ["idle", "ambient", "breathing", "life", "wobble"],
            Difficulty = 2,
            Generate = name => Build(name, "mini.idle-life", """
                try
                {
                    await mini.ConnectAsync(stopping.Token);
                    await mini.EnsureAwakeAsync(stopping.Token);

                    // The daemon's own breathing motion runs underneath whatever this program does.
                    await mini.SetWobblingAsync(true, stopping.Token);
                    Console.WriteLine("Idling. Ctrl+C to stop.");

                    var random = new Random();

                    while (!stopping.IsCancellationRequested)
                    {
                        // Wait a while, then do one small thing. The irregular interval is what
                        // makes it read as alive rather than as a loop.
                        await Task.Delay(TimeSpan.FromSeconds(random.Next(3, 9)), stopping.Token);

                        switch (random.Next(3))
                        {
                            case 0:
                                // A glance. Kept well inside the 65-degree head-to-body yaw limit.
                                await mini.GotoTargetAsync(
                                    head: HeadPose.Create(yaw: random.Next(-35, 36), pitch: random.Next(-8, 9)),
                                    duration: TimeSpan.FromSeconds(0.8),
                                    cancellationToken: stopping.Token);

                                await Task.Delay(TimeSpan.FromSeconds(1.5), stopping.Token);

                                await mini.GotoTargetAsync(
                                    head: HeadPose.Neutral,
                                    duration: TimeSpan.FromSeconds(1.0),
                                    cancellationToken: stopping.Token);
                                break;

                            case 1:
                                // An antenna twitch.
                                await mini.SetAntennasAsync(random.Next(20, 60), random.Next(-60, -20), stopping.Token);
                                await Task.Delay(300, stopping.Token);
                                await mini.SetAntennasAsync(0, 0, stopping.Token);
                                break;

                            default:
                                // A head tilt, which is the cheapest way to look curious.
                                await mini.GotoTargetAsync(
                                    head: HeadPose.Create(roll: random.Next(-20, 21)),
                                    duration: TimeSpan.FromSeconds(0.7),
                                    method: InterpolationMethod.EaseInOut,
                                    cancellationToken: stopping.Token);

                                await Task.Delay(TimeSpan.FromSeconds(2), stopping.Token);

                                await mini.GotoTargetAsync(
                                    head: HeadPose.Neutral,
                                    duration: TimeSpan.FromSeconds(0.9),
                                    cancellationToken: stopping.Token);
                                break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await mini.SetWobblingAsync(false, shutdown.Token);
                    await ParkAsync(mini, engine);
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "mini.teleop",
            Name = "Keyboard Teleoperation",
            Description = "Drives the head and antennas from the arrow keys at 50 Hz.",
            Robot = RobotKind.ReachyMini,
            Category = TemplateCategory.Interface,
            Tags = ["teleop", "keyboard", "control", "realtime", "manual"],
            Difficulty = 2,
            Generate = name => Build(name, "mini.teleop", """"
                try
                {
                    await mini.ConnectAsync(stopping.Token);
                    await mini.EnsureAwakeAsync(stopping.Token);

                    Console.WriteLine("""
                        Arrow keys  - pitch and yaw
                        A / D       - roll
                        W / S       - raise and lower
                        Q / E       - turn the body
                        Space       - recentre
                        Esc         - quit
                        """);

                    // The commanded pose is tracked here rather than read back from telemetry.
                    // Telemetry lags a round trip, and deltas accumulated against a lagging value
                    // stall the moment a key is held down.
                    double pitch = 0, yaw = 0, roll = 0, lift = 0, bodyYaw = 0;

                    var loop = new RealtimeLoop(50);

                    await loop.RunAsync(async (_, token) =>
                    {
                        while (Console.KeyAvailable)
                        {
                            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                            switch (key.Key)
                            {
                                case ConsoleKey.UpArrow: pitch -= 2; break;
                                case ConsoleKey.DownArrow: pitch += 2; break;
                                case ConsoleKey.LeftArrow: yaw += 3; break;
                                case ConsoleKey.RightArrow: yaw -= 3; break;
                                case ConsoleKey.A: roll -= 2; break;
                                case ConsoleKey.D: roll += 2; break;
                                case ConsoleKey.W: lift += 1; break;
                                case ConsoleKey.S: lift -= 1; break;
                                case ConsoleKey.Q: bodyYaw += 4; break;
                                case ConsoleKey.E: bodyYaw -= 4; break;
                                case ConsoleKey.Spacebar: pitch = yaw = roll = lift = bodyYaw = 0; break;
                                case ConsoleKey.Escape: await stopping.CancelAsync(); return;
                                default: break;
                            }
                        }

                        // Clamped here rather than relying on the SDK to throw: a teleop loop that
                        // throws on every tick once a key is held too long is unusable.
                        pitch = Math.Clamp(pitch, -35, 35);
                        roll = Math.Clamp(roll, -35, 35);
                        lift = Math.Clamp(lift, -8, 18);
                        bodyYaw = Math.Clamp(bodyYaw, -150, 150);

                        // Head yaw must stay within 65 degrees of body yaw.
                        yaw = Math.Clamp(yaw, bodyYaw - 60, bodyYaw + 60);

                        await mini.SetTargetAsync(
                            head: HeadPose.Create(z: lift, roll: roll, pitch: pitch, yaw: yaw, mm: true),
                            bodyYaw: Angle.FromDegrees(bodyYaw),
                            cancellationToken: token);
                    }, stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    await ParkAsync(mini, engine);
                }
                """"),
        };

        yield return new RobotTemplate
        {
            Id = "mini.record-replay",
            Name = "Record and Replay",
            Description = "Puts the head in gravity compensation, records a move posed by hand, saves it and plays it back.",
            Robot = RobotKind.ReachyMini,
            Category = TemplateCategory.Tooling,
            Tags = ["record", "replay", "move", "teaching", "gravity compensation"],
            Difficulty = 2,
            Generate = name => Build(name, "mini.record-replay", """
                string movePath = Path.Combine(AppContext.BaseDirectory, "recorded-move.json");

                try
                {
                    await mini.ConnectAsync(stopping.Token);
                    await mini.EnsureAwakeAsync(stopping.Token);

                    if (args.Contains("--play") && File.Exists(movePath))
                    {
                        RecordedMove saved = RecordedMove.FromJson(await File.ReadAllTextAsync(movePath, stopping.Token), "saved");
                        Console.WriteLine($"Playing {saved.Frames.Count} frames over {saved.Duration.TotalSeconds:0.#}s.");

                        await mini.PlayMoveAsync(saved, cancellationToken: stopping.Token);
                        return;
                    }

                    // Gravity compensation lets the head be moved by hand and stay where it is put.
                    // This is the mode to record in; in position control the head fights back.
                    await mini.EnableGravityCompensationAsync(stopping.Token);

                    Console.WriteLine("Recording. Move the head by hand, then press Enter.");
                    await mini.StartRecordingAsync(stopping.Token);

                    Console.ReadLine();

                    RecordedMove recorded = await mini.StopRecordingAsync("teach-in", stopping.Token);
                    Console.WriteLine($"Captured {recorded.Frames.Count} frames.");

                    if (recorded.Frames.Count == 0)
                    {
                        // The simulated transport does not record, and an older daemon may not
                        // either. Saying so beats writing an empty file and calling it a success.
                        Console.WriteLine("Nothing was recorded. Recording needs a daemon that supports it.");
                        return;
                    }

                    // Resampled onto a uniform grid, which is what playback wants.
                    await File.WriteAllTextAsync(movePath, recorded.Resample(100).ToJson(), stopping.Token);
                    Console.WriteLine($"Saved to {movePath}. Run again with --play to replay it.");

                    await mini.EnableMotorsAsync(stopping.Token);
                    await mini.PlayMoveAsync(recorded, cancellationToken: stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    await ParkAsync(mini, engine);
                }
                """, "PollenRobotics.Net.ReachyMini.Moves"),
        };

        yield return new RobotTemplate
        {
            Id = "mini.look-at",
            Name = "Look At A Point",
            Description = "Sweeps the head around a set of world-frame points, showing how look-at maps to head angles.",
            Robot = RobotKind.ReachyMini,
            Category = TemplateCategory.Navigation,
            Tags = ["lookat", "world frame", "kinematics", "coordinates"],
            Difficulty = 1,
            Generate = name => Build(name, "mini.look-at", """
                // World-frame points in metres: x forward, y left, z up, measured from the base.
                (string Name, double X, double Y, double Z)[] points =
                [
                    ("straight ahead", 1.0, 0.0, 0.06),
                    ("up and left",    0.8, 0.4, 0.40),
                    ("down and right", 0.6, -0.3, -0.10),
                    ("close up",       0.25, 0.0, 0.12),
                    ("far left",       0.9, 0.7, 0.06),
                ];

                try
                {
                    await mini.ConnectAsync(stopping.Token);
                    await mini.EnsureAwakeAsync(stopping.Token);

                    foreach ((string label, double x, double y, double z) in points)
                    {
                        Console.WriteLine($"Looking at {label} ({x:0.##}, {y:0.##}, {z:0.##}).");

                        await mini.LookAtAsync(x, y, z, TimeSpan.FromSeconds(1.2), stopping.Token);

                        // Read back what that turned into, which is the useful part of this example.
                        ReachyMiniState state = await mini.GetStateAsync(stopping.Token);
                        (Angle roll, Angle pitch, Angle yaw) = state.HeadPose.Rpy;

                        Console.WriteLine($"  -> roll {roll.Degrees:0.#}, pitch {pitch.Degrees:0.#}, yaw {yaw.Degrees:0.#} deg");

                        await Task.Delay(TimeSpan.FromSeconds(1), stopping.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Stopped.");
                }
                finally
                {
                    await ParkAsync(mini, engine);
                }
                """),
        };

        yield return new RobotTemplate
        {
            Id = "mini.jack-companion",
            Name = "LLM Companion",
            Description = "A conversational robot: the model picks a gesture to play alongside every reply.",
            Robot = RobotKind.ReachyMini,
            Category = TemplateCategory.Ai,
            Tags = ["llm", "chat", "semantic kernel", "conversation", "jack"],
            Difficulty = 3,
            Generate = name =>
            [
                ProjectScaffold.CsProj(name, RobotKind.ReachyMini, ProjectKind.Console, "PollenRobotics.Net.Ai"),
                ProjectScaffold.AppConfig(),
                new ProjectFile("Program.cs",
                    "using Microsoft.Extensions.Configuration;" + Environment.NewLine +
                    "using PollenRobotics.Net.Ai;" + Environment.NewLine +
                    "using PollenRobotics.Net.Ai.Chat;" + Environment.NewLine +
                    Preamble + """"
                    IConfiguration configuration = new ConfigurationBuilder()
                        .SetBasePath(AppContext.BaseDirectory)
                        .AddJsonFile("appsettings.json", optional: true)
                        .AddEnvironmentVariables()
                        .Build();

                    AiOptions ai = AiOptions.FromConfiguration(configuration) with
                    {
                        SystemPromptIsCustom = true,
                        SystemPrompt = """
                            You are the voice of a Reachy Mini desk robot. Keep replies to one or two
                            sentences - they are spoken aloud, not read.

                            End every reply with a gesture tag on its own line, chosen from:
                            [gesture:nod] [gesture:tilt] [gesture:perk] [gesture:droop] [gesture:shake]
                            """,
                    };

                    if (!ai.IsConfigured)
                    {
                        Console.Error.WriteLine(ai.ConfigurationProblem);
                        return;
                    }

                    var jack = new JackTheCodeBender(ai);
                    jack.Configure(ai);

                    var session = new ChatSession();

                    try
                    {
                        await mini.ConnectAsync(stopping.Token);
                        await mini.EnsureAwakeAsync(stopping.Token);
                        await mini.SetWobblingAsync(true, stopping.Token);

                        Console.WriteLine("Talk to the robot. Blank line to quit.");

                        while (!stopping.IsCancellationRequested)
                        {
                            Console.Write("> ");
                            string? line = Console.ReadLine();

                            if (string.IsNullOrWhiteSpace(line))
                            {
                                break;
                            }

                            string reply = await jack.AskCompleteAsync(session, line, cancellationToken: stopping.Token);

                            // Split the gesture tag off before printing, so the tag never reaches
                            // the user and the text never reaches the gesture player.
                            string gesture = ExtractGesture(reply);
                            Console.WriteLine(StripGesture(reply));

                            await PlayGestureAsync(mini, gesture, stopping.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("Stopped.");
                    }
                    finally
                    {
                        await ParkAsync(mini, engine);
                    }

                    static string ExtractGesture(string reply)
                    {
                        int start = reply.LastIndexOf("[gesture:", StringComparison.OrdinalIgnoreCase);
                        if (start < 0)
                        {
                            return "nod";
                        }

                        int end = reply.IndexOf(']', start);
                        return end < 0 ? "nod" : reply[(start + 9)..end].Trim().ToLowerInvariant();
                    }

                    static string StripGesture(string reply)
                    {
                        int start = reply.LastIndexOf("[gesture:", StringComparison.OrdinalIgnoreCase);
                        return start < 0 ? reply.Trim() : reply[..start].Trim();
                    }

                    static async Task PlayGestureAsync(ReachyMiniClient mini, string gesture, CancellationToken token)
                    {
                        switch (gesture)
                        {
                            case "tilt":
                                await mini.GotoTargetAsync(head: HeadPose.Create(roll: 20), duration: TimeSpan.FromSeconds(0.4), cancellationToken: token);
                                break;

                            case "perk":
                                await mini.GotoTargetAsync(
                                    head: HeadPose.Create(z: 12, pitch: -10, mm: true),
                                    antennas: (60.Degrees(), (-60).Degrees()),
                                    duration: TimeSpan.FromSeconds(0.4),
                                    method: InterpolationMethod.Cartoon,
                                    cancellationToken: token);
                                break;

                            case "droop":
                                await mini.GotoTargetAsync(
                                    head: HeadPose.Create(z: -6, pitch: 20, mm: true),
                                    antennas: ((-60).Degrees(), 60.Degrees()),
                                    duration: TimeSpan.FromSeconds(0.6),
                                    cancellationToken: token);
                                break;

                            case "shake":
                                await mini.GotoTargetAsync(head: HeadPose.Create(yaw: 22), duration: TimeSpan.FromSeconds(0.25), cancellationToken: token);
                                await mini.GotoTargetAsync(head: HeadPose.Create(yaw: -22), duration: TimeSpan.FromSeconds(0.25), cancellationToken: token);
                                break;

                            default:
                                await mini.GotoTargetAsync(head: HeadPose.Create(pitch: 14), duration: TimeSpan.FromSeconds(0.3), cancellationToken: token);
                                break;
                        }

                        await mini.GotoTargetAsync(
                            head: HeadPose.Neutral,
                            antennas: (Angle.Zero, Angle.Zero),
                            duration: TimeSpan.FromSeconds(0.5),
                            cancellationToken: token);
                    }
                    """" + Epilogue),
                ProjectScaffold.Readme(name, Catalog("mini.jack-companion")),
            ],
        };
    }

    private static IReadOnlyList<ProjectFile> Build(
        string projectName,
        string templateId,
        string body,
        params string[] extraUsings) =>
    [
        ProjectScaffold.CsProj(projectName, RobotKind.ReachyMini, ProjectKind.Console),
        new ProjectFile("Program.cs", Compose(body, extraUsings)),
        ProjectScaffold.Readme(projectName, Catalog(templateId)),
    ];

    /// <summary>
    /// Assembles a program from the preamble, the template body and any extra namespaces.
    /// </summary>
    /// <remarks>
    /// The extra using directives have to go at the very top. The preamble opens with top-level
    /// statements, and a using after those is CS1529 - which is how three templates in this
    /// catalogue were broken until TemplateCheck compiled them.
    /// </remarks>
    private static string Compose(string body, string[] extraUsings) =>
        extraUsings.Length == 0
            ? Preamble + body + Epilogue
            : string.Join(Environment.NewLine, extraUsings.Select(u => $"using {u};"))
              + Environment.NewLine
              + Preamble + body + Epilogue;

    // Resolved lazily so a template can reference its own catalogue entry while the catalogue is
    // still being built.
    private static RobotTemplate Catalog(string id) => TemplateCatalog.ById(id);
}
