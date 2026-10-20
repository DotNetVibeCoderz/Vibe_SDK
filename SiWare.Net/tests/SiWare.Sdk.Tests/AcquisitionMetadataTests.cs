using SiWare.Sdk;

namespace SiWare.Sdk.Tests;

public class AcquisitionMetadataTests
{
    private static Spectrum Create(MeasurementKind kind, ScanOptions? options) =>
        new(Guid.NewGuid(), "sensor", DateTimeOffset.UtcNow, kind, true,
            new double[] { 1000, 1100, 1200 }, kind == MeasurementKind.Dark ? new double[] { 0, 0, 0 } : new double[] { 1, 2, 3 }, options);

    [Fact]
    public void JsonAndProcessing_PreserveAcquisitionSettings()
    {
        var options = new ScanOptions(3, 200, 2);
        var input = Create(MeasurementKind.Sample, options);
        Assert.Equal(options, SpectrumFile.FromJson(SpectrumFile.ToJson(input)).Acquisition);
        Assert.Equal(options, SpectralProcessing.Smooth(input, 3).Acquisition);
        Assert.Equal(options, SpectralProcessing.StandardNormalVariate(input).Acquisition);
    }

    [Fact]
    public void Calibration_RejectsDifferentOrMissingAcquisitionSettings()
    {
        var sample = Create(MeasurementKind.Sample, new(3, 200));
        var reference = Create(MeasurementKind.Reference, new(3, 200));
        Assert.Throws<ArgumentException>(() => SpectralProcessing.Absorbance(sample, Create(MeasurementKind.Dark, new(3, 100)), reference));
        Assert.Throws<ArgumentException>(() => SpectralProcessing.Absorbance(sample, Create(MeasurementKind.Dark, null), reference));
    }

    [Fact]
    public void Spectrum_RejectsAcquisitionPointCountMismatch() =>
        Assert.Throws<ArgumentException>(() => Create(MeasurementKind.Sample, new(65)));

    [Fact]
    public async Task Simulator_RecordsRequestedAcquisitionSettings()
    {
        await using var device = new SimulatedSpectrometer();
        await device.ConnectAsync();
        var options = new ScanOptions(65, 1, 2);
        Assert.Equal(options, (await device.AcquireAsync(MeasurementKind.Sample, options)).Acquisition);
    }
}
