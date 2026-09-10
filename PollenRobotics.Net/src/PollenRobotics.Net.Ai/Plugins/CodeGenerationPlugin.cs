using PollenRobotics.Net.Core;
using System.ComponentModel;
using System.Text;
using Microsoft.SemanticKernel;
using PollenRobotics.Net.Core.Robots;

namespace PollenRobotics.Net.Ai.Plugins;

/// <summary>
/// Scaffolds runnable project skeletons against the SDK.
/// </summary>
/// <remarks>
/// <para>
/// The model writes the interesting part; this supplies the part that has to be exactly right and
/// is tedious to get right from memory - the project file, the package references, the connection
/// bring-up and the disposal. Those are where generated robot code usually fails to build.
/// </para>
/// <para>
/// Everything produced here connects through a transport interface rather than a concrete
/// transport, and takes a <c>--sim</c> switch, so the same program runs against hardware or the
/// simulator. That is a property of the scaffold, not something the model has to remember to do.
/// </para>
/// </remarks>
public sealed class CodeGenerationPlugin
{
    /// <summary>Lists what can be scaffolded.</summary>
    [KernelFunction("list_project_kinds")]
    [Description("Lists the kinds of robot application that can be scaffolded, with what each one is for.")]
    public string ListProjectKinds() => """
        console  - a command-line program. The default for behaviours, scripts and anything headless.
        desktop  - an Avalonia desktop app with a window, for anything with an operator in front of it.
        web      - an ASP.NET Core minimal API plus a Blazor page, for driving a robot from a browser.
        embedded - a worker service meant to be published self-contained and run on the robot itself.
        """;

    /// <summary>Writes the .csproj for a robot application.</summary>
    [KernelFunction("generate_project_file")]
    [Description("Generates the .csproj for a robot application, with the right target framework and PollenRobotics.Net package references.")]
    public string GenerateProjectFile(
        [Description("Project name, used as the assembly and root namespace.")] string projectName,
        [Description("Project kind: console, desktop, web or embedded.")] string kind,
        [Description("Robot: ReachyMini, MicroDuck or Reachy2.")] string robot)
    {
        if (!TryParseRobot(robot, out RobotKind robotKind, out string problem))
        {
            return problem;
        }

        string sdk = kind.ToLowerInvariant() switch
        {
            "web" => "Microsoft.NET.Sdk.Web",
            _ => "Microsoft.NET.Sdk",
        };

        // SDK packages take the publisher prefix and this build's version; everything else carries
        // its own. They used to share one list and every entry was written out at the SDK version,
        // so a desktop project asked for Avalonia 0.1.0 and would not restore.
        var packages = new List<(string Id, string Version)>
        {
            ($"{PackageIdPrefix}{RobotPackage(robotKind)}", SdkInfo.Version),
            ($"{PackageIdPrefix}PollenRobotics.Net.Simulation", SdkInfo.Version),
        };

        if (kind.Equals("desktop", StringComparison.OrdinalIgnoreCase))
        {
            packages.Add(("Avalonia", AvaloniaVersion));
            packages.Add(("Avalonia.Desktop", AvaloniaVersion));
            packages.Add(("Avalonia.Themes.Fluent", AvaloniaVersion));
        }

        if (kind.Equals("embedded", StringComparison.OrdinalIgnoreCase))
        {
            packages.Add(("Microsoft.Extensions.Hosting", ExtensionsVersion));
        }

        var builder = new StringBuilder();
        builder.AppendLine($"""<Project Sdk="{sdk}">""");
        builder.AppendLine();
        builder.AppendLine("  <PropertyGroup>");
        builder.AppendLine(kind.Equals("desktop", StringComparison.OrdinalIgnoreCase) || kind.Equals("console", StringComparison.OrdinalIgnoreCase)
            ? "    <OutputType>Exe</OutputType>"
            : "    <OutputType>Exe</OutputType>");
        builder.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        builder.AppendLine("    <Nullable>enable</Nullable>");
        builder.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        builder.AppendLine($"    <RootNamespace>{projectName}</RootNamespace>");

        if (kind.Equals("embedded", StringComparison.OrdinalIgnoreCase))
        {
            // The robots run Linux on ARM. Publishing for the wrong RID produces a binary that
            // copies across fine and then refuses to start.
            builder.AppendLine("    <RuntimeIdentifiers>linux-arm64;linux-x64</RuntimeIdentifiers>");
            builder.AppendLine("    <PublishSingleFile>true</PublishSingleFile>");
            builder.AppendLine("    <SelfContained>true</SelfContained>");
        }

        builder.AppendLine("  </PropertyGroup>");
        builder.AppendLine();
        builder.AppendLine("  <ItemGroup>");

        foreach ((string id, string version) in packages)
        {
            builder.AppendLine($"""    <PackageReference Include="{id}" Version="{version}" />""");
        }

        builder.AppendLine("  </ItemGroup>");
        builder.AppendLine();
        builder.AppendLine("</Project>");

        return builder.ToString();
    }

