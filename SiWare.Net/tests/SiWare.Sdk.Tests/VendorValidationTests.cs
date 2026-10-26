using SiWare.Sdk;

namespace SiWare.Sdk.Tests;

public class VendorValidationTests
{
    private static VendorBridgeOptions InvalidPaths => new("missing-python", "missing-worker", "missing-driver", "seconds", 0, true);

    [Fact]
    public async Task Connect_PreCancelled_DoesNotValidateOrLaunchWorker()
    {
        await using var device = new VendorSpectrometer(InvalidPaths);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.ConnectAsync(cancellation.Token));
        Assert.False(device.IsConnected);
        Assert.False(device.Device.IsSimulated);
    }

    [Fact]
    public async Task Connect_InvalidPaths_FailsBeforeLaunchingWorker()
    {
        await using var device = new VendorSpectrometer(InvalidPaths);
        if (OperatingSystem.IsWindows() && Environment.Is64BitProcess)
            await Assert.ThrowsAsync<FileNotFoundException>(() => device.ConnectAsync());
        else
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => device.ConnectAsync());
        Assert.False(device.IsConnected);
    }

    [Theory]
    [InlineData("unknown", true, 60)]
    [InlineData("seconds", false, 60)]
    [InlineData("seconds", true, 29)]
    [InlineData("seconds", true, 601)]
    public async Task Connect_InvalidConfiguration_FailsBeforeProcessStart(string unit, bool confirmed, int timeout)
    {
        var path = Path.GetTempFileName();
        try
        {
            await using var device = new VendorSpectrometer(new(path, path, path, unit, 0, confirmed, TimeoutSeconds: timeout));
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                await Assert.ThrowsAsync<PlatformNotSupportedException>(() => device.ConnectAsync());
            else if (timeout is < 30 or > 600)
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.ConnectAsync());
            else
                await Assert.ThrowsAsync<ArgumentException>(() => device.ConnectAsync());
            Assert.False(device.IsConnected);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(MeasurementKind.Dark)]
    [InlineData(MeasurementKind.Absorbance)]
    [InlineData(MeasurementKind.Normalized)]
    public async Task Acquire_UnverifiedKind_FailsBeforeWorkerLaunch(MeasurementKind kind)
    {
        await using var device = new VendorSpectrometer(InvalidPaths);
        await Assert.ThrowsAsync<NotSupportedException>(() => device.AcquireAsync(kind, new()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(256)]
    [InlineData(4097)]
    public async Task Acquire_UnsupportedGrid_FailsBeforeWorkerLaunch(int points)
    {
        await using var device = new VendorSpectrometer(InvalidPaths);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.AcquireAsync(MeasurementKind.Sample, new(points)));
    }

    [Fact]
    public async Task Acquire_ValidOptionsRequireConnection_AndHonorPreCancellation()
    {
        await using var device = new VendorSpectrometer(InvalidPaths);
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.AcquireAsync(MeasurementKind.Sample, new()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.AcquireAsync(MeasurementKind.Sample, new(), cancellation.Token));
    }

    [Fact]
    public async Task Dispose_PreventsConnectionAndSupportsRepeatedCleanup()
    {
        var device = new VendorSpectrometer(InvalidPaths);
        await device.DisposeAsync(); await device.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.ConnectAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.AcquireAsync(MeasurementKind.Sample, new()));
        Assert.False(device.IsConnected);
    }
}
