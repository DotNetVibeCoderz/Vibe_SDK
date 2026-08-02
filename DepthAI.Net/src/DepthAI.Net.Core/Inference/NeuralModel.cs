using System.Text.Json;
using System.Text.Json.Serialization;

namespace DepthAI.Inference;

/// <summary>Format artefak model yang dikenali SDK.</summary>
public enum ModelFormat
{
    Unknown = 0,
    /// <summary>OpenVINO <c>.blob</c> yang sudah dikompilasi untuk MyriadX/RVC.</summary>
    Blob,
    /// <summary><c>.superblob</c> — satu berkas dengan beberapa varian jumlah SHAVE.</summary>
    SuperBlob,
    /// <summary>ONNX; dikompilasi on-the-fly oleh perangkat/host tooling.</summary>
    Onnx,
    /// <summary>Arsip NN Luxonis (<c>.tar.xz</c>) yang berisi blob plus metadata config.</summary>
    NnArchive,
}

/// <summary>Keluarga arsitektur; menentukan parser mana yang dipakai membaca keluaran.</summary>
public enum ModelFamily
{
    /// <summary>Tidak diketahui — keluaran dipaparkan sebagai tensor mentah.</summary>
    Raw = 0,
    Yolo,
    MobileNetSsd,
    Classification,
    Segmentation,
}

/// <summary>
/// Metadata yang dibutuhkan untuk menafsirkan keluaran model. Untuk <c>.blob</c> polos
/// informasi ini tidak ada di dalam berkas, jadi harus disediakan pemanggil atau dibaca
/// dari berkas <c>.json</c> pendamping bergaya Luxonis.
/// </summary>
public sealed record ModelMetadata
{
    public ModelFamily Family { get; init; } = ModelFamily.Raw;

    /// <summary>Lebar input yang diharapkan, piksel.</summary>
    public int InputWidth { get; init; } = 640;

    public int InputHeight { get; init; } = 640;

    /// <summary>Nama kelas terurut menurut indeks.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Ambang keyakinan bawaan untuk membuang deteksi lemah.</summary>
    public float ConfidenceThreshold { get; init; } = 0.5f;

    /// <summary>Ambang IoU untuk non-maximum suppression.</summary>
    public float IouThreshold { get; init; } = 0.5f;

    /// <summary>Anchor YOLO (pasangan lebar/tinggi). Kosong untuk YOLO anchor-free (v8+).</summary>
    public IReadOnlyList<float> Anchors { get; init; } = [];

    /// <summary>Jumlah koordinat per box; 4 untuk YOLO standar.</summary>
    public int CoordinateSize { get; init; } = 4;

    /// <summary>Jumlah SHAVE core yang dikompilasi ke blob. 0 = pakai bawaan perangkat.</summary>
    public int ShaveCores { get; init; }

    public static ModelMetadata Default { get; } = new();
}

/// <summary>
/// Model neural yang sudah dimuat ke memori host dan siap di-deploy ke perangkat.
/// </summary>
public sealed class NeuralModel
{
    private NeuralModel(ReadOnlyMemory<byte> payload, ModelFormat format, ModelMetadata metadata, string name)
    {
        Payload = payload;
        Format = format;
        Metadata = metadata;
        Name = name;
    }

    /// <summary>Nama yang mudah dibaca — biasanya nama berkas tanpa ekstensi.</summary>
    public string Name { get; }

    public ModelFormat Format { get; }

    public ModelMetadata Metadata { get; }

    /// <summary>Byte model mentah yang akan diunggah ke perangkat.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    public int SizeBytes => Payload.Length;

    /// <summary>
    /// Memuat model dari disk. Bila ada berkas <c>.json</c> bersebelahan dengan nama sama,
    /// metadata dibaca dari sana kecuali <paramref name="metadata"/> diberikan eksplisit.
    /// </summary>
    public static async Task<NeuralModel> LoadFromFileAsync(
        string path,
        ModelMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Berkas model tidak ditemukan: {path}", path);
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var format = DetectFormat(path);
        var name = Path.GetFileNameWithoutExtension(path);

        metadata ??= await TryLoadSidecarMetadataAsync(path, cancellationToken) ?? ModelMetadata.Default;

        return new NeuralModel(bytes, format, metadata, name);
    }

    /// <summary>Memuat model dari stream sembarang (embedded resource, HTTP, blob storage).</summary>
    public static async Task<NeuralModel> LoadFromStreamAsync(
        Stream stream,
        ModelFormat format,
        ModelMetadata? metadata = null,
        string name = "model",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);

