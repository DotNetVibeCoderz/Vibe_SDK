using DepthAI.Devices;
using DepthAI.Inference;

namespace DepthAI.Pipelines.Nodes;

/// <summary>
/// Menjalankan model neural pada perangkat dan memaparkan tensor keluaran mentah.
/// Untuk deteksi objek pakai <see cref="DetectionNetworkNode"/> yang sudah menguraikan
/// hasilnya menjadi objek bertipe.
/// </summary>
public class NeuralNetworkNode : PipelineNode
{
    public NeuralNetworkNode(string name) : base(name)
    {
        Input = DefineInput("input");
        Out = DefineOutput("out");
        Passthrough = DefineOutput("passthrough");
    }

    public override string NodeType => "NeuralNetwork";

    /// <summary>Frame gambar yang akan diinferensi.</summary>
    public NodeInput Input { get; }

    /// <summary>Tensor keluaran mentah.</summary>
    public NodeOutput Out { get; }

    /// <summary>
    /// Frame masukan yang diteruskan, tersinkron dengan hasilnya. Pakai keluaran ini —
    /// bukan preview kamera — saat menggambar overlay, supaya kotak sejajar dengan
    /// frame yang benar-benar diinferensi.
    /// </summary>
    public NodeOutput Passthrough { get; }

    /// <summary>Model yang dijalankan. Wajib diisi sebelum pipeline dimulai.</summary>
    public NeuralModel? Model { get; set; }

    /// <summary>Jumlah thread inferensi; 0 memakai bawaan perangkat.</summary>
    public int InferenceThreads { get; set; }

    /// <summary>SHAVE core per thread; 0 memakai bawaan perangkat.</summary>
    public int ShavesPerThread { get; set; }

    /// <summary>
    /// Bila false, frame masukan dibuang saat network sibuk — perilaku yang tepat
    /// untuk realtime. Set true hanya jika setiap frame wajib diproses.
    /// </summary>
    public bool BlockingInput { get; set; }

    /// <summary>Menimpa ambang keyakinan dari metadata model saat runtime.</summary>
    public float? ConfidenceThreshold { get; set; }

    public NeuralNetworkNode WithModel(NeuralModel model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        return this;
    }

    internal override IDictionary<string, object?> GetProperties() => new Dictionary<string, object?>
    {
        ["model"] = Model?.Name,
        ["modelFormat"] = Model?.Format.ToString(),
        ["modelFamily"] = Model?.Metadata.Family.ToString(),
        ["inferenceThreads"] = InferenceThreads,
        ["shavesPerThread"] = ShavesPerThread,
        ["blockingInput"] = BlockingInput,
        ["confidenceThreshold"] = ConfidenceThreshold,
    };

    internal override void ApplyProperties(IReadOnlyDictionary<string, object?> properties)
    {
        InferenceThreads = properties.GetInt("inferenceThreads", InferenceThreads);
        ShavesPerThread = properties.GetInt("shavesPerThread", ShavesPerThread);
        BlockingInput = properties.GetBool("blockingInput", BlockingInput);

        var threshold = properties.GetFloat("confidenceThreshold", float.NaN);
        ConfidenceThreshold = float.IsNaN(threshold) ? null : threshold;

        // Payload model tidak muat di JSON pipeline; pemanggil menyambungkannya kembali
        // lewat PipelineLoadOptions.ModelResolver saat memuat.
        ModelName = properties.GetString("model");
    }

    /// <summary>
    /// Nama model yang tercatat di JSON pipeline, ada walau payload belum terpasang.
    /// Dipakai tooling (CLI, wizard) untuk melaporkan model apa yang dibutuhkan pipeline.
    /// </summary>
    public string? ModelName { get; internal set; }

    internal override void Validate(DeviceCapabilities capabilities, IList<string> errors)
    {
        if (Model is null)
        {
            errors.Add($"Node '{Name}': belum ada model. Panggil WithModel(...) sebelum menjalankan pipeline"
                + (ModelName is null ? "." : $" — JSON pipeline merujuk '{ModelName}'."));
        }

        if (ShavesPerThread > 0 && capabilities.ShaveCores > 0 && ShavesPerThread > capabilities.ShaveCores)
        {
            errors.Add($"Node '{Name}': meminta {ShavesPerThread} SHAVE tapi perangkat hanya punya {capabilities.ShaveCores}.");
        }
    }
}

