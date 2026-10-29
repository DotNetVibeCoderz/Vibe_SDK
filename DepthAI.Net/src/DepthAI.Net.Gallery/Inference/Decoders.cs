using DepthAI.Inference;

namespace DepthAI.Gallery.Inference;

/// <summary>Kotak di ruang piksel masukan model, sebelum dipetakan ke gambar asli.</summary>
public readonly record struct ModelBox(float X0, float Y0, float X1, float Y1, float Score, int Label = 0)
{
    public float Area => Math.Max(0, X1 - X0) * Math.Max(0, Y1 - Y0);

    public float IoU(ModelBox other)
    {
        var w = Math.Min(X1, other.X1) - Math.Max(X0, other.X0);
        var h = Math.Min(Y1, other.Y1) - Math.Max(Y0, other.Y0);
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        var inter = w * h;
        return inter / (Area + other.Area - inter);
    }
}

/// <summary>Non-maximum suppression serakah, per label.</summary>
public static class Nms
{
    /// <returns>Indeks kotak yang bertahan, terurut dari skor tertinggi.</returns>
    public static List<int> Run(IReadOnlyList<ModelBox> boxes, float iouThreshold, int maxCount = 300)
    {
        var order = Enumerable.Range(0, boxes.Count).OrderByDescending(i => boxes[i].Score).ToList();
        var kept = new List<int>();

        foreach (var candidate in order)
        {
            if (kept.Count >= maxCount)
            {
                break;
            }

            var box = boxes[candidate];
            if (kept.All(k => boxes[k].Label != box.Label || boxes[k].IoU(box) <= iouThreshold))
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }
}

/// <summary>Deteksi YOLO biasa, lewat <see cref="YoloParser"/> bawaan SDK.</summary>
public static class YoloDetections
{
    /// <returns>Deteksi dengan kotak ternormalisasi terhadap masukan model.</returns>
    public static IReadOnlyList<Detection> Decode(IReadOnlyDictionary<string, Tensor> tensors, ModelMetadata metadata, float? confidence = null)
    {
        using var frame = (DetectionFrame)new YoloParser(metadata).Parse(tensors, new InferenceContext
        {
            SourceWidth = metadata.InputWidth,
            SourceHeight = metadata.InputHeight,
            ConfidenceThreshold = confidence,
            StreamName = "gallery",
        });

        return frame.Detections;
    }
}

/// <summary>
/// Kandidat grid YOLOv8 yang masih menyimpan asal selnya. Model pose dan segmentasi instans
/// menaruh data tambahan (titik kunci, koefisien mask) di sel yang sama, jadi
/// <see cref="YoloParser"/> yang hanya mengembalikan kotak tidak cukup.
/// </summary>
public readonly record struct GridCandidate(int Layer, int Cell, ModelBox Box);

public static class YoloGrid
{
    /// <summary>
    /// Mendekode keluaran grid <c>[1, 5 + nc, H, W]</c> berformat TLBR (YOLOv6 R2, YOLOv8 versi
    /// Luxonis): kanal 0–3 jarak pusat sel ke tepi dalam satuan stride, kanal 4 keyakinan,
    /// sisanya skor kelas. Rumusnya sama dengan <c>decodeTLBR</c> depthai-core.
    /// </summary>
    public static List<GridCandidate> Decode(IReadOnlyList<Tensor> layers, IReadOnlyList<int> strides, float threshold, float iouThreshold)
    {
        var candidates = new List<GridCandidate>();

        for (var layer = 0; layer < layers.Count; layer++)
        {
            var tensor = layers[layer];
            var shape = tensor.Shape;
            var (channels, height, width) = (shape[^3], shape[^2], shape[^1]);
            var plane = width * height;
            var data = tensor.Span;
            var stride = strides[layer];

            for (var cell = 0; cell < plane; cell++)
            {
                if (data[(4 * plane) + cell] < threshold)
                {
                    continue;
                }

                var label = 0;
                var score = 0f;
                for (var c = 5; c < channels; c++)
                {
                    if (data[(c * plane) + cell] > score)
                    {
                        score = data[(c * plane) + cell];
                        label = c - 5;
                    }
                }

                if (score < threshold)
                {
                    continue;
                }

                var (row, col) = (cell / width, cell % width);
                candidates.Add(new GridCandidate(layer, cell, new ModelBox(
                    (col - data[cell] + 0.5f) * stride,
                    (row - data[plane + cell] + 0.5f) * stride,
                    (col + data[(2 * plane) + cell] + 0.5f) * stride,
                    (row + data[(3 * plane) + cell] + 0.5f) * stride,
                    score,
                    label)));
            }
        }

        var kept = Nms.Run(candidates.Select(c => c.Box).ToList(), iouThreshold);
        return [.. kept.Select(i => candidates[i])];
    }
}

/// <summary>Satu orang dari YOLOv8-pose: kotak plus 17 sendi COCO, di ruang piksel model.</summary>
public sealed record PoseDetection(ModelBox Box, (float X, float Y, float Score)[] Keypoints);

public static class PoseDecoder
{
    /// <summary>Pasangan sendi COCO yang membentuk kerangka, dari config model.</summary>
    public static readonly int[][] Skeleton =
    [
        [15, 13], [13, 11], [16, 14], [14, 12], [11, 12], [5, 11], [6, 12], [5, 6], [5, 7],
        [6, 8], [7, 9], [8, 10], [1, 2], [0, 1], [0, 2], [1, 3], [2, 4], [3, 5], [4, 6],
    ];

