using DepthAI;
using DepthAI.Imaging;
using DepthAI.Pipelines;
using DepthAI.Streaming;

var outputDirectory = args.Length > 0 ? args[0] : "./capture";
var frameLimit = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 100;

Directory.CreateDirectory(outputDirectory);
Console.WriteLine($"Merekam {frameLimit} pasang frame ke {Path.GetFullPath(outputDirectory)}");

await using var device = await DepthAiDevice.OpenAsync();
var pipeline = PipelinePresets.StereoDepth(fps: 30);
pipeline.Validate(device.Capabilities).ThrowIfInvalid();

await device.StartAsync(pipeline);

var pending = new List<Task>();
var colorCount = 0;
var depthCount = 0;

using var colorSubscription = device.GetStream<ImageFrame>("video").Subscribe(frame =>
{
    if (colorCount >= frameLimit)
    {
        return;
    }

    var index = Interlocked.Increment(ref colorCount);
    var copy = frame.Clone();
    lock (pending)
    {
        pending.Add(SaveColorAsync(copy, outputDirectory, index));
    }
});

using var depthSubscription = device.GetStream<DepthFrame>("depth").Subscribe(frame =>
{
    if (depthCount >= frameLimit)
    {
        return;
    }

    var index = Interlocked.Increment(ref depthCount);
    var copy = frame.Clone();
    lock (pending)
    {
        pending.Add(SaveDepthAsync(copy, outputDirectory, index));
    }
});

while (colorCount < frameLimit || depthCount < frameLimit)
{
    await Task.Delay(100);
    Console.Write($"\rwarna {colorCount}/{frameLimit}  kedalaman {depthCount}/{frameLimit}");
}

Task[] snapshot;
lock (pending)
{
    snapshot = [.. pending];
}

await Task.WhenAll(snapshot);
await device.StopAsync();

Console.WriteLine();
Console.WriteLine("Selesai.");

static async Task SaveColorAsync(ImageFrame frame, string directory, int index)
{
    try
    {
        await frame.SaveAsync(Path.Combine(directory, $"color_{index:D5}.png"));
    }
    finally
    {
        frame.Dispose();
    }
}

static async Task SaveDepthAsync(DepthFrame frame, string directory, int index)
{
    try
    {
        // PNG 16-bit menyimpan milimeter apa adanya, sehingga rekaman
        // tetap bisa dipakai untuk pengukuran, bukan sekadar dilihat.
        await frame.SaveRawDepthAsync(Path.Combine(directory, $"depth_{index:D5}.png"));
    }
    finally
    {
        frame.Dispose();
    }
}