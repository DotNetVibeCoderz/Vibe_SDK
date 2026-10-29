using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace DepthAI.Inference;

/// <summary>
/// Cara mendekode keluaran grid YOLO mentah (satu tensor <c>[1, C, H, W]</c> per stride).
/// Nama dan pemetaan subtipenya mengikuti <c>YoloDecodingFamily</c> di depthai-core.
/// </summary>
public enum YoloDecodingFamily
{
    /// <summary>
    /// Pilih dari bentuk tensor: keluaran tunggal yang sudah digabung didekode seperti
    /// sebelumnya, keluaran grid memakai <see cref="Tlbr"/>.
    /// </summary>
    Auto = 0,

    /// <summary>Anchor-free jarak ke tepi kiri-atas/kanan-bawah: YOLOv6 R2, v8, v10, v11, v12.</summary>
    Tlbr,

    /// <summary>
    /// Anchor-free pusat/ukuran: YOLOv6 dan YOLOv6 R1. Lebar dan tinggi berupa log,
    /// pusat berupa offset sel; semuanya dikali stride.
    /// </summary>
    CenterSize,

    /// <summary>Berbasis anchor: YOLOv5, v7, YOLO-P. Didekode lewat jalur tensor gabungan.</summary>
    AnchorBased,
}

/// <summary>Memetakan subtipe NN Archive ke <see cref="YoloDecodingFamily"/>.</summary>
public static class YoloDecodingFamilies
{
    /// <summary>
    /// Sama dengan <c>DetectionParser::yoloDecodingFamilyResolver</c> di depthai-core 3.9+.
    /// </summary>
    /// <remarks>
    /// Sebelum 3.9, <c>yolov6</c> dipetakan ke TLBR dan kotaknya meleset; upstream
    /// memperbaikinya menjadi pusat/ukuran. Pemetaan ini mengikuti perbaikan itu.
    /// </remarks>
    public static YoloDecodingFamily FromSubtype(string? subtype) => subtype?.ToLowerInvariant() switch
    {
        "yolov6r1" or "yolov6" => YoloDecodingFamily.CenterSize,
        "yolov6r2" or "yolov8n" or "yolov8" or "yolov10" or "yolov11" or "yolov12" => YoloDecodingFamily.Tlbr,
        "yolov5" or "yolov7" or "yolo-p" or "yolov5-u" => YoloDecodingFamily.AnchorBased,
        _ => YoloDecodingFamily.Auto,
    };
}

/// <summary>
/// Pembaca <c>config.json</c> NN Archive v1 Luxonis, yakni berkas metadata di dalam arsip
/// model <c>.tar.xz</c>/<c>.tar</c> dari model zoo dan HubAI.
/// </summary>
/// <remarks>
/// Hanya kepala (head) pertama yang dibaca, sama seperti DetectionParser upstream saat
/// diberi arsip tanpa indeks head. Kunci yang tidak dikenali diabaikan supaya arsip
/// dari versi tooling yang lebih baru tetap bisa dimuat.
/// </remarks>
public static class NnArchiveConfig
{
    /// <summary>Nama berkas metadata di dalam NN Archive.</summary>
    public const string FileName = "config.json";

    /// <summary>
    /// True bila JSON ini berbentuk config NN Archive (punya <c>config_version</c> atau
    /// <c>model.heads</c>), bukan berkas pendamping bergaya lama.
    /// </summary>
    public static bool LooksLikeArchiveConfig(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("config_version", out _)
                    || (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Mengurai isi <c>config.json</c> menjadi <see cref="ModelMetadata"/>.</summary>
    /// <exception cref="InvalidDataException">Config tidak valid, misalnya jumlah stride tidak cocok.</exception>
    public static ModelMetadata Parse(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} NN Archive bukan JSON yang valid: {ex.Message}", ex);
        }

        using (document)
        {
            var model = Property(document.RootElement, "model")
                ?? throw new InvalidDataException($"{FileName} NN Archive tidak punya bagian 'model'.");

            var (width, height) = ReadInputSize(model);
            var head = FirstElement(Property(model, "heads"));
            var meta = head is { } h ? Property(h, "metadata") : null;

            var subtype = String(meta, "subtype");
            var labels = StringList(meta, "classes");
            var yoloOutputs = StringList(meta, "yolo_outputs");
            var strides = ReadStrides(meta, head, yoloOutputs);

            return new ModelMetadata
            {
                Family = ParseFamily(head is { } hh ? String(hh, "parser") : null),
                InputWidth = width,
                InputHeight = height,
                Labels = labels,
                ConfidenceThreshold = Float(meta, "conf_threshold") ?? 0.5f,
                IouThreshold = Float(meta, "iou_threshold") ?? 0.5f,
                Anchors = FlattenAnchors(meta is { } m ? Property(m, "anchors") : null),
                Subtype = subtype,
                YoloDecoding = YoloDecodingFamilies.FromSubtype(subtype),
                Strides = strides,
                YoloOutputs = yoloOutputs,
                BackgroundClass = Bool(meta, "background_class") ?? false,
            };
        }
    }

