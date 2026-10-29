using System.Formats.Tar;
using System.Text;
using DepthAI.Imaging;
using DepthAI.Inference;
using DepthAI.Messages;
using DepthAI.Pipelines;
using DepthAI.Pipelines.Nodes;
using DepthAI.Streaming;

namespace DepthAI.Tests;

/// <summary>
/// Perilaku yang di-port dari depthai-core 3.9–3.11. Tiap kelas menyebut rilis asalnya
/// supaya jelas mana yang harus diperiksa ulang saat upstream berubah lagi.
/// </summary>
public class DepthColorizerUpstreamTests
{
    private static DepthFrame Depth(int width, int height, params ushort[] millimeters)
        => DepthFrame.Wrap(millimeters, width, height);

    [Fact]
    public void Colorize_PaintsInvalidPixelsBlack()
    {
        using var frame = Depth(3, 1, 0, 1_000, 5_000);

        var bgr = DepthColorizer.Colorize(frame);

        Assert.Equal([0, 0, 0], bgr[..3]);
        Assert.Contains(bgr[3..6], static b => b != 0);
    }

    [Fact]
    public void Colorize_MapsNearToBlueAndFarToRedWithJet()
    {
        using var frame = Depth(2, 1, 300, 12_000);

        var bgr = DepthColorizer.Colorize(frame, useLog: false);

        Assert.True(bgr[0] > bgr[2], "Piksel dekat harus kebiruan.");
        Assert.True(bgr[5] > bgr[3], "Piksel jauh harus kemerahan.");
    }

    [Fact]
    public void Colorize_LogScaleSpreadsNearDistancesFurtherThanLinear()
    {
        // Dalam skala log, 600 mm sudah jauh dari ujung dekat; dalam skala linear masih dekat 0.
        using var frame = Depth(1, 1, 600);

        var linear = DepthColorizer.Colorize(frame, colorMap: DepthColorMap.Grayscale, useLog: false);
        var log = DepthColorizer.Colorize(frame, colorMap: DepthColorMap.Grayscale, useLog: true);

        // Grayscale: dekat = terang, jadi nilai lebih kecil berarti lebih jauh di sepanjang rentang.
        Assert.True(log[0] < linear[0]);
    }

    [Fact]
    public void AutoRange_UsesThirdAndNinetyFifthPercentileOfValidPixels()
    {
        var values = Enumerable.Range(1, 100).Select(static v => (ushort)v).Append((ushort)0).ToArray();

        var (min, max) = DepthColorizer.AutoRange(values);

        // round(0.03 * 99) = 3 dan round(0.95 * 99) = 94 pada nilai terurut 1..100.
        Assert.Equal(4f, min);
        Assert.Equal(95f, max);
    }

    [Fact]
    public void Colorize_WithAutoRangeAndNoValidPixels_ReturnsBlackImage()
    {
        using var frame = Depth(2, 2, 0, 0, 0, 0);

        var bgr = DepthColorizer.Colorize(frame, minMillimeters: 0, maxMillimeters: 0);

        Assert.All(bgr, static b => Assert.Equal(0, b));
    }

    [Fact]
    public void ColorizeToImageFrame_CarriesSourceMetadata()
    {
        using var depth = Depth(1, 1, 1_000);
        using var image = DepthColorizer.ColorizeToImageFrame(depth);

        Assert.Equal(PixelFormat.Bgr888, image.Format);
        Assert.Equal(depth.SequenceNumber, image.SequenceNumber);
        Assert.Equal(depth.HostTimestamp, image.HostTimestamp);
    }
}

