using Gravicode.Science.GraviLearn;
using Gravicode.Science.GraviLearn.Decomposition;
using Gravicode.Science.GraviLearn.ModelSelection;
using Gravicode.Science.GraviNum;

namespace SiWare.Analysis;

/// <summary>Principal component analysis of a dataset: scores per sample, loadings per wavelength.</summary>
public sealed record PcaResult(
    int Components,
    double[][] Scores,
    double[][] Loadings,
    double[] ExplainedVarianceRatio,
    IReadOnlyList<double> Wavelengths,
    bool IsSimulated)
{
    /// <summary>Runs mean-centred PCA through GravicodeScience.</summary>
    public static PcaResult Fit(SpectralDataset dataset, int components = 3)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (components < 1 || components > Math.Min(dataset.SampleCount, dataset.FeatureCount))
            throw new ArgumentOutOfRangeException(nameof(components), "Components must be between 1 and min(samples, wavelengths).");
        var pca = new PCA(components);
        var scores = pca.FitTransform(dataset.ToMatrix());
        return new(components, Rows(scores), Rows(pca.ComponentVectors), pca.ExplainedVarianceRatio.ToArray(), dataset.Wavelengths, dataset.IsSimulated);
    }

    internal static double[][] Rows(NdArray matrix)
    {
        var rows = matrix.Shape[0];
        var cols = matrix.Shape[1];
        var data = matrix.ToArray();
        var result = new double[rows][];
        for (var r = 0; r < rows; r++) result[r] = data.AsSpan(r * cols, cols).ToArray();
        return result;
    }
}

/// <summary>
/// Partial least squares regression for a single response (PLS1, NIPALS). Implements GravicodeScience's
/// <see cref="IEstimator"/>, so it works with <c>Selection.CrossValidate</c> and pipelines.
/// </summary>
public sealed class PlsRegression(int components) : IEstimator
{
    private double[] xMean = [];
    private double yMean;

    public int Components { get; } = components >= 1 ? components : throw new ArgumentOutOfRangeException(nameof(components));
    /// <summary>Regression vector over wavelengths: <c>y = (x - mean) · Coefficients + intercept</c>.</summary>
    public double[] Coefficients { get; private set; } = [];
    public bool IsFitted => Coefficients.Length > 0;

    public void Fit(NdArray x, NdArray y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.Shape.Length != 2) throw new ArgumentException("X must be a samples × features matrix.", nameof(x));
        var n = x.Shape[0];
        var m = x.Shape[1];
        if (y.Size != n) throw new ArgumentException("Provide one response per sample.", nameof(y));
        if (Components > Math.Min(n - 1, m)) throw new ArgumentException($"Cannot fit {Components} components from {n} samples and {m} features.");