        return new NeuralModel(buffer.ToArray(), format, metadata ?? ModelMetadata.Default, name);
    }

    /// <summary>
    /// Membuat model tanpa bobot yang hanya membawa metadata.
    /// </summary>
    /// <remarks>
    /// Ditujukan untuk mengembangkan aplikasi melawan backend simulasi sebelum berkas
    /// <c>.blob</c> sungguhan tersedia: pipeline, parser, dan kode UI berjalan penuh,
    /// karena simulasi menghasilkan tensor sesuai <paramref name="family"/>. Model ini
    /// akan ditolak perangkat sungguhan — ganti dengan <see cref="LoadFromFileAsync"/>
    /// sebelum menjalankan pada hardware.
    /// </remarks>
    public static NeuralModel CreatePlaceholder(
        ModelFamily family,
        IReadOnlyList<string> labels,
        int inputWidth = 640,
        int inputHeight = 640,
        float confidenceThreshold = 0.5f,
        string name = "placeholder")
        => new(
            ReadOnlyMemory<byte>.Empty,
            ModelFormat.Unknown,
            new ModelMetadata
            {
                Family = family,
                Labels = labels,
                InputWidth = inputWidth,
                InputHeight = inputHeight,
                ConfidenceThreshold = confidenceThreshold,
            },
            name);

    /// <summary>True bila model tidak membawa bobot dan hanya bisa dipakai di simulasi.</summary>
    public bool IsPlaceholder => Payload.Length == 0;

    /// <summary>Membungkus byte yang sudah ada di memori.</summary>
    public static NeuralModel FromBytes(
        ReadOnlyMemory<byte> payload,
        ModelFormat format,
        ModelMetadata? metadata = null,
        string name = "model")
        => new(payload, format, metadata ?? ModelMetadata.Default, name);

    /// <summary>Membuat parser yang cocok dengan keluarga model ini.</summary>
    public IInferenceParser CreateParser() => Metadata.Family switch
    {
        ModelFamily.Yolo => new YoloParser(Metadata),
        ModelFamily.MobileNetSsd => new MobileNetSsdParser(Metadata),
        ModelFamily.Classification => new ClassificationParser(Metadata),
        ModelFamily.Segmentation => new SegmentationParser(Metadata),
        _ => new RawTensorParser(),
    };

    private static ModelFormat DetectFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".blob" => ModelFormat.Blob,
        ".superblob" => ModelFormat.SuperBlob,
        ".onnx" => ModelFormat.Onnx,
        ".tar" or ".xz" or ".gz" => ModelFormat.NnArchive,
        _ => ModelFormat.Unknown,
    };

    private static async Task<ModelMetadata?> TryLoadSidecarMetadataAsync(
        string modelPath,
        CancellationToken cancellationToken)
    {
        var sidecar = Path.ChangeExtension(modelPath, ".json");
        if (!File.Exists(sidecar))
        {
            return null;
        }

        await using var stream = File.OpenRead(sidecar);
        var config = await JsonSerializer.DeserializeAsync(
            stream, ModelJsonContext.Default.LuxonisModelConfig, cancellationToken);

        return config?.ToMetadata();
    }
}

/// <summary>
/// Bentuk berkas <c>.json</c> pendamping bergaya Luxonis/depthai (subset yang kita pakai).
/// </summary>
internal sealed class LuxonisModelConfig
{
    [JsonPropertyName("nn_config")]
    public NnConfigSection? NnConfig { get; set; }

    [JsonPropertyName("mappings")]
    public MappingsSection? Mappings { get; set; }

    public ModelMetadata ToMetadata()
    {
        var meta = NnConfig?.NnSpecificMetadata;
        var (width, height) = ParseInputSize(NnConfig?.InputSize);

        return new ModelMetadata
        {
            Family = ParseFamily(NnConfig?.NnFamily),
            InputWidth = width,
            InputHeight = height,
            Labels = Mappings?.Labels ?? [],
            ConfidenceThreshold = meta?.ConfidenceThreshold ?? 0.5f,
            IouThreshold = meta?.IouThreshold ?? 0.5f,
            Anchors = meta?.Anchors ?? [],
            CoordinateSize = meta?.Coordinates ?? 4,
        };
    }

    private static ModelFamily ParseFamily(string? family) => family?.ToLowerInvariant() switch
    {
        "yolo" or "yolov5" or "yolov6" or "yolov7" or "yolov8" => ModelFamily.Yolo,
        "mobilenet" or "mobilenet-ssd" or "ssd" => ModelFamily.MobileNetSsd,
        "classification" => ModelFamily.Classification,
        "segmentation" => ModelFamily.Segmentation,
        _ => ModelFamily.Raw,
    };

    /// <summary>Format Luxonis menulis ukuran input sebagai string "WxH".</summary>
    private static (int Width, int Height) ParseInputSize(string? inputSize)
    {
        if (string.IsNullOrWhiteSpace(inputSize))
        {
            return (640, 640);
        }

        var parts = inputSize.Split('x', 'X');
        return parts.Length == 2
            && int.TryParse(parts[0], out var w)
            && int.TryParse(parts[1], out var h)
                ? (w, h)
                : (640, 640);
    }

    internal sealed class NnConfigSection
    {
        [JsonPropertyName("NN_family")]
        public string? NnFamily { get; set; }

        [JsonPropertyName("input_size")]
        public string? InputSize { get; set; }

        [JsonPropertyName("NN_specific_metadata")]
        public NnSpecificMetadata? NnSpecificMetadata { get; set; }
    }

    internal sealed class NnSpecificMetadata
    {
        [JsonPropertyName("confidence_threshold")]
        public float? ConfidenceThreshold { get; set; }

        [JsonPropertyName("iou_threshold")]
        public float? IouThreshold { get; set; }

        [JsonPropertyName("coordinates")]
        public int? Coordinates { get; set; }

        [JsonPropertyName("anchors")]
        public List<float>? Anchors { get; set; }
    }

    internal sealed class MappingsSection
    {
        [JsonPropertyName("labels")]
        public List<string>? Labels { get; set; }
    }
}

[JsonSerializable(typeof(LuxonisModelConfig))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class ModelJsonContext : JsonSerializerContext;
