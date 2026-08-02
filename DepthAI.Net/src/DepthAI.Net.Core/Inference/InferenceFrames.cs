using System.Buffers;
using DepthAI.Streaming;

namespace DepthAI.Inference;

/// <summary>Hasil detection network untuk satu frame.</summary>
public sealed class DetectionFrame : Frame
{
    public required IReadOnlyList<Detection> Detections { get; init; }

    /// <summary>Lebar frame sumber, piksel — untuk memetakan kotak ternormalisasi ke overlay.</summary>
    public int SourceWidth { get; init; }

    public int SourceHeight { get; init; }

    public int Count => Detections.Count;

    /// <summary>Deteksi dengan keyakinan tertinggi, atau <see langword="null"/> bila kosong.</summary>
    public Detection? Best => Detections.Count == 0
        ? null
        : Detections.Aggregate(static (a, b) => b.Confidence > a.Confidence ? b : a);
}

/// <summary>Hasil classification network untuk satu frame, terurut menurun.</summary>
public sealed class ClassificationFrame : Frame
{
    public required IReadOnlyList<Classification> Results { get; init; }

    public Classification? Top => Results.Count > 0 ? Results[0] : null;
}

/// <summary>
/// Mask segmentasi per piksel. Tiap nilai adalah indeks kelas pemenang untuk piksel itu.
/// </summary>
public sealed class SegmentationFrame : Frame
{
    private byte[]? _pooled;

    internal SegmentationFrame(byte[] classMap, int width, int height, bool pooled)
    {
        ClassMapBuffer = classMap;
        Width = width;
        Height = height;
        _pooled = pooled ? classMap : null;
    }

    internal byte[] ClassMapBuffer { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Nama kelas per indeks, bila model menyertakannya.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    public ReadOnlySpan<byte> ClassMap
    {
        get
        {
            ThrowIfDisposed();
            return ClassMapBuffer.AsSpan(0, Width * Height);
        }
    }

    public byte GetClass(int x, int y)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return ClassMapBuffer[(y * Width) + x];
    }

    protected override void ReleaseBuffers()
    {
        var pooled = Interlocked.Exchange(ref _pooled, null);
        if (pooled is not null)
        {
            ArrayPool<byte>.Shared.Return(pooled);
        }
    }
}

/// <summary>
/// Keluaran tensor mentah dari neural network, untuk model yang tidak cocok dengan
/// parser bawaan. Jembatan ke ML.NET / TorchSharp / NumSharp untuk post-processing sendiri.
/// </summary>
public sealed class NeuralTensorFrame : Frame
{
    public required IReadOnlyDictionary<string, Tensor> Tensors { get; init; }

    /// <summary>Tensor pertama — jalan pintas untuk model satu keluaran.</summary>
    public Tensor First => Tensors.Values.First();

    public Tensor this[string name] => Tensors.TryGetValue(name, out var tensor)
        ? tensor
        : throw new KeyNotFoundException(
            $"Model tidak punya tensor keluaran '{name}'. Yang tersedia: {string.Join(", ", Tensors.Keys)}.");
}

/// <summary>Tensor float multi-dimensi dengan penyimpanan row-major.</summary>
public sealed class Tensor
{
    public Tensor(string name, ReadOnlyMemory<float> data, IReadOnlyList<int> shape)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var expected = shape.Aggregate(1, static (a, b) => a * b);
        if (expected != data.Length)
        {
            throw new ArgumentException(
                $"Shape [{string.Join(",", shape)}] menuntut {expected} elemen tapi data berisi {data.Length}.",
                nameof(data));
        }

        Name = name;
        Data = data;
        Shape = shape;
    }

    public string Name { get; }

    public ReadOnlyMemory<float> Data { get; }

    public IReadOnlyList<int> Shape { get; }

    public int Rank => Shape.Count;

    public int Length => Data.Length;

    public ReadOnlySpan<float> Span => Data.Span;

    /// <summary>Menyalin ke <c>float[,]</c> — hanya valid untuk tensor rank-2.</summary>
    public float[,] ToMatrix()
    {
        if (Rank != 2)
        {
            throw new InvalidOperationException($"ToMatrix() butuh tensor rank-2, tensor ini rank-{Rank}.");
        }

        var rows = Shape[0];
        var cols = Shape[1];
        var matrix = new float[rows, cols];
        var span = Span;

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                matrix[r, c] = span[(r * cols) + c];
            }
        }

        return matrix;
    }

    public override string ToString() => $"{Name} [{string.Join("x", Shape)}]";
}
