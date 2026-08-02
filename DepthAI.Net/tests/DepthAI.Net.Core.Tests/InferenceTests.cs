using DepthAI.Inference;
using DepthAI.Streaming;

namespace DepthAI.Tests;

public class BoundingBoxTests
{
    [Fact]
    public void IntersectionOverUnion_IsOneForIdenticalBoxes()
    {
        var box = new BoundingBox(0.1f, 0.1f, 0.5f, 0.5f);
        Assert.Equal(1f, box.IntersectionOverUnion(box), 4);
    }

    [Fact]
    public void IntersectionOverUnion_IsZeroForDisjointBoxes()
    {
        var left = new BoundingBox(0f, 0f, 0.2f, 0.2f);
        var right = new BoundingBox(0.8f, 0.8f, 1f, 1f);

        Assert.Equal(0f, left.IntersectionOverUnion(right), 4);
    }

    [Fact]
    public void Clamp_PullsOutOfRangeCoordinatesBack()
    {
        var clamped = new BoundingBox(-0.4f, -0.2f, 1.7f, 1.1f).Clamp();

        Assert.Equal(0f, clamped.XMin);
        Assert.Equal(1f, clamped.XMax);
    }

    [Fact]
    public void ToPixels_ScalesToFrameSize()
    {
        var (x, y, width, height) = new BoundingBox(0.25f, 0.5f, 0.75f, 1f).ToPixels(640, 480);

        Assert.Equal(160, x);
        Assert.Equal(240, y);
        Assert.Equal(320, width);
        Assert.Equal(240, height);
    }
}

public class MobileNetSsdParserTests
{
    private static readonly ModelMetadata Metadata = new()
    {
        Family = ModelFamily.MobileNetSsd,
        Labels = ["latar", "orang", "botol"],
        ConfidenceThreshold = 0.5f,
        InputWidth = 300,
        InputHeight = 300,
    };

    private static Tensor BuildOutput(params (float Label, float Confidence, float X1, float Y1, float X2, float Y2)[] rows)
    {
        const int Slots = 10;
        var data = new float[Slots * 7];

        for (var i = 0; i < rows.Length; i++)
        {
            var offset = i * 7;
            data[offset] = 0;
            data[offset + 1] = rows[i].Label;
            data[offset + 2] = rows[i].Confidence;
            data[offset + 3] = rows[i].X1;
            data[offset + 4] = rows[i].Y1;
            data[offset + 5] = rows[i].X2;
            data[offset + 6] = rows[i].Y2;
        }

        if (rows.Length < Slots)
        {
            data[rows.Length * 7] = -1;
        }

        return new Tensor("detection_out", data, [1, 1, Slots, 7]);
    }

    [Fact]
    public void Parse_DecodesRowsAndAppliesLabels()
    {
        var parser = new MobileNetSsdParser(Metadata);
        var tensors = new Dictionary<string, Tensor>
        {
            ["detection_out"] = BuildOutput((1, 0.9f, 0.1f, 0.1f, 0.4f, 0.6f)),
        };

        using var frame = Assert.IsType<DetectionFrame>(parser.Parse(tensors, new InferenceContext()));

        var detection = Assert.Single(frame.Detections);
        Assert.Equal("orang", detection.Label);
        Assert.Equal(0.9f, detection.Confidence, 4);
    }

    [Fact]
    public void Parse_DropsDetectionsBelowThreshold()
    {
        var parser = new MobileNetSsdParser(Metadata);
        var tensors = new Dictionary<string, Tensor>
        {
            ["detection_out"] = BuildOutput(
                (1, 0.9f, 0.1f, 0.1f, 0.4f, 0.6f),
                (2, 0.2f, 0.5f, 0.5f, 0.7f, 0.8f)),
        };

        using var frame = (DetectionFrame)parser.Parse(tensors, new InferenceContext());

        Assert.Single(frame.Detections);
    }

    [Fact]
    public void Parse_StopsAtTerminatorRow()
    {
        var parser = new MobileNetSsdParser(Metadata);

        // Baris setelah image_id negatif adalah padding dan tidak boleh dibaca,
        // walaupun nilainya terlihat seperti deteksi yang sah.
        var data = new float[3 * 7];
        data[0] = 0; data[1] = 1; data[2] = 0.9f; data[3] = 0.1f; data[4] = 0.1f; data[5] = 0.4f; data[6] = 0.6f;
        data[7] = -1;
        data[14] = 0; data[15] = 2; data[16] = 0.95f; data[17] = 0.2f; data[18] = 0.2f; data[19] = 0.5f; data[20] = 0.7f;

        var tensors = new Dictionary<string, Tensor>
        {
            ["detection_out"] = new Tensor("detection_out", data, [1, 1, 3, 7]),
        };

        using var frame = (DetectionFrame)parser.Parse(tensors, new InferenceContext());

        Assert.Single(frame.Detections);
    }

    [Fact]
    public void Parse_HonoursRuntimeThresholdOverride()
    {
        var parser = new MobileNetSsdParser(Metadata);
        var tensors = new Dictionary<string, Tensor>
        {
            ["detection_out"] = BuildOutput((1, 0.3f, 0.1f, 0.1f, 0.4f, 0.6f)),
        };

        using var frame = (DetectionFrame)parser.Parse(
            tensors, new InferenceContext { ConfidenceThreshold = 0.2f });

        Assert.Single(frame.Detections);
    }
}

