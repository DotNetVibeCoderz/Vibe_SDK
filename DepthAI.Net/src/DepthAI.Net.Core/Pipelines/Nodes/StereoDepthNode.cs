using DepthAI.Devices;

namespace DepthAI.Pipelines.Nodes;

/// <summary>Preset penyetelan stereo matcher.</summary>
public enum DepthPreset
{
    /// <summary>Menyeimbangkan cakupan dan akurasi; titik awal yang baik.</summary>
    Default,
    /// <summary>Lebih sedikit lubang, tapi tepi objek lebih kasar.</summary>
    HighDensity,
    /// <summary>Membuang pengukuran meragukan; peta lebih berlubang tapi lebih dipercaya.</summary>
    HighAccuracy,
    /// <summary>Jalur latensi rendah untuk kendali robot.</summary>
    FastAccuracy,
}

/// <summary>Ukuran kernel median filter pasca-pemrosesan.</summary>
public enum MedianFilter
{
    Off,
    Kernel3x3,
    Kernel5x5,
    Kernel7x7,
}

/// <summary>
/// Menghitung kedalaman dari sepasang kamera mono terkalibrasi.
/// </summary>
public sealed class StereoDepthNode : PipelineNode
{
    public StereoDepthNode(string name) : base(name)
    {
        Left = DefineInput("left");
        Right = DefineInput("right");
        Depth = DefineOutput("depth");
        Disparity = DefineOutput("disparity");
        RectifiedLeft = DefineOutput("rectifiedLeft");
        RectifiedRight = DefineOutput("rectifiedRight");
        SyncedLeft = DefineOutput("syncedLeft");
        SyncedRight = DefineOutput("syncedRight");
    }

    public override string NodeType => "StereoDepth";

    public NodeInput Left { get; }

    public NodeInput Right { get; }

    /// <summary>Kedalaman metrik dalam milimeter — keluaran yang biasanya Anda inginkan.</summary>
    public NodeOutput Depth { get; }

    /// <summary>Disparitas mentah; berguna untuk visualisasi dan debug kalibrasi.</summary>
    public NodeOutput Disparity { get; }

    public NodeOutput RectifiedLeft { get; }

    public NodeOutput RectifiedRight { get; }

    public NodeOutput SyncedLeft { get; }

    public NodeOutput SyncedRight { get; }

    public DepthPreset Preset { get; set; } = DepthPreset.Default;

    /// <summary>
    /// Mendeteksi dan membuang kecocokan yang tidak konsisten antara pandangan kiri dan kanan.
    /// Menaikkan kualitas di tepi objek dengan biaya sedikit throughput.
    /// </summary>
    public bool LeftRightCheck { get; set; } = true;

    /// <summary>Menambah presisi sub-piksel untuk objek jauh; menaikkan beban komputasi.</summary>
    public bool Subpixel { get; set; }

    /// <summary>Memperluas jangkauan dekat dengan menjalankan pass disparitas kedua.</summary>
    public bool ExtendedDisparity { get; set; }

    /// <summary>Ambang 0..255; piksel dengan keyakinan di atas nilai ini dibuang.</summary>
    public int ConfidenceThreshold { get; set; } = 200;

    public MedianFilter MedianFilter { get; set; } = MedianFilter.Kernel7x7;

    /// <summary>
    /// Menyelaraskan peta kedalaman ke sudut pandang socket ini, supaya piksel kedalaman
    /// dan piksel warna merujuk titik dunia yang sama. Wajib untuk deteksi spasial.
    /// </summary>
    public CameraSocket AlignTo { get; set; } = CameraSocket.Auto;

    /// <summary>Ukuran keluaran kedalaman; 0 berarti mengikuti resolusi kamera mono.</summary>
    public int OutputWidth { get; set; }

    public int OutputHeight { get; set; }

    internal override IDictionary<string, object?> GetProperties() => new Dictionary<string, object?>
    {
        ["preset"] = Preset.ToString(),
        ["leftRightCheck"] = LeftRightCheck,
        ["subpixel"] = Subpixel,
        ["extendedDisparity"] = ExtendedDisparity,
        ["confidenceThreshold"] = ConfidenceThreshold,
        ["medianFilter"] = MedianFilter.ToString(),
        ["alignTo"] = AlignTo.ToString(),
        ["outputWidth"] = OutputWidth,
        ["outputHeight"] = OutputHeight,
    };

    internal override void ApplyProperties(IReadOnlyDictionary<string, object?> properties)
    {
        Preset = properties.GetEnum("preset", Preset);
        LeftRightCheck = properties.GetBool("leftRightCheck", LeftRightCheck);
        Subpixel = properties.GetBool("subpixel", Subpixel);
        ExtendedDisparity = properties.GetBool("extendedDisparity", ExtendedDisparity);
        ConfidenceThreshold = properties.GetInt("confidenceThreshold", ConfidenceThreshold);
        MedianFilter = properties.GetEnum("medianFilter", MedianFilter);
        AlignTo = properties.GetEnum("alignTo", AlignTo);
        OutputWidth = properties.GetInt("outputWidth", OutputWidth);
        OutputHeight = properties.GetInt("outputHeight", OutputHeight);
    }

    internal override void Validate(DeviceCapabilities capabilities, IList<string> errors)
    {
        if (!capabilities.SupportsStereoDepth)
        {
            errors.Add($"Node '{Name}': perangkat tidak punya pasangan stereo terkalibrasi.");
        }

        if (ConfidenceThreshold is < 0 or > 255)
        {
            errors.Add($"Node '{Name}': confidenceThreshold harus 0..255, sekarang {ConfidenceThreshold}.");
        }

        // Subpixel dan extended disparity memakai blok perangkat keras yang sama.
        if (Subpixel && ExtendedDisparity)
        {
            errors.Add($"Node '{Name}': subpixel dan extendedDisparity tidak bisa aktif bersamaan.");
        }
    }
}
