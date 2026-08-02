using DepthAI.Streaming;

namespace DepthAI.Imaging;

/// <summary>
/// Konversi format piksel di host. Semua adapter imaging bermuara di sini, sehingga
/// hanya ada satu implementasi tiap konversi yang perlu dijaga kebenarannya.
/// </summary>
public static class PixelConverter
{
    /// <summary>
    /// Menormalkan frame apa pun menjadi BGR888 interleaved — bentuk yang diterima
    /// semua pustaka imaging. Mengembalikan buffer baru.
    /// </summary>
    public static byte[] ToBgr888(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var pixels = frame.Width * frame.Height;
        var source = frame.Pixels;

        switch (frame.Format)
        {
            case PixelFormat.Bgr888:
                return source[..(pixels * 3)].ToArray();

            case PixelFormat.Rgb888:
            {
                var destination = new byte[pixels * 3];
                for (var i = 0; i < pixels; i++)
                {
                    var offset = i * 3;
                    destination[offset] = source[offset + 2];
                    destination[offset + 1] = source[offset + 1];
                    destination[offset + 2] = source[offset];
                }

                return destination;
            }

            case PixelFormat.Gray8:
            {
                var destination = new byte[pixels * 3];
                for (var i = 0; i < pixels; i++)
                {
                    var value = source[i];
                    var offset = i * 3;
                    destination[offset] = value;
                    destination[offset + 1] = value;
                    destination[offset + 2] = value;
                }

                return destination;
            }

            case PixelFormat.Bgra8888:
            case PixelFormat.Rgba8888:
            {
                var swap = frame.Format == PixelFormat.Rgba8888;
                var destination = new byte[pixels * 3];
                for (var i = 0; i < pixels; i++)
                {
                    var src = i * 4;
                    var dst = i * 3;
                    destination[dst] = swap ? source[src + 2] : source[src];
                    destination[dst + 1] = source[src + 1];
                    destination[dst + 2] = swap ? source[src] : source[src + 2];
                }

                return destination;
            }

            case PixelFormat.Nv12:
                return Nv12ToBgr(source, frame.Width, frame.Height);

            case PixelFormat.Jpeg:
                throw new NotSupportedException(
                    "Frame JPEG sudah terkompresi. Dekode dengan pustaka imaging Anda "
                    + "(misalnya Image.Load pada ImageSharp) alih-alih memakai PixelConverter.");

            default:
                throw new NotSupportedException($"Format piksel {frame.Format} belum didukung.");
        }
    }

    /// <summary>Mengubah BGR888 interleaved menjadi RGB888 di tempat yang baru.</summary>
    public static byte[] BgrToRgb(ReadOnlySpan<byte> bgr)
    {
        var destination = new byte[bgr.Length];
        for (var i = 0; i + 2 < bgr.Length; i += 3)
        {
            destination[i] = bgr[i + 2];
            destination[i + 1] = bgr[i + 1];
            destination[i + 2] = bgr[i];
        }

        return destination;
    }

    /// <summary>
    /// Mengubah planar (BBB…GGG…RRR) menjadi interleaved. Node neural network memancarkan
    /// planar, sedangkan kode tampilan menginginkan interleaved.
    /// </summary>
    public static byte[] PlanarToInterleaved(ReadOnlySpan<byte> planar, int width, int height)
    {
        var pixels = width * height;
        ArgumentOutOfRangeException.ThrowIfLessThan(planar.Length, pixels * 3);

        var destination = new byte[pixels * 3];
        for (var i = 0; i < pixels; i++)
        {
            var offset = i * 3;
            destination[offset] = planar[i];
            destination[offset + 1] = planar[pixels + i];
            destination[offset + 2] = planar[(2 * pixels) + i];
        }

        return destination;
    }

    /// <summary>
    /// Konversi NV12 (Y penuh diikuti bidang UV yang di-subsample) ke BGR memakai
    /// koefisien BT.601 rentang penuh.
    /// </summary>
    private static byte[] Nv12ToBgr(ReadOnlySpan<byte> source, int width, int height)
    {
        var pixels = width * height;
        ArgumentOutOfRangeException.ThrowIfLessThan(source.Length, pixels * 3 / 2);

        var destination = new byte[pixels * 3];
        var uvPlane = pixels;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var luma = source[(y * width) + x];

                // Bidang kroma memiliki setengah resolusi pada kedua sumbu.
                var uvIndex = uvPlane + ((y / 2) * width) + (x / 2 * 2);
                var u = source[uvIndex] - 128;
                var v = source[uvIndex + 1] - 128;

                var offset = (((y * width) + x)) * 3;
                destination[offset] = ClampToByte(luma + (1.772 * u));
                destination[offset + 1] = ClampToByte(luma - (0.344136 * u) - (0.714136 * v));
                destination[offset + 2] = ClampToByte(luma + (1.402 * v));
            }
        }

        return destination;
    }

    private static byte ClampToByte(double value) => (byte)Math.Clamp(value, 0, 255);
}
