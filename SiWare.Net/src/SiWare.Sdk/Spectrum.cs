using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiWare.Sdk;

public enum MeasurementKind { Sample, Dark, Reference, Absorbance, Normalized }

/// <summary>A validated, immutable snapshot. Wavelength is nanometres in ascending order.</summary>
public sealed class Spectrum
{
    public Guid Id { get; }
    public string DeviceId { get; }
    public DateTimeOffset CapturedAt { get; }
    public MeasurementKind Kind { get; }
    public bool IsSimulated { get; }
    public IReadOnlyList<double> Wavelengths { get; }
    public IReadOnlyList<double> Values { get; }
    public int Count => Values.Count;

    [JsonConstructor]
    public Spectrum(Guid id, string deviceId, DateTimeOffset capturedAt, MeasurementKind kind,
        bool isSimulated, IReadOnlyList<double> wavelengths, IReadOnlyList<double> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(wavelengths);
        ArgumentNullException.ThrowIfNull(values);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (wavelengths.Count < 2 || wavelengths.Count > 1_000_000 || wavelengths.Count != values.Count)
            throw new ArgumentException("Spectrum requires 2–1,000,000 matching wavelength/value pairs.");
        var x = wavelengths.ToArray();
        var y = values.ToArray();
        for (var i = 0; i < x.Length; i++)
            if (!double.IsFinite(x[i]) || x[i] <= 0 || !double.IsFinite(y[i]) || (i > 0 && x[i] <= x[i - 1]))
                throw new ArgumentException("Wavelengths must be positive, finite, strictly ascending; values must be finite.");
        Id = id;
        DeviceId = deviceId;
        CapturedAt = capturedAt;
        Kind = kind;
        IsSimulated = isSimulated;
        Wavelengths = Array.AsReadOnly(x);
        Values = Array.AsReadOnly(y);
    }

    public Spectrum WithValues(double[] values, MeasurementKind kind) =>
        new(Guid.NewGuid(), DeviceId, CapturedAt, kind, IsSimulated, Wavelengths, values);
}

public static class SpectrumFile
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ToJson(Spectrum spectrum) => JsonSerializer.Serialize(spectrum, JsonOptions);
    public static Spectrum FromJson(string json) => JsonSerializer.Deserialize<Spectrum>(json, JsonOptions)
        ?? throw new FormatException("Spectrum JSON is null.");

    public static string ToCsv(Spectrum spectrum)
    {
        var output = new StringBuilder("wavelength_nm,value\n");
        for (var i = 0; i < spectrum.Count; i++)
            output.Append(spectrum.Wavelengths[i].ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(spectrum.Values[i].ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        return output.ToString();
    }

    /// <summary>CSV has no provenance; caller must explicitly supply the source and simulation status.</summary>
    public static Spectrum FromCsv(string csv, string deviceId, bool isSimulated, MeasurementKind kind = MeasurementKind.Sample)
    {
        using var reader = new StringReader(csv);
        if (reader.ReadLine()?.TrimStart('\uFEFF').Trim() != "wavelength_nm,value")
            throw new FormatException("Expected CSV header wavelength_nm,value.");
        List<double> x = [], y = [];
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split(',');
            if (fields.Length != 2 || !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                throw new FormatException($"Invalid numeric row {x.Count + 2}.");
            if (x.Count >= 1_000_000) throw new FormatException("CSV exceeds one million points.");
            x.Add(a); y.Add(b);
        }
        return new(Guid.NewGuid(), deviceId, DateTimeOffset.UtcNow, kind, isSimulated, x, y);
    }
}
