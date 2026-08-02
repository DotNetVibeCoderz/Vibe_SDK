using DepthAI.Inference;
using DepthAI.Streaming;
using SkiaSharp;

namespace DepthAI.Imaging;

/// <summary>
/// Jembatan antara frame DepthAI dan SkiaSharp. Skia adalah backend rendering Avalonia
/// dan MAUI, jadi jalur ini yang paling murah untuk menampilkan frame di kedua toolkit.
/// </summary>
public static class SkiaSharpExtensions
{
    /// <summary>Mengubah frame menjadi <see cref="SKBitmap"/>. Pemanggil memiliki bitmap hasilnya.</summary>
    public static SKBitmap ToBitmap(this ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Format == PixelFormat.Jpeg)
        {
            return SKBitmap.Decode(frame.Pixels.ToArray());
        }

        var bgr = PixelConverter.ToBgr888(frame);

        // Skia tidak punya tipe BGR 24-bit, jadi diperluas ke BGRA 32-bit dengan alfa penuh.
        var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var destination = bitmap.GetPixelSpan();
        var writable = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(destination), destination.Length);

        var pixels = frame.Width * frame.Height;
        for (var i = 0; i < pixels; i++)
        {
            var source = i * 3;
            var target = i * 4;
            writable[target] = bgr[source];
            writable[target + 1] = bgr[source + 1];
            writable[target + 2] = bgr[source + 2];
            writable[target + 3] = 255;
        }

        return bitmap;
    }

    /// <summary>Mewarnai peta kedalaman menjadi bitmap Skia.</summary>
    public static SKBitmap ToBitmap(
        this DepthFrame frame,
        DepthColorMap colorMap = DepthColorMap.Turbo,
        float? minMeters = null,
        float? maxMeters = null)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using var colorized = DepthColorizer.ToImageFrame(frame, colorMap, minMeters, maxMeters);
        return colorized.ToBitmap();
    }

    /// <summary>Mengubah frame menjadi <see cref="SKImage"/> yang siap digambar.</summary>
    public static SKImage ToSKImage(this ImageFrame frame)
    {
        using var bitmap = frame.ToBitmap();
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>
    /// Menggambar deteksi di atas kanvas memakai koordinat piksel yang benar.
    /// </summary>
    /// <remarks>
    /// Berbeda dari <c>FrameOverlay</c> di Core, versi ini bisa menggambar teks —
    /// Skia sudah membawa rendering font sendiri.
    /// </remarks>
    public static void DrawDetections(
        this SKCanvas canvas,
        IEnumerable<Detection> detections,
        float width,
        float height,
        float strokeWidth = 2f,
        float fontSize = 14f)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(detections);

        using var font = new SKFont(SKTypeface.Default, fontSize);
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = strokeWidth };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };

        foreach (var detection in detections)
        {
            var (r, g, b) = FrameOverlay.ColorFor(detection.LabelIndex);
            var color = new SKColor(r, g, b);

            var box = detection.Box;
            var rect = new SKRect(box.XMin * width, box.YMin * height, box.XMax * width, box.YMax * height);

            stroke.Color = color;
            canvas.DrawRect(rect, stroke);

            var label = $"{detection.Label} {detection.Confidence:P0}"
                + (detection.Spatial is { } spatial ? $"  {spatial.Z:F2}m" : string.Empty);

            var labelWidth = font.MeasureText(label) + 10;
            var labelHeight = fontSize + 6;

            // Chip label diletakkan di atas kotak, kecuali kotak menempel tepi atas.
            var labelTop = rect.Top >= labelHeight ? rect.Top - labelHeight : rect.Top;

            fill.Color = color;
            canvas.DrawRect(new SKRect(rect.Left, labelTop, rect.Left + labelWidth, labelTop + labelHeight), fill);
            canvas.DrawText(label, rect.Left + 5, labelTop + fontSize, SKTextAlign.Left, font, text);
        }
    }

    /// <summary>Meng-encode frame menjadi byte PNG atau JPEG.</summary>
    public static byte[] Encode(this ImageFrame frame, SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 90)
    {
        using var image = frame.ToSKImage();
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }
}