public class YoloParserTests
{
    private static readonly ModelMetadata Metadata = new()
    {
        Family = ModelFamily.Yolo,
        Labels = ["orang", "sepeda", "mobil"],
        ConfidenceThreshold = 0.5f,
        IouThreshold = 0.5f,
        InputWidth = 640,
        InputHeight = 640,
    };

    /// <summary>Tata letak anchor-free: [1, 4 + nc, anchors].</summary>
    private static Tensor BuildAnchorFree(int anchors, params (int Class, float Score, float Cx, float Cy, float W, float H)[] items)
    {
        const int Attributes = 7;
        var data = new float[Attributes * anchors];

        for (var i = 0; i < items.Length; i++)
        {
            data[(0 * anchors) + i] = items[i].Cx;
            data[(1 * anchors) + i] = items[i].Cy;
            data[(2 * anchors) + i] = items[i].W;
            data[(3 * anchors) + i] = items[i].H;
            data[((4 + items[i].Class) * anchors) + i] = items[i].Score;
        }

        return new Tensor("output0", data, [1, Attributes, anchors]);
    }

    [Fact]
    public void Parse_DecodesAnchorFreeLayout()
    {
        var parser = new YoloParser(Metadata);
        var tensors = new Dictionary<string, Tensor>
        {
            ["output0"] = BuildAnchorFree(64, (0, 0.9f, 320f, 320f, 128f, 256f)),
        };

        using var frame = (DetectionFrame)parser.Parse(tensors, new InferenceContext());

        var detection = Assert.Single(frame.Detections);
        Assert.Equal("orang", detection.Label);

        // Piksel dinormalkan terhadap ukuran input model.
        Assert.Equal(0.5f, detection.Box.CenterX, 3);
        Assert.Equal(0.2f, detection.Box.Width, 3);
    }

    [Fact]
    public void Parse_SuppressesOverlappingBoxesOfSameClass()
    {
        var parser = new YoloParser(Metadata);
        var tensors = new Dictionary<string, Tensor>
        {
            ["output0"] = BuildAnchorFree(
                64,
                (0, 0.9f, 320f, 320f, 128f, 128f),
                (0, 0.8f, 324f, 324f, 128f, 128f)),
        };

        using var frame = (DetectionFrame)parser.Parse(tensors, new InferenceContext());

        Assert.Single(frame.Detections);
        Assert.Equal(0.9f, frame.Detections[0].Confidence, 3);
    }

    [Fact]
    public void Parse_KeepsOverlappingBoxesOfDifferentClasses()
    {
        var parser = new YoloParser(Metadata);
        var tensors = new Dictionary<string, Tensor>
        {
            ["output0"] = BuildAnchorFree(
                64,
                (0, 0.9f, 320f, 320f, 128f, 128f),
                (2, 0.8f, 322f, 322f, 128f, 128f)),
        };

        using var frame = (DetectionFrame)parser.Parse(tensors, new InferenceContext());

        // NMS bekerja per kelas: orang yang memegang benda tidak boleh saling menghapus.
        Assert.Equal(2, frame.Detections.Count);
    }

    [Fact]
    public void Parse_AcceptsAlreadyNormalisedCoordinates()
    {
        var parser = new YoloParser(Metadata);
        var tensors = new Dictionary<string, Tensor>
        {
            ["output0"] = BuildAnchorFree(64, (1, 0.7f, 0.5f, 0.5f, 0.2f, 0.4f)),
        };

        using var frame = (DetectionFrame)parser.Parse(tensors, new InferenceContext());

        Assert.Equal(0.5f, frame.Detections[0].Box.CenterX, 3);
        Assert.Equal(0.2f, frame.Detections[0].Box.Width, 3);
    }
}

public class ClassificationParserTests
{
    [Fact]
    public void Parse_AppliesSoftmaxToLogits()
    {
        var parser = new ClassificationParser(new ModelMetadata
        {
            Family = ModelFamily.Classification,
            Labels = ["a", "b", "c"],
        });

        var tensors = new Dictionary<string, Tensor>
        {
            ["prob"] = new Tensor("prob", new[] { 5f, 1f, 0f }, [1, 3]),
        };

        using var frame = Assert.IsType<ClassificationFrame>(parser.Parse(tensors, new InferenceContext()));

        Assert.Equal("a", frame.Top!.Label);
        Assert.Equal(1f, frame.Results.Sum(r => r.Confidence), 3);
    }

    [Fact]
    public void Parse_LeavesExistingProbabilityDistributionAlone()
    {
        var parser = new ClassificationParser(new ModelMetadata
        {
            Family = ModelFamily.Classification,
            Labels = ["a", "b"],
        });

        var tensors = new Dictionary<string, Tensor>
        {
            ["prob"] = new Tensor("prob", new[] { 0.7f, 0.3f }, [1, 2]),
        };

        using var frame = (ClassificationFrame)parser.Parse(tensors, new InferenceContext());

        Assert.Equal(0.7f, frame.Top!.Confidence, 4);
    }
}

public class TensorTests
{
    [Fact]
    public void Constructor_RejectsShapeThatDoesNotMatchDataLength()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new Tensor("x", new float[5], [2, 3]));

        Assert.Contains("6 elemen", exception.Message);
    }

    [Fact]
    public void ToMatrix_RequiresRankTwo()
    {
        var tensor = new Tensor("x", new float[8], [2, 2, 2]);
        Assert.Throws<InvalidOperationException>(() => tensor.ToMatrix());
    }
}
