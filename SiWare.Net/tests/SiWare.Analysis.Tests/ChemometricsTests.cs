using Gravicode.Science.GraviLearn;
using Gravicode.Science.GraviLearn.ModelSelection;
using Gravicode.Science.GraviNum;
using SiWare.Analysis;
using SiWare.Sdk;

namespace SiWare.Analysis.Tests;

public class ChemometricsTests
{
    private static Spectrum Line(Func<double, double> f, int points = 41, double step = 2, ScanOptions? acquisition = null) =>
        new(Guid.NewGuid(), "dev", DateTimeOffset.UnixEpoch, MeasurementKind.Absorbance, true,
            [.. Enumerable.Range(0, points).Select(i => 1000 + step * i)], [.. Enumerable.Range(0, points).Select(i => f(1000 + step * i))], acquisition);

    [Theory]
    [InlineData(2, 2, 0, new[] { -3.0, 12, 17, 12, -3 }, 35.0)]
    [InlineData(2, 2, 1, new[] { -2.0, -1, 0, 1, 2 }, 10.0)]
    public void SavitzkyGolayCoefficients_MatchPublishedTables(int half, int order, int derivative, double[] numerators, double denominator)
    {
        var weights = Preprocessing.Coefficients(half, order, derivative, 0);
        for (var i = 0; i < weights.Length; i++) Assert.Equal(numerators[i] / denominator, weights[i], 12);
    }

    [Fact]
    public void SavitzkyGolay_IsExactForQuadraticsIncludingEdges()
    {
        var spectrum = Line(x => 0.001 * x * x - 0.5 * x + 3);
        var smoothed = Preprocessing.SavitzkyGolay(spectrum, 7, 2);
        var slope = Preprocessing.SavitzkyGolay(spectrum, 7, 2, derivative: 1);
        for (var i = 0; i < spectrum.Count; i++)
        {
            Assert.Equal(spectrum.Values[i], smoothed.Values[i], 8);
            Assert.Equal(0.002 * spectrum.Wavelengths[i] - 0.5, slope.Values[i], 8);
        }
        Assert.Equal(spectrum.Acquisition, smoothed.Acquisition);
    }