    /// <remarks>
    /// Ekspor Luxonis sudah mendekode koordinat titik kunci ke piksel masukan (diverifikasi di
    /// OAK-1: pergelangan tangan orang yang melompat jatuh di atas kepalanya). Yang tersisa
    /// hanya skor visibilitas, yang masih berupa logit.
    /// </remarks>
    public static List<PoseDetection> Decode(IReadOnlyDictionary<string, Tensor> tensors, float threshold = 0.5f, float iou = 0.5f)
    {
        var layers = new[] { tensors["output1_yolov8"], tensors["output2_yolov8"], tensors["output3_yolov8"] };
        var keypoints = new[] { tensors["kpt_output1"], tensors["kpt_output2"], tensors["kpt_output3"] };

        return [.. YoloGrid.Decode(layers, [8, 16, 32], threshold, iou).Select(c =>
        {
            var data = keypoints[c.Layer].Span;
            var count = keypoints[c.Layer].Shape[^1];
            var points = new (float, float, float)[17];
            for (var j = 0; j < 17; j++)
            {
                points[j] = (data[(3 * j * count) + c.Cell], data[(((3 * j) + 1) * count) + c.Cell], Sigmoid(data[(((3 * j) + 2) * count) + c.Cell]));
            }

            return new PoseDetection(c.Box, points);
        })];
    }

    internal static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));
}

/// <summary>Objek dari YOLOv8-seg beserta mask-nya di resolusi prototipe.</summary>
public sealed record InstanceDetection(ModelBox Box, bool[] Mask, int MaskWidth, int MaskHeight);

