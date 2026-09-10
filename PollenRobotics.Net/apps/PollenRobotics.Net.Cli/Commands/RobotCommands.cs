using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.MicroDuck;
using PollenRobotics.Net.MicroDuck.Transports;
using PollenRobotics.Net.ReachyMini;
using PollenRobotics.Net.ReachyMini.Transports;
using PollenRobotics.Net.Simulation;
using PollenRobotics.Net.Simulation.Robots;
using PollenRobotics.Net.Simulation.Transports;
using Spectre.Console;

namespace PollenRobotics.Net.Cli.Commands;

/// <summary>Runs the simulator headless and prints telemetry.</summary>
/// <remarks>
/// Useful on its own for checking that the simulation is sane, and as the thing to point a robot
/// application at while developing on a machine with no hardware.
/// </remarks>
internal static class SimCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || !RobotNames.TryParse(args[0], out RobotKind kind))
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] pollen sim <reachy-mini|microduck|reachy2>");
            return 2;
        }

        ISimulatedRobot robot = SimulationEngine.CreateRobot(kind);
        await using var engine = new SimulationEngine(robot);

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        await engine.StartAsync(stopping.Token);

        // Give the model something to do, so the telemetry is not a wall of zeroes.
        switch (robot)
        {
            case SimulatedReachyMini mini:
                mini.SetMotorMode(MotorMode.Enabled);
                mini.SetWobbling(true);
                break;

            case SimulatedMicroDuck duck:
                duck.Init();
                duck.SetVelocity(DuckVelocity.Forward(0.12));
                break;

            default:
                break;
        }

        AnsiConsole.MarkupLine($"[green]Simulating[/] {robot.Description.DisplayName} at {engine.FrequencyHz:0} Hz. Ctrl+C to stop.");
        AnsiConsole.WriteLine();

        var display = new RealtimeLoop(5);

        try
        {
            // Rewriting one line in place only works on a real console. With stdout redirected -
            // piped to a file, or read by another tool - SetCursorPosition throws "The handle is
            // invalid", and the command dies partway through instead of producing output.
            bool interactive = !Console.IsOutputRedirected;

            await display.RunAsync((_, _) =>
            {
                SimulationSnapshot snapshot = engine.LatestSnapshot;

                if (interactive)
                {
                    Console.SetCursorPosition(0, Console.CursorTop);
                }

                AnsiConsole.MarkupLine(
                    $"[dim]t={snapshot.SimulationTime.TotalSeconds,7:0.0}s[/]  " +
                    $"{(snapshot.IsMoving ? "[green]moving[/]" : "[dim]idle  [/]")}  " +
                    string.Join(" ", snapshot.JointPositions.Take(8).Select(j => $"{j * 180 / Math.PI,7:0.#}")));

                return ValueTask.CompletedTask;
            }, stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C.
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Stopped after {engine.LatestSnapshot.SimulationTime.TotalSeconds:0.#}s, {engine.Overruns} overrun(s).[/]");
        return 0;
    }
}

/// <summary>Drives a Reachy Mini from the command line.</summary>
internal static class MiniCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] pollen mini <status|wake|sleep|look|dance|stop> [[--sim]]");
            return 2;
        }

        bool simulate = args.Contains("--sim");

        IReachyMiniTransport transport;
        SimulationEngine? engine = null;

        if (simulate)
        {
            var model = new SimulatedReachyMini();
            engine = new SimulationEngine(model);
            await engine.StartAsync();
            transport = new SimulatedReachyMiniTransport(model, engine);
        }
        else
        {
            transport = new ReachyMiniDaemonTransport();
        }

        await using var mini = new ReachyMiniClient(transport, ownsTransport: true);

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        try
        {
            await mini.ConnectAsync(stopping.Token);

            switch (args[0].ToLowerInvariant())
            {
                case "status":
                    await ShowStatusAsync(mini, stopping.Token);
                    break;

                case "wake":
                    await mini.WakeUpAsync(stopping.Token);
                    AnsiConsole.MarkupLine("[green]Awake.[/]");
                    break;

                case "sleep":
                    await mini.GotoSleepAsync(stopping.Token);
                    AnsiConsole.MarkupLine("[green]Asleep.[/]");
                    break;

                case "stop":
                    await mini.CancelMoveAsync(stopping.Token);
                    await mini.DisableMotorsAsync(stopping.Token);
                    AnsiConsole.MarkupLine("[green]Motors off.[/]");
                    break;

                case "look":
                    await LookAsync(mini, args, stopping.Token);
                    break;

                case "dance":
                    await DanceAsync(mini, stopping.Token);
                    break;

                default:
                    AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(args[0])}'.[/]");
                    return 2;
            }

            return 0;
        }
        finally
        {
            if (engine is not null)
            {
                await engine.DisposeAsync();
            }
        }
    }

    private static async Task ShowStatusAsync(ReachyMiniClient mini, CancellationToken token)
    {
        ReachyMiniState state = await mini.GetStateAsync(token);
        (Angle roll, Angle pitch, Angle yaw) = state.HeadPose.Rpy;

        var table = new Table().Border(TableBorder.Rounded).HideHeaders();
        table.AddColumn("field");
        table.AddColumn("value");

        table.AddRow("endpoint", Markup.Escape(mini.Endpoint));
        table.AddRow("state", mini.State.ToString());
        table.AddRow("motors", state.MotorMode.ToString());
        table.AddRow("head", $"roll {roll.Degrees:0.#}  pitch {pitch.Degrees:0.#}  yaw {yaw.Degrees:0.#} deg");
        table.AddRow("antennas", $"right {state.Antennas.Right.Degrees:0.#}  left {state.Antennas.Left.Degrees:0.#} deg");
        table.AddRow("body yaw", $"{state.BodyYaw.Degrees:0.#} deg");
        table.AddRow("move running", state.IsMoveRunning ? "yes" : "no");

        AnsiConsole.Write(table);
    }

    private static async Task LookAsync(ReachyMiniClient mini, string[] args, CancellationToken token)
    {
        double[] coordinates = [.. args.Skip(1)
            .Where(a => !a.StartsWith('-'))
            .Select(a => double.TryParse(a, out double v) ? v : double.NaN)
            .Where(double.IsFinite)];

        if (coordinates.Length < 3)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] pollen mini look <x> <y> <z>  [dim](metres, world frame)[/]");
            return;
        }

        await mini.EnsureAwakeAsync(token);
        await mini.LookAtAsync(coordinates[0], coordinates[1], coordinates[2], TimeSpan.FromSeconds(1.2), token);

        AnsiConsole.MarkupLine($"[green]Looking at[/] ({coordinates[0]:0.##}, {coordinates[1]:0.##}, {coordinates[2]:0.##}).");
    }

    private static async Task DanceAsync(ReachyMiniClient mini, CancellationToken token)
    {
        await mini.EnsureAwakeAsync(token);

        (double Roll, double Pitch, double Yaw, double RightAntenna, double LeftAntenna)[] routine =
        [
            (0, -10, 25, 60, -60),
            (15, 5, -25, -60, 60),
            (-15, 5, 25, 60, -60),
            (0, -12, 0, 70, -70),
            (0, 0, 0, 0, 0),
        ];

        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
            .StartAsync(async context =>
            {
                ProgressTask task = context.AddTask("[green]Dancing[/]", maxValue: routine.Length);

                foreach ((double roll, double pitch, double yaw, double right, double left) in routine)
                {
                    await mini.GotoTargetAsync(
                        head: HeadPose.Create(roll: roll, pitch: pitch, yaw: yaw),
                        antennas: (Angle.FromDegrees(right), Angle.FromDegrees(left)),
                        duration: TimeSpan.FromSeconds(0.6),
                        method: InterpolationMethod.Cartoon,
                        cancellationToken: token);

                    task.Increment(1);
                }
            });
    }
}

