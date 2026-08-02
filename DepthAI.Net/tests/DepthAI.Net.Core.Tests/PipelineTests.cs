using DepthAI.Devices;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Pipelines.Nodes;

namespace DepthAI.Tests;

public class PipelineBuilderTests
{
    private static DeviceCapabilities FullCapabilities => new()
    {
        ColorCameraCount = 1,
        MonoCameraCount = 2,
        SupportsStereoDepth = true,
        HasImu = true,
        ShaveCores = 16,
    };

    [Fact]
    public void AddStereoDepth_WiresBothMonoCameras()
    {
        var pipeline = Pipeline.CreateBuilder()
            .AddStereoDepth("stereo")
            .StreamOut("stereo.depth", "depth")
            .Build(FullCapabilities);

        Assert.Equal(3, pipeline.Nodes.Count);
        Assert.Contains(pipeline.Links, l => l.To == "stereo.left");
        Assert.Contains(pipeline.Links, l => l.To == "stereo.right");
    }

    [Fact]
    public void AddObjectDetection_InsertsResizeWhenSourceSizeDiffersFromModel()
    {
        var model = NeuralModel.CreatePlaceholder(ModelFamily.MobileNetSsd, ["person"], 300, 300);

        var pipeline = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
            .AddObjectDetection(model, "rgb.preview", "detector")
            .StreamOut("detector.detections", "detections")
            .Build(FullCapabilities);

        // Ketidakcocokan ukuran adalah penyebab paling sering deteksi "berjalan tapi
        // tidak menemukan apa-apa", jadi builder wajib menyisipkan node penyesuai.
        var manip = Assert.IsType<ImageManipNode>(pipeline.GetNode("detector_resize"));
        Assert.Equal(300, manip.ResizeWidth);
        Assert.Equal(300, manip.ResizeHeight);

        Assert.Contains(pipeline.Links, l => l is { From: "rgb.preview", To: "detector_resize.input" });
        Assert.Contains(pipeline.Links, l => l is { From: "detector_resize.out", To: "detector.input" });
    }

    [Fact]
    public void AddObjectDetection_LinksDirectlyWhenSizesMatch()
    {
        var model = NeuralModel.CreatePlaceholder(ModelFamily.MobileNetSsd, ["person"], 300, 300);

        var pipeline = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera => camera.WithPreview(300, 300))
            .AddObjectDetection(model, "rgb.preview", "detector")
            .StreamOut("detector.detections", "detections")
            .Build(FullCapabilities);

        Assert.DoesNotContain(pipeline.Nodes, n => n is ImageManipNode);
        Assert.Contains(pipeline.Links, l => l is { From: "rgb.preview", To: "detector.input" });
    }

    [Fact]
    public void Validate_ReportsDanglingRequiredInput()
    {
        var pipeline = Pipeline.Create();
        pipeline.AddStereoDepth("stereo");

        var result = pipeline.Validate(FullCapabilities);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("stereo.left"));
        Assert.Contains(result.Errors, e => e.Contains("stereo.right"));
    }

    [Fact]
    public void Validate_RejectsSubpixelTogetherWithExtendedDisparity()
    {
        var pipeline = Pipeline.CreateBuilder()
            .AddStereoDepth("stereo", node =>
            {
                node.Subpixel = true;
                node.ExtendedDisparity = true;
            })
            .StreamOut("stereo.depth", "depth")
            .BuildUnvalidated();

        var result = pipeline.Validate(FullCapabilities);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("subpixel"));
    }

    [Fact]
    public void Validate_ReportsMissingHardware()
    {
        var pipeline = Pipeline.CreateBuilder()
            .AddStereoDepth("stereo")
            .StreamOut("stereo.depth", "depth")
            .BuildUnvalidated();

        // Perangkat satu kamera warna seperti OAK-1 tidak bisa menjalankan stereo depth.
        var result = pipeline.Validate(new DeviceCapabilities
        {
            ColorCameraCount = 1,
            MonoCameraCount = 0,
            SupportsStereoDepth = false,
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("stereo"));
    }

    [Fact]
    public void Validate_WarnsWhenNoOutputStreams()
    {
        var pipeline = Pipeline.Create();
        pipeline.AddColorCamera("rgb");

        var result = pipeline.Validate(FullCapabilities);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("output stream"));
    }

    [Fact]
    public void Add_RejectsDuplicateNodeName()
    {
        var pipeline = Pipeline.Create();
        pipeline.AddColorCamera("rgb");

        var exception = Assert.Throws<ArgumentException>(() => pipeline.AddColorCamera("rgb"));
        Assert.Contains("unik", exception.Message);
    }

    [Fact]
    public void ResolveOutput_ThrowsWithHelpfulMessageForUnknownPort()
    {
        var pipeline = Pipeline.Create();
        pipeline.AddColorCamera("rgb");

        var exception = Assert.Throws<KeyNotFoundException>(() => pipeline.ResolveOutput("rgb.nonsense"));
        Assert.Contains("preview", exception.Message);
    }

    [Fact]
    public void ResolveOutput_ThrowsOnMalformedPath()
        => Assert.Throws<FormatException>(() => Pipeline.Create().ResolveOutput("rgb"));
}

