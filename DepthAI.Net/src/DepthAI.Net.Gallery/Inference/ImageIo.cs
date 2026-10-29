using DepthAI.Streaming;
using SkiaSharp;

namespace DepthAI.Gallery.Inference;

/// <summary>Konversi antara bitmap Skia dan <see cref="ImageFrame"/> BGR yang dikirim ke kamera.</summary>
internal static class ImageIo
{
    /// <summary>Abu-abu 114: warna bingkai yang dipakai saat model YOLO dilatih.</summary>
    private static readonly SKColor PadColor = new(114, 114, 114);

    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    public static SKBitmap Load(string path)
        => SKBitmap.Decode(path) ?? throw new InvalidDataException($"Gambar tidak bisa dibaca: {path}");

    /// <summary>Gambar utuh, diperkecil dan diberi bingkai ke ukuran masukan model.</summary>
    public static ImageFrame ToModelInput(SKBitmap source, Letterbox letterbox)
    {
        using var target = new SKBitmap(new SKImageInfo(letterbox.TargetWidth, letterbox.TargetHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(PadColor);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(
                image,
                SKRect.Create(letterbox.PadX, letterbox.PadY, letterbox.ContentWidth, letterbox.ContentHeight),
                Sampling);
        }

        return ToFrame(target);
    }

    /// <summary>
    /// Potongan gambar, diregangkan ke ukuran model. Dipakai tahap kedua (umur, emosi) yang
    /// dilatih dengan potongan wajah persegi, jadi wilayahnya sebaiknya sudah persegi.
    /// </summary>
    public static ImageFrame CropToModel(SKBitmap source, SKRect region, int width, int height)
    {
        using var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.Black);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, region, SKRect.Create(width, height), Sampling);
        }

        return ToFrame(target);
    }

    public static ImageFrame ToFrame(SKBitmap bitmap)
    {
        using var bgra = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
        var source = bgra ?? bitmap;
        var pixels = source.GetPixelSpan();
        var bgr = new byte[source.Width * source.Height * 3];

        for (var y = 0; y < source.Height; y++)
        {
            var row = pixels.Slice(y * source.RowBytes, source.Width * 4);
            var output = bgr.AsSpan(y * source.Width * 3, source.Width * 3);
            for (var x = 0; x < source.Width; x++)
            {
                output[(x * 3) + 0] = row[(x * 4) + 0];
                output[(x * 3) + 1] = row[(x * 4) + 1];
                output[(x * 3) + 2] = row[(x * 4) + 2];
            }
        }

        return ImageFrame.CopyFrom(bgr, source.Width, source.Height, PixelFormat.Bgr888);
    }

    /// <summary>Frame BGR dari kamera menjadi bitmap untuk ditampilkan.</summary>
    public static SKBitmap FromFrame(ImageFrame frame)
    {
        var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        var bgrOrder = frame.Format != PixelFormat.Rgb888;

        for (var y = 0; y < frame.Height; y++)
        {
            var row = frame.GetRow(y);
            var output = pixels.Slice(y * bitmap.RowBytes, frame.Width * 4);
            for (var x = 0; x < frame.Width; x++)
            {
                var (b, g, r) = bgrOrder
                    ? (row[(x * 3) + 0], row[(x * 3) + 1], row[(x * 3) + 2])
                    : (row[(x * 3) + 2], row[(x * 3) + 1], row[(x * 3) + 0]);
                output[(x * 4) + 0] = b;
                output[(x * 4) + 1] = g;
                output[(x * 4) + 2] = r;
                output[(x * 4) + 3] = 255;
            }
        }

        return bitmap;
    }
}