/// <summary>Drives a MicroDuck from the command line.</summary>
internal static class DuckCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] pollen duck <status|init|walk|turn|trick|quack|relax> [[--sim]]");
            return 2;
        }

        bool simulate = args.Contains("--sim");

        IMicroDuckTransport transport;
        SimulationEngine? engine = null;

        if (simulate)
        {
            var model = new SimulatedMicroDuck();
            engine = new SimulationEngine(model);
            await engine.StartAsync();
            transport = new SimulatedMicroDuckTransport(model, engine);
        }
        else
        {
            transport = new MicroDuckDaemonTransport();
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

            switch (args[0].ToLowerInvariant())
            {
                case "status":
                    await ShowStatusAsync(duck, stopping.Token);
                    break;

                case "init":
                    await duck.InitAsync(stopping.Token);
                    AnsiConsole.MarkupLine("[green]Standing.[/]");
                    break;

                case "relax":
                    await duck.RelaxAsync(stopping.Token);
                    AnsiConsole.MarkupLine("[yellow]Relaxed - the duck has collapsed where it stood.[/]");
                    break;

                case "quack":
                    await duck.QuackAsync(stopping.Token);
                    break;

                case "walk":
                    await WalkAsync(duck, args, stopping.Token);
                    break;

                case "turn":
                    await TurnAsync(duck, args, stopping.Token);
                    break;

                case "trick":
                    await TrickAsync(duck, args, stopping.Token);
                    break;

                default:
                    AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(args[0])}'.[/]");
                    return 2;
            }

            return 0;
        }
        finally
        {
            // Whatever happened, do not leave the duck walking.
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            try
            {
                await duck.StopAsync(shutdown.Token);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Could not send a stop: {Markup.Escape(ex.Message)}[/]");
            }

            if (engine is not null)
            {
                await engine.DisposeAsync();
            }
        }
    }

    private static async Task ShowStatusAsync(MicroDuckClient duck, CancellationToken token)
    {
        MicroDuckState state = await duck.GetStateAsync(token);
        MicroDuckHealth health = await duck.GetHealthAsync(token);
        (Angle roll, Angle pitch, Angle yaw) = state.BodyRpy;

        var table = new Table().Border(TableBorder.Rounded).HideHeaders();
        table.AddColumn("field");
        table.AddColumn("value");

        table.AddRow("endpoint", Markup.Escape(duck.Endpoint));
        table.AddRow("state", duck.State.ToString());
        table.AddRow("health", health.IsHealthy ? "[green]ok[/]" : "[red]not ok[/]");
        table.AddRow("firmware", Markup.Escape(health.FirmwareVersion));
        table.AddRow("loop", $"{state.LoopRateHz:0} Hz");
        table.AddRow("battery", double.IsNaN(state.BatteryVolts) ? "[dim]not reported[/]" : $"{state.BatteryVolts:0.00} V");
        table.AddRow("body", $"roll {roll.Degrees:0.#}  pitch {pitch.Degrees:0.#}  yaw {yaw.Degrees:0.#} deg");
        table.AddRow("fallen", state.IsFallen ? "[red]yes[/]" : "no");
        table.AddRow("active slot", state.ActiveSlot?.ToString() ?? "[dim]none[/]");

        AnsiConsole.Write(table);

        foreach (string warning in health.Warnings)
        {
            AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(warning)}");
        }
    }

    private static async Task WalkAsync(MicroDuckClient duck, string[] args, CancellationToken token)
    {
        double seconds = ParseNumber(args, 1) ?? 3;
        double speed = ParseNumber(args, 2) ?? 0.12;

        await duck.InitAsync(token);
        AnsiConsole.MarkupLine($"[green]Walking[/] at {speed:0.##} m/s for {seconds:0.#}s.");
        await duck.DriveForAsync(DuckVelocity.Forward(speed), TimeSpan.FromSeconds(seconds), token);
    }

    private static async Task TurnAsync(MicroDuckClient duck, string[] args, CancellationToken token)
    {
        double degrees = ParseNumber(args, 1) ?? 90;

        await duck.InitAsync(token);
        AnsiConsole.MarkupLine($"[green]Turning[/] {degrees:0.#} deg.");

        // At roughly 60 deg/s, which is comfortably inside what the gait can track.
        await duck.DriveForAsync(
            DuckVelocity.Turn(Angle.FromDegrees(Math.Sign(degrees) * 60)),
            TimeSpan.FromSeconds(Math.Abs(degrees) / 60.0),
            token);
    }

    private static async Task TrickAsync(MicroDuckClient duck, string[] args, CancellationToken token)
    {
        string requested = args.ElementAtOrDefault(1) ?? "stand";

        DuckActionSlot slot;

        try
        {
            slot = DuckActionSlotExtensions.ParseSlot(requested);
        }
        catch (ArgumentException)
        {
            AnsiConsole.MarkupLine($"[red]Unknown trick '{Markup.Escape(requested)}'.[/]");
            AnsiConsole.MarkupLine($"[dim]Try: {string.Join(", ", Enum.GetValues<DuckActionSlot>().Select(s => s.ToWireValue()))}[/]");
            return;
        }

        await duck.InitAsync(token);
        AnsiConsole.MarkupLine($"[green]Performing[/] {slot.ToWireValue()}.");
        await duck.PerformAsync(slot, token);
    }

    private static double? ParseNumber(string[] args, int index) =>
        args.ElementAtOrDefault(index) is { } value && double.TryParse(value, out double parsed) ? parsed : null;
}
