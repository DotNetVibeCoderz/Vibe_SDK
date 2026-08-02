using DepthAI.Inference;
using DepthAI.Streaming;

namespace DepthAI.Imaging;

/// <summary>
/// Menggambar anotasi langsung di atas buffer piksel BGR888.
/// </summary>
/// <remarks>
/// Hanya bentuk geometris, tanpa teks: rendering teks membutuhkan font dan shaping,
/// yang berarti dependensi berat di Core. Lapisan UI (Avalonia, Blazor, WPF) sudah
/// punya rendering teks sendiri dan lebih baik menggambar label di sana.
/// </remarks>
public static class FrameOverlay
{
    /// <summary>Palet stabil per indeks kelas, sehingga objek yang sama selalu berwarna sama.</summary>
    private static readonly (byte R, byte G, byte B)[] Palette =
    [
        (232, 93, 117), (86, 182, 194), (247, 184, 75), (129, 199, 132),
        (149, 117, 205), (255, 138, 101), (79, 195, 247), (240, 98, 146),
    ];

    /// <summary>Warna yang dipakai untuk indeks kelas tertentu.</summary>
    public static (byte R, byte G, byte B) ColorFor(int labelIndex)
        => Palette[Math.Abs(labelIndex) % Palette.Length];

    /// <summary>
    /// Menggambar kotak deteksi ke buffer BGR888 di tempat.
    /// </summary>
    /// <param name="bgr">Buffer piksel yang dimodifikasi.</param>
    /// <param name="width">Lebar gambar, piksel.</param>
    /// <param name="height">Tinggi gambar, piksel.</param>
    /// <param name="detections">Deteksi dengan kotak ternormalisasi.</param>
    /// <param name="thickness">Ketebalan garis, piksel.</param>
    public static void DrawDetections(
        Span<byte> bgr,
        int width,
        int height,
        IEnumerable<Detection> detections,
        int thickness = 2)
    {
        ArgumentNullException.ThrowIfNull(detections);

        foreach (var detection in detections)
        {
            var (x, y, w, h) = detection.Box.ToPixels(width, height);
            DrawRectangle(bgr, width, height, x, y, w, h, ColorFor(detection.LabelIndex), thickness);
        }
    }

    /// <summary>Menggambar kotak berongga.</summary>
    public static void DrawRectangle(
        Span<byte> bgr,
        int width,
        int height,
        int x,
        int y,
        int rectangleWidth,
        int rectangleHeight,
        (byte R, byte G, byte B) color,
        int thickness = 2)
    {
        if (rectangleWidth <= 0 || rectangleHeight <= 0)
        {
            return;
        }

        thickness = Math.Max(1, thickness);

        for (var t = 0; t < thickness; t++)
        {
            DrawHorizontalLine(bgr, width, height, x, x + rectangleWidth, y + t, color);
            DrawHorizontalLine(bgr, width, height, x, x + rectangleWidth, y + rectangleHeight - 1 - t, color);
            DrawVerticalLine(bgr, width, height, y, y + rectangleHeight, x + t, color);
            DrawVerticalLine(bgr, width, height, y, y + rectangleHeight, x + rectangleWidth - 1 - t, color);
        }
    }

    /// <summary>
    /// Mengisi persegi dengan pencampuran alfa. Berguna sebagai latar chip label
    /// supaya teks yang digambar UI tetap terbaca di atas gambar apa pun.
    /// </summary>
    public static void FillRectangle(
        Span<byte> bgr,
        int width,
        int height,
        int x,
        int y,
        int rectangleWidth,
        int rectangleHeight,
        (byte R, byte G, byte B) color,
        float alpha = 1f)
    {
        alpha = Math.Clamp(alpha, 0f, 1f);

        for (var row = Math.Max(0, y); row < Math.Min(height, y + rectangleHeight); row++)
        {
            for (var column = Math.Max(0, x); column < Math.Min(width, x + rectangleWidth); column++)
            {
                Blend(bgr, ((row * width) + column) * 3, color, alpha);
            }
        }
    }

    private static void DrawHorizontalLine(
        Span<byte> bgr, int width, int height, int x0, int x1, int y, (byte R, byte G, byte B) color)
    {
        if (y < 0 || y >= height)
        {
            return;
        }

        for (var x = Math.Max(0, x0); x < Math.Min(width, x1); x++)
        {
            Blend(bgr, ((y * width) + x) * 3, color, 1f);
        }
    }

    private static void DrawVerticalLine(
        Span<byte> bgr, int width, int height, int y0, int y1, int x, (byte R, byte G, byte B) color)
    {
        if (x < 0 || x >= width)
        {
            return;
        }

        for (var y = Math.Max(0, y0); y < Math.Min(height, y1); y++)
        {
            Blend(bgr, ((y * width) + x) * 3, color, 1f);
        }
    }

    private static void Blend(Span<byte> bgr, int offset, (byte R, byte G, byte B) color, float alpha)
    {
        if (offset < 0 || offset + 2 >= bgr.Length)
        {
            return;
        }

        if (alpha >= 1f)
        {
            bgr[offset] = color.B;
            bgr[offset + 1] = color.G;
            bgr[offset + 2] = color.R;
            return;
        }

        bgr[offset] = (byte)((bgr[offset] * (1 - alpha)) + (color.B * alpha));
        bgr[offset + 1] = (byte)((bgr[offset + 1] * (1 - alpha)) + (color.G * alpha));
        bgr[offset + 2] = (byte)((bgr[offset + 2] * (1 - alpha)) + (color.R * alpha));
    }
}
