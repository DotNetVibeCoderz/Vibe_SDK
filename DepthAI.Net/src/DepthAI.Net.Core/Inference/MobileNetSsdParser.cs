using System.Buffers;
using DepthAI.Streaming;

namespace DepthAI.Inference;

/// <summary>
/// Dekoder detection-output MobileNet-SSD: tensor <c>[1, 1, N, 7]</c> dengan tiap baris
/// berisi <c>[image_id, label, confidence, xmin, ymin, xmax, ymax]</c> yang sudah
/// ternormalisasi. Baris pengisi ditandai <c>image_id &lt; 0</c>.
/// </summary>
public sealed class MobileNetSsdParser(ModelMetadata metadata) : IInferenceParser
{
    private const int Stride = 7;

    private readonly ModelMetadata _metadata = metadata
        ?? throw new ArgumentNullException(nameof(metadata));

    public ModelFamily Family => ModelFamily.MobileNetSsd;

    public Frame Parse(IReadOnlyDictionary<string, Tensor> tensors, InferenceContext context)
    {
        ArgumentNullException.ThrowIfNull(tensors);

        if (tensors.Count == 0)
        {
            throw new InvalidOperationException("Keluaran MobileNet-SSD kosong.");
        }

        var tensor = tensors.Values.First();
        if (tensor.Length % Stride != 0)
        {
            throw new InvalidOperationException(
                $"Keluaran MobileNet-SSD harus kelipatan {Stride} elemen, tapi panjangnya {tensor.Length}.");
        }

        var data = tensor.Span;
        var threshold = context.ConfidenceThreshold ?? _metadata.ConfidenceThreshold;
        var detections = new List<Detection>();

        for (var offset = 0; offset + Stride <= data.Length; offset += Stride)
        {
            // image_id negatif menandai akhir deteksi nyata; sisanya padding.
            if (data[offset] < 0)
            {
                break;
            }

            var confidence = data[offset + 2];
            if (confidence < threshold)
            {
                continue;
            }

            var labelIndex = (int)data[offset + 1];

            detections.Add(new Detection
            {
                LabelIndex = labelIndex,
                Label = YoloParser.LabelFor(labelIndex, _metadata.Labels),
                Confidence = confidence,
                Box = new BoundingBox(data[offset + 3], data[offset + 4], data[offset + 5], data[offset + 6]).Clamp(),
            });
        }

        return new DetectionFrame
        {
            Detections = [.. detections.OrderByDescending(static d => d.Confidence)],
            SourceWidth = context.SourceWidth,
            SourceHeight = context.SourceHeight,
            SequenceNumber = context.SequenceNumber,
            DeviceTimestamp = context.DeviceTimestamp,
            StreamName = context.StreamName,
        };
    }
}

/// <summary>
/// Dekoder classification: satu vektor skor. Bila skor belum berupa distribusi
/// probabilitas, softmax diterapkan supaya keyakinan bisa dibandingkan antar model.
/// </summary>
public sealed class ClassificationParser(ModelMetadata metadata) : IInferenceParser
{
    private readonly ModelMetadata _metadata = metadata
        ?? throw new ArgumentNullException(nameof(metadata));

    /// <summary>Jumlah maksimum hasil teratas yang dikembalikan.</summary>
    public int TopK { get; init; } = 5;

    public ModelFamily Family => ModelFamily.Classification;

    public Frame Parse(IReadOnlyDictionary<string, Tensor> tensors, InferenceContext context)
    {
        ArgumentNullException.ThrowIfNull(tensors);

        if (tensors.Count == 0)
        {
            throw new InvalidOperationException("Keluaran classification kosong.");
        }

        var scores = tensors.Values.First().Span;
        var probabilities = IsProbabilityDistribution(scores) ? scores.ToArray() : Softmax(scores);

        var results = new List<Classification>(probabilities.Length);
        for (var i = 0; i < probabilities.Length; i++)
        {
            results.Add(new Classification
            {
                LabelIndex = i,
                Label = YoloParser.LabelFor(i, _metadata.Labels),
                Confidence = probabilities[i],
            });
        }

        return new ClassificationFrame
        {
            Results = [.. results.OrderByDescending(static r => r.Confidence).Take(TopK)],
            SequenceNumber = context.SequenceNumber,
            DeviceTimestamp = context.DeviceTimestamp,
            StreamName = context.StreamName,
        };
    }

