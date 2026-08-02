using DepthAI.Devices;
using DepthAI.Pipelines;

namespace DepthAI.Backends;

/// <summary>Opsi saat membuka perangkat.</summary>
public sealed record DeviceOpenOptions
{
    /// <summary>Batas kecepatan USB; berguna untuk menghindari masalah kabel pada mode SuperSpeed.</summary>
    public UsbSpeed MaxUsbSpeed { get; init; } = UsbSpeed.SuperPlus;

    /// <summary>Berapa lama menunggu perangkat selesai boot.</summary>
    public TimeSpan BootTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Path firmware kustom; null memakai firmware bawaan native runtime.</summary>
    public string? FirmwarePath { get; init; }

    public static DeviceOpenOptions Default { get; } = new();
}

/// <summary>
/// Lapisan transport yang bisa ditukar antara SDK dan perangkat.
/// </summary>
/// <remarks>
/// Ada dua implementasi: <c>NativeBackend</c> yang berbicara dengan depthai-core lewat
/// P/Invoke, dan <c>SimulationBackend</c> yang menghasilkan data sintetis. Abstraksi ini
/// yang membuat seluruh ekosistem — CLI, wizard, sample, unit test — bisa dijalankan
/// dan dikembangkan tanpa kamera OAK terpasang.
/// </remarks>
public interface IDepthAiBackend : IDisposable
{
    /// <summary>Nama backend untuk log dan diagnostik.</summary>
    string Name { get; }

    /// <summary>
    /// True bila backend siap dipakai di mesin ini. Backend native mengembalikan false
    /// saat pustaka native tidak bisa dimuat.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Backend menghasilkan data sintetis, bukan dari hardware nyata.</summary>
    bool IsSimulation { get; }

    /// <summary>Memindai perangkat yang terhubung.</summary>
    IReadOnlyList<DeviceInfo> EnumerateDevices();

    /// <summary>Membuka sesi ke satu perangkat.</summary>
    Task<IDeviceSession> OpenAsync(
        DeviceInfo device,
        DeviceOpenOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Sesi aktif ke satu perangkat.</summary>
public interface IDeviceSession : IAsyncDisposable
{
    DeviceInfo Info { get; }

    DeviceCapabilities Capabilities { get; }

    bool IsRunning { get; }

    /// <summary>
    /// Mengunggah pipeline dan mulai menjalankannya. Paket akan dikirim ke
    /// <paramref name="onPacket"/> dari thread latar; implementasi harus memanggilnya
    /// secara berurutan per stream.
    /// </summary>
    Task StartAsync(Pipeline pipeline, Action<DevicePacket> onPacket, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    DeviceTelemetry ReadTelemetry();
}
