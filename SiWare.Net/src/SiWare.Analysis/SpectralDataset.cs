using Gravicode.Science.GraviFrame;
using Gravicode.Science.GraviNum;
using SiWare.Sdk;

namespace SiWare.Analysis;

/// <summary>
/// Spectra on one shared wavelength grid, optionally paired with laboratory reference values.
/// Rows are samples and columns are wavelengths, matching scikit-learn's <c>X</c> layout.
/// </summary>
public sealed class SpectralDataset
{
    private readonly Spectrum[] spectra;
    private readonly double[]? references;

    public SpectralDataset(IReadOnlyList<Spectrum> spectra, IReadOnlyList<double>? referenceValues = null, string? referenceName = null)
    {
        ArgumentNullException.ThrowIfNull(spectra);
        if (spectra.Count < 2) throw new ArgumentException("A dataset needs at least two spectra.", nameof(spectra));
        var first = spectra[0];
        for (var s = 1; s < spectra.Count; s++)
        {
            var other = spectra[s];
            if (other.Count != first.Count || other.Kind != first.Kind || other.IsSimulated != first.IsSimulated || other.Acquisition != first.Acquisition)
                throw new ArgumentException($"Spectrum {s} differs from the first in length, kind, provenance, or acquisition settings.", nameof(spectra));
            for (var i = 0; i < first.Count; i++)
                if (Math.Abs(other.Wavelengths[i] - first.Wavelengths[i]) > 1e-6)
                    throw new ArgumentException($"Spectrum {s} is on a different wavelength grid.", nameof(spectra));
        }
        if (referenceValues != null)
        {
            if (referenceValues.Count != spectra.Count) throw new ArgumentException("Provide one reference value per spectrum.", nameof(referenceValues));
            if (referenceValues.Any(v => !double.IsFinite(v))) throw new ArgumentException("Reference values must be finite.", nameof(referenceValues));
            references = [.. referenceValues];
        }
        this.spectra = [.. spectra];
        ReferenceName = referenceName ?? "reference";
    }

    public IReadOnlyList<Spectrum> Spectra => spectra;
    public IReadOnlyList<double> Wavelengths => spectra[0].Wavelengths;
    public IReadOnlyList<double>? ReferenceValues => references;
    public string ReferenceName { get; }
    public int SampleCount => spectra.Length;
    public int FeatureCount => spectra[0].Count;
    public MeasurementKind Kind => spectra[0].Kind;
    /// <summary>True when the spectra came from the simulator; propagate it to every report and plot.</summary>
    public bool IsSimulated => spectra[0].IsSimulated;

    /// <summary>The samples × wavelengths matrix.</summary>
    public NdArray ToMatrix()
    {
        var data = new double[SampleCount * FeatureCount];
        for (var s = 0; s < spectra.Length; s++)
            for (var i = 0; i < FeatureCount; i++)
                data[s * FeatureCount + i] = spectra[s].Values[i];
        return new NdArray(data, SampleCount, FeatureCount);
    }

    /// <summary>The reference values as a column vector; throws when the dataset has none.</summary>
    public NdArray ToTarget() => NdArray.FromValues(references ?? throw new InvalidOperationException("The dataset has no reference values."));

    /// <summary>Applies a per-spectrum transform, keeping the reference values.</summary>
    public SpectralDataset Select(Func<Spectrum, Spectrum> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return new([.. spectra.Select(transform)], references, ReferenceName);
    }

    /// <summary>
    /// Wide table: one row per sample with <c>id</c>, the reference value when present, and one column per wavelength
    /// (named <c>nm_1354.6875</c>, the exact wavelength), ready for <see cref="DataFrame.WriteCsv"/>.
    /// </summary>
    public DataFrame ToDataFrame()
    {
        var columns = new Dictionary<string, double[]> { ["sample"] = [.. Enumerable.Range(1, SampleCount).Select(i => (double)i)] };
        if (references != null) columns[ReferenceName] = references;
        for (var i = 0; i < FeatureCount; i++)
            columns.Add(FormattableString.Invariant($"nm_{Wavelengths[i]:R}"), [.. spectra.Select(s => s.Values[i])]);
        return DataFrame.FromColumns(columns);
    }
}
