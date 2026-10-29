using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Pipelines.Nodes;
using DepthAI.Streaming;

namespace DepthAI.Tests;

/// <summary>
/// Gambar dari host ke neural network lewat <see cref="HostInputNode"/>. Di simulasi isi gambar
/// tidak dilihat, jadi yang diuji adalah iramanya: satu gambar, satu hasil, nomor urut yang sama.
/// </summary>
public class HostInputTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static Pipeline DetectorFedByHost(out NeuralModel model)
    {
        model = NeuralModel.CreatePlaceholder(ModelFamily.Yolo, ["orang", "helm"], 320, 320);
        var pipeline = Pipeline.Create();
        var image = pipeline.AddHostInput("image");
        var nn = pipeline.AddDetectionNetwork("detector");
        nn.Model = model;
        image.Out.LinkTo(nn.Input);
        pipeline.AddOutputStream(nn.Detections, "detections");
        return pipeline;
    }

    private static ImageFrame Gray(int width, int height)
        => ImageFrame.CopyFrom(new byte[width * height * 3], width, height, PixelFormat.Bgr888);

    [Fact]
    public async Task InferAsync_ReturnsOneResultCarryingTheSentSequence()
    {
        await using var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);
        await device.StartAsync(DetectorFedByHost(out _));
        using var frame = Gray(320, 320);

        using var first = await device.InferAsync<DetectionFrame>("image", frame, "detections", Timeout);
        using var second = await device.InferAsync<DetectionFrame>("image", frame, "detections", Timeout);

        Assert.Equal(first.SequenceNumber + 1, second.SequenceNumber);
        Assert.NotEmpty(first.Detections);
    }

    [Fact]
    public async Task InferAsync_ResultOutlivesTheStreamCallback()
    {
        await using var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);
        await device.StartAsync(DetectorFedByHost(out _));
        using var frame = Gray(320, 320);

        var result = await device.InferAsync<DetectionFrame>("image", frame, "detections", Timeout);
        await Task.Delay(100);

        // Frame stream dibuang setelah callback; frame milik pemanggil tidak.
        Assert.False(result.IsDisposed);
        result.Dispose();
    }

    [Fact]
    public async Task HostFedStream_IsSilentUntilAnImageIsSent()
    {
        await using var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);
        await device.StartAsync(DetectorFedByHost(out _));

        var received = 0;
        using var subscription = device.GetStream<DetectionFrame>("detections")
            .Subscribe(new CountingObserver(() => Interlocked.Increment(ref received)));

        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref received));

        using var frame = Gray(320, 320);
        device.SendFrame("image", frame);
        await WaitUntil(() => Volatile.Read(ref received) == 1);
    }

    [Fact]
    public async Task SendFrame_RejectsUnknownInput()
    {
        await using var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);
        await device.StartAsync(DetectorFedByHost(out _));
        using var frame = Gray(32, 32);

        Assert.Throws<KeyNotFoundException>(() => device.SendFrame("camera", frame));
    }

    [Fact]
    public async Task SendFrame_RequiresARunningPipeline()
    {
        await using var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);
        using var frame = Gray(32, 32);

        Assert.Throws<InvalidOperationException>(() => device.SendFrame("image", frame));
    }

    [Fact]
    public void HostInput_RoundTripsThroughPipelineJson()
    {
        var pipeline = DetectorFedByHost(out var model);

        var loaded = Pipeline.FromJson(pipeline.ToJson(), new PipelineLoadOptions
        {
            ModelResolver = _ => model,
        });

        Assert.IsType<HostInputNode>(loaded.Nodes.Single(n => n.Name == "image"));
        Assert.Contains(loaded.Links, l => l.From == "image.out" && l.To == "detector.input");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Kondisi tidak terpenuhi sebelum batas waktu.");
            await Task.Delay(20);
        }
    }

    private sealed class CountingObserver(Action onNext) : IObserver<DetectionFrame>
    {
        public void OnNext(DetectionFrame value) => onNext();

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }
}

public class StreamLifetimeTests
{
    [Fact]
    public async Task CancellingTheStartToken_DoesNotStopTheStream()
    {
        await using var device = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);
        var pipeline = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera => camera.WithPreview(160, 120))
            .StreamOut("rgb.preview", "video")
            .Build(device.Capabilities);

        using (var start = new CancellationTokenSource())
        {
            await device.StartAsync(pipeline, start.Token);
            await start.CancelAsync();
        }

        // Galeri pernah tersandung di sini: pembatalan operasi yang kebetulan memulai
        // pipeline ikut mematikan stream-nya.
        using var frame = await device.GetStream<ImageFrame>("video").FirstAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.True(device.IsRunning);
    }
}
