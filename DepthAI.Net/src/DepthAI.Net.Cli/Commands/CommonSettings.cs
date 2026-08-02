using System.ComponentModel;
using DepthAI;
using DepthAI.Simulation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DepthAI.Cli.Commands;

/// <summary>Opsi yang dipakai setiap perintah yang menyentuh perangkat.</summary>
public class DeviceSettings : CommandSettings
{
    [CommandOption("-d|--device <SERIAL>")]
    [Description("Serial perangkat. Bila dihilangkan, perangkat pertama yang tersedia dipakai.")]
    public string? Serial { get; init; }

    [CommandOption("--simulate")]
    [Description("Paksa backend simulasi, walau ada hardware terpasang.")]
    public bool Simulate { get; init; }

    [CommandOption("--require-hardware")]
    [Description("Gagal bila runtime native tidak tersedia, alih-alih jatuh ke simulasi.")]
    public bool RequireHardware { get; init; }

    /// <summary>Menerjemahkan flag CLI menjadi opsi SDK.</summary>
    public DepthAiOptions ToOptions() => new()
    {
        Backend = (Simulate, RequireHardware) switch
        {
            (true, _) => BackendSelection.SimulationOnly,
            (_, true) => BackendSelection.NativeOnly,
            _ => BackendSelection.Auto,
        },
        Simulation = SimulationOptions.Default,
    };

    public override Spectre.Console.ValidationResult Validate()
        => Simulate && RequireHardware
            ? Spectre.Console.ValidationResult.Error("--simulate dan --require-hardware saling bertentangan.")
            : Spectre.Console.ValidationResult.Success();
}

/// <summary>Utilitas bersama seluruh perintah CLI.</summary>
internal static class CliHelpers
{
    /// <summary>
    /// Membuka perangkat sesuai opsi, dengan pesan yang jelas bila tidak ada yang cocok.
    /// </summary>
    public static async Task<DepthAiDevice> OpenAsync(DeviceSettings settings, CancellationToken cancellationToken)
    {
        var options = settings.ToOptions();

        var device = string.IsNullOrWhiteSpace(settings.Serial)
            ? await DepthAiDevice.OpenAsync(options, cancellationToken)
            : await DepthAiDevice.OpenBySerialAsync(settings.Serial, options, cancellationToken);

        if (device.IsSimulated && !settings.Simulate)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Catatan:[/] tidak ada hardware terdeteksi, memakai perangkat simulasi. "
                + "Pakai [grey]--require-hardware[/] agar gagal alih-alih menyimulasikan.");
        }

        return device;
    }

    /// <summary>Menampilkan banner konsisten di seluruh perintah.</summary>
    public static void WriteBanner()
    {
        AnsiConsole.Write(new Rule("[bold cyan]DepthAI.Net[/]").LeftJustified());
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F2} KB",
        _ => $"{bytes} B",
    };
}
