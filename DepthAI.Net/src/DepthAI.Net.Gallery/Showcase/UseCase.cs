using DepthAI.Gallery.Inference;
using DepthAI.Inference;
using DepthAI.Streaming;
using SkiaSharp;

namespace DepthAI.Gallery.Showcase;

/// <summary>Model tambahan yang dijalankan pada potongan hasil model utama (kaskade).</summary>
public sealed record StageModel(string Key, ZooModel Model);

/// <summary>Bagaimana hasil model utama dibaca di kode contoh.</summary>
public enum SnippetStyle
{
    /// <summary>Model YOLO dengan metadata: SDK langsung memberi <see cref="DetectionFrame"/>.</summary>
    Detections,
    /// <summary>Tensor mentah yang didekode dekoder galeri.</summary>
    Tensors,
    /// <summary>Klasifikasi: SDK langsung memberi <see cref="ClassificationFrame"/>.</summary>
    Classification,
}

/// <summary>Satu skenario nyata yang dipamerkan galeri.</summary>
public sealed record UseCase
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Satu kalimat: apa yang dilakukan kamera.</summary>
    public required string Summary { get; init; }

    /// <summary>Masalah nyata yang dipecahkan, dan siapa yang memakainya.</summary>
    public required string Application { get; init; }

    public required IReadOnlyList<string> Sectors { get; init; }

    public required ZooModel Primary { get; init; }

    public IReadOnlyList<StageModel> Stages { get; init; } = [];

    /// <summary>Nama berkas di folder Samples yang direkam untuk use case ini.</summary>
    public required IReadOnlyList<string> Samples { get; init; }

    /// <summary>Catatan jujur tentang batas model atau langkah berikutnya.</summary>
    public string? Note { get; init; }

    public int LiveFps { get; init; } = 15;

    public SnippetStyle Snippet { get; init; } = SnippetStyle.Tensors;

    /// <summary>Baris dekode khas use case untuk kode contoh; variabel <c>result</c> sudah tersedia.</summary>
    public required string SnippetBody { get; init; }

    public required Func<AnalysisContext, Task<GalleryResult>> Analyze { get; init; }
}

/// <summary>
/// Semua yang dibutuhkan satu analisis: tensor model utama, cara memetakan koordinatnya ke
/// gambar, dan jalan untuk menjalankan model tahap kedua di kamera.
/// </summary>
public sealed class AnalysisContext
{
    public required IReadOnlyDictionary<string, Tensor> Tensors { get; init; }

    public required ModelMetadata Metadata { get; init; }

    public required Letterbox Letterbox { get; init; }

    /// <summary>Gambar yang dianalisis: foto asli, atau frame passthrough pada mode live.</summary>
    public required SKBitmap Image { get; init; }

    /// <summary>Menjalankan model tahap kedua di kamera dan mengembalikan tensornya.</summary>
    public required Func<string, ImageFrame, Task<IReadOnlyDictionary<string, Tensor>>> InferStage { get; init; }

    public required Func<string, ModelMetadata> StageMetadata { get; init; }

    public int InputWidth => Metadata.InputWidth;

    public int InputHeight => Metadata.InputHeight;

    /// <summary>Jumlah inferensi tahap kedua yang sudah dijalankan.</summary>
    public int StageInferences { get; private set; }

    public async Task<IReadOnlyDictionary<string, Tensor>> RunStageAsync(string key, ImageFrame frame)
    {
        StageInferences++;
        return await InferStage(key, frame);
    }

    /// <summary>Kotak di piksel model menjadi kotak ternormalisasi di gambar asli.</summary>
    public ResultBox Box(ModelBox box, string label, ResultTone tone = ResultTone.Neutral, string? caption = null, IReadOnlyList<ResultPoint>? keypoints = null)
        => Normalized(box.X0 / InputWidth, box.Y0 / InputHeight, box.X1 / InputWidth, box.Y1 / InputHeight, label, box.Score, tone, caption, keypoints);

    /// <summary>Kotak ternormalisasi terhadap masukan model menjadi kotak di gambar asli.</summary>
    public ResultBox Normalized(float x0, float y0, float x1, float y1, string label, float score, ResultTone tone = ResultTone.Neutral, string? caption = null, IReadOnlyList<ResultPoint>? keypoints = null)
    {
        var (ix0, iy0) = Letterbox.ToImage(x0, y0);
        var (ix1, iy1) = Letterbox.ToImage(x1, y1);
        return new ResultBox
        {
            X0 = Math.Clamp(ix0, 0, 1),
            Y0 = Math.Clamp(iy0, 0, 1),
            X1 = Math.Clamp(ix1, 0, 1),
            Y1 = Math.Clamp(iy1, 0, 1),
            Label = label,
            Score = score,
            Tone = tone,
            Caption = caption,
            Keypoints = keypoints,
        };
    }

    public ResultBox Detection(Detection detection, string label, ResultTone tone = ResultTone.Neutral, string? caption = null)
        => Normalized(detection.Box.XMin, detection.Box.YMin, detection.Box.XMax, detection.Box.YMax, label, detection.Confidence, tone, caption);

    /// <summary>Titik di piksel model menjadi titik ternormalisasi di gambar asli.</summary>
    public ResultPoint Point(float modelX, float modelY, float score = 1)
    {
        var (x, y) = Letterbox.ToImage(modelX / InputWidth, modelY / InputHeight);
        return new ResultPoint(x, y, score);
    }

    /// <summary>Wilayah gambar asli dalam piksel bitmap, dari kotak ternormalisasi.</summary>
    public SKRect PixelRect(ResultBox box)
        => new(box.X0 * Image.Width, box.Y0 * Image.Height, box.X1 * Image.Width, box.Y1 * Image.Height);
}
