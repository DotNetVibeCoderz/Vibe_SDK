using System.Diagnostics;
using SiWare.Sdk;

namespace SiWare.Sdk.Tests;

public class ContinuousAcquisitionTests
{
    private static readonly ScanOptions Fast = new(Points: 16, IntegrationMilliseconds: 1);

    private static async Task<SimulatedSpectrometer> Connected()
    {
        var device = new SimulatedSpectrometer();
        await device.ConnectAsync();
        return device;
    }

    [Fact]
    public async Task AcquireSeries_YieldsRequestedNumberOfDistinctSpectra()
    {
        await using var device = await Connected();
        var series = new List<Spectrum>();
        await foreach (var spectrum in device.AcquireSeriesAsync(MeasurementKind.Sample, Fast, new(4, TimeSpan.Zero)))
            series.Add(spectrum);

        Assert.Equal(4, series.Count);
        Assert.Equal(4, series.Select(s => s.Id).Distinct().Count());
        Assert.All(series, s => Assert.Equal(MeasurementKind.Sample, s.Kind));
        Assert.All(series, s => Assert.Equal(Fast, s.Acquisition));
        Assert.True(series.Zip(series.Skip(1)).All(p => p.First.CapturedAt <= p.Second.CapturedAt));
    }

    [Fact]
    public async Task AcquireSeries_WaitsAtLeastTheIntervalBetweenStarts()
    {
        await using var device = await Connected();
        var interval = TimeSpan.FromMilliseconds(60);
        var watch = Stopwatch.StartNew();
        var count = 0;
        await foreach (var _ in device.AcquireSeriesAsync(MeasurementKind.Sample, Fast, new(3, interval)))
            count++;
        Assert.Equal(3, count);
        Assert.True(watch.Elapsed >= interval * 2 - TimeSpan.FromMilliseconds(15), $"Elapsed {watch.Elapsed}");
    }

    [Fact]
    public async Task AcquireSeries_CancellationStopsAndKeepsEarlierSpectra()
    {
        await using var device = await Connected();
        using var cancellation = new CancellationTokenSource();
        var series = new List<Spectrum>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var spectrum in device.AcquireSeriesAsync(MeasurementKind.Sample, Fast,
                new(100, TimeSpan.FromSeconds(30)), cancellationToken: cancellation.Token))
            {
                series.Add(spectrum);
                cancellation.Cancel();
            }
        });
        Assert.Single(series);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(ContinuousOptions.MaxMeasurements + 1, 0)]
    [InlineData(2, -1)]
    [InlineData(2, 3601)]
    public async Task AcquireSeries_RejectsInvalidOptionsBeforeAcquiring(int measurements, int intervalSeconds)
    {
        await using var device = await Connected();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in device.AcquireSeriesAsync(MeasurementKind.Sample, Fast,
                new(measurements, TimeSpan.FromSeconds(intervalSeconds)))) { }
        });
    }

    [Fact]
    public async Task AcquireSeries_RequiresConnectedDevice()
    {
        await using var device = new SimulatedSpectrometer();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in device.AcquireSeriesAsync(MeasurementKind.Sample, Fast, new(2, TimeSpan.Zero))) { }
        });
    }
}
