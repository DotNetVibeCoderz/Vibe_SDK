namespace DepthAI.Inference;

/// <summary>
/// Ambang deteksi yang bisa diganti saat pipeline berjalan, padanan
/// <c>dai::DetectionParserConfig</c> (depthai-core 3.11) yang dikirim ke <c>inputConfig</c>.
/// Kirim lewat <see cref="DepthAiDevice.SendConfig"/>.
/// </summary>
/// <remarks>
/// Hanya ambang yang bisa diubah; tata letak model, label, dan keluarga dekoding tetap
/// milik node, sama seperti upstream. Nilai null berarti "biarkan nilai yang sedang aktif",
/// sehingga satu ambang bisa diubah tanpa harus mengingat ambang lainnya.
/// </remarks>
public sealed record DetectionParserConfig
{
    /// <summary>Keyakinan minimum deteksi, 0..1.</summary>
    public float? ConfidenceThreshold { get; init; }

    /// <summary>Ambang IoU NMS, 0..1. Tidak dipakai MobileNet-SSD.</summary>
    public float? IouThreshold { get; init; }

    internal void Validate()
    {
        if (ConfidenceThreshold is < 0 or > 1 || float.IsNaN(ConfidenceThreshold ?? 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConfidenceThreshold), ConfidenceThreshold, "Ambang keyakinan harus di rentang 0..1.");
        }

        if (IouThreshold is < 0 or > 1 || float.IsNaN(IouThreshold ?? 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(IouThreshold), IouThreshold, "Ambang IoU harus di rentang 0..1.");
        }
    }
}