        var residualX = x.ToArray();
        var residualY = y.ToArray();
        xMean = new double[m];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < m; j++) xMean[j] += residualX[i * m + j] / n;
        yMean = residualY.Average();
        for (var i = 0; i < n; i++)
        {
            residualY[i] -= yMean;
            for (var j = 0; j < m; j++) residualX[i * m + j] -= xMean[j];
        }

        var weights = new double[Components][];
        var loadings = new double[Components][];
        var yLoadings = new double[Components];
        var t = new double[n];
        for (var a = 0; a < Components; a++)
        {
            // w = X'y / |X'y|
            var w = new double[m];
            for (var i = 0; i < n; i++)
                for (var j = 0; j < m; j++) w[j] += residualX[i * m + j] * residualY[i];
            var norm = Math.Sqrt(w.Sum(v => v * v));
            if (norm < 1e-12) throw new ArithmeticException($"Component {a + 1} explains no remaining covariance; use fewer components.");
            for (var j = 0; j < m; j++) w[j] /= norm;

            // t = Xw, p = X't / t't, q = y't / t't
            var tt = 0.0;
            for (var i = 0; i < n; i++)
            {
                var sum = 0.0;
                for (var j = 0; j < m; j++) sum += residualX[i * m + j] * w[j];
                t[i] = sum;
                tt += sum * sum;
            }
            var p = new double[m];
            var q = 0.0;
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < m; j++) p[j] += residualX[i * m + j] * t[i] / tt;
                q += residualY[i] * t[i] / tt;
            }
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < m; j++) residualX[i * m + j] -= t[i] * p[j];
                residualY[i] -= q * t[i];
            }
            weights[a] = w; loadings[a] = p; yLoadings[a] = q;
        }

        // B = W (P'W)^-1 q
        var ptw = new double[Components, Components];
        for (var r = 0; r < Components; r++)
            for (var c = 0; c < Components; c++)
                for (var j = 0; j < m; j++) ptw[r, c] += loadings[r][j] * weights[c][j];
        var inner = LinearAlgebra.Solve(ptw, yLoadings);
        var coefficients = new double[m];
        for (var a = 0; a < Components; a++)
            for (var j = 0; j < m; j++) coefficients[j] += weights[a][j] * inner[a];
        Coefficients = coefficients;
    }

    public NdArray Predict(NdArray x)
    {
        if (!IsFitted) throw new InvalidOperationException("Fit the model before predicting.");
        ArgumentNullException.ThrowIfNull(x);
        var m = Coefficients.Length;
        if (x.Shape.Length != 2 || x.Shape[1] != m) throw new ArgumentException($"Expected {m} features.", nameof(x));
        var data = x.ToArray();
        var n = x.Shape[0];
        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            var sum = yMean;
            for (var j = 0; j < m; j++) sum += (data[i * m + j] - xMean[j]) * Coefficients[j];
            result[i] = sum;
        }
        return NdArray.FromValues(result);
    }
}

/// <summary>Cross-validated PLS calibration of one reference property.</summary>
public sealed record CalibrationResult(
    string Property,
    int Components,
    int Folds,
    double RmseCv,
    double R2Cv,
    double[] Measured,
    double[] CrossValidatedPredictions,
    double[] RmseCvByComponents,
    double[] Coefficients,
    IReadOnlyList<double> Wavelengths,
    bool IsSimulated)
{
    /// <summary>
    /// Evaluates 1..<paramref name="maxComponents"/> PLS components with K-fold cross-validation, picks the count with the lowest
    /// RMSECV, and refits it on all samples for <see cref="Coefficients"/>.
    /// </summary>
    public static CalibrationResult Fit(SpectralDataset dataset, int maxComponents = 8, int folds = 5, int seed = 42)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var x = dataset.ToMatrix();
        var y = dataset.ToTarget();
        var n = dataset.SampleCount;
        if (folds < 2 || folds > n) throw new ArgumentOutOfRangeException(nameof(folds));
        var smallestTrain = n - (int)Math.Ceiling(n / (double)folds);
        maxComponents = Math.Min(maxComponents, Math.Min(smallestTrain - 1, dataset.FeatureCount));
        if (maxComponents < 1) throw new ArgumentException("Too few samples for cross-validated PLS.", nameof(dataset));

        var splits = Selection.KFold(n, folds, seed: seed);
        var measured = y.ToArray();
        var rmse = new double[maxComponents];
        var predictionsByComponents = new double[maxComponents][];
        for (var a = 1; a <= maxComponents; a++)
        {
            var predicted = new double[n];
            foreach (var (train, test) in splits)
            {
                var model = new PlsRegression(a);
                model.Fit(x.Take(train), NdArray.FromValues(train.Select(i => measured[i])));
                var foldPrediction = model.Predict(x.Take(test)).ToArray();
                for (var k = 0; k < test.Length; k++) predicted[test[k]] = foldPrediction[k];
            }
            predictionsByComponents[a - 1] = predicted;
            rmse[a - 1] = Metrics.RootMeanSquaredError(y, NdArray.FromValues(predicted));
        }

        var best = Array.IndexOf(rmse, rmse.Min()) + 1;
        var final = new PlsRegression(best);
        final.Fit(x, y);
        var bestPredictions = predictionsByComponents[best - 1];
        return new(dataset.ReferenceName, best, folds, rmse[best - 1], Metrics.R2Score(y, NdArray.FromValues(bestPredictions)),
            measured, bestPredictions, rmse, final.Coefficients, dataset.Wavelengths, dataset.IsSimulated);
    }
}