    /// <summary>Writes a runnable program skeleton.</summary>
    [KernelFunction("generate_program_skeleton")]
    [Description("Generates a complete, compiling Program.cs that connects to a robot or the simulator and leaves a clearly marked place for the behaviour. Use it as the starting point, then fill in the behaviour.")]
    public string GenerateProgramSkeleton(
        [Description("Robot: ReachyMini, MicroDuck or Reachy2.")] string robot,
        [Description("Project kind: console, desktop, web or embedded.")] string kind = "console",
        [Description("One line describing what the program should do, used as the header comment.")] string description = "")
    {
        if (!TryParseRobot(robot, out RobotKind robotKind, out string problem))
        {
            return problem;
        }

        return robotKind switch
        {
            RobotKind.ReachyMini => ReachyMiniSkeleton(description, kind),
            RobotKind.MicroDuck => MicroDuckSkeleton(description),
            RobotKind.Reachy2 => Reachy2Skeleton(description),
            _ => problem,
        };
    }

    /// <summary>Reports the SDK rules generated code has to satisfy.</summary>
    [KernelFunction("get_safety_rules")]
    [Description("Lists the safety and bring-up rules that code for a given robot must follow. Read these before writing motion code.")]
    public string GetSafetyRules(
        [Description("Robot: ReachyMini, MicroDuck or Reachy2.")] string robot)
    {
        if (!TryParseRobot(robot, out RobotKind robotKind, out string problem))
        {
            return problem;
        }

        return robotKind switch
        {
            RobotKind.ReachyMini => """
                Reachy Mini:
                - Head pitch and roll are limited to +/-40 deg, body yaw to +/-160 deg.
                - Head yaw must stay within 65 deg of body yaw. To turn further, move both in the
                  same SetTarget call, or leave AutomaticBodyYaw on and let the daemon follow.
                - Build incremental head motion on the last COMMANDED pose (CommandedHeadPose), not
                  on telemetry. Telemetry lags a round trip and deltas against it stall.
                - The SDK throws on a limit violation by default. That is deliberate; do not switch
                  to RobotSafetyOptions.Permissive to make an error go away.
                - Call EnsureAwakeAsync rather than WakeUpAsync on startup, so the robot does not
                  replay its greeting every time an app restarts.
                """,

            RobotKind.MicroDuck => """
                MicroDuck:
                - InitAsync must run before anything else. A relaxed duck ignores commands silently.
                - Velocity is an intent that the daemon holds until replaced. A one-shot command
                  walks forever - use DriveForAsync, or keep sending and stop explicitly.
                - Do not command joint angles. A learned policy owns the servos; you send velocities
                  and action slots.
                - A fallen duck ignores velocity commands. Watch state.IsFallen and run
                  RecoverAsync, or start RunFallRecoveryAsync alongside your logic.
                - RelaxAsync drops the duck where it stands. Make sure that is somewhere it can fall.
                """,

            RobotKind.Reachy2 => """
                Reachy 2:
                - TurnOnAsync before moving. Arms first, then grippers: a gripper energised on a
                  compliant arm can swing the whole limb.
                - Movements queue per part. A goto returns a handle immediately and the motion
                  happens later - await handle.WaitAsync to follow it.
                - Seed inverse kinematics with the arm's present joints. Seeding from zero gives a
                  valid solution reached by an unacceptable route.
                - Keep IKContinuousMode.Continuous along a trajectory so the elbow does not flip
                  between neighbouring waypoints.
                - Parts are discovered at connect time. Check MobileBase for null; not every robot
                  has one.
                - TurnOffAsync makes everything compliant: the arms sag and anything held drops.
                """,

            _ => problem,
        };
    }

    private static string ReachyMiniSkeleton(string description, string kind)
    {
        string header = string.IsNullOrWhiteSpace(description) ? "A Reachy Mini behaviour." : description;

        return $$"""
            // {{header}}
            // Run with --sim to drive the simulator instead of a robot.
            using PollenRobotics.Net.Core.Geometry;
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
                // Let the finally block park the robot rather than dying mid-motion.
                e.Cancel = true;
                stopping.Cancel();
            };

            try
            {
                await mini.ConnectAsync(stopping.Token);
                await mini.EnsureAwakeAsync(stopping.Token);

                Console.WriteLine($"Connected to {mini.Endpoint}. Ctrl+C to stop.");

                // ---- behaviour starts here ----

                await mini.GotoTargetAsync(
                    head: HeadPose.Create(z: 10, pitch: -5, mm: true),
                    antennas: (30.Degrees(), (-30).Degrees()),
                    duration: TimeSpan.FromSeconds(1.5),
                    cancellationToken: stopping.Token);

                // ---- behaviour ends here ----
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Stopping.");
            }
            finally
            {
                // Park before letting go, so the robot is not left mid-pose with torque on.
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await mini.GotoSleepAsync(shutdown.Token);

                if (engine is not null)
                {
                    await engine.DisposeAsync();
                }
            }
            """;
    }

