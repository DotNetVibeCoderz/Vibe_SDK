using SiWare.Sdk;

namespace SiWare.Sdk.Tests;

public class DeviceTests
{
    [Fact]
    public async Task Acquire_Disconnected_Throws()
    {
        await using var device = new SimulatedSpectrometer();
        Assert.False(device.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.AcquireAsync(MeasurementKind.Sample, new()));
    }

    [Theory]
    [InlineData(MeasurementKind.Sample)]
    [InlineData(MeasurementKind.Dark)]
    [InlineData(MeasurementKind.Reference)]
    public async Task Acquire_Connected_ReturnsRequestedGridAndProvenance(MeasurementKind kind)
    {
        await using var device = new SimulatedSpectrometer();
        await device.ConnectAsync();
        var result = await device.AcquireAsync(kind, new(17, 1, 2));
        Assert.True(device.IsConnected);
        Assert.Equal(17, result.Count);
        Assert.Equal(1350, result.Wavelengths[0]);
        Assert.Equal(2550, result.Wavelengths[^1]);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(device.Device.Id, result.DeviceId);
        Assert.True(result.IsSimulated);
        await device.DisconnectAsync();
        Assert.False(device.IsConnected);
    }

    [Fact]
    public async Task Acquire_SameSeedAndOptions_ProducesSameSignal()
    {
        await using var a = new SimulatedSpectrometer(123);
        await using var b = new SimulatedSpectrometer(123);
        await a.ConnectAsync(); await b.ConnectAsync();
        var first = await a.AcquireAsync(MeasurementKind.Sample, new(17, 1, 3));
        var second = await b.AcquireAsync(MeasurementKind.Sample, new(17, 1, 3));
        Assert.Equal(first.Values, second.Values);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Acquire_CancelledDuringIntegration_ReleasesGateForNextScan()
    {
        await using var device = new SimulatedSpectrometer();
        await device.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var acquisition = device.AcquireAsync(MeasurementKind.Sample, new(17, 1000), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        var next = await device.AcquireAsync(MeasurementKind.Sample, new(17, 1)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(17, next.Count);
    }

    [Fact]
    public async Task Connect_PreCancelled_DoesNotConnect()
    {
        await using var device = new SimulatedSpectrometer();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.ConnectAsync(cancelled.Token));
        Assert.False(device.IsConnected);
    }

    [Fact]
    public async Task Dispose_IsIdempotentAndPreventsReconnectionAndAcquisition()
    {
        var device = new SimulatedSpectrometer();
        await device.ConnectAsync();
        await device.DisposeAsync(); await device.DisposeAsync();
        Assert.False(device.IsConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.ConnectAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.AcquireAsync(MeasurementKind.Sample, new()));
    }

    [Theory]
    [InlineData(1, 100, 1)]
    [InlineData(4097, 100, 1)]
    [InlineData(257, 0, 1)]
    [InlineData(257, 60001, 1)]
    [InlineData(257, 100, 0)]
    [InlineData(257, 100, 101)]
    public async Task Acquire_InvalidOptions_Throws(int points, int integration, int averages)
    {
        await using var device = new SimulatedSpectrometer();
        await device.ConnectAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.AcquireAsync(MeasurementKind.Sample, new(points, integration, averages)));
    }

    [Fact]
    public async Task Acquire_DerivedKindOrNullOptions_Throws()
    {
        await using var device = new SimulatedSpectrometer();
        await device.ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => device.AcquireAsync(MeasurementKind.Absorbance, new()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => device.AcquireAsync(MeasurementKind.Sample, null!));
    }
}
