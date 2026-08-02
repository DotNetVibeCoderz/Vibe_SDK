using DepthAI.Inference;
using DepthAI.Streaming;

namespace DepthAI.Backends;

/// <summary>Jenis payload yang dibawa <see cref="DevicePacket"/>.</summary>
public enum PacketKind
{
    /// <summary>Piksel gambar mentah.</summary>
    Image,
    /// <summary>Kedalaman 16-bit little-endian, milimeter.</summary>
    Depth,
    /// <summary>Tensor keluaran neural network.</summary>
    NeuralTensors,
    /// <summary>Bitstream video terkompresi.</summary>
    Encoded,
    /// <summary>Laporan IMU.</summary>
    Imu,
}

/// <summary>
/// Satu unit data mentah dari perangkat, sebelum diterjemahkan menjadi tipe publik.
/// Ini adalah antarmuka sempit yang harus dipenuhi backend — semua semantik frame,
/// pooling, dan parsing terjadi di lapisan atasnya.
/// </summary>
public sealed class DevicePacket
{
    public required string StreamName { get; init; }

    public required PacketKind Kind { get; init; }

    /// <summary>Byte payload. Hanya <see cref="PayloadLength"/> byte pertama yang valid.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    public int PayloadLength => Payload.Length;

    public int Width { get; init; }

    public int Height { get; init; }

    public PixelFormat Format { get; init; } = PixelFormat.Unknown;

    public long SequenceNumber { get; init; }

    public TimeSpan DeviceTimestamp { get; init; }

    /// <summary>Tensor keluaran; hanya terisi bila <see cref="Kind"/> adalah <see cref="PacketKind.NeuralTensors"/>.</summary>
    public IReadOnlyDictionary<string, Tensor>? Tensors { get; init; }

    /// <summary>Pembacaan IMU; hanya terisi bila <see cref="Kind"/> adalah <see cref="PacketKind.Imu"/>.</summary>
    public ImuReading? Imu { get; init; }
}

/// <summary>Satu sampel IMU.</summary>
public sealed record ImuReading
{
    /// <summary>Percepatan linear, m/s².</summary>
    public (float X, float Y, float Z) Accelerometer { get; init; }

    /// <summary>Kecepatan sudut, rad/s.</summary>
    public (float X, float Y, float Z) Gyroscope { get; init; }

    /// <summary>Quaternion orientasi bila rotation vector diaktifkan.</summary>
    public (float I, float J, float K, float Real)? Rotation { get; init; }

    public TimeSpan Timestamp { get; init; }
}

/// <summary>Telemetri kesehatan perangkat.</summary>
public sealed record DeviceTelemetry
{
    public float ChipTemperatureCelsius { get; init; }

    public float LeonCssUsagePercent { get; init; }

    public float LeonMssUsagePercent { get; init; }

    public long DdrUsedBytes { get; init; }

    public long DdrTotalBytes { get; init; }

    public static DeviceTelemetry Empty { get; } = new();
}
