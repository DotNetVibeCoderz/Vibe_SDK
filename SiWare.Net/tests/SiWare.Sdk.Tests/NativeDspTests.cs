using SiWare.Sdk;
using static SiWare.Sdk.Tests.SpectrumTests;

namespace SiWare.Sdk.Tests;

public class NativeDspTests
{
    [Fact]
    public void RustAbiV1_IsAvailableInTestOutput() =>
        Assert.True(NativeDsp.IsAvailable, "Build native/siware-dsp in release mode before running the native integration suite.");

    public static IEnumerable<object[]> Signals => new[]
    {
        new double[] { 1, 2, 3 }, new double[] { -5, 1, 7, -2, 9 },
        new double[] { 1e10 + 1, 1e10 + 3, 1e10 + 9 },
        new double[] { -double.MaxValue, 0, double.MaxValue },
        new double[] { double.Epsilon, double.Epsilon * 2, double.Epsilon * 3 },
        Enumerable.Range(0, 257).Select(i => 100 + Math.Sin(i / 10.0) * 50).ToArray()
    }.Select(x => new object[] { x });

    [Theory, MemberData(nameof(Signals))]
    public void NativeSnvAndSmoothing_MatchManagedOnValidSignals(double[] values)
    {
        var spectrum = Create(values, wavelengths: Enumerable.Range(0, values.Length).Select(i => 1000.0 + i).ToArray());
        Equivalent(SpectralProcessing.StandardNormalVariate(spectrum), NativeDsp.StandardNormalVariate(spectrum));
        foreach (var window in new[] { 1, 3 })
            Equivalent(SpectralProcessing.Smooth(spectrum, window), NativeDsp.Smooth(spectrum, window));
    }

    [Fact]
    public void NativeAbsorbance_MatchesManagedAndRejectsInvalidCalibration()
    {
        var sample = Create([110, 20, 11]);
        var dark = Create([10, 10, 10], MeasurementKind.Dark);
        var reference = Create([110, 110, 110], MeasurementKind.Reference);
        Equivalent(SpectralProcessing.Absorbance(sample, dark, reference), NativeDsp.Absorbance(sample, dark, reference));
        Assert.Throws<ArgumentException>(() => NativeDsp.Absorbance(Create([10, 10, 10]), dark, reference));
        Assert.Throws<ArgumentException>(() => NativeDsp.Absorbance(sample, dark, Create(kind: MeasurementKind.Reference, device: "wrong")));
        Assert.Throws<ArgumentException>(() => NativeDsp.Absorbance(reference, dark, sample));
    }

    [Fact]
    public void NativeSnvAndSmooth_InvalidInputs_Throw()
    {
        Assert.Throws<ArgumentException>(() => NativeDsp.StandardNormalVariate(Create([1, 1, 1])));
        Assert.Throws<ArgumentException>(() => NativeDsp.Smooth(Create(), 2));
        Assert.Throws<ArgumentException>(() => NativeDsp.Smooth(Create(), 5));
    }

    [Fact]
    public void ExtremeAbsorbanceAndSmoothing_MatchManaged()
    {
        var sample = Create([double.Epsilon, double.MaxValue, 1]);
        var dark = Create([0, 0, 0], MeasurementKind.Dark);
        var reference = Create([double.MaxValue, double.Epsilon, 1], MeasurementKind.Reference);
        Equivalent(SpectralProcessing.Absorbance(sample, dark, reference), NativeDsp.Absorbance(sample, dark, reference));
        var extreme = Create([double.MaxValue, double.MaxValue, double.MaxValue]);
        Equivalent(SpectralProcessing.Smooth(extreme, 3), NativeDsp.Smooth(extreme, 3));
        Assert.All(SpectralProcessing.Smooth(extreme, 3).Values, value => Assert.Equal(double.MaxValue, value));
    }

    private static void Equivalent(Spectrum expected, Spectrum actual, double tolerance = 1e-10)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Wavelengths, actual.Wavelengths);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.DeviceId, actual.DeviceId);
        Assert.Equal(expected.IsSimulated, actual.IsSimulated);
        Assert.Equal(expected.CapturedAt, actual.CapturedAt);
        for (var i = 0; i < expected.Count; i++)
            Assert.InRange(Math.Abs(expected.Values[i] - actual.Values[i]), 0, tolerance * Math.Max(1, Math.Abs(expected.Values[i])));
    }
}
