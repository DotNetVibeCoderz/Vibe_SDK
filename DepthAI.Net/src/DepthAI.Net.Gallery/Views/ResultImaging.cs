using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DepthAI.Gallery.Inference;
using SkiaSharp;

namespace DepthAI.Gallery.Views;

/// <summary>Mengubah hasil menjadi bitmap yang ditampilkan panggung.</summary>
internal static class ResultImaging
{
    /// <summary>
    /// Gambar setelah efek use case: wajah dipikselkan atau latar diganti. Efek dikerjakan pada
    /// piksel, bukan sebagai overlay, karena itulah yang akan disimpan aplikasi sungguhan.
    /// </summary>
    public static SKBitmap Compose(SKBitmap image, GalleryResult result)
    {
        var output = image.Copy(SKColorType.Bgra8888);
        switch (result.Effect)
        {
            case ImageEffect.PixelateBoxes:
                foreach (var box in result.Boxes)
                {
                    Pixelate(output, box);
                }

                break;

            case ImageEffect.ReplaceBackground when result.Mask is { Kind: MaskKind.Alpha } mask:
                ReplaceBackground(output, image, mask);
                break;
        }

        return output;
    }

    private static void Pixelate(SKBitmap bitmap, ResultBox box)
    {
        // Kotak wajah dari model cukup ketat; dilebarkan sedikit agar rambut dan dagu ikut tertutup.
        var rect = SKRectI.Round(new SKRect(
            (box.X0 - ((box.X1 - box.X0) * 0.15f)) * bitmap.Width,
            (box.Y0 - ((box.Y1 - box.Y0) * 0.15f)) * bitmap.Height,
            (box.X1 + ((box.X1 - box.X0) * 0.15f)) * bitmap.Width,
            (box.Y1 + ((box.Y1 - box.Y0) * 0.1f)) * bitmap.Height));
        rect.Intersect(new SKRectI(0, 0, bitmap.Width, bitmap.Height));
        if (rect.Width < 2 || rect.Height < 2)
        {
            return;
        }

        var cells = Math.Max(4, Math.Min(10, rect.Width / 6));
        var cell = Math.Max(2, rect.Width / cells);
        var pixels = bitmap.GetPixelSpan();
        for (var y0 = rect.Top; y0 < rect.Bottom; y0 += cell)
        {
            for (var x0 = rect.Left; x0 < rect.Right; x0 += cell)
            {
                int r = 0, g = 0, b = 0, n = 0;
                var (x1, y1) = (Math.Min(rect.Right, x0 + cell), Math.Min(rect.Bottom, y0 + cell));
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var p = (y * bitmap.RowBytes) + (x * 4);
                        b += pixels[p];
                        g += pixels[p + 1];
                        r += pixels[p + 2];
                        n++;
                    }
                }

                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var p = (y * bitmap.RowBytes) + (x * 4);
                        pixels[p] = (byte)(b / n);
                        pixels[p + 1] = (byte)(g / n);
                        pixels[p + 2] = (byte)(r / n);
                    }
                }
            }
        }
    }

    private static void ReplaceBackground(SKBitmap output, SKBitmap image, ResultMask mask)
    {
        using var alpha = new SKBitmap(new SKImageInfo(mask.Width, mask.Height, SKColorType.Alpha8, SKAlphaType.Unpremul));
        mask.Values.AsSpan(0, mask.Width * mask.Height).CopyTo(alpha.GetPixelSpan());

        using var canvas = new SKCanvas(output);
        var bounds = SKRect.Create(output.Width, output.Height);

        // Latar virtual: gradien permukaan Depth Field dengan satu pita cahaya Turbo yang samar.
        using (var background = new SKPaint())
        {
            background.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(output.Width, output.Height),
                [new SKColor(0x1B, 0x3A, 0x4B), new SKColor(0x14, 0x1C, 0x21), new SKColor(0x30, 0x12, 0x3B)],
                [0f, 0.55f, 1f],
                SKShaderTileMode.Clamp);
            canvas.DrawRect(bounds, background);
        }

        using (var band = new SKPaint())
        {
            band.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, output.Height * 0.78f),
                new SKPoint(output.Width, output.Height * 0.62f),
                [new SKColor(0x1B, 0xCF, 0xD4, 0), new SKColor(0x62, 0xFC, 0x6B, 40), new SKColor(0xFE, 0x9B, 0x2D, 0)],
                SKShaderTileMode.Clamp);
            canvas.DrawRect(bounds, band);
        }

        // Orang digambar ulang di atas latar, dibatasi mask yang diperhalus saat diperbesar.
        canvas.SaveLayer(bounds, null);
        using (var source = SKImage.FromBitmap(image))
        {
            canvas.DrawImage(source, bounds, new SKSamplingOptions(SKFilterMode.Linear));
        }

        using (var maskImage = SKImage.FromBitmap(alpha))
        // Mask model hanya 256×144; tanpa sedikit blur tepinya bergerigi saat diperbesar.
        using (var clip = new SKPaint { BlendMode = SKBlendMode.DstIn, ImageFilter = SKImageFilter.CreateBlur(output.Width / 500f, output.Width / 500f) })
        {
            canvas.DrawImage(maskImage, bounds, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), clip);
        }

        canvas.Restore();
    }

    /// <summary>Mask kelas/instans atau heatmap sebagai lapisan berwarna semi-transparan.</summary>
    public static WriteableBitmap? BuildOverlay(GalleryResult result)
    {
        if (result.Heatmap is { } heat)
        {
            return Paint(heat.Width, heat.Height, i =>
            {
                var v = heat.Values[i];
                return v < 0.04f ? default : (Palette.Turbo(0.15f + (v * 0.85f)), (byte)(80 + (v * 150)));
            });
        }

        if (result.Mask is not { } mask || mask.Kind == MaskKind.Alpha)
        {
            return null;
        }

        var colors = mask.Kind == MaskKind.Instances
            ? result.Boxes.Select(Palette.ForBox).ToArray()
            : mask.Labels.Select(Palette.ForLabel).ToArray();

        return Paint(mask.Width, mask.Height, i =>
        {
            var v = mask.Values[i];
            var index = mask.Kind == MaskKind.Instances ? v - 1 : v;
            return mask.Kind == MaskKind.Instances && v == 0 || v == byte.MaxValue || index >= colors.Length
                ? default
                : (colors[index], (byte)120);
        });
    }

    private static WriteableBitmap Paint(int width, int height, Func<int, (Color Color, byte Alpha)> pixel)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var row = new byte[width * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (color, alpha) = pixel((y * width) + x);
                row[(x * 4) + 0] = (byte)(color.B * alpha / 255);
                row[(x * 4) + 1] = (byte)(color.G * alpha / 255);
                row[(x * 4) + 2] = (byte)(color.R * alpha / 255);
                row[(x * 4) + 3] = alpha;
            }

            Marshal.Copy(row, 0, buffer.Address + (y * buffer.RowBytes), row.Length);
        }

        return bitmap;
    }

    public static WriteableBitmap ToAvalonia(SKBitmap source)
    {
        using var bgra = source.ColorType == SKColorType.Bgra8888 ? null : source.Copy(SKColorType.Bgra8888);
        var bitmap = bgra ?? source;
        var output = new WriteableBitmap(new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = output.Lock();
        var pixels = bitmap.GetPixelSpan();
        var rowBytes = bitmap.Width * 4;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = pixels.Slice(y * bitmap.RowBytes, rowBytes);
            unsafe
            {
                row.CopyTo(new Span<byte>((byte*)buffer.Address + (y * buffer.RowBytes), rowBytes));
            }
        }

        return output;
    }
}