    [Fact]
    public void SavitzkyGolay_RejectsInvalidSettingsAndUnevenGridsForDerivatives()
    {
        var spectrum = Line(x => x);
        Assert.Throws<ArgumentOutOfRangeException>(() => Preprocessing.SavitzkyGolay(spectrum, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => Preprocessing.SavitzkyGolay(spectrum, 5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Preprocessing.SavitzkyGolay(spectrum, 5, 2, 3));
        var uneven = new Spectrum(Guid.NewGuid(), "dev", DateTimeOffset.UnixEpoch, MeasurementKind.Absorbance, true,
            [1, 2, 3, 5, 6, 7, 8], [1, 2, 3, 5, 6, 7, 8]);
        Assert.Throws<ArgumentException>(() => Preprocessing.SavitzkyGolay(uneven, 3, 2, 1));
        Assert.Equal(uneven.Count, Preprocessing.SavitzkyGolay(uneven, 3, 2).Count);
    }

    [Fact]
    public void Msc_RemovesOffsetAndSlopeRelativeToReference()
    {
        var reference = Line(x => Math.Sin(x / 20) + 2);
        var distorted = reference.WithValues([.. reference.Values.Select(v => 1.7 * v + 0.4)], reference.Kind);
        var corrected = Preprocessing.MultiplicativeScatterCorrection(new SpectralDataset([distorted, distorted]), [.. reference.Values]);
        for (var i = 0; i < reference.Count; i++) Assert.Equal(reference.Values[i], corrected.Spectra[0].Values[i], 9);
    }

    [Fact]
    public void Dataset_RejectsMixedGridsProvenanceAndAcquisitionSettings()
    {
        var a = Line(x => x);
        Assert.Throws<ArgumentException>(() => new SpectralDataset([a, Line(x => x, step: 3)]));
        Assert.Throws<ArgumentException>(() => new SpectralDataset([a, Line(x => x, acquisition: new ScanOptions(41))]));
        Assert.Throws<ArgumentException>(() => new SpectralDataset([a, a], [1.0]));
        Assert.Throws<ArgumentException>(() => new SpectralDataset([a, a], [1.0, double.NaN]));
        Assert.Throws<InvalidOperationException>(() => new SpectralDataset([a, a]).ToTarget());
    }

    [Fact]
    public void DatasetJson_RoundTripsProvenanceReferencesAndSettings()
    {
        var dataset = SimulatedSampleSet.Generate(samples: 6, points: 32);
        var restored = SpectralDatasetFile.FromJson(SpectralDatasetFile.ToJson(dataset));
        Assert.True(restored.IsSimulated);
        Assert.Equal(dataset.ReferenceValues, restored.ReferenceValues);
        Assert.Equal(SimulatedSampleSet.PropertyName, restored.ReferenceName);
        Assert.Equal(dataset.Spectra[0].Acquisition, restored.Spectra[0].Acquisition);
        Assert.Equal(dataset.Spectra[3].Values, restored.Spectra[3].Values);
        var frame = dataset.ToDataFrame();
        Assert.Equal(6, frame.RowCount);
    }

    [Fact]
    public void Pca_ExplainsVarianceInDescendingOrder()
    {
        var pca = PcaResult.Fit(SimulatedSampleSet.Generate(samples: 30, points: 64), 3);
        Assert.Equal(30, pca.Scores.Length);
        Assert.Equal(64, pca.Loadings[0].Length);
        Assert.True(pca.ExplainedVarianceRatio[0] >= pca.ExplainedVarianceRatio[1]);
        Assert.InRange(pca.ExplainedVarianceRatio.Sum(), 0.9, 1.0 + 1e-9);
        Assert.True(pca.IsSimulated);
        Assert.Throws<ArgumentOutOfRangeException>(() => PcaResult.Fit(SimulatedSampleSet.Generate(samples: 5, points: 64), 6));
    }

    [Fact]
    public void Pls_RecoversAnExactLinearRelation()
    {
        var random = new Random(3);
        var rows = Enumerable.Range(0, 25).Select(_ => Enumerable.Range(0, 6).Select(_ => random.NextDouble()).ToArray()).ToArray();
        double[] beta = [2, -1, 0.5, 0, 3, -2];
        var y = rows.Select(r => 4 + r.Zip(beta, (a, b) => a * b).Sum()).ToArray();
        var model = new PlsRegression(6);
        model.Fit(NdArray.FromRows(rows), NdArray.FromValues(y));
        var predicted = model.Predict(NdArray.FromRows(rows)).ToArray();
        for (var i = 0; i < y.Length; i++) Assert.Equal(y[i], predicted[i], 8);
        for (var j = 0; j < beta.Length; j++) Assert.Equal(beta[j], model.Coefficients[j], 8);
    }

    [Fact]
    public void Pls_WorksWithGravicodeCrossValidation()
    {
        var dataset = SimulatedSampleSet.Generate(samples: 40, points: 64);
        var result = Selection.CrossValidate(() => new PlsRegression(3), dataset.ToMatrix(), dataset.ToTarget(),
            folds: 4, scorer: Metrics.R2Score);
        Assert.Equal(4, result.Scores.Length);
        Assert.True(result.Mean > 0.9, result.ToString());
    }

    [Fact]
    public void Pls_ValidatesUsage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlsRegression(0));
        Assert.Throws<InvalidOperationException>(() => new PlsRegression(1).Predict(NdArray.Zeros(2, 3)));
        Assert.Throws<ArgumentException>(() => new PlsRegression(5).Fit(NdArray.Zeros(4, 3), NdArray.Zeros(4)));
    }

    [Fact]
    public void Calibration_SelectsComponentsAndPredictsSimulatedMoisture()
    {
        var calibration = CalibrationResult.Fit(SimulatedSampleSet.Generate(samples: 50, points: 96), maxComponents: 6);
        Assert.InRange(calibration.Components, 1, 6);
        Assert.Equal(calibration.RmseCvByComponents.Min(), calibration.RmseCv);
        Assert.True(calibration.R2Cv > 0.95, $"R² {calibration.R2Cv}");
        Assert.Equal(50, calibration.CrossValidatedPredictions.Length);
        Assert.Equal(96, calibration.Coefficients.Length);
        Assert.True(calibration.IsSimulated);
    }
}