/// <summary>
/// Neural network yang keluarannya sudah diuraikan menjadi <see cref="Detection"/>
/// bertipe memakai metadata model.
/// </summary>
public class DetectionNetworkNode : NeuralNetworkNode
{
    public DetectionNetworkNode(string name) : base(name)
    {
        Detections = DefineOutput("detections");
    }

    public override string NodeType => "DetectionNetwork";

    /// <summary>Deteksi yang sudah diurai, dipancarkan sebagai <see cref="DetectionFrame"/>.</summary>
    public NodeOutput Detections { get; }
}

/// <summary>
/// Detection network yang digabung dengan peta kedalaman, sehingga tiap deteksi
/// membawa posisi 3D. Butuh <see cref="StereoDepthNode"/> yang diselaraskan ke
/// kamera sumbernya.
/// </summary>
public sealed class SpatialDetectionNetworkNode : DetectionNetworkNode
{
    public SpatialDetectionNetworkNode(string name) : base(name)
    {
        DepthInput = DefineInput("depth");
        BoundingBoxMapping = DefineOutput("boundingBoxMapping");
        PassthroughDepth = DefineOutput("passthroughDepth");
    }

    public override string NodeType => "SpatialDetectionNetwork";

    public NodeInput DepthInput { get; }

    /// <summary>Region kedalaman yang dipakai per deteksi; berguna untuk debug penyelarasan.</summary>
    public NodeOutput BoundingBoxMapping { get; }

    public NodeOutput PassthroughDepth { get; }

    /// <summary>
    /// Mengecilkan kotak sebelum mengambil sampel kedalaman, supaya piksel latar di tepi
    /// kotak tidak mencemari estimasi jarak. 0.5 berarti memakai 50% bagian tengah.
    /// </summary>
    public float BoundingBoxScaleFactor { get; set; } = 0.5f;

    /// <summary>Kedalaman di bawah nilai ini (mm) diabaikan saat mengambil sampel.</summary>
    public int DepthLowerThresholdMm { get; set; } = 100;

    public int DepthUpperThresholdMm { get; set; } = 10_000;

    internal override IDictionary<string, object?> GetProperties()
    {
        var props = base.GetProperties();
        props["boundingBoxScaleFactor"] = BoundingBoxScaleFactor;
        props["depthLowerThresholdMm"] = DepthLowerThresholdMm;
        props["depthUpperThresholdMm"] = DepthUpperThresholdMm;
        return props;
    }

    internal override void ApplyProperties(IReadOnlyDictionary<string, object?> properties)
    {
        base.ApplyProperties(properties);
        BoundingBoxScaleFactor = properties.GetFloat("boundingBoxScaleFactor", BoundingBoxScaleFactor);
        DepthLowerThresholdMm = properties.GetInt("depthLowerThresholdMm", DepthLowerThresholdMm);
        DepthUpperThresholdMm = properties.GetInt("depthUpperThresholdMm", DepthUpperThresholdMm);
    }

    internal override void Validate(DeviceCapabilities capabilities, IList<string> errors)
    {
        base.Validate(capabilities, errors);

        if (!capabilities.SupportsStereoDepth)
        {
            errors.Add($"Node '{Name}': deteksi spasial butuh stereo depth, yang tidak dimiliki perangkat ini.");
        }

        if (BoundingBoxScaleFactor is <= 0 or > 1)
        {
            errors.Add($"Node '{Name}': boundingBoxScaleFactor harus di rentang (0..1], sekarang {BoundingBoxScaleFactor}.");
        }

        if (DepthLowerThresholdMm >= DepthUpperThresholdMm)
        {
            errors.Add($"Node '{Name}': ambang kedalaman bawah ({DepthLowerThresholdMm}mm) harus lebih kecil dari ambang atas ({DepthUpperThresholdMm}mm).");
        }
    }
}
