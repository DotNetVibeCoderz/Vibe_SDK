using DepthAI.Backends;
using DepthAI.Interop;
using DepthAI.Simulation;
using Microsoft.Extensions.Logging;

namespace DepthAI;

/// <summary>Cara SDK memilih backend.</summary>
public enum BackendSelection
{
    /// <summary>
    /// Pakai hardware bila runtime native tersedia, selain itu jatuh ke simulasi.
    /// Bawaan: aplikasi tetap bisa dikembangkan tanpa kamera terpasang.
    /// </summary>
    Auto,

    /// <summary>Wajib hardware; melempar bila runtime native tidak tersedia.</summary>
    NativeOnly,

    /// <summary>Selalu simulasi, walau ada hardware. Dipakai test dan demo.</summary>
    SimulationOnly,
}

/// <summary>Opsi global SDK.</summary>
public sealed record DepthAiOptions
{
    public BackendSelection Backend { get; init; } = BackendSelection.Auto;

    public SimulationOptions Simulation { get; init; } = SimulationOptions.Default;

    public DeviceOpenOptions DeviceOpen { get; init; } = DeviceOpenOptions.Default;

    /// <summary>Logger factory; null berarti tanpa logging.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    public static DepthAiOptions Default { get; } = new();

    /// <summary>Opsi yang memaksa mode simulasi — jalan pintas untuk test dan demo.</summary>
    public static DepthAiOptions Simulated { get; } = new() { Backend = BackendSelection.SimulationOnly };
}

/// <summary>Titik masuk statis untuk enumerasi perangkat dan pemilihan backend.</summary>
public static class DepthAi
{
    /// <summary>Versi rakitan SDK.</summary>
    public static string Version => typeof(DepthAi).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Versi depthai-core native, atau null bila runtime native tidak dimuat.</summary>
    public static string? NativeVersion => NativeRuntime.Version;

    /// <summary>True bila hardware sungguhan bisa dipakai pada mesin ini.</summary>
    public static bool IsNativeAvailable => NativeRuntime.IsAvailable;

    /// <summary>Penjelasan kenapa runtime native tidak tersedia, untuk ditampilkan ke pengguna.</summary>
    public static string? NativeUnavailableReason => NativeRuntime.UnavailableReason;

    /// <summary>Membuat backend sesuai opsi. Pemanggil bertanggung jawab membuangnya.</summary>
    public static IDepthAiBackend CreateBackend(DepthAiOptions? options = null)
    {
        options ??= DepthAiOptions.Default;

        switch (options.Backend)
        {
            case BackendSelection.SimulationOnly:
                return new SimulationBackend(
                    options.Simulation,
                    options.LoggerFactory?.CreateLogger<SimulationBackend>());

            case BackendSelection.NativeOnly:
            {
                var native = new NativeBackend(options.LoggerFactory?.CreateLogger<NativeBackend>());
                if (!native.IsAvailable)
                {
                    native.Dispose();
                    throw new DepthAiException(
                        $"BackendSelection.NativeOnly diminta tapi runtime native tidak tersedia: "
                        + $"{NativeRuntime.UnavailableReason}");
                }

                return native;
            }

            default:
            {
                var native = new NativeBackend(options.LoggerFactory?.CreateLogger<NativeBackend>());
                if (native.IsAvailable)
                {
                    return native;
                }

                native.Dispose();
                options.LoggerFactory?.CreateLogger(typeof(DepthAi))
                    .LogInformation(
                        "Runtime native DepthAI tidak tersedia ({Reason}); memakai backend simulasi.",
                        NativeRuntime.UnavailableReason);

                return new SimulationBackend(
                    options.Simulation,
                    options.LoggerFactory?.CreateLogger<SimulationBackend>());
            }
        }
    }

    /// <summary>Memindai perangkat yang terhubung.</summary>
    public static IReadOnlyList<Devices.DeviceInfo> ListDevices(DepthAiOptions? options = null)
    {
        using var backend = CreateBackend(options);
        return backend.EnumerateDevices();
    }

    /// <summary>
    /// Memindai bus USB untuk perangkat OAK yang secara fisik terpasang.
    /// </summary>
    /// <remarks>
    /// Berbeda dari <see cref="ListDevices"/>, pemindaian ini tidak melewati backend dan
    /// tetap bekerja walau runtime native tidak ada. Gunanya untuk membedakan dua keadaan
    /// yang sangat berbeda tapi terlihat sama dari <see cref="ListDevices"/>: benar-benar
    /// tidak ada kamera, versus ada kamera tapi pustaka nativenya belum terpasang.
    /// </remarks>
    public static IReadOnlyList<Devices.UsbDeviceDescriptor> ScanUsbDevices()
        => Devices.UsbDeviceScanner.Scan();

    /// <summary>
    /// Ringkasan keadaan yang bisa ditindaklanjuti, menggabungkan status runtime native
    /// dengan apa yang benar-benar terpasang di USB.
    /// </summary>
    public static string DescribeEnvironment()
    {
        if (IsNativeAvailable)
        {
            return $"Runtime native tersedia (depthai-core {NativeVersion}).";
        }

        var usb = ScanUsbDevices();

        if (usb.Count == 0)
        {
            return $"Runtime native tidak tersedia ({NativeUnavailableReason}) "
                + "dan tidak ada perangkat OAK terdeteksi di USB. Aplikasi berjalan dalam mode simulasi.";
        }

        var descriptions = string.Join(", ", usb.Select(d => d.Description));

        return $"Terdeteksi {usb.Count} perangkat OAK di USB ({descriptions}), "
            + $"tapi runtime native tidak tersedia ({NativeUnavailableReason}). "
            + "Perangkat tidak bisa dibuka sampai pustaka native terpasang; sementara itu aplikasi memakai simulasi.";
    }
}
