using DepthAI.Streaming;

namespace DepthAI.Inference;

/// <summary>Konteks frame sumber yang dibutuhkan parser untuk menghasilkan hasil bertipe.</summary>
public readonly record struct InferenceContext
{
    /// <summary>Lebar frame yang masuk ke neural network, piksel.</summary>
    public int SourceWidth { get; init; }

    public int SourceHeight { get; init; }

    public long SequenceNumber { get; init; }

    public TimeSpan DeviceTimestamp { get; init; }

    public string StreamName { get; init; }

    /// <summary>
    /// Menimpa ambang keyakinan dari metadata model. Berguna untuk menyetel sensitivitas
    /// saat runtime tanpa memuat ulang model.
    /// </summary>
    public float? ConfidenceThreshold { get; init; }
}

/// <summary>
/// Menerjemahkan tensor keluaran mentah menjadi objek .NET bertipe.
/// Implementasikan antarmuka ini untuk arsitektur yang belum didukung bawaan.
/// </summary>
public interface IInferenceParser
{
    /// <summary>Keluarga yang ditangani parser ini; dipakai untuk diagnostik.</summary>
    ModelFamily Family { get; }

    /// <summary>
    /// Mengubah tensor menjadi frame hasil. Implementasi harus mengembalikan frame yang
    /// sudah lepas dari buffer tensor, karena tensor bisa didaur ulang setelah panggilan ini.
    /// </summary>
    Frame Parse(IReadOnlyDictionary<string, Tensor> tensors, InferenceContext context);
}

/// <summary>Meneruskan tensor apa adanya, untuk post-processing kustom di host.</summary>
public sealed class RawTensorParser : IInferenceParser
{
    public ModelFamily Family => ModelFamily.Raw;

    public Frame Parse(IReadOnlyDictionary<string, Tensor> tensors, InferenceContext context)
        => new NeuralTensorFrame
        {
            Tensors = tensors,
            SequenceNumber = context.SequenceNumber,
            DeviceTimestamp = context.DeviceTimestamp,
            StreamName = context.StreamName,
        };
}
