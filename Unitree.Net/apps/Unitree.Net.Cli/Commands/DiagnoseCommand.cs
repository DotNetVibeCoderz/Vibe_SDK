using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Unitree.Net.Core;
using Unitree.Net.Interop;

namespace Unitree.Net.Cli.Commands;

/// <summary>
/// Checks the local environment without needing a robot.
/// </summary>
/// <remarks>
/// Written to be the first thing anyone runs when the robot "doesn't connect". Almost every such report
/// turns out to be one of three things: the wrong network interface, the native library missing, or
/// multicast being filtered — and this command distinguishes them.
/// </remarks>
internal static class DiagnoseCommand
{
    internal static Task<int> RunAsync(IConfiguration configuration)
    {
        AnsiConsole.Write(new Rule("[bold]Unitree.Net diagnostics[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var options = new UnitreeOptions();
        configuration.GetSection(UnitreeOptions.SectionName).Bind(options);

        bool allWell = true;

        allWell &= ReportConfiguration(options);
        AnsiConsole.WriteLine();

        allWell &= ReportNativeLibrary(options);
        AnsiConsole.WriteLine();

        allWell &= ReportNetworkInterfaces(options);
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Rule().LeftJustified());

        AnsiConsole.MarkupLine(allWell
            ? "[green]All checks passed.[/] If the robot still does not appear, confirm it is powered and on the same subnet."
            : "[yellow]Some checks reported problems.[/] Address them before trying to connect.");

        return Task.FromResult(allWell ? 0 : 1);
    }

    private static bool ReportConfiguration(UnitreeOptions options)
    {
        var table = new Table()
            .Title("[bold]Configuration[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Setting")
            .AddColumn("Value");

        table.AddRow("Model", options.Model.ToString());
        table.AddRow("Transport", options.Transport.ToString());
        table.AddRow(
            "NetworkInterface",
            string.IsNullOrWhiteSpace(options.NetworkInterface)
                ? "[yellow]<not set>[/]"
                : Markup.Escape(options.NetworkInterface));
        table.AddRow("DomainId", options.DomainId.ToString());
        table.AddRow("Control rate", $"{options.GetEffectiveControlFrequencyHz()} Hz");

        if (options.Transport == DdsTransportKind.ManagedMulticast)
        {
            table.AddRow("Multicast group", Markup.Escape($"{options.MulticastAddress}:{options.MulticastPort}"));
        }

        AnsiConsole.Write(table);

        bool ok = true;

        try
        {
            options.Validate();
        }
        catch (OptionsValidationFailure ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]✗ Configuration invalid:[/] {ex.Message}");
            ok = false;
        }

        if (string.IsNullOrWhiteSpace(options.NetworkInterface))
        {
            AnsiConsole.MarkupLine(
                "[yellow]![/] No network interface configured. On a multi-homed host the choice is arbitrary " +
                "and is the most common cause of a silent connection failure.");
        }

        return ok;
    }

    private static bool ReportNativeLibrary(UnitreeOptions options)
    {
        AnsiConsole.MarkupLine("[bold]Native library[/]");

        string version = CycloneDdsTransport.GetNativeVersion();
        bool available = version.Length > 0;

        if (available)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]✓[/] unitree_net_native loaded: {version}");
            return true;
        }

        if (options.Transport == DdsTransportKind.CycloneNative)
        {
            AnsiConsole.MarkupLine(
                "[red]✗[/] unitree_net_native could not be loaded, but the configured transport requires it.");
            AnsiConsole.MarkupLine("  Build it as described in [bold]native/README.md[/], or switch to");
            AnsiConsole.MarkupLine("  [bold]Unitree:Transport=ManagedMulticast[/] for host-only development.");
            return false;
        }

        AnsiConsole.MarkupLine(
            $"[grey]-[/] unitree_net_native is not loaded, which is fine for the {options.Transport} transport.");
        AnsiConsole.MarkupLine("  Note that this transport cannot talk to robot firmware.");
        return true;
    }

    private static bool ReportNetworkInterfaces(UnitreeOptions options)
    {
        var table = new Table()
            .Title("[bold]Network interfaces[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Name")
            .AddColumn("Status")
            .AddColumn("Multicast")
            .AddColumn("IPv4 address");

        bool foundConfigured = string.IsNullOrWhiteSpace(options.NetworkInterface);
        bool anyUsable = false;

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            string? address = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address.ToString();

            if (address is null)
            {
                continue;
            }

            bool isUp = nic.OperationalStatus == OperationalStatus.Up;
            bool isConfigured = string.Equals(nic.Name, options.NetworkInterface, StringComparison.OrdinalIgnoreCase);

            if (isConfigured)
            {
                foundConfigured = true;
            }

            if (isUp && nic.SupportsMulticast && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            {
                anyUsable = true;
            }

            string name = isConfigured ? $"[bold cyan]{Markup.Escape(nic.Name)}[/]" : Markup.Escape(nic.Name);

            table.AddRow(
                name,
                isUp ? "[green]Up[/]" : "[grey]Down[/]",
                nic.SupportsMulticast ? "[green]yes[/]" : "[red]no[/]",
                Markup.Escape(address));
        }

        AnsiConsole.Write(table);

        bool ok = true;

        if (!foundConfigured)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]✗[/] The configured interface '{options.NetworkInterface}' was not found, or has no IPv4 address.");
            ok = false;
        }

        if (!anyUsable)
        {
            AnsiConsole.MarkupLine("[red]✗[/] No interface is up, non-loopback and multicast-capable.");
            ok = false;
        }

        return ok;
    }
}
