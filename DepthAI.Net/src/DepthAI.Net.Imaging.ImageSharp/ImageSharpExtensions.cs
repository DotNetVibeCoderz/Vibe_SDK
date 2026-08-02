using DepthAI.Inference;
using DepthAI.Streaming;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// ImageSharp punya tipe ImageFrame sendiri (satu frame animasi), yang tidak ada
// hubungannya dengan frame perangkat kita. Alias ini menghilangkan ambiguitasnya.
using ImageFrame = DepthAI.Streaming.ImageFrame;

namespace DepthAI.Imaging;

/// <summary>
/// Jembatan antara frame DepthAI dan ImageSharp — pustaka imaging lintas platform
/// yang direkomendasikan untuk aplikasi .NET modern.
/// </summary>
public static class ImageSharpExtensions
{
    /// <summary>
    /// Mengubah frame menjadi <see cref="Image{TPixel}"/> ImageSharp.
    /// </summary>
    /// <remarks>
    /// Piksel disalin: gambar yang dihasilkan tidak terikat siklus hidup pool frame,
    /// jadi aman disimpan atau dikirim ke thread lain setelah frame dibuang.
    /// </remarks>
    public static Image<Rgb24> ToImage(this ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Format == PixelFormat.Jpeg)
        {
            // Frame JPEG sudah terkompresi; serahkan dekodingnya ke ImageSharp.
            return Image.Load<Rgb24>(frame.Pixels);
        }

        var bgr = PixelConverter.ToBgr888(frame);
        var image = new Image<Rgb24>(frame.Width, frame.Height);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var offset = y * frame.Width * 3;

                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = offset + (x * 3);
                    row[x] = new Rgb24(bgr[pixel + 2], bgr[pixel + 1], bgr[pixel]);
                }
            }
        });

        return image;
    }

    /// <summary>Mewarnai peta kedalaman menjadi gambar yang bisa ditampilkan.</summary>
    public static Image<Rgb24> ToImage(
        this DepthFrame frame,
        DepthColorMap colorMap = DepthColorMap.Turbo,
        float? minMeters = null,
        float? maxMeters = null)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using var colorized = DepthColorizer.ToImageFrame(frame, colorMap, minMeters, maxMeters);
        return colorized.ToImage();
    }

    /// <summary>
    /// Mengubah frame menjadi gambar dengan kotak deteksi tergambar di atasnya.
    /// </summary>
    public static Image<Rgb24> ToImageWithDetections(
        this ImageFrame frame,
        IEnumerable<Detection> detections,
        int thickness = 2)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(detections);

        var bgr = PixelConverter.ToBgr888(frame);
        FrameOverlay.DrawDetections(bgr, frame.Width, frame.Height, detections, thickness);

        using var annotated = ImageFrame.Wrap(bgr, frame.Width, frame.Height, PixelFormat.Bgr888);
        return annotated.ToImage();
    }

    /// <summary>Menyimpan frame ke berkas; format ditentukan dari ekstensi.</summary>
    public static async Task SaveAsync(
        this ImageFrame frame,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var image = frame.ToImage();
        await image.SaveAsync(path, cancellationToken);
    }

    /// <summary>Menyimpan peta kedalaman yang sudah diwarnai ke berkas gambar.</summary>
    public static async Task SaveAsync(
        this DepthFrame frame,
        string path,
        DepthColorMap colorMap = DepthColorMap.Turbo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var image = frame.ToImage(colorMap);
        await image.SaveAsync(path, cancellationToken);
    }

    /// <summary>
    /// Menyimpan kedalaman mentah sebagai PNG 16-bit grayscale, sehingga nilai
    /// milimeter tetap utuh untuk analisis lanjutan — berbeda dari versi berwarna
    /// yang hanya untuk dilihat manusia.
    /// </summary>
    public static async Task SaveRawDepthAsync(
        this DepthFrame frame,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var image = new Image<L16>(frame.Width, frame.Height);

        // Disalin ke array karena span tidak boleh ditangkap oleh lambda ProcessPixelRows.
        var source = frame.ToArray();

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new L16(source[(y * frame.Width) + x]);
                }
            }
        });

        await image.SaveAsPngAsync(path, cancellationToken);
    }

    /// <summary>Meng-encode frame menjadi byte JPEG — praktis untuk streaming lewat HTTP.</summary>
    public static async Task<byte[]> ToJpegAsync(
        this ImageFrame frame,
        int quality = 85,
        CancellationToken cancellationToken = default)
    {
        using var image = frame.ToImage();
        using var buffer = new MemoryStream();

        await image.SaveAsJpegAsync(
            buffer,
            new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality },
            cancellationToken);

        return buffer.ToArray();
    }
}
