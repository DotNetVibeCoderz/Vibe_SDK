// The pollen CLI: the fastest way to check a robot is reachable, list what the SDK knows about it,
// and scaffold a project without opening the wizard.
//
//   pollen doctor                       - diagnose a connection without needing a robot
//   pollen joints reachy-mini           - print the joint model
//   pollen templates [filter]           - list the project templates
//   pollen new <template> <name> [dir]  - scaffold a project
//   pollen sim <robot>                  - run the simulator headless and print telemetry
//   pollen mini <command>               - drive a Reachy Mini
//   pollen duck <command>               - drive a MicroDuck
using PollenRobotics.Net.Core;
using PollenRobotics.Net.Cli.Commands;
using Spectre.Console;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "doctor" => await DoctorCommand.RunAsync(args[1..]),
        "joints" => JointsCommand.Run(args[1..]),
        "templates" => TemplatesCommand.Run(args[1..]),
        "new" => await NewCommand.RunAsync(args[1..]),
        "sim" => await SimCommand.RunAsync(args[1..]),
        "mini" => await MiniCommand.RunAsync(args[1..]),
        "duck" => await DuckCommand.RunAsync(args[1..]),
        "version" => Version(),
        _ => Unknown(args[0]),
    };
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
    return 130;
}
catch (Exception ex)
{
    // Runtime strings reach Spectre as markup, and a transport name containing square brackets is
    // markup syntax. Escaping is not optional here - it has crashed this CLI before.
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");

    if (args.Contains("--verbose"))
    {
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
    }

    return 1;
}

static int Version()
{
    AnsiConsole.MarkupLine($"[bold]PollenRobotics.Net[/] {SdkInfo.Version}");
    AnsiConsole.MarkupLine($"[dim]{SdkInfo.Credit}[/]");
    return 0;
}

static int Unknown(string command)
{
    AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(command)}'.[/]");
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    AnsiConsole.Write(new FigletText("pollen").Color(Color.SpringGreen3));
    AnsiConsole.MarkupLine("[dim]Unofficial .NET SDK for Pollen Robotics - Gravicode Studios[/]");
    AnsiConsole.WriteLine();

    var table = new Table().Border(TableBorder.None).HideHeaders();
    table.AddColumn(new TableColumn("command").PadRight(4));
    table.AddColumn("what it does");

    table.AddRow("[green]doctor[/] [dim][[robot]][/]", "Check whether a robot is reachable, and explain it if not");
    table.AddRow("[green]joints[/] <robot>", "Print the joint model with limits");
    table.AddRow("[green]templates[/] [dim][[filter]][/]", "List the project templates");
    table.AddRow("[green]new[/] <template> <name>", "Scaffold a project from a template");
    table.AddRow("[green]sim[/] <robot>", "Run the simulator headless and print telemetry");
    table.AddRow("[green]mini[/] <command>", "Drive a Reachy Mini: status, wake, sleep, look, dance");
    table.AddRow("[green]duck[/] <command>", "Drive a MicroDuck: status, init, walk, trick, relax");
    table.AddRow("[green]version[/]", "Print the version");

    AnsiConsole.Write(table);
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[dim]Robots: reachy-mini, microduck, reachy2. Add --sim to drive the simulator.[/]");
}
