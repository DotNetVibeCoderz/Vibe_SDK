using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Sensors;

namespace Unitree.Net.Cli.Commands;

/// <summary>
/// Prints a single telemetry snapshot and exits.
/// </summary>
internal static class StatusCommand
{
    internal static async Task<int> RunAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var robot = provider.GetRequiredService<UnitreeRobot>();
        var telemetry = provider.GetRequiredService<TelemetryHub>();

        await AnsiConsole.Status()
            .StartAsync("Connecting…", async _ => await robot.ConnectAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        TelemetrySnapshot? snapshot = telemetry.GetSnapshot();

        if (snapshot is null)
        {
            AnsiConsole.MarkupLine("[yellow]Connected, but no telemetry has arrived yet.[/]");
            return 1;
        }

        AnsiConsole.Write(TelemetryRenderer.BuildTable(robot, snapshot.Value));
        return 0;
    }
}

/// <summary>
/// Renders a live telemetry dashboard until cancelled.
/// </summary>
internal static class MonitorCommand
{
    internal static async Task<int> RunAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var robot = provider.GetRequiredService<UnitreeRobot>();
        var telemetry = provider.GetRequiredService<TelemetryHub>();

        await AnsiConsole.Status()
            .StartAsync("Connecting…", async _ => await robot.ConnectAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        AnsiConsole.MarkupLine("[grey]Press Ctrl+C to stop.[/]");
        AnsiConsole.WriteLine();

        var layout = new Table().Border(TableBorder.Rounded);

        try
        {
            await AnsiConsole.Live(layout)
                .AutoClear(false)
                .StartAsync(async context =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));

                    while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    {
                        TelemetrySnapshot? snapshot = telemetry.GetSnapshot();

                        if (snapshot is null)
                        {
                            continue;
                        }

                        // Rebuilding the table each tick rather than mutating rows keeps the refresh
                        // atomic; a partially updated table flickers badly in a terminal.
                        context.UpdateTarget(TelemetryRenderer.BuildTable(robot, snapshot.Value));
                    }
                })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C.
        }

        return 0;
    }
}

/// <summary>
/// Shared telemetry table rendering.
/// </summary>
internal static class TelemetryRenderer
{
    internal static Table BuildTable(UnitreeRobot robot, TelemetrySnapshot snapshot)
    {
        // Transport names embed their endpoint in square brackets, which is also Spectre's markup
        // syntax — anything derived from runtime state has to be escaped before it reaches a markup
        // string, or a perfectly valid transport name throws while being displayed.
        string transportName = Markup.Escape(robot.Participant.Transport.Name);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold]{Markup.Escape(robot.Model.ToString())}[/] via [grey]{transportName}[/]")
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        RobotSafetyOptions safety = robot.Options.Safety;

        table.AddRow("Connection", FormatConnection(robot.RefreshConnectionState()));

        table.AddRow(
            "Battery",
            FormatBattery(snapshot.Battery, safety.MinBatterySocPercent));

        table.AddRow(
            "Pack",
            $"{snapshot.Battery.PackVoltage.ToString("0.0", CultureInfo.InvariantCulture)} V, " +
            $"{snapshot.Battery.CurrentAmps.ToString("0.00", CultureInfo.InvariantCulture)} A, " +
            $"{snapshot.Battery.CycleCount} cycles");

        table.AddRow(
            "Cell imbalance",
            snapshot.Battery.HasCellImbalanceWarning
                ? $"[yellow]{snapshot.Battery.CellImbalanceMillivolts} mV[/]"
                : $"{snapshot.Battery.CellImbalanceMillivolts} mV");

        table.AddRow(
            "Orientation",
            $"roll {Degrees(snapshot.Orientation.Roll)}  pitch {Degrees(snapshot.Orientation.Pitch)}  yaw {Degrees(snapshot.Orientation.Yaw)}");

        table.AddRow(
            "Motor temp",
            FormatTemperature(snapshot.MaxMotorTemperatureCelsius, safety.MaxMotorTemperatureCelsius));

        table.AddRow("Feet loaded", $"{snapshot.FootContact.ContactCount} / 4");

        table.AddRow(
            "Odometry",
            $"x {snapshot.OdometryPosition.X.ToString("0.00", CultureInfo.InvariantCulture)} m, " +
            $"y {snapshot.OdometryPosition.Y.ToString("0.00", CultureInfo.InvariantCulture)} m");

        table.AddRow(
            "Velocity",
            $"{snapshot.Velocity.Length().ToString("0.00", CultureInfo.InvariantCulture)} m/s");

        table.AddRow(
            "Body height",
            $"{snapshot.BodyHeight.ToString("0.00", CultureInfo.InvariantCulture)} m");

        if (robot.LowLevel.IsEmergencyStopped)
        {
            table.AddRow("[red]Emergency stop[/]", "[red]LATCHED[/]");
        }

        return table;
    }

    private static string FormatConnection(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "[green]Connected[/]",
        ConnectionState.Stale => "[yellow]Stale[/]",
        ConnectionState.Connecting => "[yellow]Connecting[/]",
        _ => $"[red]{state}[/]",
    };

    private static string FormatBattery(BatteryStatus battery, int minimumPercent)
    {
        string suffix = battery.IsCharging ? " (charging)" : string.Empty;
        string text = $"{battery.StateOfChargePercent}%{suffix}";

        if (battery.StateOfChargePercent == 0)
        {
            return "[grey]not reported[/]";
        }

        return battery.StateOfChargePercent < minimumPercent
            ? $"[red]{text}[/]"
            : battery.StateOfChargePercent < minimumPercent * 2
                ? $"[yellow]{text}[/]"
                : $"[green]{text}[/]";
    }

    private static string FormatTemperature(int celsius, int limit)
    {
        string text = $"{celsius} °C";

        return celsius > limit
            ? $"[red]{text}[/]"
            : celsius > limit - 10
                ? $"[yellow]{text}[/]"
                : $"[green]{text}[/]";
    }

    private static string Degrees(float radians) =>
        float.RadiansToDegrees(radians).ToString("0.#", CultureInfo.InvariantCulture) + "°";
}
