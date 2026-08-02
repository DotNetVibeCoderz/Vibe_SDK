using DepthAI.Streaming;

namespace DepthAI.Inference;

/// <summary>
/// Dekoder keluaran YOLO. Mengenali dua tata letak yang paling umum ditemui di ekosistem
/// OAK dan memilihnya dari bentuk tensor, bukan dari nama model:
/// <list type="bullet">
/// <item>anchor-free (v8/v10/v11): <c>[1, 4 + nc, anchors]</c> — tanpa skor objectness</item>
/// <item>berbasis anchor (v5/v6/v7): <c>[1, anchors, 5 + nc]</c> — kolom ke-5 adalah objectness</item>
/// </list>
/// </summary>
public sealed class YoloParser(ModelMetadata metadata) : IInferenceParser
{
    private readonly ModelMetadata _metadata = metadata
        ?? throw new ArgumentNullException(nameof(metadata));

    public ModelFamily Family => ModelFamily.Yolo;

    public Frame Parse(IReadOnlyDictionary<string, Tensor> tensors, InferenceContext context)
    {
        ArgumentNullException.ThrowIfNull(tensors);

        if (tensors.Count == 0)
        {
            throw new InvalidOperationException("Keluaran YOLO kosong — tidak ada tensor untuk didekode.");
        }

        var tensor = tensors.Values.First();
        var threshold = context.ConfidenceThreshold ?? _metadata.ConfidenceThreshold;
        var candidates = DecodeCandidates(tensor, threshold);
        var kept = NonMaximumSuppression(candidates, _metadata.IouThreshold);

        return new DetectionFrame
        {
            Detections = kept,
            SourceWidth = context.SourceWidth,
            SourceHeight = context.SourceHeight,
            SequenceNumber = context.SequenceNumber,
            DeviceTimestamp = context.DeviceTimestamp,
            StreamName = context.StreamName,
        };
    }

    private List<Detection> DecodeCandidates(Tensor tensor, float threshold)
    {
        var shape = tensor.Shape;
        var data = tensor.Span;

        // Buang dimensi batch agar penanganan rank-2 dan rank-3 seragam.
        var dims = shape.Count == 3 && shape[0] == 1
            ? new[] { shape[1], shape[2] }
            : [.. shape];

        if (dims.Length != 2)
        {
            throw new InvalidOperationException(
                $"Keluaran YOLO harus rank-2 atau rank-3 dengan batch 1, tapi bentuknya [{string.Join(",", shape)}].");
        }

        var (rows, cols) = (dims[0], dims[1]);

        // Tata letak anchor-free menaruh atribut di sumbu pertama dan biasanya jauh
        // lebih sedikit dari jumlah anchor, jadi baris-lebih-kecil adalah penanda andal.
        return rows < cols
            ? DecodeAnchorFree(data, attributes: rows, anchors: cols, threshold)
            : DecodeAnchorBased(data, anchors: rows, attributes: cols, threshold);
    }

    /// <summary>Tata letak <c>[4 + nc, anchors]</c>, tersimpan per atribut (column-major terhadap anchor).</summary>
    private List<Detection> DecodeAnchorFree(ReadOnlySpan<float> data, int attributes, int anchors, float threshold)
    {
        var classCount = attributes - 4;
        if (classCount <= 0)
        {
            throw new InvalidOperationException(
                $"Keluaran YOLO anchor-free butuh lebih dari 4 atribut, tapi hanya ada {attributes}.");
        }

        var results = new List<Detection>();

        for (var a = 0; a < anchors; a++)
        {
            var bestClass = -1;
            var bestScore = 0f;

            for (var c = 0; c < classCount; c++)
            {
                var score = data[((4 + c) * anchors) + a];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            if (bestClass < 0 || bestScore < threshold)
            {
                continue;
            }

            var cx = data[a];
            var cy = data[anchors + a];
            var w = data[(2 * anchors) + a];
            var h = data[(3 * anchors) + a];

            results.Add(CreateDetection(bestClass, bestScore, cx, cy, w, h));
        }

        return results;
    }

    /// <summary>Tata letak <c>[anchors, 5 + nc]</c> dengan objectness di indeks 4.</summary>
    private List<Detection> DecodeAnchorBased(ReadOnlySpan<float> data, int anchors, int attributes, float threshold)
    {
        var classCount = attributes - 5;
        if (classCount <= 0)
        {
            throw new InvalidOperationException(
                $"Keluaran YOLO berbasis anchor butuh lebih dari 5 atribut, tapi hanya ada {attributes}.");
        }

        var results = new List<Detection>();

        for (var a = 0; a < anchors; a++)
        {
            var offset = a * attributes;
            var objectness = data[offset + 4];
            if (objectness < threshold)
            {
                continue;
            }

            var bestClass = -1;
            var bestScore = 0f;

            for (var c = 0; c < classCount; c++)
            {
                var score = data[offset + 5 + c];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            // Skor akhir YOLO adalah objectness dikali probabilitas kelas.
            var confidence = objectness * bestScore;
            if (bestClass < 0 || confidence < threshold)
            {
                continue;
            }

            results.Add(CreateDetection(
                bestClass, confidence, data[offset], data[offset + 1], data[offset + 2], data[offset + 3]));
        }

        return results;
    }

    private Detection CreateDetection(int labelIndex, float confidence, float cx, float cy, float w, float h)
    {
        // Model YOLO memancarkan piksel relatif terhadap ukuran input; normalisasikan
        // agar kotak tetap benar saat digambar di atas frame berukuran lain.
        var scaleX = _metadata.InputWidth > 0 ? _metadata.InputWidth : 1;
        var scaleY = _metadata.InputHeight > 0 ? _metadata.InputHeight : 1;

        // Keluaran yang sudah ternormalisasi (semua nilai <= 1) tidak perlu dibagi lagi.
        var normalized = cx <= 1f && cy <= 1f && w <= 1f && h <= 1f;
        if (!normalized)
        {
            cx /= scaleX;
            cy /= scaleY;
            w /= scaleX;
            h /= scaleY;
        }

        var box = new BoundingBox(cx - (w / 2f), cy - (h / 2f), cx + (w / 2f), cy + (h / 2f)).Clamp();

        return new Detection
        {
            LabelIndex = labelIndex,
            Label = LabelFor(labelIndex, _metadata.Labels),
            Confidence = confidence,
            Box = box,
        };
    }

    internal static string LabelFor(int index, IReadOnlyList<string> labels)
        => index >= 0 && index < labels.Count ? labels[index] : $"class_{index}";

    /// <summary>
    /// Non-maximum suppression per kelas: kotak dari kelas berbeda yang bertumpuk
    /// (orang memegang gelas) tidak boleh saling menghapus.
    /// </summary>
    internal static List<Detection> NonMaximumSuppression(List<Detection> candidates, float iouThreshold)
    {
        var kept = new List<Detection>(candidates.Count);

        foreach (var group in candidates.GroupBy(static d => d.LabelIndex))
        {
            var ordered = group.OrderByDescending(static d => d.Confidence).ToList();

            while (ordered.Count > 0)
            {
                var best = ordered[0];
                kept.Add(best);
                ordered.RemoveAt(0);
                ordered.RemoveAll(d => best.Box.IntersectionOverUnion(d.Box) > iouThreshold);
            }
        }

        return [.. kept.OrderByDescending(static d => d.Confidence)];
    }
}
