namespace DepthAI.Gallery.Inference;

/// <summary>
/// Pemetaan antara gambar asli dan masukan model yang ukurannya tetap. Gambar diperkecil
/// dengan rasio aspek terjaga lalu diberi bingkai, sehingga objek tidak gepeng; koordinat
/// hasil model harus dipetakan balik lewat bingkai yang sama.
/// </summary>
/// <remarks>
/// Alternatifnya, meregangkan gambar ke ukuran model, lebih sederhana tapi menurunkan
/// akurasi pada model persegi (APD, plat nomor 640×640) yang diberi foto 16:9.
/// </remarks>
public readonly record struct Letterbox(int SourceWidth, int SourceHeight, int TargetWidth, int TargetHeight)
{
    /// <summary>Tanpa bingkai: sumber sudah berukuran masukan model, seperti preview kamera live.</summary>
    public static Letterbox Identity(int width, int height) => new(width, height, width, height);

    public float Scale => Math.Min((float)TargetWidth / SourceWidth, (float)TargetHeight / SourceHeight);

    public float ContentWidth => SourceWidth * Scale;

    public float ContentHeight => SourceHeight * Scale;

    public float PadX => (TargetWidth - ContentWidth) / 2f;

    public float PadY => (TargetHeight - ContentHeight) / 2f;

    /// <summary>Titik ternormalisasi di ruang model menjadi titik ternormalisasi di gambar asli.</summary>
    public (float X, float Y) ToImage(float modelX, float modelY)
        => (((modelX * TargetWidth) - PadX) / ContentWidth, ((modelY * TargetHeight) - PadY) / ContentHeight);

    /// <summary>Titik ternormalisasi di gambar asli menjadi titik ternormalisasi di ruang model.</summary>
    public (float X, float Y) ToModel(float imageX, float imageY)
        => (((imageX * ContentWidth) + PadX) / TargetWidth, ((imageY * ContentHeight) + PadY) / TargetHeight);
}
