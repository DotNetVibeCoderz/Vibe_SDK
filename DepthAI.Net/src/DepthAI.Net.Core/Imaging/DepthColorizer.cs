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

    /// <summary>
    /// Mewarnai kedalaman dengan semantik <c>dai::utility::colorizeDepthFrame</c> (depthai-core 3.9):
    /// rentang dalam milimeter, skala logaritmik opsional, dan rentang otomatis.
    /// </summary>
    /// <param name="frame">Frame sumber.</param>
    /// <param name="minMillimeters">Batas bawah rentang, mm. Bawaan upstream 300 mm.</param>
    /// <param name="maxMillimeters">
    /// Batas atas rentang, mm. Bila tidak lebih besar dari <paramref name="minMillimeters"/>
    /// (misalnya keduanya 0), rentang dihitung dari persentil ke-3 dan ke-95 piksel valid.
    /// </param>
    /// <param name="colorMap">Peta warna. Upstream memakai Jet sebagai bawaan.</param>
    /// <param name="useLog">
    /// Skala logaritmik: detail di jarak dekat lebih terlihat, yang jauh dimampatkan.
    /// </param>
    /// <returns>
    /// Buffer BGR888. Piksel tanpa pengukuran selalu hitam. Bila rentang tidak bisa
    /// ditentukan (tidak ada piksel valid), seluruh gambar hitam, sama seperti upstream.
    /// </returns>
    public static byte[] Colorize(
        DepthFrame frame,
        float minMillimeters = 300f,
        float maxMillimeters = 12_000f,
        DepthColorMap colorMap = DepthColorMap.Jet,
        bool useLog = true)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var source = frame.Millimeters;
        var destination = new byte[frame.Width * frame.Height * 3];

        if (maxMillimeters <= minMillimeters)
        {
            (minMillimeters, maxMillimeters) = AutoRange(source);
            if (maxMillimeters <= minMillimeters)
            {
                return destination;
            }
        }

        // Epsilon mencegah log(0), persis seperti implementasi OpenCV upstream.
        const float Epsilon = 1e-6f;
        var low = useLog ? MathF.Log(minMillimeters + Epsilon) : minMillimeters;
        var high = useLog ? MathF.Log(maxMillimeters + Epsilon) : maxMillimeters;
        if (high <= low)
        {
            return destination;
        }

        var scale = 255f / (high - low);

        // Upstream memetakan ke 8-bit dulu baru menerapkan colormap, jadi warna hanya punya
        // 256 kemungkinan; menghitungnya sekali lebih murah daripada polinomial per piksel.
        var lut = new (byte R, byte G, byte B)[256];
        for (var i = 0; i < lut.Length; i++)
        {
            lut[i] = Map(i / 255f, colorMap);
        }

        for (var i = 0; i < source.Length; i++)
        {
            var mm = source[i];
            if (mm == 0)
            {
                continue;
            }

            var value = useLog ? MathF.Log(mm + Epsilon) : mm;

            // convertTo(CV_8U) OpenCV membulatkan lalu menjenuhkan ke 0..255.
            var index = (int)Math.Clamp(MathF.Round((value - low) * scale), 0f, 255f);
            var (r, g, b) = lut[index];

            var offset = i * 3;
            destination[offset] = b;
            destination[offset + 1] = g;
            destination[offset + 2] = r;
        }

        return destination;
    }

    /// <summary>
    /// Persentil ke-3 dan ke-95 dari piksel valid, dengan indeks
    /// <c>round(p/100 * (n-1))</c> seperti <c>computeAutoDepthRange</c> upstream.
    /// </summary>
    /// <remarks>
    /// Histogram 16-bit menggantikan sort upstream: hasilnya identik karena nilai
    /// kedalaman berupa bilangan bulat, tapi waktunya linear.
    /// </remarks>
    internal static (float Min, float Max) AutoRange(ReadOnlySpan<ushort> millimeters)
    {
        var histogram = new int[ushort.MaxValue + 1];
        var valid = 0;

        foreach (var mm in millimeters)
        {
            if (mm != 0)
            {
                histogram[mm]++;
                valid++;
            }
        }

        if (valid == 0)
        {
            return (0f, 0f);
        }

        return (ValueAtRank(histogram, Rank(3.0)), ValueAtRank(histogram, Rank(95.0)));

        long Rank(double percentile) => (long)Math.Round(percentile / 100.0 * (valid - 1), MidpointRounding.AwayFromZero);

        static float ValueAtRank(int[] histogram, long rank)
        {
            long seen = 0;
            for (var value = 1; value < histogram.Length; value++)
            {
                seen += histogram[value];
                if (seen > rank)
                {
                    return value;
                }
            }

            return histogram.Length - 1;
        }
    }

    /// <summary>
    /// Versi <see cref="Colorize"/> yang membungkus hasilnya sebagai <see cref="ImageFrame"/>
    /// dan membawa nomor urut serta timestamp frame sumber, seperti <c>setMetadata</c> upstream.
    /// </summary>
    public static ImageFrame ColorizeToImageFrame(
        DepthFrame frame,
        float minMillimeters = 300f,
        float maxMillimeters = 12_000f,
        DepthColorMap colorMap = DepthColorMap.Jet,
        bool useLog = true)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var pixels = Colorize(frame, minMillimeters, maxMillimeters, colorMap, useLog);
        var image = ImageFrame.Wrap(pixels, frame.Width, frame.Height, Streaming.PixelFormat.Bgr888);
        image.SequenceNumber = frame.SequenceNumber;
        image.DeviceTimestamp = frame.DeviceTimestamp;
        image.HostTimestamp = frame.HostTimestamp;
        image.StreamName = frame.StreamName;
        return image;
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
