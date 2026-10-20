using Matplotlib.Net;
using Matplotlib.Net.Artists;
using Matplotlib.Net.Rendering;
using Matplotlib.Net.Styling;
using SiWare.Analysis;
using SiWare.Sdk;
using Visual.Net.Core.Colors;

namespace SiWare.Visualization;

/// <summary>
/// Builds Matplotlib.Net figures for spectra and chemometric results. Save with <c>figure.Save("plot.png")</c>
/// (also <c>.svg</c>, <c>.pdf</c>, <c>.jpg</c>). Charts of simulated data say so in their title.
/// </summary>
public static class SpectrumCharts
{
    private static readonly Color Accent = Color.Parse("#365fea");
    private static readonly Color Muted = Color.Parse("#8a97aa");

    /// <summary>Overlays spectra on one wavelength axis; at most <paramref name="maxTraces"/> are drawn, evenly sampled.</summary>
    public static Figure Overlay(IReadOnlyList<Spectrum> spectra, string title = "Spectra", IReadOnlyList<string>? labels = null, int maxTraces = 40)
    {
        ArgumentNullException.ThrowIfNull(spectra);
        if (spectra.Count == 0) throw new ArgumentException("Provide at least one spectrum.", nameof(spectra));
        if (labels != null && labels.Count != spectra.Count) throw new ArgumentException("Provide one label per spectrum.", nameof(labels));
        var figure = NewFigure(9, 4.8);
        var ax = figure.AddSubplot();
        var stride = Math.Max(1, (int)Math.Ceiling(spectra.Count / (double)maxTraces));
        for (var s = 0; s < spectra.Count; s += stride)
        {
            var spectrum = spectra[s];
            ax.Plot([.. spectrum.Wavelengths], [.. spectrum.Values], labels?[s], lineWidth: spectra.Count > 6 ? 1.0 : 1.8,
                alpha: spectra.Count > 6 ? 0.75 : 1.0);
        }
        ax.SetTitle(Title(title, spectra.Any(s => s.IsSimulated)));
        ax.SetXLabel("Wavelength (nm)");
        ax.SetYLabel(AxisLabel(spectra[0].Kind));
        if (labels != null && spectra.Count <= 10) ax.Legend(LegendLocation.Best);
        return figure;
    }

    /// <summary>PCA score plot of two components, optionally coloured by reference values.</summary>
    public static Figure PcaScores(PcaResult pca, int xComponent = 1, int yComponent = 2, IReadOnlyList<double>? colorValues = null, string? colorLabel = null)
    {
        ArgumentNullException.ThrowIfNull(pca);
        if (xComponent < 1 || yComponent < 1 || xComponent > pca.Components || yComponent > pca.Components || xComponent == yComponent)
            throw new ArgumentOutOfRangeException(nameof(xComponent), "Choose two different fitted components.");
        var figure = NewFigure(6.8, 5.6);
        var ax = figure.AddSubplot();
        var x = pca.Scores.Select(row => row[xComponent - 1]).ToArray();
        var y = pca.Scores.Select(row => row[yComponent - 1]).ToArray();
        if (colorValues != null)
        {
            ax.Scatter(x, y, colorValues: [.. colorValues], colormap: "viridis", sizes: [46], alpha: 0.9,
                edgeColor: Color.Parse("#ffffff"), edgeWidth: 0.7);
            ax.AddColorbar(colorLabel ?? "reference");
        }
        else ax.Scatter(x, y, color: Accent, sizes: [46], alpha: 0.85, edgeColor: Color.Parse("#ffffff"));
        ax.AxHLine(0, Muted, LineStyle.Dotted);
        ax.AxVLine(0, Muted, LineStyle.Dotted);
        ax.SetTitle(Title("PCA scores", pca.IsSimulated));
        ax.SetXLabel(FormattableString.Invariant($"PC{xComponent} ({pca.ExplainedVarianceRatio[xComponent - 1]:P1})"));
        ax.SetYLabel(FormattableString.Invariant($"PC{yComponent} ({pca.ExplainedVarianceRatio[yComponent - 1]:P1})"));
        return figure;
    }

    /// <summary>PCA loadings against wavelength: which bands drive each component.</summary>
    public static Figure PcaLoadings(PcaResult pca, int components = 2)
    {
        ArgumentNullException.ThrowIfNull(pca);
        components = Math.Clamp(components, 1, pca.Components);
        var figure = NewFigure(9, 4.2);
        var ax = figure.AddSubplot();
        var wavelengths = pca.Wavelengths.ToArray();
        for (var c = 0; c < components; c++)
            ax.Plot(wavelengths, pca.Loadings[c], FormattableString.Invariant($"PC{c + 1} ({pca.ExplainedVarianceRatio[c]:P1})"), lineWidth: 1.8);
        ax.AxHLine(0, Muted, LineStyle.Dotted);
        ax.SetTitle(Title("PCA loadings", pca.IsSimulated));
        ax.SetXLabel("Wavelength (nm)");
        ax.SetYLabel("Loading");
        ax.Legend(LegendLocation.Best);
        return figure;
    }

    /// <summary>Cross-validated predictions against reference values, with the identity line and RMSECV/R².</summary>
    public static Figure PredictedVsMeasured(CalibrationResult calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        var figure = NewFigure(6.4, 6);
        var ax = figure.AddSubplot();
        var all = calibration.Measured.Concat(calibration.CrossValidatedPredictions).ToArray();
        var pad = (all.Max() - all.Min()) * 0.05;
        double low = all.Min() - pad, high = all.Max() + pad;
        ax.Plot([low, high], [low, high], "ideal", color: Muted, style: LineStyle.Dashed);
        ax.Scatter(calibration.Measured, calibration.CrossValidatedPredictions, "samples", color: Accent, sizes: [40], alpha: 0.85,
            edgeColor: Color.Parse("#ffffff"));
        ax.SetXLim(low, high);
        ax.SetTitle(Title(FormattableString.Invariant(
            $"PLS · {calibration.Components} LV · RMSECV {calibration.RmseCv:G3} · R² {calibration.R2Cv:F3}"), calibration.IsSimulated));
        ax.SetXLabel($"Measured {calibration.Property}");
        ax.SetYLabel($"Predicted {calibration.Property} (cross-validated)");
        ax.Legend(LegendLocation.UpperLeft);
        return figure;
    }

    /// <summary>RMSECV for each number of PLS latent variables, marking the chosen one.</summary>
    public static Figure ComponentSelection(CalibrationResult calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        var figure = NewFigure(6.4, 4.2);
        var ax = figure.AddSubplot();
        var counts = Enumerable.Range(1, calibration.RmseCvByComponents.Length).Select(i => (double)i).ToArray();
        ax.Plot(counts, calibration.RmseCvByComponents, "RMSECV", color: Accent, marker: MarkerStyle.Circle);
        ax.AxVLine(calibration.Components, Muted, LineStyle.Dotted);
        ax.SetTitle(Title("Latent variable selection", calibration.IsSimulated));
        ax.SetXLabel("PLS latent variables");
        ax.SetYLabel($"RMSECV ({calibration.Property})");
        return figure;
    }

    private static Figure NewFigure(double width, double height) => new(width, height, dpi: 110, style: PlotStyle.SeabornWhiteGrid());

    private static string Title(string title, bool simulated) => simulated ? $"{title} — SIMULATED DATA" : title;

    private static string AxisLabel(MeasurementKind kind) => kind switch
    {
        MeasurementKind.Absorbance => "Absorbance",
        MeasurementKind.Normalized => "SNV (σ)",
        _ => "Intensity (a.u.)"
    };
}
