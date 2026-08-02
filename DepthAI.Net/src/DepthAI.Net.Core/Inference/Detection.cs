using System.Globalization;

namespace DepthAI.Inference;

/// <summary>
/// Kotak pembatas dalam koordinat ternormalisasi (0..1) relatif terhadap ukuran
/// frame. Ternormalisasi supaya hasil tetap benar walau frame di-resize di host.
/// </summary>
public readonly record struct BoundingBox(float XMin, float YMin, float XMax, float YMax)
{
    public float Width => XMax - XMin;

    public float Height => YMax - YMin;

    public float CenterX => (XMin + XMax) / 2f;

    public float CenterY => (YMin + YMax) / 2f;

    public float Area => Math.Max(0, Width) * Math.Max(0, Height);

    /// <summary>Memetakan ke piksel pada frame berukuran <paramref name="width"/> x <paramref name="height"/>.</summary>
    public (int X, int Y, int Width, int Height) ToPixels(int width, int height)
    {
        var x = (int)MathF.Round(XMin * width);
        var y = (int)MathF.Round(YMin * height);
        var w = (int)MathF.Round(Width * width);
        var h = (int)MathF.Round(Height * height);
        return (x, y, w, h);
    }

    /// <summary>Menjepit koordinat ke rentang 0..1; jaring pengaman untuk keluaran model yang meleset.</summary>
    public BoundingBox Clamp() => new(
        Math.Clamp(XMin, 0f, 1f),
        Math.Clamp(YMin, 0f, 1f),
        Math.Clamp(XMax, 0f, 1f),
        Math.Clamp(YMax, 0f, 1f));

    /// <summary>Intersection over Union — dipakai non-maximum suppression.</summary>
    public float IntersectionOverUnion(BoundingBox other)
    {
        var interXMin = Math.Max(XMin, other.XMin);
        var interYMin = Math.Max(YMin, other.YMin);
        var interXMax = Math.Min(XMax, other.XMax);
        var interYMax = Math.Min(YMax, other.YMax);

        var interW = Math.Max(0, interXMax - interXMin);
        var interH = Math.Max(0, interYMax - interYMin);
        var intersection = interW * interH;
        var union = Area + other.Area - intersection;

        return union <= 0 ? 0 : intersection / union;
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"[{XMin:F3},{YMin:F3} → {XMax:F3},{YMax:F3}]");
}

/// <summary>Titik dalam ruang kamera, meter. Sumbu Z menjauh dari kamera.</summary>
public readonly record struct SpatialPoint(float X, float Y, float Z)
{
    /// <summary>Jarak euclidean dari kamera, meter.</summary>
    public float Distance => MathF.Sqrt((X * X) + (Y * Y) + (Z * Z));
}

/// <summary>Satu objek terdeteksi.</summary>
public sealed record Detection
{
    /// <summary>Indeks kelas mentah dari model.</summary>
    public required int LabelIndex { get; init; }

    /// <summary>Nama kelas dari metadata model; jatuh ke indeks bila label tidak tersedia.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Skor keyakinan, 0..1.</summary>
    public required float Confidence { get; init; }

    public required BoundingBox Box { get; init; }

    /// <summary>
    /// Posisi 3D bila deteksi berasal dari spatial detection network (butuh stereo depth);
    /// <see langword="null"/> untuk deteksi 2D biasa.
    /// </summary>
    public SpatialPoint? Spatial { get; init; }

    public override string ToString()
    {
        var name = string.IsNullOrEmpty(Label) ? LabelIndex.ToString(CultureInfo.InvariantCulture) : Label;
        var summary = string.Create(CultureInfo.InvariantCulture, $"{name} {Confidence:P1} {Box}");

        return Spatial is { } spatial
            ? summary + string.Create(CultureInfo.InvariantCulture, $" @ {spatial.Z:F2}m")
            : summary;
    }
}

/// <summary>Satu hasil klasifikasi.</summary>
public sealed record Classification
{
    public required int LabelIndex { get; init; }

    public string Label { get; init; } = string.Empty;

    public required float Confidence { get; init; }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Label} {Confidence:P1}");
}
