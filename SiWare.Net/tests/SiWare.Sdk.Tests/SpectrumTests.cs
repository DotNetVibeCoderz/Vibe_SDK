using System.Globalization;
using System.Text.Json;
using SiWare.Sdk;

namespace SiWare.Sdk.Tests;

public class SpectrumTests
{
    internal static Spectrum Create(double[]? values = null, MeasurementKind kind = MeasurementKind.Sample,
        string device = "device-1", bool simulated = true, double[]? wavelengths = null) =>
        new(Guid.NewGuid(), device, DateTimeOffset.Parse("2026-10-20T10:00:00+07:00", CultureInfo.InvariantCulture),
            kind, simulated, wavelengths ?? [1000, 1100, 1200], values ?? [1, 2, 3]);

    [Fact]
    public void Constructor_CopiesInputAndExposesReadOnlyCollections()
    {
        double[] axis = [1000, 1100, 1200], values = [1, 2, 3];
        var spectrum = Create(values, wavelengths: axis);
        axis[0] = 0; values[0] = 999;
        Assert.Equal(1000, spectrum.Wavelengths[0]);
        Assert.Equal(1, spectrum.Values[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<double>)spectrum.Values)[0] = 0);
        Assert.Throws<NotSupportedException>(() => ((IList<double>)spectrum.Wavelengths)[0] = 0);
    }

    public static IEnumerable<object[]> InvalidAxes => new[]
    {
        new double[] { 1000 }, new double[] { 1000, 1000, 1200 },
        new double[] { 1200, 1100, 1000 }, new double[] { 0, 1100, 1200 },
        new double[] { -1, 1100, 1200 }, new double[] { double.NaN, 1100, 1200 },
        new double[] { 1000, 1100, double.PositiveInfinity }
    }.Select(x => new object[] { x });

    [Theory, MemberData(nameof(InvalidAxes))]
    public void Constructor_InvalidAxis_Throws(double[] axis) =>
        Assert.Throws<ArgumentException>(() => Create(wavelengths: axis));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_NonfiniteIntensity_Throws(double value) =>
        Assert.Throws<ArgumentException>(() => Create([1, value, 3]));

    [Fact]
    public void Constructor_InvalidIdentityKindOrCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => Create(device: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(kind: (MeasurementKind)999));
        Assert.Throws<ArgumentException>(() => Create([1, 2]));
    }

    [Fact]
    public void WithValues_PreservesProvenanceButCreatesNewIdentity()
    {
        var original = Create();
        var changed = original.WithValues([4, 5, 6], MeasurementKind.Normalized);
        Assert.NotEqual(original.Id, changed.Id);
        Assert.Equal(original.DeviceId, changed.DeviceId);
        Assert.Equal(original.CapturedAt, changed.CapturedAt);
        Assert.Equal(original.IsSimulated, changed.IsSimulated);
        Assert.Equal(original.Wavelengths, changed.Wavelengths);
        Assert.Equal(MeasurementKind.Normalized, changed.Kind);
        Assert.Equal(new double[] { 1, 2, 3 }, original.Values);
    }

    [Fact]
    public void Json_Roundtrip_PreservesAllMetadataAndValues()
    {
        var source = Create([1.2345678901234567, -2.5, 3e100], MeasurementKind.Reference, simulated: false);
        var result = SpectrumFile.FromJson(SpectrumFile.ToJson(source));
        Assert.Equal(source.Id, result.Id);
        Assert.Equal(source.DeviceId, result.DeviceId);
        Assert.Equal(source.CapturedAt, result.CapturedAt);
        Assert.Equal(source.Kind, result.Kind);
        Assert.Equal(source.IsSimulated, result.IsSimulated);
        Assert.Equal(source.Wavelengths, result.Wavelengths);
        Assert.Equal(source.Values, result.Values);
    }

    [Fact]
    public void Json_MalformedOrInvalidSpectrum_IsRejected()
    {
        Assert.Throws<JsonException>(() => SpectrumFile.FromJson("{"));
        Assert.Throws<FormatException>(() => SpectrumFile.FromJson("null"));
        var json = SpectrumFile.ToJson(Create()).Replace("Sample", "UnknownKind", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => SpectrumFile.FromJson(json));
        var invalid = SpectrumFile.ToJson(Create()).Replace("1200", "1000", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SpectrumFile.FromJson(invalid));
    }

    [Theory]
    [InlineData("id-ID")]
    [InlineData("fr-FR")]
    public void Csv_Roundtrip_IsCultureIndependentAndUsesExplicitProvenance(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var source = Create([1.25, -2.5, 3e-100]);
            var csv = SpectrumFile.ToCsv(source);
            Assert.Contains("1000,1.25\n", csv);
            var result = SpectrumFile.FromCsv(csv, "imported", false, MeasurementKind.Dark);
            Assert.Equal(source.Values, result.Values);
            Assert.Equal(source.Wavelengths, result.Wavelengths);
            Assert.Equal("imported", result.DeviceId);
            Assert.False(result.IsSimulated);
            Assert.Equal(MeasurementKind.Dark, result.Kind);
            Assert.NotEqual(source.Id, result.Id);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("wrong,value\n1000,1\n1100,2")]
    [InlineData("wavelength_nm,value\n1000,1,2\n1100,2")]
    [InlineData("wavelength_nm,value\n1000,not-a-number\n1100,2")]
    public void Csv_MalformedRows_ThrowFormatException(string csv) =>
        Assert.Throws<FormatException>(() => SpectrumFile.FromCsv(csv, "import", true));

    [Theory]
    [InlineData("wavelength_nm,value\n1000,NaN\n1100,2")]
    [InlineData("wavelength_nm,value\n1100,1\n1000,2")]
    [InlineData("wavelength_nm,value\n1000,1")]
    public void Csv_InvalidSpectrum_Throws(string csv) =>
        Assert.Throws<ArgumentException>(() => SpectrumFile.FromCsv(csv, "import", true));

    [Fact]
    public void Csv_BomCrLfAndBlankLines_AreSupported()
    {
        var spectrum = SpectrumFile.FromCsv("\uFEFFwavelength_nm,value\r\n1000,1\r\n\r\n1100,2\r\n", "import", true);
        Assert.Equal(2, spectrum.Count);
    }
}
