using DepthAI.Devices;
using DepthAI.Streaming;

namespace DepthAI.Pipelines.Nodes;

/// <summary>Resolusi sensor kamera warna.</summary>
public enum ColorResolution
{
    The720P,
    The800P,
    The1080P,
    The1200P,
    The4K,
    The5Mp,
    The12Mp,
    The13Mp,
    The48Mp,
}

/// <summary>Resolusi sensor kamera mono.</summary>
public enum MonoResolution
{
    The400P,
    The480P,
    The720P,
    The800P,
    The1200P,
}

/// <summary>Urutan channel warna pada frame keluaran.</summary>
public enum ColorOrder
{
    Bgr,
    Rgb,
}

/// <summary>
/// Kamera warna (RGB). Memaparkan beberapa keluaran paralel dengan resolusi berbeda:
/// <c>Preview</c> untuk masukan neural network, <c>Video</c> untuk rekaman/tampilan,
/// <c>Still</c> untuk foto resolusi penuh sesuai permintaan.
/// </summary>
public sealed class ColorCameraNode : PipelineNode
{
    public ColorCameraNode(string name) : base(name)
    {
        Preview = DefineOutput("preview");
        Video = DefineOutput("video");
        Still = DefineOutput("still");
        Isp = DefineOutput("isp");
        InputControl = DefineInput("inputControl");
    }

    public override string NodeType => "ColorCamera";

    /// <summary>Keluaran kecil beresolusi tetap; sumber lazim untuk neural network.</summary>
    public NodeOutput Preview { get; }

    /// <summary>Keluaran resolusi video, cocok untuk ditampilkan atau di-encode.</summary>
    public NodeOutput Video { get; }

    /// <summary>Foto resolusi penuh, dipancarkan hanya saat diminta lewat <see cref="InputControl"/>.</summary>
    public NodeOutput Still { get; }

    /// <summary>Keluaran ISP mentah sebelum penskalaan.</summary>
    public NodeOutput Isp { get; }

    /// <summary>Masukan perintah kamera (fokus, eksposur, trigger still).</summary>
    public NodeInput InputControl { get; }

    public CameraSocket Socket { get; set; } = CameraSocket.Rgb;

    public ColorResolution Resolution { get; set; } = ColorResolution.The1080P;

    public int Fps { get; set; } = 30;

    public int PreviewWidth { get; set; } = 640;

    public int PreviewHeight { get; set; } = 480;

    /// <summary>
    /// True memberi piksel interleaved (BGRBGR...), false memberi planar (BBB...GGG...RRR).
    /// Neural network umumnya menginginkan planar; kode tampilan menginginkan interleaved.
    /// </summary>
    public bool Interleaved { get; set; } = true;

    public ColorOrder ColorOrder { get; set; } = ColorOrder.Bgr;

    /// <summary>Menyimpan rasio aspek sensor saat menskalakan ke ukuran preview.</summary>
    public bool KeepPreviewAspectRatio { get; set; } = true;

    /// <summary>Format piksel yang dihasilkan keluaran <see cref="Preview"/>.</summary>
    public PixelFormat PreviewFormat => ColorOrder == ColorOrder.Bgr ? PixelFormat.Bgr888 : PixelFormat.Rgb888;

    /// <summary>Menyetel ukuran preview; pemanggilan fluent yang jamak dipakai.</summary>
    public ColorCameraNode WithPreview(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        PreviewWidth = width;
        PreviewHeight = height;
        return this;
    }

    public (int Width, int Height) GetSensorSize() => Resolution switch
    {
        ColorResolution.The720P => (1280, 720),
        ColorResolution.The800P => (1280, 800),
        ColorResolution.The1080P => (1920, 1080),
        ColorResolution.The1200P => (1920, 1200),
        ColorResolution.The4K => (3840, 2160),
        ColorResolution.The5Mp => (2592, 1944),
        ColorResolution.The12Mp => (4056, 3040),
        ColorResolution.The13Mp => (4208, 3120),
        ColorResolution.The48Mp => (8000, 6000),
        _ => (1920, 1080),
    };

    internal override IDictionary<string, object?> GetProperties() => new Dictionary<string, object?>
    {
        ["socket"] = Socket.ToString(),
        ["resolution"] = Resolution.ToString(),
        ["fps"] = Fps,
        ["previewWidth"] = PreviewWidth,
        ["previewHeight"] = PreviewHeight,
        ["interleaved"] = Interleaved,
        ["colorOrder"] = ColorOrder.ToString(),
        ["keepPreviewAspectRatio"] = KeepPreviewAspectRatio,
    };

    internal override void ApplyProperties(IReadOnlyDictionary<string, object?> properties)
    {
        Socket = properties.GetEnum("socket", Socket);
        Resolution = properties.GetEnum("resolution", Resolution);
        Fps = properties.GetInt("fps", Fps);
        PreviewWidth = properties.GetInt("previewWidth", PreviewWidth);
        PreviewHeight = properties.GetInt("previewHeight", PreviewHeight);
        Interleaved = properties.GetBool("interleaved", Interleaved);
        ColorOrder = properties.GetEnum("colorOrder", ColorOrder);
        KeepPreviewAspectRatio = properties.GetBool("keepPreviewAspectRatio", KeepPreviewAspectRatio);
    }

    internal override void Validate(DeviceCapabilities capabilities, IList<string> errors)
    {
        if (capabilities.ColorCameraCount == 0)
        {
            errors.Add($"Node '{Name}': perangkat tidak melaporkan kamera warna.");
        }

        if (Fps is < 1 or > 120)
        {
            errors.Add($"Node '{Name}': fps {Fps} di luar rentang yang didukung (1..120).");
        }

        if (PreviewWidth <= 0 || PreviewHeight <= 0)
        {
            errors.Add($"Node '{Name}': ukuran preview harus positif, sekarang {PreviewWidth}x{PreviewHeight}.");
        }
    }
}

/// <summary>Kamera mono (grayscale). Dua node ini memberi masukan bagi stereo depth.</summary>
public sealed class MonoCameraNode : PipelineNode
{
    public MonoCameraNode(string name) : base(name)
    {
        Out = DefineOutput("out");
        InputControl = DefineInput("inputControl");
    }

    public override string NodeType => "MonoCamera";

    public NodeOutput Out { get; }

    public NodeInput InputControl { get; }

    public CameraSocket Socket { get; set; } = CameraSocket.Left;

    public MonoResolution Resolution { get; set; } = MonoResolution.The400P;

    public int Fps { get; set; } = 30;

    public (int Width, int Height) GetSensorSize() => Resolution switch
    {
        MonoResolution.The400P => (640, 400),
        MonoResolution.The480P => (640, 480),
        MonoResolution.The720P => (1280, 720),
        MonoResolution.The800P => (1280, 800),
        MonoResolution.The1200P => (1920, 1200),
        _ => (640, 400),
    };

    internal override IDictionary<string, object?> GetProperties() => new Dictionary<string, object?>
    {
        ["socket"] = Socket.ToString(),
        ["resolution"] = Resolution.ToString(),
        ["fps"] = Fps,
    };

    internal override void ApplyProperties(IReadOnlyDictionary<string, object?> properties)
    {
        Socket = properties.GetEnum("socket", Socket);
        Resolution = properties.GetEnum("resolution", Resolution);
        Fps = properties.GetInt("fps", Fps);
    }

    internal override void Validate(DeviceCapabilities capabilities, IList<string> errors)
    {
        if (capabilities.MonoCameraCount == 0)
        {
            errors.Add($"Node '{Name}': perangkat tidak melaporkan kamera mono.");
        }
    }
}
