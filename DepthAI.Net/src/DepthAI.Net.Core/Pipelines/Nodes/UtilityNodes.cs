using DepthAI.Devices;
using DepthAI.Streaming;

namespace DepthAI.Pipelines.Nodes;

/// <summary>Profil kompresi untuk <see cref="VideoEncoderNode"/>.</summary>
public enum VideoProfile
{
    MjpegLossy,
    MjpegLossless,
    H264Main,
    H265Main,
}

/// <summary>
/// Mengubah bentuk frame di perangkat: resize, crop, rotate, konversi format.
/// Menjalankannya di perangkat menghemat bandwidth USB dan CPU host.
/// </summary>
public sealed class ImageManipNode : PipelineNode
{
    public ImageManipNode(string name) : base(name)
    {
        Input = DefineInput("input");
        Out = DefineOutput("out");
    }

    public override string NodeType => "ImageManip";

    public NodeInput Input { get; }

    public NodeOutput Out { get; }

    /// <summary>Lebar keluaran; 0 mempertahankan lebar masukan.</summary>
    public int ResizeWidth { get; set; }

    public int ResizeHeight { get; set; }

    /// <summary>
    /// Menyimpan rasio aspek dengan letterbox alih-alih meregangkan. Model deteksi
    /// yang dilatih pada gambar letterbox akan meleset bila masukannya diregangkan.
    /// </summary>
    public bool KeepAspectRatio { get; set; } = true;

    /// <summary>Crop ternormalisasi (0..1) yang diterapkan sebelum resize; null berarti frame penuh.</summary>
    public (float XMin, float YMin, float XMax, float YMax)? Crop { get; set; }

    /// <summary>Rotasi searah jarum jam dalam derajat; harus kelipatan 90.</summary>
    public int RotateDegrees { get; set; }

    public PixelFormat OutputFormat { get; set; } = PixelFormat.Bgr888;

    public bool Interleaved { get; set; } = true;

    public ImageManipNode WithResize(int width, int height)
    {
        ResizeWidth = width;
        ResizeHeight = height;
        return this;
    }

    internal override IDictionary<string, object?> GetProperties()
    {
        var props = new Dictionary<string, object?>
        {
            ["resizeWidth"] = ResizeWidth,
            ["resizeHeight"] = ResizeHeight,
            ["keepAspectRatio"] = KeepAspectRatio,
            ["rotateDegrees"] = RotateDegrees,
            ["outputFormat"] = OutputFormat.ToString(),
            ["interleaved"] = Interleaved,
        };

        if (Crop is { } crop)
        {
            props["crop"] = new[] { crop.XMin, crop.YMin, crop.XMax, crop.YMax };
        }

        return props;
    }

    internal override void ApplyProperties(IReadOnlyDictionary<string, object?> properties)
    {
        ResizeWidth = properties.GetInt("resizeWidth", ResizeWidth);
        ResizeHeight = properties.GetInt("resizeHeight", ResizeHeight);
        KeepAspectRatio = properties.GetBool("keepAspectRatio", KeepAspectRatio);
        RotateDegrees = properties.GetInt("rotateDegrees", RotateDegrees);
        OutputFormat = properties.GetEnum("outputFormat", OutputFormat);
        Interleaved = properties.GetBool("interleaved", Interleaved);

        var crop = properties.GetFloatList("crop");
        Crop = crop.Count == 4 ? (crop[0], crop[1], crop[2], crop[3]) : null;
    }

    internal override void Validate(DeviceCapabilities capabilities, IList<string> errors)
    {
        if (RotateDegrees % 90 != 0)
        {
            errors.Add($"Node '{Name}': rotateDegrees harus kelipatan 90, sekarang {RotateDegrees}.");
        }

        if (Crop is { } crop && (crop.XMin >= crop.XMax || crop.YMin >= crop.YMax))
        {
            errors.Add($"Node '{Name}': rectangle crop kosong — min harus lebih kecil dari max.");
        }
    }
}

