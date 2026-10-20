using SiWare.Sdk;
using static SiWare.Sdk.Tests.SpectrumTests;

namespace SiWare.Sdk.Tests;

public class ProcessingTests
{
    [Fact]
    public void Absorbance_CorrectsDarkAndUsesBaseTenLogarithm()
    {
        var sample = Create([110, 20, 11]);
        var result = SpectralProcessing.Absorbance(sample,
            Create([10, 10, 10], MeasurementKind.Dark), Create([110, 110, 110], MeasurementKind.Reference));
        Assert.Equal(new double[] { 0, 1, 2 }, result.Values);
        Assert.Equal(MeasurementKind.Absorbance, result.Kind);
        Assert.NotEqual(sample.Id, result.Id);
        Assert.Equal(sample.DeviceId, result.DeviceId);
        Assert.Equal(sample.CapturedAt, result.CapturedAt);
        Assert.True(result.IsSimulated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Absorbance_MismatchedCalibration_Throws(int mismatch)
    {
        var reference = mismatch switch
        {
            0 => Create(kind: MeasurementKind.Reference, device: "another-device"),
            1 => Create(kind: MeasurementKind.Reference, simulated: false),
            2 => Create(kind: MeasurementKind.Reference, wavelengths: [1000, 1101, 1200]),
            _ => Create([1, 2], MeasurementKind.Reference, wavelengths: [1000, 1100])
        };
        Assert.Throws<ArgumentException>(() => SpectralProcessing.Absorbance(Create(), Create([0, 0, 0], MeasurementKind.Dark), reference));
    }

    [Theory]
    [InlineData(MeasurementKind.Reference, MeasurementKind.Dark, MeasurementKind.Reference)]
    [InlineData(MeasurementKind.Sample, MeasurementKind.Sample, MeasurementKind.Reference)]
    [InlineData(MeasurementKind.Sample, MeasurementKind.Dark, MeasurementKind.Dark)]
    public void Absorbance_WrongKinds_Throws(MeasurementKind sample, MeasurementKind dark, MeasurementKind reference) =>
        Assert.Throws<ArgumentException>(() => SpectralProcessing.Absorbance(Create(kind: sample), Create(kind: dark), Create(kind: reference)));

    [Theory]
    [InlineData(10, 10, 20)]
    [InlineData(9, 10, 20)]
    [InlineData(20, 10, 10)]
    [InlineData(20, 10, 9)]
    public void Absorbance_NonpositiveCorrectedIntensity_Throws(double sample, double dark, double reference) =>
        Assert.Throws<ArgumentException>(() => SpectralProcessing.Absorbance(Create([sample, sample, sample]),
            Create([dark, dark, dark], MeasurementKind.Dark), Create([reference, reference, reference], MeasurementKind.Reference)));

    [Fact]
    public void Absorbance_SampleAboveReference_AllowsNegativeAbsorbance()
    {
        var result = SpectralProcessing.Absorbance(Create([100, 100, 100]),
            Create([0, 0, 0], MeasurementKind.Dark), Create([10, 10, 10], MeasurementKind.Reference));
        Assert.All(result.Values, value => Assert.Equal(-1, value));
    }

    [Fact]
    public void Absorbance_UnderflowingRatio_RemainsFinite()
    {
        var result = SpectralProcessing.Absorbance(Create([double.Epsilon, 1, 1]),
            Create([0, 0, 0], MeasurementKind.Dark), Create([double.MaxValue, 2, 2], MeasurementKind.Reference));
        Assert.Equal(Math.Log10(double.MaxValue) - Math.Log10(double.Epsilon), result.Values[0], 12);
    }

    [Fact]
    public void Absorbance_OverflowingDarkCorrection_IsRejected() =>
        Assert.Throws<ArgumentException>(() => SpectralProcessing.Absorbance(Create([double.MaxValue, 1, 1]),
            Create([-double.MaxValue, 0, 0], MeasurementKind.Dark), Create([double.MaxValue, 2, 2], MeasurementKind.Reference)));

    [Fact]
    public void Snv_UsesSampleStandardDeviationAndPreservesMetadata()
    {
        var source = Create([1, 2, 3]);
        var result = SpectralProcessing.StandardNormalVariate(source);
        for (var i = 0; i < 3; i++) Assert.Equal(i - 1, result.Values[i], 12);
        Assert.Equal(0, result.Values.Average(), 12);
        Assert.Equal(1, result.Values.Sum(v => v * v) / (result.Count - 1), 12);
        Assert.Equal(source.CapturedAt, result.CapturedAt);
        Assert.Equal(MeasurementKind.Normalized, result.Kind);
    }

    [Fact]
    public void Snv_ConstantInputThrowsButExtremeFiniteInputIsNormalized()
    {
        Assert.Throws<ArgumentException>(() => SpectralProcessing.StandardNormalVariate(Create([2, 2, 2])));
        var result = SpectralProcessing.StandardNormalVariate(Create([-double.MaxValue, 0, double.MaxValue]));
        Assert.Equal(new double[] { -1, 0, 1 }, result.Values);
    }

    [Fact]
    public void Smooth_EdgesUseOnlyAvailableNeighbors()
    {
        var source = Create([1, 2, 9, 4, 5], wavelengths: [1000, 1100, 1200, 1300, 1400]);
        var result = SpectralProcessing.Smooth(source, 3);
        var expected = new double[] { 1.5, 4, 5, 6, 4.5 };
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], result.Values[i], 12);
        Assert.Equal(source.Kind, result.Kind);
        Assert.Equal(source.Values, SpectralProcessing.Smooth(source, 1).Values);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Smooth_InvalidWindow_Throws(int window) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SpectralProcessing.Smooth(Create(), window));
}
