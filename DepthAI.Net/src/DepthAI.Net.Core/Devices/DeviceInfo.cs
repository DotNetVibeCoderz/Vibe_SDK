namespace DepthAI.Devices;

/// <summary>Protokol koneksi perangkat OAK.</summary>
public enum DeviceProtocol
{
    Unknown = 0,
    Usb = 1,
    PoE = 2,
}

/// <summary>Kecepatan link USB yang dinegosiasikan.</summary>
public enum UsbSpeed
{
    Unknown = 0,
    Low = 1,
    Full = 2,
    High = 3,
    Super = 4,
    SuperPlus = 5,
}

/// <summary>Status perangkat pada saat enumerasi.</summary>
public enum DeviceState
{
    Unknown = 0,
    /// <summary>Perangkat terdeteksi dan siap dibuka.</summary>
    Available = 1,
    /// <summary>Perangkat sudah dibuka oleh proses lain.</summary>
    InUse = 2,
    /// <summary>Perangkat sedang boot firmware.</summary>
    Booting = 3,
    /// <summary>Perangkat sudah di-boot dan menjalankan pipeline.</summary>
    Booted = 4,
    /// <summary>Perangkat terdeteksi tapi gagal diinisialisasi.</summary>
    Faulty = 5,
}

/// <summary>
/// Kemampuan hardware yang dilaporkan perangkat. Dipakai untuk validasi pipeline
/// sebelum di-deploy, sehingga error muncul di host, bukan di tengah stream.
/// </summary>
public sealed record DeviceCapabilities
{
    /// <summary>Jumlah kamera warna (RGB) yang terpasang.</summary>
    public int ColorCameraCount { get; init; }

    /// <summary>Jumlah kamera mono; stereo depth butuh minimal dua.</summary>
    public int MonoCameraCount { get; init; }

    /// <summary>True bila perangkat punya pasangan stereo terkalibrasi.</summary>
    public bool SupportsStereoDepth { get; init; }

    /// <summary>True bila ada IMU on-board.</summary>
    public bool HasImu { get; init; }

    /// <summary>Jumlah SHAVE core yang tersedia untuk inferensi.</summary>
    public int ShaveCores { get; init; }

    /// <summary>Nama sensor per socket kamera, misal <c>CAM_A -> IMX378</c>.</summary>
    public IReadOnlyDictionary<CameraSocket, string> Sensors { get; init; }
        = new Dictionary<CameraSocket, string>();

    public static DeviceCapabilities Unknown { get; } = new();
}

/// <summary>
/// Deskriptor perangkat OAK hasil enumerasi. Immutable — snapshot pada saat scan.
/// </summary>
public sealed record DeviceInfo
{
    /// <summary>Serial number / MxId perangkat. Stabil lintas reboot, dipakai sebagai identitas.</summary>
    public required string SerialNumber { get; init; }

    /// <summary>Nama perangkat yang bisa dibaca manusia, misal <c>OAK-D-Pro</c>.</summary>
    public string Name { get; init; } = "OAK";

    /// <summary>Path koneksi: path USB, atau alamat IP untuk PoE.</summary>
    public string ConnectionPath { get; init; } = string.Empty;

    public DeviceProtocol Protocol { get; init; } = DeviceProtocol.Unknown;

    public DeviceState State { get; init; } = DeviceState.Unknown;

    /// <summary>Versi firmware yang berjalan; kosong bila perangkat belum di-boot.</summary>
    public string? FirmwareVersion { get; init; }

    public UsbSpeed UsbSpeed { get; init; } = UsbSpeed.Unknown;

    public DeviceCapabilities Capabilities { get; init; } = DeviceCapabilities.Unknown;

    /// <summary>True bila perangkat ini disintesis oleh backend simulasi, bukan hardware nyata.</summary>
    public bool IsSimulated { get; init; }

    public override string ToString()
        => $"{Name} [{SerialNumber}] {Protocol} {State}{(IsSimulated ? " (simulated)" : string.Empty)}";
}
