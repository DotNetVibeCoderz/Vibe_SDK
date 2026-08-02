using DepthAI;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Streaming;

// Membuka perangkat OAK pertama yang tersedia. Bila runtime native depthai tidak
// terpasang, SDK memakai perangkat simulasi supaya aplikasi ini tetap berjalan.
await using var device = await DepthAiDevice.OpenAsync();

Console.WriteLine($"Terhubung ke {device.Info.Name} ({device.Info.SerialNumber})");
if (device.IsSimulated)
{
    Console.WriteLine("Mode simulasi — tidak ada hardware terdeteksi.");
}

var pipeline = PipelinePresets.Create("PIPELINE_PRESET", model: null, fps: CAMERA_FPS);
pipeline.Validate(device.Capabilities).ThrowIfInvalid();

await device.StartAsync(pipeline);

var frames = 0;
var started = DateTimeOffset.UtcNow;

using var subscription = device.GetStream<ImageFrame>("video").Subscribe(frame =>
{
    if (Interlocked.Increment(ref frames) % CAMERA_FPS != 0)
    {
        return;
    }

    var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
    Console.WriteLine($"{frames,6} frame  {frame.Width}x{frame.Height}  {frames / elapsed:F1} fps");
});

Console.WriteLine("Berjalan. Tekan Enter untuk berhenti.");
Console.ReadLine();

await device.StopAsync();