    /// <summary>Membaca <c>config.json</c> dari arsip <c>.tar</c> atau <c>.tar.gz</c>.</summary>
    /// <returns>Metadata, atau null bila arsip tidak berisi <c>config.json</c>.</returns>
    public static async Task<ModelMetadata?> ReadFromTarAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var file = File.OpenRead(path);
        Stream source = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(file, CompressionMode.Decompress)
                : file;

        await using (source)
        {
            await using var tar = new TarReader(source);
            while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
            {
                if (!string.Equals(Path.GetFileName(entry.Name), FileName, StringComparison.OrdinalIgnoreCase)
                    || entry.DataStream is null)
                {
                    continue;
                }

                using var buffer = new MemoryStream();
                await entry.DataStream.CopyToAsync(buffer, cancellationToken);
                return Parse(buffer.ToArray());
            }
        }

        return null;
    }

    private static ModelFamily ParseFamily(string? parser) => parser switch
    {
        "YOLO" or "YOLOExtendedParser" => ModelFamily.Yolo,
        "SSD" or "MOBILENET" => ModelFamily.MobileNetSsd,
        "SegmentationParser" => ModelFamily.Segmentation,
        "ClassificationParser" => ModelFamily.Classification,
        _ => ModelFamily.Raw,
    };

    /// <summary>Ukuran input dari <c>inputs[0].shape</c> dengan menghormati <c>layout</c> (NCHW/NHWC).</summary>
    private static (int Width, int Height) ReadInputSize(JsonElement model)
    {
        if (FirstElement(Property(model, "inputs")) is not { } input
            || Property(input, "shape") is not { ValueKind: JsonValueKind.Array } shape)
        {
            return (640, 640);
        }

        var dims = shape.EnumerateArray()
            .Select(static d => d.TryGetInt32(out var v) ? v : 0)
            .ToArray();
        var layout = (String(input, "layout") ?? (dims.Length == 4 && dims[1] <= 4 ? "NCHW" : "NHWC")).ToUpperInvariant();

        var w = layout.IndexOf('W', StringComparison.Ordinal);
        var h = layout.IndexOf('H', StringComparison.Ordinal);
        return w >= 0 && h >= 0 && w < dims.Length && h < dims.Length && dims[w] > 0 && dims[h] > 0
            ? (dims[w], dims[h])
            : (640, 640);
    }

    /// <summary>
    /// Validasi yang sama dengan <c>configureYOLONetworkParser</c> upstream: stride harus
    /// positif dan jumlahnya sama dengan jumlah keluaran YOLO.
    /// </summary>
    private static int[] ReadStrides(JsonElement? meta, JsonElement? head, IReadOnlyList<string> yoloOutputs)
    {
        if (meta is not { } m || Property(m, "strides") is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        var strides = array.EnumerateArray().Select(static s => s.GetInt32()).ToArray();
        if (strides.Length == 0)
        {
            throw new InvalidDataException("'strides' pada NN Archive tidak boleh kosong.");
        }

        if (strides.Any(static s => s <= 0))
        {
            throw new InvalidDataException("Semua nilai 'strides' pada NN Archive harus positif.");
        }

        // Upstream menghitung keluaran YOLO dari yolo_outputs, atau dari nama keluaran head
        // yang mengandung "_yolo" — head lain (mask, protos) tidak punya stride.
        var outputCount = yoloOutputs.Count > 0
            ? yoloOutputs.Count
            : StringList(head, "outputs").Count(static o => o.Contains("_yolo", StringComparison.Ordinal));

        if (outputCount == 0)
        {
            throw new InvalidDataException("Keluaran YOLO harus didefinisikan bila 'strides' diisi.");
        }

        if (strides.Length != outputCount)
        {
            throw new InvalidDataException(
                $"Jumlah 'strides' harus sama dengan jumlah keluaran YOLO: {strides.Length} stride untuk {outputCount} keluaran.");
        }

        return strides;
    }

    /// <summary>Anchor NN Archive berbentuk <c>[layer][anchor][w,h]</c>; diratakan menjadi pasangan w/h.</summary>
    private static float[] FlattenAnchors(JsonElement? anchors)
    {
        if (anchors is not { ValueKind: JsonValueKind.Array } root)
        {
            return [];
        }

        var values = new List<float>();
        Flatten(root, values);
        return [.. values];

        static void Flatten(JsonElement element, List<float> values)
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                values.Add(element.GetSingle());
                return;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                {
                    Flatten(child, values);
                }
            }
        }
    }

    private static JsonElement? Property(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind != JsonValueKind.Null
                ? value
                : null;

    private static JsonElement? FirstElement(JsonElement? array)
        => array is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0 ? a[0] : null;

    private static string? String(JsonElement? element, string name)
        => element is { } e && Property(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static float? Float(JsonElement? element, string name)
        => element is { } e && Property(e, name) is { ValueKind: JsonValueKind.Number } v ? v.GetSingle() : null;

    private static bool? Bool(JsonElement? element, string name)
        => element is { } e && Property(e, name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v
            ? v.GetBoolean()
            : null;

    private static string[] StringList(JsonElement? element, string name)
        => element is { } e && Property(e, name) is { ValueKind: JsonValueKind.Array } array
            ? [.. array.EnumerateArray()
                .Where(static v => v.ValueKind == JsonValueKind.String)
                .Select(static v => v.GetString()!)]
            : [];
}
