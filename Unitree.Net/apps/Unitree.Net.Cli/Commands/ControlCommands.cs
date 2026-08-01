using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Unitree.Net.Ai;
using Unitree.Net.Control;
using Unitree.Net.Core;

namespace Unitree.Net.Cli.Commands;

/// <summary>
/// Executes a posture change: stand, sit, damp or recover.
/// </summary>
internal static class PostureCommand
{
    internal static async Task<int> RunAsync(
        IServiceProvider provider,
        string posture,
        CancellationToken cancellationToken)
    {
        var robot = provider.GetRequiredService<UnitreeRobot>();

        await AnsiConsole.Status()
            .StartAsync("Connecting…", async _ => await robot.ConnectAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        switch (posture)
        {
            case "stand":
                AnsiConsole.MarkupLine("[yellow]Standing up. Keep clear of the robot.[/]");
                await robot.Sport.StandUpAsync(cancellationToken).ConfigureAwait(false);
                await robot.Sport.BalanceStandAsync(cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine("[green]Standing and ready for velocity commands.[/]");
                break;

            case "sit":
                await robot.Sport.SitAsync(cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine("[green]Sitting.[/]");
                break;

            case "damp":
                await robot.Sport.DampAsync(cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine("[green]Damping engaged. The robot will settle under gravity.[/]");
                break;

            case "recover":
                AnsiConsole.MarkupLine("[yellow]Attempting recovery stand. Ensure the robot has clear space.[/]");
                await robot.Sport.RecoveryStandAsync(cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine("[green]Recovery stand executed.[/]");
                break;

            default:
                AnsiConsole.MarkupLineInterpolated($"[red]Unknown posture '{posture}'.[/]");
                return 1;
        }

        return 0;
    }
}

/// <summary>
/// Drives the robot at a body-frame velocity for a fixed duration.
/// </summary>
internal static class MoveCommand
{
    internal static async Task<int> RunAsync(
        IServiceProvider provider,
        string[] args,
        CancellationToken cancellationToken)
    {
        // Only leading positional arguments are velocity components; anything starting with '-' belongs
        // to the configuration binder and must not be parsed as a number here.
        string[] positional = [.. args.TakeWhile(a => !a.StartsWith('-'))];

        if (positional.Length < 3)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] unitree move <forward m/s> <lateral m/s> <yaw rad/s> [[seconds]]");
            return 1;
        }

        if (!TryParse(positional[0], out float forward) ||
            !TryParse(positional[1], out float lateral) ||
            !TryParse(positional[2], out float yawRate))
        {
            AnsiConsole.MarkupLine("[red]Velocity components must be numbers.[/]");
            return 1;
        }

        double seconds = 2.0;

        if (positional.Length >= 4 && !double.TryParse(positional[3], CultureInfo.InvariantCulture, out seconds))
        {
            AnsiConsole.MarkupLine("[red]Duration must be a number of seconds.[/]");
            return 1;
        }

        if (seconds is <= 0 or > 60)
        {
            AnsiConsole.MarkupLine("[red]Duration must be between 0 and 60 seconds.[/]");
            return 1;
        }

        var robot = provider.GetRequiredService<UnitreeRobot>();

        await AnsiConsole.Status()
            .StartAsync("Connecting…", async _ => await robot.ConnectAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        var requested = new VelocityCommand(forward, lateral, yawRate);
        VelocityCommand clamped = requested.Clamp(robot.Options.Safety.Velocity);

        if (requested != clamped)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Clamped to the safety envelope:[/] {clamped.Forward:0.##}, {clamped.Lateral:0.##}, {clamped.YawRate:0.##}");
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[yellow]Moving for {seconds:0.#} s. Ctrl+C stops immediately.[/]");

        // The stream owns the command cadence, and its disposal sends a stop — so the robot halts
        // whether this completes normally, throws, or is cancelled. No command timeout is needed:
        // the duration is bounded here, and one is what used to cut every move short at 500 ms
        // regardless of how many seconds were asked for.
        using VelocityStream stream = robot.Sport.StartVelocityStream();
        stream.Command = clamped;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stream.Stop();
        }

        AnsiConsole.MarkupLine("[green]Stopped.[/]");
        return 0;

        static bool TryParse(string text, out float value) =>
            float.TryParse(text, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// An interactive chat session with the robot through the configured language model.
/// </summary>
internal static class AiCommand
{
    internal static async Task<int> RunAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var options = provider.GetRequiredService<AiOptions>();
        var robot = provider.GetRequiredService<UnitreeRobot>();

        try
        {
            options.Validate();
        }
        catch (OptionsValidationFailure ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]AI configuration error:[/] {ex.Message}");
            return 2;
        }

        await AnsiConsole.Status()
            .StartAsync("Connecting…", async _ => await robot.ConnectAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        var engine = provider.GetRequiredService<AiWorkflowEngine>();

        AnsiConsole.Write(new Rule($"[bold]{options.Provider} / {options.GetEffectiveModelId()}[/]").LeftJustified());

        AnsiConsole.MarkupLine(options.ExposeMotionFunctions
            ? "[yellow]Motion functions are exposed to the model.[/]"
            : "[grey]Motion functions are disabled; the model can observe but not move the robot.[/]");

        AnsiConsole.MarkupLine("[grey]Type 'exit' to quit, 'reset' to clear the conversation.[/]");
        AnsiConsole.WriteLine();

        while (!cancellationToken.IsCancellationRequested)
        {
            string input = AnsiConsole.Prompt(new TextPrompt<string>("[bold cyan]you>[/]").AllowEmpty());

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (input.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                engine.ResetConversation();
                AnsiConsole.MarkupLine("[grey]Conversation cleared.[/]");
                continue;
            }

            AnsiConsole.Markup("[bold green]robot>[/] ");

            try
            {
                await foreach (string chunk in engine.AskStreamingAsync(input, cancellationToken).ConfigureAwait(false))
                {
                    AnsiConsole.Write(new Text(chunk));
                }

                AnsiConsole.WriteLine();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failing model call should not end the session — the provider may simply be
                // rate-limiting, and the operator can retry or switch providers.
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLineInterpolated($"[red]Model call failed:[/] {ex.Message}");
            }

            AnsiConsole.WriteLine();
        }

        return 0;
    }
}
