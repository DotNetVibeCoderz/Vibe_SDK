using System.Globalization;
using System.Text;
using SiWare.Analysis;
using SiWare.Sdk;

namespace SiWare.Ai;

/// <summary>
/// Deterministic, compact descriptions of spectra and models. Only these facts are sent to a language model, never raw
/// data, so the prompt stays small and every statement the model makes can be traced to a computed number.
/// </summary>
public static class SpectrumFacts
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Describe(Spectrum spectrum, int maxFeatures = 6)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        var values = spectrum.Values;
        var builder = new StringBuilder();
        Line(builder, $"Measurement: {spectrum.Kind} spectrum from device {spectrum.DeviceId}, {spectrum.Count} points.");
        builder.AppendLine(spectrum.IsSimulated
            ? "Provenance: SIMULATED data from a software simulator, not a physical measurement."
            : "Provenance: hardware acquisition (vendor driver not yet validated on a physical sensor).");
        Line(builder, $"Wavelength range: {spectrum.Wavelengths[0]:F0}–{spectrum.Wavelengths[^1]:F0} nm.");
        Line(builder, $"Value range: {values.Min():G4} to {values.Max():G4}, mean {values.Average():G4}.");

        // Absorbance and derived spectra show bands as maxima; raw intensity shows absorption as minima.
        var bandsAreMaxima = spectrum.Kind is MeasurementKind.Absorbance or MeasurementKind.Normalized;
        var features = LocalExtrema(spectrum, bandsAreMaxima).Take(maxFeatures).OrderBy(f => f.Nm).ToList();
        builder.AppendLine(features.Count == 0
            ? "No distinct bands found."
            : (bandsAreMaxima ? "Absorption bands (local maxima): " : "Absorption features (local minima of intensity): ")
              + string.Join("; ", features.Select(f => string.Create(Invariant, $"{f.Nm:F0} nm (value {f.Value:G4}, prominence {f.Prominence:G3})"))));
        return builder.ToString();
    }

    public static string Describe(CalibrationResult calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        var builder = new StringBuilder();
        Line(builder, $"PLS calibration of {calibration.Property}: {calibration.Measured.Length} samples, {calibration.Folds}-fold cross-validation.");
        builder.AppendLine(calibration.IsSimulated ? "Provenance: SIMULATED dataset." : "Provenance: measured dataset.");
        Line(builder, $"Chosen latent variables: {calibration.Components}; RMSECV {calibration.RmseCv:G4}; R² (CV) {calibration.R2Cv:F3}.");
        Line(builder, $"Reference range: {calibration.Measured.Min():G4} to {calibration.Measured.Max():G4}.");
        var top = calibration.Coefficients.Select((c, i) => (Nm: calibration.Wavelengths[i], Weight: c))
            .OrderByDescending(p => Math.Abs(p.Weight)).Take(5).OrderBy(p => p.Nm);
        builder.AppendLine("Largest regression coefficients at: " + string.Join("; ", top.Select(p => string.Create(Invariant, $"{p.Nm:F0} nm ({p.Weight:+0.###E+0;-0.###E+0})"))) + ".");
        return builder.ToString();
    }

    public static string Describe(PcaResult pca)
    {
        ArgumentNullException.ThrowIfNull(pca);
        var builder = new StringBuilder();
        Line(builder, $"PCA of {pca.Scores.Length} spectra, {pca.Components} components{(pca.IsSimulated ? ", SIMULATED data" : "")}.");
        for (var c = 0; c < pca.Components; c++)
        {
            var loadings = pca.Loadings[c];
            var peak = Enumerable.Range(0, loadings.Length).OrderByDescending(i => Math.Abs(loadings[i])).First();
            Line(builder, $"PC{c + 1}: {pca.ExplainedVarianceRatio[c]:P1} of variance; strongest loading at {pca.Wavelengths[peak]:F0} nm.");
        }
        return builder.ToString();
    }

    private static void Line(StringBuilder builder, FormattableString text) => builder.AppendLine(text.ToString(Invariant));

    private static IEnumerable<(double Nm, double Value, double Prominence)> LocalExtrema(Spectrum spectrum, bool maxima)
    {
        var y = spectrum.Values;
        var n = y.Count;
        var sign = maxima ? 1.0 : -1.0;
        var span = Math.Max(2, n / 40);
        var range = y.Max() - y.Min();
        if (range <= 0) yield break;
        var found = new List<(double, double, double)>();
        for (var i = span; i < n - span; i++)
        {
            var isPeak = true;
            for (var k = i - span; k <= i + span && isPeak; k++)
                if (k != i && sign * y[k] > sign * y[i]) isPeak = false;
            if (!isPeak) continue;
            // Prominence relative to the lower of the two surrounding window minima.
            var left = Enumerable.Range(Math.Max(0, i - 4 * span), 4 * span).Min(k => sign * y[k]);
            var right = Enumerable.Range(i + 1, Math.Min(4 * span, n - i - 1)).Min(k => sign * y[k]);
            var prominence = sign * y[i] - Math.Max(left, right);
            if (prominence > 0.02 * range) found.Add((spectrum.Wavelengths[i], y[i], prominence));
        }
        foreach (var item in found.OrderByDescending(f => f.Item3)) yield return item;
    }
}
