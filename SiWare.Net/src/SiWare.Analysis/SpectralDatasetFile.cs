using System.Text.Json;
using SiWare.Sdk;

namespace SiWare.Analysis;

/// <summary>
/// JSON persistence for datasets, keeping provenance and reference values: <c>{ property, references, spectra }</c>
/// where each spectrum uses the <see cref="SpectrumFile"/> schema. Use <see cref="SpectralDataset.ToDataFrame"/> for CSV.
/// </summary>
public static class SpectralDatasetFile
{
    public static string ToJson(SpectralDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return JsonSerializer.Serialize(new Document(dataset.ReferenceValues == null ? null : dataset.ReferenceName,
            dataset.ReferenceValues?.ToArray(), [.. dataset.Spectra]), SpectrumFile.JsonOptions);
    }

    public static SpectralDataset FromJson(string json)
    {
        var document = JsonSerializer.Deserialize<Document>(json, SpectrumFile.JsonOptions) ?? throw new FormatException("Dataset JSON is null.");
        if (document.Spectra is not { Length: > 0 }) throw new FormatException("Dataset JSON has no spectra.");
        return new(document.Spectra, document.References, document.Property);
    }

    private sealed record Document(string? Property, double[]? References, Spectrum[]? Spectra);
}