public static class InstanceSegmentationDecoder
{
    /// <remarks>
    /// Arsip Luxonis menyertakan model kedua (<c>mult_*.superblob</c>) untuk mengalikan koefisien
    /// dengan prototipe di perangkat. Di sini perkaliannya dilakukan di host: hanya untuk kotak
    /// yang lolos NMS, jadi biayanya kecil dan pipeline cukup satu neural network.
    /// </remarks>
    public static List<InstanceDetection> Decode(IReadOnlyDictionary<string, Tensor> tensors, int inputWidth, float threshold = 0.5f, float iou = 0.5f)
    {
        var layers = new[] { tensors["output1_yolov8"], tensors["output2_yolov8"], tensors["output3_yolov8"] };
        var coefficients = new[] { tensors["output1_masks"], tensors["output2_masks"], tensors["output3_masks"] };
        var protos = tensors["protos_output"];
        var (prototypes, protoHeight, protoWidth) = (protos.Shape[^3], protos.Shape[^2], protos.Shape[^1]);
        var protoPlane = protoWidth * protoHeight;
        var scale = (float)protoWidth / inputWidth;
        var proto = protos.Span;

        var results = new List<InstanceDetection>();
        foreach (var candidate in YoloGrid.Decode(layers, [8, 16, 32], threshold, iou))
        {
            var coeffTensor = coefficients[candidate.Layer];
            var coeffPlane = coeffTensor.Shape[^1] * coeffTensor.Shape[^2];
            var coeffData = coeffTensor.Span;
            var coeff = new float[prototypes];
            for (var k = 0; k < prototypes; k++)
            {
                coeff[k] = coeffData[(k * coeffPlane) + candidate.Cell];
            }

            var box = candidate.Box;
            var mask = new bool[protoPlane];
            var x0 = Math.Max(0, (int)(box.X0 * scale));
            var y0 = Math.Max(0, (int)(box.Y0 * scale));
            var x1 = Math.Min(protoWidth, (int)MathF.Ceiling(box.X1 * scale));
            var y1 = Math.Min(protoHeight, (int)MathF.Ceiling(box.Y1 * scale));

            // Mask dipotong ke kotaknya, sama seperti ultralytics: di luar kotak prototipe
            // sering masih "menyala" untuk objek lain sekelas.
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var p = (y * protoWidth) + x;
                    var sum = 0f;
                    for (var k = 0; k < prototypes; k++)
                    {
                        sum += coeff[k] * proto[(k * protoPlane) + p];
                    }

                    mask[p] = sum > 0; // sigmoid(sum) > 0.5
                }
            }

            results.Add(new InstanceDetection(box, mask, protoWidth, protoHeight));
        }

        return results;
    }
}

/// <summary>Wajah dari YuNet: kotak, lima landmark, skor; semuanya di piksel masukan model.</summary>
public sealed record FaceDetection(ModelBox Box, (float X, float Y)[] Landmarks);

public static class YuNetDecoder
{
    private static readonly int[] Steps = [8, 16, 32, 64];

    private static readonly float[][] MinSizes = [[10, 16, 24], [32, 48], [64, 96], [128, 192, 256]];

    private const float CenterVariance = 0.1f;
    private const float SizeVariance = 0.2f;

    /// <summary>
    /// Dekode YuNet generasi pertama (libfacedetection) yang keluarannya bertumpu pada prior:
    /// <c>loc [N, 14]</c>, <c>conf [N, 2]</c>, <c>iou [N, 1]</c>. Rumusnya mengikuti
    /// <c>FaceDetectorYN</c> OpenCV 4.5.4.
    /// </summary>
    public static List<FaceDetection> Decode(IReadOnlyDictionary<string, Tensor> tensors, int inputWidth, int inputHeight, float threshold = 0.6f, float iou = 0.3f)
    {
        var priors = Priors(inputWidth, inputHeight);
        var loc = tensors["loc"].Span;
        var conf = tensors["conf"].Span;
        var iouScores = tensors["iou"].Span;

        if (loc.Length != priors.Count * 14)
        {
            throw new InvalidDataException($"YuNet {inputWidth}×{inputHeight} butuh {priors.Count} prior, tensor loc punya {loc.Length / 14}.");
        }

        var boxes = new List<ModelBox>();
        var landmarks = new List<(float, float)[]>();
        for (var i = 0; i < priors.Count; i++)
        {
            var score = MathF.Sqrt(Math.Clamp(conf[(i * 2) + 1], 0, 1) * Math.Clamp(iouScores[i], 0, 1));
            if (score < threshold)
            {
                continue;
            }

            var (px, py, pw, ph) = priors[i];
            var l = loc.Slice(i * 14, 14);
            var cx = px + (l[0] * CenterVariance * pw);
            var cy = py + (l[1] * CenterVariance * ph);
            var w = pw * MathF.Exp(l[2] * SizeVariance);
            var h = ph * MathF.Exp(l[3] * SizeVariance);
            boxes.Add(new ModelBox((cx - (w / 2)) * inputWidth, (cy - (h / 2)) * inputHeight, (cx + (w / 2)) * inputWidth, (cy + (h / 2)) * inputHeight, score));

            var marks = new (float, float)[5];
            for (var k = 0; k < 5; k++)
            {
                marks[k] = ((px + (l[4 + (2 * k)] * CenterVariance * pw)) * inputWidth, (py + (l[5 + (2 * k)] * CenterVariance * ph)) * inputHeight);
            }

            landmarks.Add(marks);
        }

        return [.. Nms.Run(boxes, iou).Select(i => new FaceDetection(boxes[i], landmarks[i]))];
    }

