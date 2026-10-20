using SiWare.Analysis;
using SiWare.Visualization;

namespace SiWare.Analysis.Tests;

public class VisualizationTests
{
    private static readonly SpectralDataset Dataset = SimulatedSampleSet.Generate(samples: 20, points: 64);

    [Fact]
    public void Charts_RenderPngAndLabelSimulatedData()
    {
        var pca = PcaResult.Fit(Dataset, 2);
        var calibration = CalibrationResult.Fit(Dataset, maxComponents: 4, folds: 4);
        foreach (var figure in new[]
        {
            SpectrumCharts.Overlay(Dataset.Spectra), SpectrumCharts.PcaScores(pca, colorValues: Dataset.ReferenceValues, colorLabel: "moisture"),
            SpectrumCharts.PcaLoadings(pca), SpectrumCharts.PredictedVsMeasured(calibration), SpectrumCharts.ComponentSelection(calibration)
        })
        {
            var png = figure.ToPng();
            Assert.True(png.Length > 5_000);
            Assert.Equal(0x89, png[0]);
            Assert.Contains("SIMULATED DATA", figure.ToSvg());
        }
    }

    [Fact]
    public void Charts_ValidateArguments()
    {
        var pca = PcaResult.Fit(Dataset, 2);
        Assert.Throws<ArgumentException>(() => SpectrumCharts.Overlay([]));
        Assert.Throws<ArgumentException>(() => SpectrumCharts.Overlay(Dataset.Spectra, labels: ["one"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpectrumCharts.PcaScores(pca, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpectrumCharts.PcaScores(pca, 1, 3));
    }
}