    private static string MicroDuckSkeleton(string description)
    {
        string header = string.IsNullOrWhiteSpace(description) ? "A MicroDuck behaviour." : description;

        return $$"""
            // {{header}}
            // Run with --sim to drive the simulator instead of a robot.
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

            try
            {
                await duck.ConnectAsync(stopping.Token);

                // Nothing moves until the servos are powered and homed.
                await duck.InitAsync(stopping.Token);

                // A fallen duck ignores velocity commands, so watch for it in the background.
                _ = duck.RunFallRecoveryAsync(stopping.Token);

                Console.WriteLine($"Connected to {duck.Endpoint}. Ctrl+C to stop.");

                // ---- behaviour starts here ----

                await duck.DriveForAsync(DuckVelocity.Forward(0.12), TimeSpan.FromSeconds(3), stopping.Token);
                await duck.QuackAsync(stopping.Token);

                // ---- behaviour ends here ----
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Stopping.");
            }
            finally
            {
                // Stop before letting go. The daemon holds the last velocity indefinitely.
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await duck.StopAsync(shutdown.Token);

                if (engine is not null)
                {
                    await engine.DisposeAsync();
                }
            }
            """;
    }

    private static string Reachy2Skeleton(string description)
    {
        string header = string.IsNullOrWhiteSpace(description) ? "A Reachy 2 behaviour." : description;

        return $$"""
            // {{header}}
            using PollenRobotics.Net.Core.Geometry;
            using PollenRobotics.Net.Reachy2;

            string host = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "localhost";

            await using var reachy = Reachy2Client.Connect(Reachy2Options.ForHost(host));

            using var stopping = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stopping.Cancel();
            };

            try
            {
                await reachy.ConnectAsync(stopping.Token);
                await reachy.TurnOnAsync(stopping.Token);

                Console.WriteLine($"Connected to {reachy.RobotName} at {reachy.Endpoint}.");

                // Parts are discovered at connect time; not every robot reports all of them.
                if (reachy.RightArm is not { } arm)
                {
                    Console.Error.WriteLine("This robot has no right arm.");
                    return;
                }

                // ---- behaviour starts here ----

                double[] rest = await arm.GetJointPositionsAsync(stopping.Token);
                Reachy2GotoHandle move = await arm.GotoJointsAsync(rest, TimeSpan.FromSeconds(2), cancellationToken: stopping.Token);

                // A goto returns as soon as it is queued. Wait for it to actually finish.
                await move.WaitAsync(cancellationToken: stopping.Token);

                // ---- behaviour ends here ----
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Stopping.");
            }
            finally
            {
                // Leaving the arms energised is usually the safer end state - turning them off here
                // would let them sag, and drop anything a gripper is holding.
                Console.WriteLine("Done.");
            }
            """;
    }

    /// <summary>The publisher prefix on NuGet IDs. Namespaces stay unprefixed.</summary>
    private const string PackageIdPrefix = "Gravicode.";

    /// <summary>Versions of the third-party packages generated projects may reference.</summary>
    /// <remarks>
    /// Kept in step with Directory.Packages.props by hand. They are the versions this SDK is built
    /// and tested against, so a generated project that pins them behaves like the gallery does.
    /// </remarks>
    private const string AvaloniaVersion = "12.1.2";

    private const string ExtensionsVersion = "10.0.6";

    private static string RobotPackage(RobotKind kind) => kind switch
    {
        RobotKind.ReachyMini => "PollenRobotics.Net.ReachyMini",
        RobotKind.MicroDuck => "PollenRobotics.Net.MicroDuck",
        RobotKind.Reachy2 => "PollenRobotics.Net.Reachy2",
        _ => "PollenRobotics.Net",
    };

    private static bool TryParseRobot(string robot, out RobotKind kind, out string problem)
    {
        if (Enum.TryParse(robot.Replace(" ", string.Empty), ignoreCase: true, out kind))
        {
            problem = string.Empty;
            return true;
        }

        problem = $"Unknown robot '{robot}'. Valid values: ReachyMini, MicroDuck, Reachy2.";
        return false;
    }
}
