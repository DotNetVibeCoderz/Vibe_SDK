using Avalonia.Media;
using DepthAI.Gallery.Inference;

namespace DepthAI.Gallery.Views;

/// <summary>
/// Warna overlay. Label diwarnai dari peta Turbo, peta yang sama dengan pewarna kedalaman SDK
/// dan depth ribbon, sehingga hasil inferensi terasa satu keluarga dengan sisa antarmuka.
/// </summary>
internal static class Palette
{
    public static readonly Color Alert = Color.Parse("#F07A8E");
    public static readonly Color Good = Color.Parse("#46C2A8");
    public static readonly Color Notice = Color.Parse("#F2A03D");

    /// <summary>Aproksimasi polinomial Turbo (Mikhailov, Google 2019), t di 0–1.</summary>
    public static Color Turbo(float t)
    {
        t = Math.Clamp(t, 0, 1);
        var r = 0.13572138 + (t * (4.61539260 + (t * (-42.66032258 + (t * (132.13108234 + (t * (-152.94239396 + (t * 59.28637943)))))))));
        var g = 0.09140261 + (t * (2.19418839 + (t * (4.84296658 + (t * (-14.18503333 + (t * (4.27729857 + (t * 2.82956604)))))))));
        var b = 0.10667330 + (t * (12.64194608 + (t * (-60.58204836 + (t * (110.36276771 + (t * (-89.90310912 + (t * 27.34824973)))))))));
        return Color.FromRgb(ToByte(r), ToByte(g), ToByte(b));

        static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
    }

    /// <summary>
    /// Warna stabil per label: posisi di Turbo diambil dari hash label lalu disebar dengan rasio
    /// emas, dan ujung gelap biru tua dihindari karena hilang di atas foto.
    /// </summary>
    public static Color ForLabel(string label)
    {
        var hash = 0u;
        foreach (var c in label)
        {
            hash = (hash * 31) + c;
        }

        var t = (float)((hash * 0.6180339887) % 1.0);
        return Turbo(0.18f + (t * 0.8f));
    }

    public static Color ForIndex(int index) => Turbo(0.18f + (float)((index * 0.6180339887) % 1.0 * 0.8));

    public static Color ForBox(ResultBox box) => box.Tone switch
    {
        ResultTone.Alert => Alert,
        ResultTone.Good => Good,
        ResultTone.Notice => Notice,
        _ => ForLabel(box.Label),
    };

    public static Color ForTone(ResultTone tone, Color neutral) => tone switch
    {
        ResultTone.Alert => Alert,
        ResultTone.Good => Good,
        ResultTone.Notice => Notice,
        _ => neutral,
    };

    /// <summary>Tinta yang terbaca di atas <paramref name="background"/>.</summary>
    public static Color InkOn(Color background)
        => (0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B) > 150
            ? Color.Parse("#10191E")
            : Colors.White;
}