public class PipelineSerializationTests
{
    [Fact]
    public void RoundTrip_PreservesGraphAndProperties()
    {
        var original = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera =>
            {
                camera.Fps = 24;
                camera.WithPreview(512, 288);
                camera.ColorOrder = ColorOrder.Rgb;
            })
            .AddStereoDepth("stereo", node =>
            {
                node.Preset = DepthPreset.HighAccuracy;
                node.ConfidenceThreshold = 180;
                node.AlignTo = CameraSocket.Rgb;
            })
            .StreamOut("rgb.preview", "video", maxSize: 8, blocking: true)
            .StreamOut("stereo.depth", "depth")
            .BuildUnvalidated();

        var restored = Pipeline.FromJson(original.ToJson());

        Assert.Equal(original.Nodes.Count, restored.Nodes.Count);
        Assert.Equal(original.Links.Count, restored.Links.Count);

        var camera = restored.GetNode<ColorCameraNode>("rgb");
        Assert.Equal(24, camera.Fps);
        Assert.Equal(512, camera.PreviewWidth);
        Assert.Equal(ColorOrder.Rgb, camera.ColorOrder);

        var stereo = restored.GetNode<StereoDepthNode>("stereo");
        Assert.Equal(DepthPreset.HighAccuracy, stereo.Preset);
        Assert.Equal(180, stereo.ConfidenceThreshold);
        Assert.Equal(CameraSocket.Rgb, stereo.AlignTo);

        var stream = restored.OutputStreams.Single(s => s.Name == "video");
        Assert.Equal(8, stream.MaxSize);
        Assert.True(stream.Blocking);
    }

    [Fact]
    public void FromJson_RecordsModelNameAndReconnectsViaResolver()
    {
        var model = NeuralModel.CreatePlaceholder(ModelFamily.Yolo, ["person"], 640, 640, name: "yolov8n");

        var pipeline = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera => camera.WithPreview(640, 640))
            .AddObjectDetection(model, "rgb.preview", "detector")
            .StreamOut("detector.detections", "detections")
            .BuildUnvalidated();

        var json = pipeline.ToJson();

        // Payload model tidak diserialisasi — hanya namanya, karena bobot bisa ratusan MB.
        Assert.DoesNotContain("yolov8n.blob", json, StringComparison.Ordinal);

        var withoutResolver = Pipeline.FromJson(json);
        var detached = withoutResolver.GetNode<DetectionNetworkNode>("detector");
        Assert.Null(detached.Model);
        Assert.Equal("yolov8n", detached.ModelName);

        var withResolver = Pipeline.FromJson(json, new PipelineLoadOptions { ModelResolver = _ => model });
        Assert.NotNull(withResolver.GetNode<DetectionNetworkNode>("detector").Model);
    }

    [Fact]
    public void FromJson_ThrowsOnUnknownNodeType()
    {
        const string Json = """
            { "nodes": [ { "name": "x", "type": "Teleporter" } ] }
            """;

        var exception = Assert.Throws<NotSupportedException>(() => Pipeline.FromJson(Json));
        Assert.Contains("ColorCamera", exception.Message);
    }

    [Theory]
    [InlineData("rgb-preview")]
    [InlineData("stereo-depth")]
    [InlineData("record-rgbd")]
    [InlineData("imu-stream")]
    public void Presets_SurviveJsonRoundTrip(string preset)
    {
        var pipeline = PipelinePresets.Create(preset);
        var restored = Pipeline.FromJson(pipeline.ToJson());

        Assert.Equal(pipeline.Nodes.Count, restored.Nodes.Count);
        Assert.Equal(pipeline.OutputStreams.Count, restored.OutputStreams.Count);
    }

    [Fact]
    public void PipelinePresets_Create_RejectsUnknownName()
        => Assert.Throws<ArgumentException>(() => PipelinePresets.Create("tidak-ada"));
}
