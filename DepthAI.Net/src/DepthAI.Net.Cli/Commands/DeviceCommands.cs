using System.ComponentModel;
using System.Text.Json;
using DepthAI.Devices;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DepthAI.Cli.Commands;

/// <summary>Menampilkan versi SDK dan status runtime native.</summary>
public sealed class InfoCommand : Command<InfoCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        CliHelpers.WriteBanner();

        var table = new Table().Border(TableBorder.Rounded).HideHeaders();
        table.AddColumn("k");
        table.AddColumn("v");

        table.AddRow("Versi SDK", DepthAi.Version);
        table.AddRow("Runtime .NET", Environment.Version.ToString());
        table.AddRow("Platform", $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} "
            + $"({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");

        if (DepthAi.IsNativeAvailable)
        {
            table.AddRow("Runtime native", $"[green]tersedia[/] (depthai-core {DepthAi.NativeVersion})");
        }
        else
        {
            table.AddRow("Runtime native", "[yellow]tidak tersedia[/]");
            table.AddRow("Alasan", $"[grey]{DepthAi.NativeUnavailableReason}[/]");
        }

        // Pemindaian USB dilakukan terpisah dari backend supaya perbedaan antara
        // "tidak ada kamera" dan "ada kamera tapi pustaka native belum terpasang"
        // terlihat jelas — dua keadaan itu butuh tindakan yang sama sekali berbeda.
        var usbDevices = DepthAi.ScanUsbDevices();

        table.AddRow("Perangkat USB", usbDevices.Count == 0
            ? "[grey]tidak ada perangkat OAK terdeteksi[/]"
            : $"[green]{usbDevices.Count} terdeteksi[/]");

        foreach (var device in usbDevices)
        {
            // Serial hanya muncul setelah perangkat di-boot; sebelum itu MyriadX
            // memakai instance USB generik, bukan MxId-nya.
            var serial = device.SerialNumber is null ? string.Empty : $", MxId {device.SerialNumber}";

            table.AddRow(
                string.Empty,
                $"[grey]{device.Description} — {device.VendorId:X4}:{device.ProductId:X4}, {device.State}{serial}[/]");
        }

        if (!DepthAi.IsNativeAvailable)
        {
            table.AddRow("Dampak", usbDevices.Count == 0
                ? "[grey]Perintah berjalan pada perangkat simulasi.[/]"
                : "[yellow]Perangkat fisik tidak bisa dibuka sampai pustaka native terpasang; "
                    + "perintah memakai simulasi.[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Gravicode Studios — dipimpin Kang Fadhil[/]");
        return 0;
    }
}

/// <summary>Menampilkan semua perangkat yang terdeteksi.</summary>
public sealed class DeviceListCommand : Command<DeviceListCommand.Settings>
{
    public sealed class Settings : DeviceSettings
    {
        [CommandOption("--json")]
        [Description("Keluarkan sebagai JSON, untuk dipipa ke tool lain.")]
        public bool Json { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var devices = DepthAi.ListDevices(settings.ToOptions());

        if (settings.Json)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(devices, DeviceJsonContext.Default.IReadOnlyListDeviceInfo));
            return 0;
        }

        var usbDevices = DepthAi.ScanUsbDevices();

        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Tidak ada perangkat yang bisa dibuka.[/]");
            AnsiConsole.MarkupLine("[grey]Periksa kabel USB, daya perangkat, dan izin udev pada Linux.[/]");
            return 1;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Nama");
        table.AddColumn("Serial");
        table.AddColumn("Protokol");
        table.AddColumn("Status");
        table.AddColumn("Firmware");
        table.AddColumn("Kemampuan");

        foreach (var device in devices)
        {
            var capabilities = new List<string>();
            if (device.Capabilities.ColorCameraCount > 0)
            {
                capabilities.Add($"{device.Capabilities.ColorCameraCount}x RGB");
            }

            if (device.Capabilities.MonoCameraCount > 0)
            {
                capabilities.Add($"{device.Capabilities.MonoCameraCount}x mono");
            }

            if (device.Capabilities.SupportsStereoDepth)
            {
                capabilities.Add("stereo");
            }

            if (device.Capabilities.HasImu)
            {
                capabilities.Add("IMU");
            }

            table.AddRow(
                device.IsSimulated ? $"[grey]{device.Name}[/]" : device.Name,
                device.SerialNumber,
                device.Protocol.ToString(),
                device.State == DeviceState.Available ? "[green]Available[/]" : device.State.ToString(),
                device.FirmwareVersion ?? "-",
                capabilities.Count > 0 ? string.Join(", ", capabilities) : "-");
        }