/// <summary>YOLO grid mentah, strides, dan perbaikan YOLOv6 dari depthai-core 3.9.</summary>
public class YoloGridDecodingTests
{
    /// <summary>Grid 2x2 dengan satu sel aktif di (baris 0, kolom 1).</summary>
    private static Tensor Grid(string name, float b0, float b1, float b2, float b3, float score = 0.9f)
    {
        const int Channels = 7;
        const int Plane = 4;
        var data = new float[Channels * Plane];
        const int Cell = 1;

        data[Cell] = b0;
        data[Plane + Cell] = b1;
        data[(2 * Plane) + Cell] = b2;
        data[(3 * Plane) + Cell] = b3;
        data[(4 * Plane) + Cell] = score;
        data[(5 * Plane) + Cell] = 0.3f;
        data[(6 * Plane) + Cell] = 0.8f;

        return new Tensor(name, data, [1, Channels, 2, 2]);
    }

    private static ModelMetadata Metadata(YoloDecodingFamily family, params int[] strides) => new()
    {
        Family = ModelFamily.Yolo,
        Labels = ["kucing", "anjing"],
        InputWidth = 16,
        InputHeight = 16,
        YoloDecoding = family,
        Strides = strides,
    };

    private static DetectionFrame Parse(ModelMetadata metadata, params Tensor[] tensors)
        => (DetectionFrame)new YoloParser(metadata).Parse(
            tensors.ToDictionary(static t => t.Name),
            new InferenceContext { StreamName = "nn" });

    [Fact]
    public void Tlbr_DecodesDistancesFromCellCentre()
    {
        var frame = Parse(Metadata(YoloDecodingFamily.Tlbr, 8), Grid("output1_yolo", 0.5f, 0.5f, 0.5f, 0.5f));

        var detection = Assert.Single(frame.Detections);
        Assert.Equal("anjing", detection.Label);
        Assert.Equal(0.8f, detection.Confidence, 4);
        Assert.Equal(new BoundingBox(0.5f, 0f, 1f, 0.5f), detection.Box);
    }

    [Fact]
    public void CenterSize_UsesCellOffsetAndExponentialSize()
    {
        // Pusat (1 + 0.5) * 8 = 12, lebar e^0 * 8 = 8 → kotak 8..16 piksel.
        var frame = Parse(Metadata(YoloDecodingFamily.CenterSize, 8), Grid("output1_yolo", 0.5f, 0.5f, 0f, 0f));

        var detection = Assert.Single(frame.Detections);
        Assert.Equal(new BoundingBox(0.5f, 0f, 1f, 0.5f), detection.Box);
    }

    [Fact]
    public void Grid_RejectsStrideCountThatDoesNotMatchOutputs()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Parse(
            Metadata(YoloDecodingFamily.Tlbr, 8, 16),
            Grid("output1_yolo", 0.5f, 0.5f, 0.5f, 0.5f)));

        Assert.Contains("stride", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grid_IgnoresOutputsWithoutYoloSuffix()
    {
        // Sama seperti upstream: tensor lain (misalnya mask) bukan keluaran deteksi.
        var frame = Parse(
            Metadata(YoloDecodingFamily.Tlbr, 8),
            Grid("output1_yolo", 0.5f, 0.5f, 0.5f, 0.5f),
            new Tensor("protos", new float[4], [1, 1, 2, 2]));

        Assert.Single(frame.Detections);
    }

    [Theory]
    [InlineData("yolov6", YoloDecodingFamily.CenterSize)]
    [InlineData("YOLOv6R1", YoloDecodingFamily.CenterSize)]
    [InlineData("yolov6r2", YoloDecodingFamily.Tlbr)]
    [InlineData("yolov8", YoloDecodingFamily.Tlbr)]
    [InlineData("yolov11", YoloDecodingFamily.Tlbr)]
    [InlineData("yolov5", YoloDecodingFamily.AnchorBased)]
    [InlineData(null, YoloDecodingFamily.Auto)]
    public void FromSubtype_FollowsUpstreamResolver(string? subtype, YoloDecodingFamily expected)
        => Assert.Equal(expected, YoloDecodingFamilies.FromSubtype(subtype));

    [Fact]
    public void IouOverrideFromContext_ControlsSuppression()
    {
        // Dua kotak kelas sama dengan IoU ≈ 0.82 di antara anchor kosong: tertahan pada
        // ambang 0.5, lolos pada 0.9. Tata letak anchor-free [1, 4 + nc, anchors].
        var data = new float[]
        {
            320, 330, 0, 0, 0, 0,
            320, 320, 0, 0, 0, 0,
            100, 100, 0, 0, 0, 0,
            100, 100, 0, 0, 0, 0,
            0.9f, 0.8f, 0, 0, 0, 0,
        };
        var tensor = new Tensor("out", data, [1, 5, 6]);
        var parser = new YoloParser(new ModelMetadata { Family = ModelFamily.Yolo, Labels = ["a"] });
        var tensors = new Dictionary<string, Tensor> { ["out"] = tensor };

        var strict = (DetectionFrame)parser.Parse(tensors, new InferenceContext { IouThreshold = 0.5f });
        var loose = (DetectionFrame)parser.Parse(tensors, new InferenceContext { IouThreshold = 0.9f });

        Assert.Single(strict.Detections);
        Assert.Equal(2, loose.Count);
    }
}

