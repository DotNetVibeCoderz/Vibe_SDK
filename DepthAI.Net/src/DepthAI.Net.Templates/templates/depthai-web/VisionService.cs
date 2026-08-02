using DepthAI;
using DepthAI.Imaging;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Streaming;

namespace DepthAiWebApp;

/// <summary>
/// Menjaga satu perangkat tetap terbuka untuk seluruh aplikasi dan menyiarkan
/// frame terbaru ke komponen yang berlangganan.
/// </summary>
/// <remarks>
/// Perangkat hanya bisa dibuka satu proses, jadi ini singleton — bukan
/// scoped per koneksi. Semua sirkuit Blazor melihat frame yang sama.
/// </remarks>
public sealed class VisionService : BackgroundService
{
    private ImageFrame? _latestFrame;

    public IReadOnlyList<Detection> LatestDetections { get; private set; } = [];

    public string DeviceName { get; private set; } = "menghubungkan…";

    public bool IsSimulated { get; private set; }

    /// <summary>Dipicu tiap kali ada frame baru; komponen memakai ini untuk merender ulang.</summary>
    public event Action? FrameReady;

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
            .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
            .AddObjectDetection(model, "rgb.preview", "detector")
            .StreamOut("rgb.preview", "video")
            .StreamOut("detector.detections", "detections")
            .Build(device.Capabilities);

        await device.StartAsync(pipeline, stoppingToken);

        using var detectionSubscription = device.GetStream<DetectionFrame>("detections")
            .Subscribe(frame => LatestDetections = frame.Detections);

        // Dibatasi ~12 fps: melebihi itu, encoding JPEG dan lalu lintas
        // SignalR menjadi hambatan, bukan kameranya.
        using var videoSubscription = device.GetStream<ImageFrame>("video")
            .Throttle(TimeSpan.FromMilliseconds(80))
            .Subscribe(frame =>
            {
                var previous = Interlocked.Exchange(ref _latestFrame, frame.Clone());
                previous?.Dispose();
                FrameReady?.Invoke();
            });

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Penghentian aplikasi yang normal.
        }

        await device.StopAsync(CancellationToken.None);
    }

    /// <summary>Frame terbaru sebagai data URI yang siap dipasang ke tag img.</summary>
    public async Task<string?> GetFrameDataUriAsync()
    {
        var frame = Volatile.Read(ref _latestFrame);
        if (frame is null)
        {
            return null;
        }

        var jpeg = await frame.ToJpegAsync(quality: 70);
        return "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
    }
}