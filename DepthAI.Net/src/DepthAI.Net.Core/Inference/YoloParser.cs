using DepthAI.Streaming;

namespace DepthAI.Inference;

/// <summary>
/// Dekoder keluaran YOLO. Mengenali tata letak yang paling umum ditemui di ekosistem OAK:
/// <list type="bullet">
/// <item>anchor-free (v8/v10/v11): <c>[1, 4 + nc, anchors]</c> — tanpa skor objectness</item>
/// <item>berbasis anchor (v5/v6/v7): <c>[1, anchors, 5 + nc]</c> — kolom ke-5 adalah objectness</item>
/// <item>
/// grid mentah per stride: satu tensor <c>[1, 5 + nc, H, W]</c> per keluaran <c>*_yolo</c>,
/// didekode menurut <see cref="ModelMetadata.YoloDecoding"/> seperti DetectionParser depthai-core
/// </item>
/// </list>
/// Dua tata letak pertama dipilih dari bentuk tensor, bukan dari nama model.
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

        var threshold = context.ConfidenceThreshold ?? _metadata.ConfidenceThreshold;
        var iouThreshold = context.IouThreshold ?? _metadata.IouThreshold;

        var gridLayers = ResolveGridLayers(tensors);
        var candidates = gridLayers.Count > 0
            ? DecodeGrid(gridLayers, threshold)
            : DecodeCandidates(tensors.Values.First(), threshold);
        var kept = NonMaximumSuppression(candidates, iouThreshold);

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

    /// <summary>Stride bawaan depthai-core untuk tiga kepala deteksi P3/P4/P5.</summary>
    private static readonly int[] DefaultStrides = [8, 16, 32];

    /// <summary>
    /// Tensor grid mentah yang harus didekode per stride, terurut seperti upstream:
    /// nama yang mengandung <c>_yolo</c>, diurutkan ordinal.
    /// </summary>
    private List<(Tensor Tensor, int Stride)> ResolveGridLayers(IReadOnlyDictionary<string, Tensor> tensors)
    {
        if (_metadata.YoloDecoding == YoloDecodingFamily.AnchorBased)
        {
            return [];
        }

        IEnumerable<string> candidates = _metadata.YoloOutputs.Count > 0 ? _metadata.YoloOutputs : tensors.Keys;
        var layers = candidates
            .Where(static n => n.Contains("_yolo", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n => tensors.TryGetValue(n, out var t) ? t : null)
            .OfType<Tensor>()
            .Where(static t => t.Rank == 3 || (t.Rank == 4 && t.Shape[0] == 1))
            .ToList();

        if (layers.Count == 0)
        {
            return [];
        }

        var strides = _metadata.Strides.Count > 0 ? _metadata.Strides : DefaultStrides;
        if (strides.Count != layers.Count)
        {
            throw new InvalidOperationException(
                $"Jumlah stride ({strides.Count}) tidak cocok dengan jumlah keluaran grid YOLO ({layers.Count}: "
                + $"{string.Join(", ", layers.Select(static t => t.Name))}). Isi ModelMetadata.Strides sesuai model.");
        }

        return [.. layers.Select((t, i) => (t, strides[i]))];
    }

    /// <summary>
    /// Mendekode keluaran grid CHW. Kanal 0..3 adalah kotak, 4 skor, 5.. probabilitas kelas.
    /// Perhitungannya mengikuti <c>decodeTLBR</c>/<c>decodeR1AF</c> depthai-core 3.9+.
    /// </summary>
    private List<Detection> DecodeGrid(List<(Tensor Tensor, int Stride)> layers, float threshold)
    {
        var centerSize = _metadata.YoloDecoding == YoloDecodingFamily.CenterSize;
        float inputWidth = _metadata.InputWidth > 0 ? _metadata.InputWidth : 1;
        float inputHeight = _metadata.InputHeight > 0 ? _metadata.InputHeight : 1;
        var results = new List<Detection>();

        foreach (var (tensor, stride) in layers)
        {
            var shape = tensor.Shape;
            var offset = shape.Count - 3;
            var (channels, height, width) = (shape[offset], shape[offset + 1], shape[offset + 2]);
            var classCount = channels - 5;
            if (classCount <= 0)
            {
                throw new InvalidOperationException(
                    $"Keluaran grid YOLO '{tensor.Name}' butuh lebih dari 5 kanal, tapi hanya ada {channels}.");
            }

            var data = tensor.Span;
            var plane = width * height;

            for (var row = 0; row < height; row++)
            {
                for (var col = 0; col < width; col++)
                {
                    var cell = (row * width) + col;
                    if (data[(4 * plane) + cell] < threshold)
                    {
                        continue;
                    }

                    var bestClass = 0;
                    var bestScore = 0f;
                    for (var c = 0; c < classCount; c++)
                    {
                        var score = data[((5 + c) * plane) + cell];
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestClass = c;
                        }
                    }

                    if (bestScore < threshold)
                    {
                        continue;
                    }

                    var (b0, b1, b2, b3) = (data[cell], data[plane + cell], data[(2 * plane) + cell], data[(3 * plane) + cell]);
                    float xMin, yMin, xMax, yMax;

                    if (centerSize)
                    {
                        // YOLOv6 R1: offset pusat dalam sel dan log ukuran, semuanya dalam satuan stride.
                        // Sebelum 3.9 upstream membaca nilai ini sebagai piksel jadi; kotaknya meleset.
                        var cx = (col + b0) * stride;
                        var cy = (row + b1) * stride;
                        var w = MathF.Exp(b2) * stride;
                        var h = MathF.Exp(b3) * stride;
                        (xMin, yMin, xMax, yMax) = (cx - (w / 2f), cy - (h / 2f), cx + (w / 2f), cy + (h / 2f));
                    }
                    else
                    {
                        // TLBR: jarak dari pusat sel ke tiap tepi, dalam satuan stride.
                        xMin = (col - b0 + 0.5f) * stride;
                        yMin = (row - b1 + 0.5f) * stride;
                        xMax = (col + b2 + 0.5f) * stride;
                        yMax = (row + b3 + 0.5f) * stride;
                    }

                    xMin = Math.Clamp(xMin, 0f, inputWidth);
                    yMin = Math.Clamp(yMin, 0f, inputHeight);
                    xMax = Math.Clamp(xMax, 0f, inputWidth);
                    yMax = Math.Clamp(yMax, 0f, inputHeight);

                    if (xMax <= xMin || yMax <= yMin)
                    {
                        continue;
                    }

                    results.Add(new Detection
                    {
                        LabelIndex = bestClass,
                        Label = LabelFor(bestClass, _metadata.Labels),
                        Confidence = bestScore,
                        Box = new BoundingBox(xMin / inputWidth, yMin / inputHeight, xMax / inputWidth, yMax / inputHeight),
                    });
                }
            }
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
