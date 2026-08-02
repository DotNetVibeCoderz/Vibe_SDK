using DepthAI.Devices;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Streaming;

namespace DepthAI.Tests;

/// <summary>
/// Test end-to-end pada backend simulasi.
/// </summary>
/// <remarks>
/// Test ini menjalankan jalur kode yang sama persis dengan hardware — termasuk parser
/// inferensi sungguhan — sehingga bisa berjalan di CI tanpa kamera terpasang.
/// </remarks>
public class SimulationEndToEndTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ListDevices_ReportsSimulatedDevice()
    {
        var devices = DepthAi.ListDevices(DepthAiOptions.Simulated);

        var device = Assert.Single(devices);
        Assert.True(device.IsSimulated);
        Assert.True(device.Capabilities.SupportsStereoDepth);
    }

    [Fact]
    public async Task ColorStream_DeliversFramesWithExpectedShape()
    {
        await using var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);

        var pipeline = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera => camera.WithPreview(320, 240))
            .StreamOut("rgb.preview", "video")
            .Build(device.Capabilities);

        await device.StartAsync(pipeline);

        var frame = await device.GetStream<ImageFrame>("video").FirstAsync(Cancellation());

        Assert.Equal(320, frame.Width);
        Assert.Equal(240, frame.Height);
        Assert.Equal(PixelFormat.Bgr888, frame.Format);
        Assert.Equal(320 * 240 * 3, frame.ByteLength);

        frame.Dispose();
        await device.StopAsync();
    }

    [Fact]
    public async Task DepthStream_ProducesMeasurableDistances()
    {
        await using var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);

        var pipeline = PipelinePresets.StereoDepth();
        await device.StartAsync(pipeline);

        var frame = await device.GetStream<DepthFrame>("depth").FirstAsync(Cancellation());

        var measured = 0;
        for (var y = 0; y < frame.Height; y += 8)
        {
            for (var x = 0; x < frame.Width; x += 8)
            {
                if (frame.GetDistanceMeters(x, y) is not null)
                {
                    measured++;
                }
            }
        }

        Assert.True(measured > 0, "peta kedalaman simulasi tidak berisi pengukuran valid");

        frame.Dispose();
        await device.StopAsync();
    }

    [Fact]
    public async Task DetectionStream_IsDecodedByTheRealParser()
    {
        var model = NeuralModel.CreatePlaceholder(
            ModelFamily.MobileNetSsd, ["orang", "botol", "kursi"], 300, 300);

        await using var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);

        var pipeline = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera => camera.WithPreview(300, 300))
            .AddObjectDetection(model, "rgb.preview", "detector")
            .StreamOut("detector.detections", "detections")
            .Build(device.Capabilities);

        await device.StartAsync(pipeline);

        var frame = await device.GetStream<DetectionFrame>("detections").FirstAsync(Cancellation());

        Assert.NotEmpty(frame.Detections);
        Assert.All(frame.Detections, d =>
        {
            Assert.Contains(d.Label, new[] { "orang", "botol", "kursi" });
            Assert.InRange(d.Confidence, 0f, 1f);
            Assert.InRange(d.Box.XMin, 0f, 1f);
            Assert.InRange(d.Box.YMax, 0f, 1f);
        });

        frame.Dispose();
        await device.StopAsync();
    }

    [Fact]
    public async Task YoloModel_IsDecodedFromSimulatedTensors()
    {
        var model = NeuralModel.CreatePlaceholder(
            ModelFamily.Yolo, ["orang", "sepeda", "mobil"], 640, 640);

        await using var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);

        var pipeline = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera => camera.WithPreview(640, 640))
            .AddObjectDetection(model, "rgb.preview", "detector")
            .StreamOut("detector.detections", "detections")
            .Build(device.Capabilities);

        await device.StartAsync(pipeline);

        var frame = await device.GetStream<DetectionFrame>("detections").FirstAsync(Cancellation());

        Assert.NotEmpty(frame.Detections);

        frame.Dispose();
        await device.StopAsync();
    }

    [Fact]
    public async Task GetStream_ThrowsWithAvailableNamesWhenStreamIsUnknown()
    {
        await using var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);

        await device.StartAsync(PipelinePresets.RgbPreview());

        var exception = Assert.Throws<KeyNotFoundException>(() => device.GetStream<ImageFrame>("tidak-ada"));
        Assert.Contains("video", exception.Message);

        await device.StopAsync();
    }

    [Fact]
    public async Task StartAsync_RejectsPipelineTheDeviceCannotRun()
    {
        await using var device = await DepthAiDevice.OpenAsync(new DepthAiOptions
        {
            Backend = BackendSelection.SimulationOnly,
            Simulation = new Simulation.SimulationOptions { DeviceCount = 1 },
        });

        var pipeline = Pipeline.Create();
        var nn = pipeline.AddDetectionNetwork("detector");
        var camera = pipeline.AddColorCamera("rgb");
        camera.Preview.LinkTo(nn.Input);
        pipeline.AddOutputStream(nn.Detections, "detections");

        // Node NN tanpa model harus ditolak sebelum start, bukan gagal diam-diam.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => device.StartAsync(pipeline));
        Assert.Contains("model", exception.Message);
    }

    [Fact]
    public async Task Telemetry_ReportsPlausibleChipTemperature()
    {
        await using var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);
        await device.StartAsync(PipelinePresets.RgbPreview());

        var telemetry = device.ReadTelemetry();

        Assert.InRange(telemetry.ChipTemperatureCelsius, 20f, 100f);
        Assert.True(telemetry.DdrTotalBytes > 0);

        await device.StopAsync();
    }

    [Fact]
    public async Task Simulation_IsDeterministicForTheSameSeed()
    {
        var options = new DepthAiOptions
        {
            Backend = BackendSelection.SimulationOnly,
            Simulation = new Simulation.SimulationOptions { Seed = 4242 },
        };

        var first = await FirstDetectionLabelsAsync(options);
        var second = await FirstDetectionLabelsAsync(options);

        Assert.Equal(first, second);
    }

    private static async Task<IReadOnlyList<string>> FirstDetectionLabelsAsync(DepthAiOptions options)
    {
        var model = NeuralModel.CreatePlaceholder(
            ModelFamily.MobileNetSsd, ["orang", "botol", "kursi"], 300, 300);

        await using var device = await DepthAiDevice.OpenAsync(options);

        var pipeline = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera => camera.WithPreview(300, 300))
            .AddObjectDetection(model, "rgb.preview", "detector")
            .StreamOut("detector.detections", "detections")
            .Build(device.Capabilities);

        await device.StartAsync(pipeline);

        using var frame = await device.GetStream<DetectionFrame>("detections").FirstAsync(Cancellation());
        await device.StopAsync();

        return [.. frame.Detections.Select(d => d.Label)];
    }

    private static CancellationToken Cancellation() => new CancellationTokenSource(Timeout).Token;
}

/// <summary>
/// Pemindaian USB berjalan tanpa runtime native, jadi bisa diuji di mesin mana pun —
/// termasuk mesin CI yang tidak punya kamera.
/// </summary>
public class UsbDeviceScannerTests
{
    [Fact]
    public void Scan_DoesNotThrowOnAnyPlatform()
    {
        var devices = UsbDeviceScanner.Scan();
        Assert.NotNull(devices);
    }

    [Fact]
    public void Scan_OnlyReturnsMovidiusVendorDevices()
        => Assert.All(UsbDeviceScanner.Scan(),
            device => Assert.Equal(UsbDeviceScanner.MovidiusVendorId, device.VendorId));

    [Fact]
    public void DescribeEnvironment_MentionsSimulationWhenNativeIsMissing()
    {
        var summary = DepthAi.DescribeEnvironment();

        Assert.False(string.IsNullOrWhiteSpace(summary));

        if (!DepthAi.IsNativeAvailable)
        {
            Assert.Contains("simulasi", summary, StringComparison.OrdinalIgnoreCase);
        }
    }
}