/// <summary>config.json NN Archive v1, termasuk strides dan background_class (3.9).</summary>
public class NnArchiveConfigTests
{
    private const string YoloConfig = """
        {
          "config_version": "1.0",
          "model": {
            "metadata": { "name": "yolov6n", "path": "yolov6n.blob" },
            "inputs": [ { "name": "images", "shape": [1, 3, 384, 512], "layout": "NCHW" } ],
            "outputs": [ { "name": "output1_yolo" }, { "name": "output2_yolo" }, { "name": "output3_yolo" } ],
            "heads": [ {
              "parser": "YOLO",
              "metadata": {
                "classes": ["person", "bicycle"],
                "n_classes": 2,
                "iou_threshold": 0.45,
                "conf_threshold": 0.3,
                "subtype": "yolov6",
                "yolo_outputs": ["output1_yolo", "output2_yolo", "output3_yolo"],
                "strides": [8, 16, 32],
                "anchors": null
              },
              "outputs": ["output1_yolo", "output2_yolo", "output3_yolo"]
            } ]
          }
        }
        """;

    [Fact]
    public void Parse_ReadsYoloHeadMetadata()
    {
        var metadata = NnArchiveConfig.Parse(Encoding.UTF8.GetBytes(YoloConfig));

        Assert.Equal(ModelFamily.Yolo, metadata.Family);
        Assert.Equal(512, metadata.InputWidth);
        Assert.Equal(384, metadata.InputHeight);
        Assert.Equal(["person", "bicycle"], metadata.Labels);
        Assert.Equal(0.3f, metadata.ConfidenceThreshold, 4);
        Assert.Equal(0.45f, metadata.IouThreshold, 4);
        Assert.Equal([8, 16, 32], metadata.Strides);
        Assert.Equal("yolov6", metadata.Subtype);
        Assert.Equal(YoloDecodingFamily.CenterSize, metadata.YoloDecoding);
    }

