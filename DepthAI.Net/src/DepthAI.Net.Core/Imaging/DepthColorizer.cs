using DepthAI.Streaming;

namespace DepthAI.Imaging;

/// <summary>Peta warna untuk memvisualkan kedalaman.</summary>
public enum DepthColorMap
{
    /// <summary>Biru (dekat) → merah (jauh). Umum dipakai di tooling depth.</summary>
    Jet,
    /// <summary>Peta perseptual yang seragam; perbedaan halus lebih mudah terlihat.</summary>
    Turbo,
    /// <summary>Grayscale, dekat = terang.</summary>
    Grayscale,
}

/// <summary>
/// Mengubah peta kedalaman menjadi piksel berwarna untuk ditampilkan.
/// </summary>
/// <remarks>
/// Ada di Core dan menghasilkan byte BGR polos supaya tidak menyeret dependensi imaging
/// apa pun — paket adapter (ImageSharp, SkiaSharp, System.Drawing) tinggal membungkus
/// keluarannya.
/// </remarks>
public static class DepthColorizer
{
    /// <summary>
    /// Mewarnai frame kedalaman menjadi buffer BGR888 baru.
    /// </summary>
    /// <param name="frame">Frame sumber.</param>
    /// <param name="colorMap">Peta warna yang dipakai.</param>
    /// <param name="minMeters">Jarak yang dipetakan ke ujung "dekat"; null memakai rentang frame.</param>
    /// <param name="maxMeters">Jarak yang dipetakan ke ujung "jauh".</param>
    /// <param name="invalidColor">
    /// Warna untuk piksel tanpa pengukuran. Hitam dipilih sebagai bawaan supaya lubang
    /// data terbaca sebagai "tidak diketahui", bukan sebagai objek yang sangat dekat.
    /// </param>
    public static byte[] ToBgr(
        DepthFrame frame,
        DepthColorMap colorMap = DepthColorMap.Turbo,
        float? minMeters = null,
        float? maxMeters = null,
        (byte B, byte G, byte R) invalidColor = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var min = minMeters ?? frame.MinDepthMeters;
        var max = maxMeters ?? frame.MaxDepthMeters;
        var range = Math.Max(0.001f, max - min);

        var source = frame.Millimeters;
        var destination = new byte[frame.Width * frame.Height * 3];

        for (var i = 0; i < source.Length; i++)
        {
            var offset = i * 3;
            var mm = source[i];

            if (mm == 0)
            {
                destination[offset] = invalidColor.B;
                destination[offset + 1] = invalidColor.G;
                destination[offset + 2] = invalidColor.R;
                continue;
            }

            var normalized = Math.Clamp(((mm / 1000f) - min) / range, 0f, 1f);
            var (r, g, b) = Map(normalized, colorMap);

            destination[offset] = b;
            destination[offset + 1] = g;
            destination[offset + 2] = r;
        }

        return destination;
    }

    /// <summary>Mewarnai kedalaman dan membungkusnya sebagai <see cref="ImageFrame"/>.</summary>
    public static ImageFrame ToImageFrame(
        DepthFrame frame,
        DepthColorMap colorMap = DepthColorMap.Turbo,
        float? minMeters = null,
        float? maxMeters = null)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var pixels = ToBgr(frame, colorMap, minMeters, maxMeters);
        return ImageFrame.Wrap(pixels, frame.Width, frame.Height, Streaming.PixelFormat.Bgr888);
    }

    private static (byte R, byte G, byte B) Map(float t, DepthColorMap colorMap) => colorMap switch
    {
        DepthColorMap.Grayscale => Grayscale(t),
        DepthColorMap.Jet => Jet(t),
        _ => Turbo(t),
    };

    private static (byte R, byte G, byte B) Grayscale(float t)
    {
        // Dekat = terang lebih cocok dengan intuisi "objek dekat menonjol".
        var value = (byte)(255 * (1 - t));
        return (value, value, value);
    }

    private static (byte R, byte G, byte B) Jet(float t)
    {
        var r = Math.Clamp(1.5f - Math.Abs((4 * t) - 3), 0f, 1f);
        var g = Math.Clamp(1.5f - Math.Abs((4 * t) - 2), 0f, 1f);
        var b = Math.Clamp(1.5f - Math.Abs((4 * t) - 1), 0f, 1f);
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    /// <summary>
    /// Aproksimasi polinomial peta Turbo Google. Cukup akurat untuk visualisasi
    /// tanpa perlu tabel lookup 256 entri.
    /// </summary>
    private static (byte R, byte G, byte B) Turbo(float t)
    {
        var r = 0.1357f + (t * (4.5974f + (t * (-42.3277f + (t * (130.5887f + (t * (-150.5666f + (t * 58.1375f)))))))));
        var g = 0.0914f + (t * (2.1856f + (t * (4.8052f + (t * (-14.0195f + (t * (4.2109f + (t * 2.7747f)))))))));
        var b = 0.1067f + (t * (12.5925f + (t * (-60.1097f + (t * (109.0745f + (t * (-88.5066f + (t * 26.8183f)))))))));

        return (
            (byte)(Math.Clamp(r, 0f, 1f) * 255),
            (byte)(Math.Clamp(g, 0f, 1f) * 255),
            (byte)(Math.Clamp(b, 0f, 1f) * 255));
    }
}
