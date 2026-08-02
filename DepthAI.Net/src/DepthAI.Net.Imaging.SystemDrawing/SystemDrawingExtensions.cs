using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DepthAI.Streaming;

namespace DepthAI.Imaging;

/// <summary>
/// Jembatan ke <see cref="Bitmap"/> System.Drawing untuk aplikasi WinForms dan WPF
/// yang sudah ada.
/// </summary>
/// <remarks>
/// Khusus Windows. Untuk aplikasi lintas platform pakai adapter ImageSharp atau SkiaSharp.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class SystemDrawingExtensions
{
    /// <summary>Mengubah frame menjadi <see cref="Bitmap"/> 24-bit. Pemanggil wajib membuangnya.</summary>
    public static Bitmap ToBitmap(this ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Format == Streaming.PixelFormat.Jpeg)
        {
            using var stream = new MemoryStream(frame.ToArray());
            return new Bitmap(stream);
        }

        var bgr = PixelConverter.ToBgr888(frame);
        var bitmap = new Bitmap(frame.Width, frame.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

        var data = bitmap.LockBits(
            new Rectangle(0, 0, frame.Width, frame.Height),
            ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);

        try
        {
            // Baris GDI+ dipadatkan ke kelipatan 4 byte, jadi salin per baris
            // alih-alih satu blok — menyalin sekaligus akan menggeser gambar.
            var sourceStride = frame.Width * 3;
            for (var y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(bgr, y * sourceStride, data.Scan0 + (y * data.Stride), sourceStride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    /// <summary>Mewarnai peta kedalaman menjadi bitmap.</summary>
    public static Bitmap ToBitmap(
        this DepthFrame frame,
        DepthColorMap colorMap = DepthColorMap.Turbo,
        float? minMeters = null,
        float? maxMeters = null)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using var colorized = DepthColorizer.ToImageFrame(frame, colorMap, minMeters, maxMeters);
        return colorized.ToBitmap();
    }

    /// <summary>Menyimpan frame ke berkas; format ditentukan dari ekstensi.</summary>
    public static void Save(this ImageFrame frame, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var bitmap = frame.ToBitmap();
        bitmap.Save(path);
    }
}