    [Fact]
    public void Parse_RejectsStridesThatDoNotMatchYoloOutputs()
    {
        var json = YoloConfig.Replace("\"strides\": [8, 16, 32]", "\"strides\": [8, 16]", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() => NnArchiveConfig.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Contains("2 stride untuk 3 keluaran", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CountsYoloSuffixedHeadOutputsWhenYoloOutputsIsMissing()
    {
        var json = YoloConfig.Replace(
            "\"yolo_outputs\": [\"output1_yolo\", \"output2_yolo\", \"output3_yolo\"],", string.Empty, StringComparison.Ordinal);

        Assert.Equal([8, 16, 32], NnArchiveConfig.Parse(Encoding.UTF8.GetBytes(json)).Strides);
    }

    [Fact]
    public void Parse_ReadsBackgroundClassForSegmentation()
    {
        const string json = """
            { "config_version": "1.0", "model": {
                "inputs": [ { "shape": [1, 256, 256, 3], "layout": "NHWC" } ],
                "heads": [ { "parser": "SegmentationParser",
                             "metadata": { "classes": ["bg", "road"], "background_class": true } } ] } }
            """;

        var metadata = NnArchiveConfig.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(ModelFamily.Segmentation, metadata.Family);
        Assert.True(metadata.BackgroundClass);
        Assert.Equal(256, metadata.InputWidth);
    }

    [Fact]
    public async Task LoadFromFile_UsesConfigJsonNextToExtractedModel()
    {
        var directory = Directory.CreateTempSubdirectory("depthai-nnarchive-");
        try
        {
            var model = Path.Combine(directory.FullName, "yolov6n.blob");
            await File.WriteAllBytesAsync(model, [1, 2, 3], TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(directory.FullName, NnArchiveConfig.FileName), YoloConfig, TestContext.Current.CancellationToken);

            var loaded = await NeuralModel.LoadFromFileAsync(model, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(ModelFamily.Yolo, loaded.Metadata.Family);
            Assert.Equal(YoloDecodingFamily.CenterSize, loaded.Metadata.YoloDecoding);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoadFromFile_ReadsConfigInsideTarArchive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"depthai-{Guid.NewGuid():N}.tar");
        try
        {
            await using (var file = File.Create(path))
            await using (var tar = new TarWriter(file))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "config.json")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(YoloConfig)),
                };
                await tar.WriteEntryAsync(entry, TestContext.Current.CancellationToken);
            }

            var loaded = await NeuralModel.LoadFromFileAsync(path, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(ModelFormat.NnArchive, loaded.Format);
            Assert.Equal(["person", "bicycle"], loaded.Metadata.Labels);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class SegmentationBackgroundClassTests
{
    [Fact]
    public void BackgroundChannel_MarksPixelsItWinsAsNoClass()
    {
        // Logit [1, 3, 1, 2]: piksel 0 dimenangkan latar, piksel 1 oleh kelas 2.
        var logits = new float[] { 0.9f, 0.1f, 0.5f, 0.2f, 0.1f, 0.7f };
        var tensor = new Tensor("out", logits, [1, 3, 1, 2]);
        var tensors = new Dictionary<string, Tensor> { ["out"] = tensor };

        using var withBackground = (SegmentationFrame)new SegmentationParser(
            new ModelMetadata { Family = ModelFamily.Segmentation, BackgroundClass = true }).Parse(tensors, default);
        using var plain = (SegmentationFrame)new SegmentationParser(
            new ModelMetadata { Family = ModelFamily.Segmentation }).Parse(tensors, default);

        Assert.Equal([SegmentationFrame.NoClass, (byte)2], withBackground.ClassMap.ToArray());
        Assert.Equal([(byte)0, (byte)2], plain.ClassMap.ToArray());
    }
}

/// <summary>Ambang deteksi saat runtime, padanan DetectionParserConfig (3.11).</summary>
public class RuntimeDetectionConfigTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<DepthAiDevice> StartDetectorAsync()
    {
        var model = NeuralModel.CreatePlaceholder(ModelFamily.MobileNetSsd, ["orang", "botol"], 300, 300);
        var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);

        var pipeline = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera => camera.WithPreview(300, 300))
            .AddObjectDetection(model, "rgb.preview", "detector")
            .StreamOut("detector.detections", "detections")
            .StreamOut("rgb.preview", "video")
            .Build(device.Capabilities);

        await device.StartAsync(pipeline);
        return device;
    }

    [Fact]
    public async Task SendConfig_AppliesToFramesAfterTheUpdate()
    {
        await using var device = await StartDetectorAsync();
        using var cts = new CancellationTokenSource(Timeout);
        var stream = device.GetStream<DetectionFrame>("detections");

        long lastBefore;
        using (var before = await stream.FirstAsync(cts.Token))
        {
            Assert.NotEmpty(before.Detections);
            lastBefore = before.SequenceNumber;
        }

        // Keyakinan simulasi paling tinggi 0.92, jadi 0.95 harus menyaring semuanya.
        device.SendConfig("detector", new DetectionParserConfig { ConfidenceThreshold = 0.95f });

        await foreach (var frame in stream.ToAsyncEnumerable(cancellationToken: cts.Token))
        {
            using (frame)
            {
                // Satu frame mungkin sudah dikonversi saat konfigurasi berubah; lewati.
                if (frame.SequenceNumber <= lastBefore + 2)
                {
                    continue;
                }

                Assert.Empty(frame.Detections);
                break;
            }
        }
    }

    [Fact]
    public async Task SendConfig_RejectsUnknownAndNonDetectionNodes()
    {
        await using var device = await StartDetectorAsync();

        Assert.Throws<KeyNotFoundException>(() => device.SendConfig("tidak-ada", new DetectionParserConfig()));
        Assert.Throws<InvalidOperationException>(() => device.SendConfig("rgb", new DetectionParserConfig()));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => device.SendConfig("detector", new DetectionParserConfig { IouThreshold = 1.5f }));
    }

