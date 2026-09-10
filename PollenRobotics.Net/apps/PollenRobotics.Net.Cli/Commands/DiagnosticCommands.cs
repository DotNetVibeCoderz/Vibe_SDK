using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.MicroDuck;
using PollenRobotics.Net.ReachyMini;
using PollenRobotics.Net.Reachy2;
using PollenRobotics.Net.Transport;
using Spectre.Console;

namespace PollenRobotics.Net.Cli.Commands;

/// <summary>
/// The first thing to run when a robot will not connect.
/// </summary>
/// <remarks>
/// Needs no robot. Most connection failures are one of four things - the daemon is not running, the
/// host name does not resolve, the port is closed, or the code is not on the machine it thinks it
/// is - and each of those has a different fix. Reporting them separately saves the guessing.
/// </remarks>
internal static class DoctorCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? only = args.FirstOrDefault(a => !a.StartsWith('-'));

        AnsiConsole.MarkupLine("[bold]Environment[/]");
        AnsiConsole.MarkupLine($"  .NET      {Environment.Version}");
        AnsiConsole.MarkupLine($"  OS        {Markup.Escape(Environment.OSVersion.VersionString)}");
        AnsiConsole.MarkupLine($"  Machine   {Markup.Escape(Environment.MachineName)}");
        AnsiConsole.WriteLine();

        bool anyReachable = false;

        if (only is null or "reachy-mini" or "reachymini" or "mini")
        {
            anyReachable |= await CheckReachyMiniAsync();
        }

        if (only is null or "microduck" or "duck")
        {
            anyReachable |= CheckMicroDuck();
        }

        if (only is null or "reachy2" or "reachy-2")
        {
            anyReachable |= await CheckReachy2Async(args);
        }

        AnsiConsole.WriteLine();

        if (!anyReachable)
        {
            AnsiConsole.MarkupLine("[yellow]No robot answered.[/] That is expected on a machine with no hardware attached.");
            AnsiConsole.MarkupLine("Everything in this SDK runs against the simulator: add [green]--sim[/] to any command,");
            AnsiConsole.MarkupLine("or try [green]pollen sim reachy-mini[/].");
        }

        return 0;
    }

    private static async Task<bool> CheckReachyMiniAsync()
    {
        AnsiConsole.MarkupLine("[bold]Reachy Mini[/]");

        foreach (ReachyMiniOptions options in new[]
        {
            ReachyMiniOptions.Default with { ConnectionMode = ReachyMiniConnectionMode.LocalhostOnly },
            ReachyMiniOptions.Default with { ConnectionMode = ReachyMiniConnectionMode.Network },
        })
        {
            using var http = new HttpJsonClient(options.BaseAddress, TimeSpan.FromSeconds(2));
            bool answered = await http.PingAsync("status");

            if (answered)
            {
                AnsiConsole.MarkupLine($"  [green]ok[/]    daemon answering at {Markup.Escape(options.BaseAddress.ToString())}");
                return true;
            }

            AnsiConsole.MarkupLine($"  [dim]--[/]    nothing at {Markup.Escape(options.BaseAddress.ToString())}");
        }

        AnsiConsole.MarkupLine("        [dim]Start the daemon on the machine the robot is plugged into, or check that[/]");
        AnsiConsole.MarkupLine("        [dim]reachy-mini.local resolves from here.[/]");
        return false;
    }

    private static bool CheckMicroDuck()
    {
        AnsiConsole.MarkupLine("[bold]MicroDuck[/]");

        MicroDuckOptions options = MicroDuckOptions.Default;

        if (File.Exists(options.SocketPath))
        {
            AnsiConsole.MarkupLine($"  [green]ok[/]    socket present at {Markup.Escape(options.SocketPath)}");
            AnsiConsole.MarkupLine("        [dim]Mutating calls are gated on uid/gid; check allow_uids in robotd.toml if they fail.[/]");
            return true;
        }

        AnsiConsole.MarkupLine($"  [dim]--[/]    no socket at {Markup.Escape(options.SocketPath)}");

        if (!OperatingSystem.IsLinux())
        {
            // Worth saying explicitly: robotd only ever exists on the duck, so a developer machine
            // finding nothing here is the normal case rather than a fault.
            AnsiConsole.MarkupLine("        [dim]robotd runs on the duck itself. From a development machine, forward the[/]");
            AnsiConsole.MarkupLine("        [dim]socket over SSH or use MicroDuckOptions.ForTcp against a bridge.[/]");
        }

        return false;
    }

    private static async Task<bool> CheckReachy2Async(string[] args)
    {
        string host = ReadOption(args, "--host") ?? "localhost";
        AnsiConsole.MarkupLine("[bold]Reachy 2[/]");

        var options = Reachy2Options.ForHost(host);

        try
        {
            await using var client = new Reachy2Client(options with { RequestTimeout = TimeSpan.FromSeconds(2) });
            await client.ConnectAsync();

            AnsiConsole.MarkupLine($"  [green]ok[/]    {Markup.Escape(client.RobotName)} at {Markup.Escape(options.Address.ToString())}");
            AnsiConsole.MarkupLine($"        hardware {Markup.Escape(client.Info?.VersionHard ?? "?")}, software {Markup.Escape(client.Info?.VersionSoft ?? "?")}");
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [dim]--[/]    {Markup.Escape(options.Address.ToString())}: {Markup.Escape(Shorten(ex.Message))}");
            AnsiConsole.MarkupLine("        [dim]Pass --host <address> to check a different robot.[/]");
            return false;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Shorten(string message)
    {
        int newline = message.IndexOf('\n');
        string firstLine = newline >= 0 ? message[..newline] : message;
        return firstLine.Length <= 120 ? firstLine : firstLine[..120] + "...";
    }
}

/// <summary>Prints a robot's joint model.</summary>
internal static class JointsCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Which robot?[/] reachy-mini, microduck or reachy2.");
            return 2;
        }

        if (!RobotNames.TryParse(args[0], out RobotKind kind))
        {
            AnsiConsole.MarkupLine($"[red]Unknown robot '{Markup.Escape(args[0])}'.[/]");
            return 2;
        }

        RobotDescription description = RobotCatalog.For(kind);

        var table = new Table().Border(TableBorder.Rounded).Title($"[bold]{description.DisplayName}[/]");
        table.AddColumn(new TableColumn("#").RightAligned());
        table.AddColumn("joint");
        table.AddColumn(new TableColumn("lower").RightAligned());
        table.AddColumn(new TableColumn("upper").RightAligned());
        table.AddColumn(new TableColumn("range").RightAligned());

        foreach (JointDescriptor joint in description.Joints)
        {
            table.AddRow(
                joint.Index.ToString(),
                joint.Name,
                $"{joint.Lower.Degrees:0.#}",
                $"{joint.Upper.Degrees:0.#}",
                $"{joint.Upper.Degrees - joint.Lower.Degrees:0.#}");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{description.JointCount} actuated joints. Limits in degrees, wire order as shown.[/]");

        if (kind == RobotKind.ReachyMini)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Pose limits bind before these do:[/]");
            AnsiConsole.MarkupLine("  head pitch and roll  [bold]+/-40 deg[/]");
            AnsiConsole.MarkupLine("  body yaw             [bold]+/-160 deg[/]");
            AnsiConsole.MarkupLine("  head yaw             [bold]within 65 deg of body yaw[/]");
        }

        return 0;
    }
}

/// <summary>Maps the names people type onto <see cref="RobotKind"/>.</summary>
internal static class RobotNames
{
    public static bool TryParse(string value, out RobotKind kind)
    {
        switch (value.ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty))
        {
            case "reachymini" or "mini":
                kind = RobotKind.ReachyMini;
                return true;

            case "microduck" or "duck":
                kind = RobotKind.MicroDuck;
                return true;

            case "reachy2" or "reachytwo":
                kind = RobotKind.Reachy2;
                return true;

            default:
                kind = default;
                return false;
        }
    }
}
