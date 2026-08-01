using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Unitree.Net.Cli.Commands;
using Unitree.Net.Core;
using Unitree.Net.Extensions.DependencyInjection;

namespace Unitree.Net.Cli;

/// <summary>
/// Entry point for the <c>unitree</c> command-line tool.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string command = args[0].ToLowerInvariant();
        string[] rest = args[1..];

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables("UNITREE_")
            .AddCommandLine(rest)
            .Build();

        // Diagnose deliberately runs before any DI wiring: its whole purpose is to work when the
        // configuration is wrong, which is precisely when building the container would throw.
        if (command == "diagnose")
        {
            return await DiagnoseCommand.RunAsync(configuration).ConfigureAwait(false);
        }

        // Scaffolding needs no robot and no configuration, so it runs before the container is built
        // for the same reason diagnose does.
        switch (command)
        {
            case "templates":
                return AutomationCommands.ListTemplates();

            case "new":
                return await AutomationCommands.NewProjectAsync(rest).ConfigureAwait(false);

            case "deploy":
                return await AutomationCommands.DeployAsync(rest, CancellationToken.None).ConfigureAwait(false);
        }

        var services = new ServiceCollection();

        // The machine-readable commands must emit nothing on stdout but their JSON. The console
        // logger writes there too, so a single info line is enough to make the output unparsable —
        // which is exactly what happened the first time the VS Code extension called `probe`.
        bool machineReadable = command is "probe" or "stream";

        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));

            if (machineReadable)
            {
                builder.ClearProviders();
                return;
            }

            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
        });

        try
        {
            services.AddUnitreeRobot(configuration);
            services.AddUnitreeAi(configuration);
        }
        catch (OptionsValidationFailure ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Configuration error:[/] {ex.Message}");
            return 2;
        }

        await using ServiceProvider provider = services.BuildServiceProvider();

        using var cancellation = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // Cancel rather than terminate: every command's cleanup path stops the robot, and abrupt
            // termination would leave it holding its last command.
            eventArgs.Cancel = true;
            AnsiConsole.MarkupLine("[yellow]Stopping…[/]");
            cancellation.Cancel();
        };

        try
        {
            return command switch
            {
                "monitor" => await MonitorCommand.RunAsync(provider, cancellation.Token).ConfigureAwait(false),
                "status" => await StatusCommand.RunAsync(provider, cancellation.Token).ConfigureAwait(false),
                "move" => await MoveCommand.RunAsync(provider, rest, cancellation.Token).ConfigureAwait(false),
                "stand" => await PostureCommand.RunAsync(provider, "stand", cancellation.Token).ConfigureAwait(false),
                "sit" => await PostureCommand.RunAsync(provider, "sit", cancellation.Token).ConfigureAwait(false),
                "damp" => await PostureCommand.RunAsync(provider, "damp", cancellation.Token).ConfigureAwait(false),
                "recover" => await PostureCommand.RunAsync(provider, "recover", cancellation.Token).ConfigureAwait(false),
                "ai" => await AiCommand.RunAsync(provider, cancellation.Token).ConfigureAwait(false),
                "probe" => await AutomationCommands.ProbeAsync(provider, cancellation.Token).ConfigureAwait(false),
                "stream" => await AutomationCommands.StreamAsync(provider, rest, cancellation.Token).ConfigureAwait(false),
                _ => UnknownCommand(command),
            };
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
        catch (UnitreeConnectionException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Connection failed:[/] {ex.Message}");
            AnsiConsole.MarkupLine("[grey]Run [bold]unitree diagnose[/] to check the transport and interfaces.[/]");
            return 3;
        }
        catch (UnitreeException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] {ex.Message}");
            return 4;
        }
    }

    private static int UnknownCommand(string command)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]Unknown command '{command}'.[/]");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        AnsiConsole.Write(new FigletText("unitree").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[bold]Unitree.Net command-line tool[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Command[/]")
            .AddColumn("[bold]Description[/]");

        table.AddRow("diagnose", "Check the transport, native library and network interfaces. Needs no robot.");
        table.AddRow("status", "Print a one-shot telemetry snapshot.");
        table.AddRow("monitor", "Live telemetry dashboard. Ctrl+C to stop.");
        table.AddRow("templates", "List the project templates as JSON.");
        table.AddRow("new", "Scaffold a project. --name, --output, and --template or --kind.");
        table.AddRow("probe", "Connect, print one telemetry snapshot as JSON, and exit.");
        table.AddRow("stream", "Stream telemetry as newline-delimited JSON. --interval ms.");
        table.AddRow("stand", "Stand the robot up and enter balanced standing.");
        table.AddRow("sit", "Sit the robot down.");
        table.AddRow("damp", "Enter damping mode — the safe way to end a session.");
        table.AddRow("recover", "Recover to standing after a fall.");
        table.AddRow("move [grey]<fwd> <lat> <yaw> [[seconds]][/]", "Drive at a body-frame velocity for a duration.");
        table.AddRow("ai", "Chat with the robot through the configured language model.");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Configuration comes from appsettings.json, appsettings.Local.json,[/]");
        AnsiConsole.MarkupLine("[grey]UNITREE_-prefixed environment variables, then --Key=Value arguments.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Example:[/] unitree monitor --Unitree:NetworkInterface=eth0");
    }
}