        AnsiConsole.Write(table);

        // Perangkat fisik dilaporkan terpisah: yang terpasang di USB belum tentu
        // sama dengan yang bisa dibuka, dan selisih itulah yang perlu dilihat pengguna.
        if (usbDevices.Count > 0 && !DepthAi.IsNativeAvailable)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Terpasang di USB tapi belum bisa dibuka:[/] {usbDevices.Count} perangkat");

            foreach (var device in usbDevices)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"  [grey]{device.Description} — {device.VendorId:X4}:{device.ProductId:X4} ({device.State})[/]");
            }

            AnsiConsole.MarkupLineInterpolated(
                $"[grey]{DepthAi.NativeUnavailableReason}[/]");
        }

        return 0;
    }
}

/// <summary>Menampilkan detail dan telemetri satu perangkat.</summary>
public sealed class DeviceInfoCommand : AsyncCommand<DeviceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DeviceSettings settings, CancellationToken cancellationToken)
    {
        await using var device = await CliHelpers.OpenAsync(settings, cancellationToken);

        var info = device.Info;
        var telemetry = device.ReadTelemetry();

        var table = new Table().Border(TableBorder.Rounded).HideHeaders();
        table.AddColumn("k");
        table.AddColumn("v");

        table.AddRow("Nama", info.Name);
        table.AddRow("Serial", info.SerialNumber);
        table.AddRow("Protokol", $"{info.Protocol} ({info.UsbSpeed})");
        table.AddRow("Koneksi", info.ConnectionPath);
        table.AddRow("Firmware", info.FirmwareVersion ?? "-");
        table.AddRow("Kamera warna", info.Capabilities.ColorCameraCount.ToString());
        table.AddRow("Kamera mono", info.Capabilities.MonoCameraCount.ToString());
        table.AddRow("Stereo depth", info.Capabilities.SupportsStereoDepth ? "ya" : "tidak");
        table.AddRow("IMU", info.Capabilities.HasImu ? "ya" : "tidak");
        table.AddRow("SHAVE core", info.Capabilities.ShaveCores.ToString());

        if (info.Capabilities.Sensors.Count > 0)
        {
            table.AddRow("Sensor", string.Join(", ", info.Capabilities.Sensors.Select(s => $"{s.Key}={s.Value}")));
        }

        table.AddRow("Suhu chip", $"{telemetry.ChipTemperatureCelsius:F1} °C");
        table.AddRow("DDR terpakai",
            $"{CliHelpers.FormatBytes(telemetry.DdrUsedBytes)} / {CliHelpers.FormatBytes(telemetry.DdrTotalBytes)}");

        AnsiConsole.Write(table);
        return 0;
    }
}

/// <summary>Memantau kejadian hotplug sampai dibatalkan.</summary>
public sealed class DeviceWatchCommand : AsyncCommand<DeviceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DeviceSettings settings, CancellationToken cancellationToken)
    {

        await using var watcher = new DeviceWatcher(settings.ToOptions());
        watcher.PollInterval = TimeSpan.FromSeconds(1);

        watcher.DeviceConnected += (_, e) =>
            AnsiConsole.MarkupLineInterpolated($"[green]+[/] {DateTime.Now:HH:mm:ss}  terhubung   {e.Device}");

        watcher.DeviceDisconnected += (_, e) =>
            AnsiConsole.MarkupLineInterpolated($"[red]-[/] {DateTime.Now:HH:mm:ss}  terputus    {e.Device}");

        watcher.DeviceStateChanged += (_, e) =>
            AnsiConsole.MarkupLineInterpolated($"[yellow]~[/] {DateTime.Now:HH:mm:ss}  status      {e.Device}");

        AnsiConsole.MarkupLine("[grey]Memantau perangkat. Tekan Ctrl+C untuk berhenti.[/]");
        watcher.Start();

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[grey]Berhenti memantau.[/]");
        }

        return 0;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(IReadOnlyList<DeviceInfo>))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class DeviceJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