    [Fact]
    public void IouThreshold_RoundTripsThroughPipelineJson()
    {
        var pipeline = new Pipeline();
        pipeline.AddDetectionNetwork("detector", node => node.IouThreshold = 0.35f);

        var restored = Pipeline.FromJson(pipeline.ToJson());

        var node = Assert.IsType<DetectionNetworkNode>(Assert.Single(restored.Nodes));
        Assert.Equal(0.35f, node.IouThreshold!.Value, 4);
    }
}

/// <summary>
/// Berkas <c>.dai</c> (3.10). Fixture dibuat dengan depthai 3.11.0 Python
/// (<c>msg.save()</c>), sehingga test ini membuktikan kompatibilitas dua arah format.
/// </summary>
public class DaiMessageFileTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "depthai-3.11", name);

    [Fact]
    public void LoadsBgrImgFrameSavedByPythonDepthai()
    {
        using var frame = DaiMessageFile.LoadImageFrame(Fixture("bgr.dai"));

        Assert.Equal(PixelFormat.Bgr888, frame.Format);
        Assert.Equal((6, 4, 18), (frame.Width, frame.Height, frame.Stride));
        Assert.Equal(42, frame.SequenceNumber);
        Assert.Equal(TimeSpan.FromSeconds(3.25), frame.DeviceTimestamp);

        // Piksel (x=5, y=3): B = 5 * 10, G = 3 * 20, R = 7.
        var pixel = frame.GetRow(3).Slice(5 * 3, 3).ToArray();
        Assert.Equal([50, 60, 7], pixel);
    }

    [Fact]
    public void LoadsPlanarBgrAsInterleaved()
    {
        using var planar = DaiMessageFile.LoadImageFrame(Fixture("bgr_planar.dai"));
        using var interleaved = DaiMessageFile.LoadImageFrame(Fixture("bgr.dai"));

        Assert.Equal(PixelFormat.Bgr888, planar.Format);
        Assert.Equal(interleaved.Pixels.ToArray(), planar.Pixels.ToArray());
    }

    [Fact]
    public void LoadsRaw16AsDepthFrame()
    {
        var frame = Assert.IsType<DepthFrame>(DaiMessageFile.Load(Fixture("depth.dai")));
        using (frame)
        {
            Assert.Equal((4, 3), (frame.Width, frame.Height));
            Assert.Equal(7, frame.SequenceNumber);
            Assert.Null(frame.GetDistanceMeters(0, 0));
            Assert.Equal(5_500, frame.GetMillimeters(3, 2));
        }
    }

    [Fact]
    public void LoadsGray8()
    {
        using var frame = DaiMessageFile.LoadImageFrame(Fixture("gray.dai"));

        Assert.Equal(PixelFormat.Gray8, frame.Format);
        Assert.All(frame.Pixels.ToArray(), static p => Assert.Equal(9, p));
    }

    [Fact]
    public void LoadsImgDetections()
    {
        using var frame = DaiMessageFile.LoadDetections(Fixture("det.dai"));

        var detection = Assert.Single(frame.Detections);
        Assert.Equal(5, frame.SequenceNumber);
        Assert.Equal((3, "cup"), (detection.LabelIndex, detection.Label));
        Assert.Equal(0.75f, detection.Confidence, 4);
        Assert.Equal(0.6f, detection.Box.YMax, 4);
    }

    [Fact]
    public void ImageFrame_RoundTrips()
    {
        var path = TempPath();
        try
        {
            using var original = ImageFrame.Wrap([1, 2, 3, 4, 5, 6], 2, 1, PixelFormat.Rgb888);
            original.SequenceNumber = 9;
            original.DeviceTimestamp = TimeSpan.FromMilliseconds(1234.5);
            original.Save(path);

            using var loaded = DaiMessageFile.LoadImageFrame(path + DaiMessageFile.Extension);

            Assert.Equal(original.Pixels.ToArray(), loaded.Pixels.ToArray());
            Assert.Equal(PixelFormat.Rgb888, loaded.Format);
            Assert.Equal(9, loaded.SequenceNumber);
            Assert.Equal(original.DeviceTimestamp, loaded.DeviceTimestamp);
            Assert.Equal(original.HostTimestamp, loaded.HostTimestamp);
        }
        finally
        {
            File.Delete(path + DaiMessageFile.Extension);
        }
    }

    [Fact]
    public void DepthFrame_RoundTrips()
    {
        var path = TempPath() + DaiMessageFile.Extension;
        try
        {
            using var original = DepthFrame.Wrap([0, 800, 1_600, 65_000], 2, 2);
            original.Save(path);

            using var loaded = DaiMessageFile.LoadDepthFrame(path);
            Assert.Equal(original.Millimeters.ToArray(), loaded.Millimeters.ToArray());
            Assert.Equal(DaiMessageFile.ImgFrameSchema, DaiMessageFile.ReadSchemaName(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DetectionFrame_RoundTrips()
    {
        var path = TempPath() + DaiMessageFile.Extension;
        try
        {
            using var original = new DetectionFrame
            {
                Detections =
                [
                    new Detection { LabelIndex = 0, Label = "orang", Confidence = 0.9f, Box = new BoundingBox(0.1f, 0.2f, 0.3f, 0.4f) },
                    new Detection { LabelIndex = 4, Confidence = 0.5f, Box = new BoundingBox(0.5f, 0.5f, 0.9f, 0.8f) },
                ],
            };
            original.Save(path);

            using var loaded = DaiMessageFile.LoadDetections(path);
            Assert.Equal(original.Detections, loaded.Detections);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReportsSchemaMismatchLikeUpstream()
    {
        var error = Assert.Throws<InvalidDataException>(() => DaiMessageFile.LoadDetections(Fixture("bgr.dai")));

        Assert.StartsWith("Schema mismatch when reading file", error.Message, StringComparison.Ordinal);
        Assert.Contains("got: dai.proto.img_frame.ImgFrame", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_RejectsFormatWithoutDepthaiEquivalent()
    {
        using var frame = ImageFrame.Wrap(new byte[4], 1, 1, PixelFormat.Bgra8888);

        Assert.Throws<NotSupportedException>(() => frame.Save(TempPath()));
    }

    [Theory]
    [InlineData("capture", "capture.dai")]
    [InlineData("capture.bin", "capture.bin")]
    public void ResolvePath_AddsExtensionOnlyWhenMissing(string input, string expected)
        => Assert.Equal(expected, DaiMessageFile.ResolvePath(input));

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"depthai-{Guid.NewGuid():N}");
}
