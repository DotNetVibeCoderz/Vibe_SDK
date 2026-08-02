using DepthAI.Imaging;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Streaming;

namespace DepthAI.Tests;

public class FrameLifetimeTests
{
    [Fact]
    public void DisposedFrame_ThrowsInsteadOfReadingRecycledMemory()
    {
        var frame = ImageFrame.CopyFrom(new byte[300], 10, 10, PixelFormat.Bgr888);
        frame.Dispose();

        // Buffer sudah kembali ke pool; membacanya diam-diam akan memberi piksel milik
        // frame lain, jadi harus jadi error yang terlihat.
        Assert.Throws<ObjectDisposedException>(() => _ = frame.Pixels.Length);
    }

    [Fact]
    public void Clone_SurvivesDisposalOfOriginal()
    {
        var source = new byte[300];
        source[42] = 200;

        var frame = ImageFrame.CopyFrom(source, 10, 10, PixelFormat.Bgr888);
        var copy = frame.Clone();
        frame.Dispose();

        Assert.Equal(200, copy.Pixels[42]);
        copy.Dispose();
    }

    [Fact]
    public void DepthFrame_ReturnsNullForPixelsWithoutMeasurement()
    {
        var buffer = new ushort[100];
        buffer[0] = 0;
        buffer[1] = 1500;

        using var frame = DepthFrame.Wrap(buffer, 10, 10);

        // Nol berarti "tidak ada pengukuran", bukan jarak nol.
        Assert.Null(frame.GetDistanceMeters(0, 0));
        Assert.Equal(1.5f, frame.GetDistanceMeters(1, 0)!.Value, 3);
    }

    [Fact]
    public void DepthFrame_TreatsOutOfRangeMeasurementsAsMissing()
    {
        var buffer = new ushort[100];
        buffer[0] = 50_000;

        using var frame = DepthFrame.Wrap(buffer, 10, 10);

        Assert.Null(frame.GetDistanceMeters(0, 0));
    }

    [Fact]
    public void ToMeterMatrix_UsesNaNForMissingPixels()
    {
        var buffer = new ushort[4];
        buffer[0] = 2000;

        using var frame = DepthFrame.Wrap(buffer, 2, 2);
        var matrix = frame.ToMeterMatrix();

        Assert.Equal(2f, matrix[0, 0], 3);
        Assert.True(float.IsNaN(matrix[0, 1]));
    }
}

public class FrameStreamTests
{
    [Fact]
    public void Subscribe_DeliversToAllObservers()
    {
        var stream = new TestStream();
        var first = 0;
        var second = 0;

        using var a = stream.Subscribe(_ => first++);
        using var b = stream.Subscribe(_ => second++);

        stream.Emit(1);
        stream.Emit(2);

        Assert.Equal(2, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public void Dispose_StopsDelivery()
    {
        var stream = new TestStream();
        var count = 0;

        var subscription = stream.Subscribe(_ => count++);
        stream.Emit(1);
        subscription.Dispose();
        stream.Emit(2);

        Assert.Equal(1, count);
    }

    [Fact]
    public void ThrowingObserver_DoesNotBreakOthers()
    {
        var stream = new TestStream();
        var healthy = 0;

        using var bad = stream.Subscribe(_ => throw new InvalidOperationException("rewel"));
        using var good = stream.Subscribe(_ => healthy++);

        stream.Emit(1);

        Assert.Equal(1, healthy);
    }

    [Fact]
    public void Where_FiltersItems()
    {
        var stream = new TestStream();
        var seen = new List<int>();

        using var subscription = stream.Where(v => v % 2 == 0).Subscribe(seen.Add);

        for (var i = 1; i <= 5; i++)
        {
            stream.Emit(i);
        }

        Assert.Equal([2, 4], seen);
    }

    /// <summary>Sumber sederhana untuk menguji semantik langganan tanpa perangkat.</summary>
    private sealed class TestStream : IObservable<int>
    {
        private readonly List<IObserver<int>> _observers = [];

        public IDisposable Subscribe(IObserver<int> observer)
        {
            _observers.Add(observer);
            return new Subscription(() => _observers.Remove(observer));
        }

        public void Emit(int value)
        {
            foreach (var observer in _observers.ToArray())
            {
                try
                {
                    observer.OnNext(value);
                }
                catch (InvalidOperationException)
                {
                    // Meniru isolasi observer yang dilakukan FrameStream.
                }
            }
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}

public class ImagingTests
{
    [Fact]
    public void PixelConverter_SwapsChannelsForRgbSource()
    {
        var pixels = new byte[] { 10, 20, 30 };
        using var frame = ImageFrame.Wrap(pixels, 1, 1, PixelFormat.Rgb888);

        var bgr = PixelConverter.ToBgr888(frame);

        Assert.Equal([30, 20, 10], bgr);
    }

    [Fact]
    public void PixelConverter_ExpandsGrayscaleToThreeChannels()
    {
        using var frame = ImageFrame.Wrap([128], 1, 1, PixelFormat.Gray8);

        var bgr = PixelConverter.ToBgr888(frame);

        Assert.Equal([128, 128, 128], bgr);
    }

    [Fact]
    public void PixelConverter_RejectsCompressedFramesWithGuidance()
    {
        using var frame = ImageFrame.Wrap(new byte[10], 1, 1, PixelFormat.Jpeg);

        var exception = Assert.Throws<NotSupportedException>(() => PixelConverter.ToBgr888(frame));
        Assert.Contains("terkompresi", exception.Message);
    }

    [Fact]
    public void DepthColorizer_MarksMissingPixelsDistinctly()
    {
        var buffer = new ushort[2];
        buffer[0] = 0;
        buffer[1] = 2000;

        using var frame = DepthFrame.Wrap(buffer, 2, 1);
        var bgr = DepthColorizer.ToBgr(frame, DepthColorMap.Turbo);

        // Piksel tanpa pengukuran diwarnai hitam, bukan warna "sangat dekat".
        Assert.Equal(0, bgr[0]);
        Assert.Equal(0, bgr[1]);
        Assert.Equal(0, bgr[2]);
        Assert.True(bgr[3] + bgr[4] + bgr[5] > 0);
    }

    [Fact]
    public void FrameOverlay_ColorsAreStablePerLabel()
        => Assert.Equal(FrameOverlay.ColorFor(3), FrameOverlay.ColorFor(3));

    [Fact]
    public void FrameOverlay_DrawsInsideBufferBounds()
    {
        var pixels = new byte[10 * 10 * 3];

        var detections = new[]
        {
            new Detection { LabelIndex = 0, Confidence = 0.9f, Box = new BoundingBox(0.8f, 0.8f, 1.5f, 1.5f) },
        };

        // Kotak yang melewati tepi frame tidak boleh menulis di luar buffer.
        FrameOverlay.DrawDetections(pixels, 10, 10, detections);

        Assert.Contains(pixels, b => b != 0);
    }
}