    private static bool IsProbabilityDistribution(ReadOnlySpan<float> scores)
    {
        var sum = 0f;
        foreach (var score in scores)
        {
            if (score < 0f || score > 1f)
            {
                return false;
            }

            sum += score;
        }

        return Math.Abs(sum - 1f) < 0.05f;
    }

    private static float[] Softmax(ReadOnlySpan<float> logits)
    {
        // Kurangi maksimum sebelum eksponensiasi supaya tidak overflow pada logit besar.
        var max = float.NegativeInfinity;
        foreach (var value in logits)
        {
            max = Math.Max(max, value);
        }

        var result = new float[logits.Length];
        var sum = 0f;

        for (var i = 0; i < logits.Length; i++)
        {
            result[i] = MathF.Exp(logits[i] - max);
            sum += result[i];
        }

        if (sum > 0)
        {
            for (var i = 0; i < result.Length; i++)
            {
                result[i] /= sum;
            }
        }

        return result;
    }
}

/// <summary>
/// Dekoder segmentasi. Menerima peta kelas <c>[1, H, W]</c> yang sudah di-argmax
/// maupun logit per kelas <c>[1, C, H, W]</c> yang masih perlu di-argmax di host.
/// </summary>
public sealed class SegmentationParser(ModelMetadata metadata) : IInferenceParser
{
    private readonly ModelMetadata _metadata = metadata
        ?? throw new ArgumentNullException(nameof(metadata));

    public ModelFamily Family => ModelFamily.Segmentation;

    public Frame Parse(IReadOnlyDictionary<string, Tensor> tensors, InferenceContext context)
    {
        ArgumentNullException.ThrowIfNull(tensors);

        if (tensors.Count == 0)
        {
            throw new InvalidOperationException("Keluaran segmentation kosong.");
        }

        var tensor = tensors.Values.First();
        var dims = tensor.Shape.Count == 4 && tensor.Shape[0] == 1
            ? new[] { tensor.Shape[1], tensor.Shape[2], tensor.Shape[3] }
            : [.. tensor.Shape];

        return dims.Length switch
        {
            3 => ParseLogits(tensor, dims[0], dims[1], dims[2], context),
            2 => ParseClassMap(tensor, dims[0], dims[1], context),
            _ => throw new InvalidOperationException(
                $"Keluaran segmentation harus rank-2 atau rank-3 (opsional dengan batch), bukan [{string.Join(",", tensor.Shape)}]."),
        };
    }

    private SegmentationFrame ParseLogits(Tensor tensor, int classes, int height, int width, InferenceContext context)
    {
        var pixels = width * height;
        var map = ArrayPool<byte>.Shared.Rent(pixels);
        var data = tensor.Span;

        for (var p = 0; p < pixels; p++)
        {
            byte best = 0;
            var bestScore = float.NegativeInfinity;

            for (var c = 0; c < classes; c++)
            {
                var score = data[(c * pixels) + p];
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (byte)c;
                }
            }

            map[p] = best;
        }

        return Build(map, width, height, context);
    }

    private SegmentationFrame ParseClassMap(Tensor tensor, int height, int width, InferenceContext context)
    {
        var pixels = width * height;
        var map = ArrayPool<byte>.Shared.Rent(pixels);
        var data = tensor.Span;

        for (var p = 0; p < pixels; p++)
        {
            map[p] = (byte)Math.Clamp((int)data[p], 0, byte.MaxValue);
        }

        return Build(map, width, height, context);
    }

    private SegmentationFrame Build(byte[] map, int width, int height, InferenceContext context)
        => new(map, width, height, pooled: true)
        {
            Labels = _metadata.Labels,
            SequenceNumber = context.SequenceNumber,
            DeviceTimestamp = context.DeviceTimestamp,
            StreamName = context.StreamName,
        };
}
