using DepthAI;
using DepthAI.Imaging;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Streaming;

namespace DepthAI.Sample.VisionApi;

/// <summary>Menjaga perangkat tetap terbuka dan menyimpan hasil terbaru untuk endpoint API.</summary>
public sealed class VisionService : BackgroundService
{
    private ImageFrame? _latestFrame;
    private DepthFrame? _latestDepth;
    private long _frameCount;

    public IReadOnlyList<Detection> LatestDetections { get; private set; } = [];

    public string DeviceName { get; private set; } = "menghubungkan…";

    public bool IsSimulated { get; private set; }

    public bool IsRunning { get; private set; }

    public long FrameCount => Interlocked.Read(ref _frameCount);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var model = NeuralModel.CreatePlaceholder(
            ModelFamily.MobileNetSsd,
            labels: ["person", "bottle", "chair"],
            inputWidth: 300,
            inputHeight: 300);

        await using var device = await DepthAiDevice.OpenAsync(cancellationToken: stoppingToken);

        DeviceName = device.Info.Name;
        IsSimulated = device.IsSimulated;

        var pipeline = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera => camera.WithPreview(640, 400))
            .AddSpatialObjectDetection(model, "rgb.preview", "detector")
            .StreamOut("rgb.preview", "video")
            .StreamOut("detector.detections", "detections")
            .StreamOut("detector_stereo.depth", "depth")
            .Build(device.Capabilities);

        await device.StartAsync(pipeline, stoppingToken);
        IsRunning = true;

        using var detections = device.GetStream<DetectionFrame>("detections")
            .Subscribe(frame => LatestDetections = frame.Detections);

        using var video = device.GetStream<ImageFrame>("video").Subscribe(frame =>
        {
            Interlocked.Increment(ref _frameCount);
            var previous = Interlocked.Exchange(ref _latestFrame, frame.Clone());
            previous?.Dispose();
        });

        using var depth = device.GetStream<DepthFrame>("depth").Subscribe(frame =>
        {
            var previous = Interlocked.Exchange(ref _latestDepth, frame.Clone());
            previous?.Dispose();
        });

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Penghentian aplikasi yang normal.
        }

        IsRunning = false;
        await device.StopAsync(CancellationToken.None);
    }

    public float? ReadDepthMeters(int x, int y)
    {
        var depth = Volatile.Read(ref _latestDepth);
        if (depth is null || x < 0 || y < 0 || x >= depth.Width || y >= depth.Height)
        {
            return null;
        }

        return depth.GetDistanceMeters(x, y);
    }

    public async Task<byte[]?> GetLatestJpegAsync()
    {
        var frame = Volatile.Read(ref _latestFrame);
        return frame is null ? null : await frame.ToJpegAsync();
    }
}