    /// <summary>Prior ternormalisasi (cx, cy, w, h), urut level → baris → kolom → ukuran.</summary>
    internal static List<(float X, float Y, float W, float H)> Priors(int width, int height)
    {
        // Ukuran peta fitur dihitung persis seperti OpenCV, termasuk pembulatan ke bawahnya:
        // 480 / 64 menjadi 7, bukan 8, dan itulah yang membuat jumlahnya 17610.
        var (fw, fh) = (((width + 1) / 2) / 2, ((height + 1) / 2) / 2);
        var priors = new List<(float, float, float, float)>();

        for (var level = 0; level < Steps.Length; level++)
        {
            (fw, fh) = (fw / 2, fh / 2);
            for (var row = 0; row < fh; row++)
            {
                for (var col = 0; col < fw; col++)
                {
                    foreach (var size in MinSizes[level])
                    {
                        priors.Add((
                            (col + 0.5f) * Steps[level] / width,
                            (row + 0.5f) * Steps[level] / height,
                            size / width,
                            size / height));
                    }
                }
            }
        }

        return priors;
    }
}

/// <summary>Telapak tangan dari MediaPipe palm detection: kotak dan tujuh titik.</summary>
public static class PalmDecoder
{
    /// <summary>
    /// Anchor SSD MediaPipe untuk masukan 192: stride 8 dengan dua anchor per sel, lalu tiga
    /// lapisan stride 16 yang berbagi peta 12×12 sehingga ada enam anchor per sel. Total 2016.
    /// </summary>
    internal static List<(float X, float Y)> Anchors(int size)
    {
        var anchors = new List<(float, float)>();
        foreach (var (stride, perCell) in new[] { (8, 2), (16, 6) })
        {
            var cells = size / stride;
            for (var y = 0; y < cells; y++)
            {
                for (var x = 0; x < cells; x++)
                {
                    for (var a = 0; a < perCell; a++)
                    {
                        anchors.Add(((x + 0.5f) / cells, (y + 0.5f) / cells));
                    }
                }
            }
        }

        return anchors;
    }

    /// <returns>Kotak dan titik, ternormalisasi terhadap masukan model.</returns>
    public static List<(ModelBox Box, (float X, float Y)[] Points)> Decode(IReadOnlyDictionary<string, Tensor> tensors, int size = 192, float threshold = 0.5f, float iou = 0.3f)
    {
        var regressors = tensors["Identity"].Span;
        var scores = tensors["Identity_1"].Span;
        var anchors = Anchors(size);
        var boxes = new List<ModelBox>();
        var points = new List<(float, float)[]>();

        for (var i = 0; i < anchors.Count; i++)
        {
            var score = PoseDecoder.Sigmoid(Math.Clamp(scores[i], -100, 100));
            if (score < threshold)
            {
                continue;
            }

            var r = regressors.Slice(i * 18, 18);
            var (ax, ay) = anchors[i];
            var cx = ax + (r[0] / size);
            var cy = ay + (r[1] / size);
            var w = r[2] / size;
            var h = r[3] / size;
            boxes.Add(new ModelBox(cx - (w / 2), cy - (h / 2), cx + (w / 2), cy + (h / 2), score));

            var p = new (float, float)[7];
            for (var k = 0; k < 7; k++)
            {
                p[k] = (ax + (r[4 + (2 * k)] / size), ay + (r[5 + (2 * k)] / size));
            }

            points.Add(p);
        }

        return [.. Nms.Run(boxes, iou).Select(i => (boxes[i], points[i]))];
    }
}