/// <summary>Meng-encode frame di perangkat menjadi MJPEG/H.264/H.265.</summary>
public sealed class VideoEncoderNode : PipelineNode
{
    public VideoEncoderNode(string name) : base(name)
    {
        Input = DefineInput("input");
        Bitstream = DefineOutput("bitstream");
    }

    public override string NodeType => "VideoEncoder";

    public NodeInput Input { get; }

    /// <summary>Bitstream terkompresi; tulis langsung ke berkas atau kirim lewat jaringan.</summary>
    public NodeOutput Bitstream { get; }

    public VideoProfile Profile { get; set; } = VideoProfile.MjpegLossy;

    /// <summary>Kualitas JPEG 0..100; diabaikan untuk profil H.26x.</summary>
    public int Quality { get; set; } = 95;

    /// <summary>Bitrate untuk profil H.26x, kbps. 0 memakai bawaan encoder.</summary>
    public int BitrateKbps { get; set; }

    /// <summary>Frekuensi keyframe. 0 berarti hanya keyframe pertama (tidak disarankan untuk streaming).</summary>
    public int KeyframeFrequency { get; set; } = 30;

    public int Fps { get; set; } = 30;

    internal override IDictionary<string, object?> GetProperties() => new Dictionary<string, object?>
    {
        ["profile"] = Profile.ToString(),
        ["quality"] = Quality,
        ["bitrateKbps"] = BitrateKbps,
        ["keyframeFrequency"] = KeyframeFrequency,
        ["fps"] = Fps,
    };

    internal override void ApplyProperties(IReadOnlyDictionary<string, object?> properties)
    {
        Profile = properties.GetEnum("profile", Profile);
        Quality = properties.GetInt("quality", Quality);
        BitrateKbps = properties.GetInt("bitrateKbps", BitrateKbps);
        KeyframeFrequency = properties.GetInt("keyframeFrequency", KeyframeFrequency);
        Fps = properties.GetInt("fps", Fps);
    }

    internal override void Validate(DeviceCapabilities capabilities, IList<string> errors)
    {
        if (Quality is < 1 or > 100)
        {
            errors.Add($"Node '{Name}': quality harus 1..100, sekarang {Quality}.");
        }
    }
}

/// <summary>Sensor IMU yang bisa diminta dari node <see cref="ImuNode"/>.</summary>
[Flags]
public enum ImuSensors
{
    None = 0,
    Accelerometer = 1,
    Gyroscope = 2,
    Magnetometer = 4,
    RotationVector = 8,
    All = Accelerometer | Gyroscope | Magnetometer | RotationVector,
}

/// <summary>Memancarkan paket IMU dari sensor gerak on-board.</summary>
public sealed class ImuNode : PipelineNode
{
    public ImuNode(string name) : base(name)
    {
        Out = DefineOutput("out");
    }

    public override string NodeType => "Imu";

    public NodeOutput Out { get; }

    public ImuSensors Sensors { get; set; } = ImuSensors.Accelerometer | ImuSensors.Gyroscope;

    /// <summary>Laju sampling per sensor, Hz.</summary>
    public int RateHz { get; set; } = 100;

    /// <summary>Jumlah laporan yang dikumpulkan sebelum dikirim; batch besar menurunkan overhead USB.</summary>
    public int BatchReportThreshold { get; set; } = 1;

    internal override IDictionary<string, object?> GetProperties() => new Dictionary<string, object?>
    {
        ["sensors"] = Sensors.ToString(),
        ["rateHz"] = RateHz,
        ["batchReportThreshold"] = BatchReportThreshold,
    };

    internal override void ApplyProperties(IReadOnlyDictionary<string, object?> properties)
    {
        Sensors = properties.GetEnum("sensors", Sensors);
        RateHz = properties.GetInt("rateHz", RateHz);
        BatchReportThreshold = properties.GetInt("batchReportThreshold", BatchReportThreshold);
    }

    internal override void Validate(DeviceCapabilities capabilities, IList<string> errors)
    {
        if (!capabilities.HasImu)
        {
            errors.Add($"Node '{Name}': perangkat tidak punya IMU.");
        }
    }
}