/// <summary>Kotak baris teks dari peta probabilitas DB (PaddleOCR detection).</summary>
public static class TextDetectionDecoder
{
    /// <summary>
    /// Ambang biner lalu komponen terhubung. Versi PaddleOCR memakai poligon dan "unclip"
    /// dengan pyclipper; untuk dokumen yang lurus, kotak sejajar sumbu yang dilebarkan dengan
    /// rumus offset yang sama (luas × rasio / keliling) sudah memadai.
    /// </summary>
    public static List<ModelBox> Decode(Tensor map, float binaryThreshold = 0.3f, float boxThreshold = 0.6f, float unclipRatio = 1.5f, int minSide = 3)
    {
        var (height, width) = (map.Shape[^2], map.Shape[^1]);
        var data = map.Span;
        var visited = new bool[width * height];
        var stack = new Stack<int>();
        var boxes = new List<ModelBox>();

        for (var start = 0; start < data.Length; start++)
        {
            if (visited[start] || data[start] < binaryThreshold)
            {
                continue;
            }

            int x0 = width, y0 = height, x1 = 0, y1 = 0, count = 0;
            var sum = 0f;
            visited[start] = true;
            stack.Push(start);

            while (stack.Count > 0)
            {
                var p = stack.Pop();
                var (y, x) = (p / width, p % width);
                (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
                sum += data[p];
                count++;

                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (nx, ny) = (x + dx, y + dy);
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }

                        var n = (ny * width) + nx;
                        if (!visited[n] && data[n] >= binaryThreshold)
                        {
                            visited[n] = true;
                            stack.Push(n);
                        }
                    }
                }
            }

            var (w, h) = (x1 - x0 + 1, y1 - y0 + 1);
            var mean = sum / count;
            if (Math.Min(w, h) < minSide || mean < boxThreshold)
            {
                continue;
            }

            var offset = w * h * unclipRatio / (2f * (w + h));
            boxes.Add(new ModelBox(
                Math.Max(0, x0 - offset),
                Math.Max(0, y0 - offset),
                Math.Min(width, x1 + 1 + offset),
                Math.Min(height, y1 + 1 + offset),
                mean));
        }

        return boxes;
    }
}

public static class ScoreMath
{
    public static float[] Softmax(ReadOnlySpan<float> logits)
    {
        var max = float.MinValue;
        foreach (var v in logits)
        {
            max = Math.Max(max, v);
        }

        var result = new float[logits.Length];
        var sum = 0f;
        for (var i = 0; i < logits.Length; i++)
        {
            result[i] = MathF.Exp(logits[i] - max);
            sum += result[i];
        }

        for (var i = 0; i < result.Length; i++)
        {
            result[i] /= sum;
        }

        return result;
    }

    /// <summary>Probabilitas apa adanya bila sudah berjumlah satu, softmax bila masih logit.</summary>
    public static float[] AsProbabilities(ReadOnlySpan<float> values)
    {
        var sum = 0f;
        var negative = false;
        foreach (var v in values)
        {
            sum += v;
            negative |= v < 0;
        }

        return !negative && Math.Abs(sum - 1f) < 0.02f ? values.ToArray() : Softmax(values);
    }
}

/// <summary>Memetakan peta hasil model (mask, kepadatan) ke grid yang menutupi gambar asli.</summary>
public static class MapResampler
{
    /// <summary>Lebar grid keluaran; cukup halus untuk overlay dan tetap kecil disimpan.</summary>
    public const int OutputWidth = 384;

    public static (int Width, int Height) OutputSize(int imageWidth, int imageHeight)
        => (OutputWidth, Math.Max(1, (int)MathF.Round(OutputWidth * (float)imageHeight / imageWidth)));

    /// <summary>
    /// Sampel tetangga terdekat. <paramref name="sample"/> menerima koordinat sel di peta
    /// sumber yang membentang di seluruh masukan model (termasuk bingkai letterbox).
    /// </summary>
    public static T[] Resample<T>(int sourceWidth, int sourceHeight, Letterbox letterbox, int imageWidth, int imageHeight, Func<int, int, T> sample)
    {
        var (width, height) = OutputSize(imageWidth, imageHeight);
        var output = new T[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (mx, my) = letterbox.ToModel((x + 0.5f) / width, (y + 0.5f) / height);
                var sx = Math.Clamp((int)(mx * sourceWidth), 0, sourceWidth - 1);
                var sy = Math.Clamp((int)(my * sourceHeight), 0, sourceHeight - 1);
                output[(y * width) + x] = sample(sx, sy);
            }
        }

        return output;
    }
}
