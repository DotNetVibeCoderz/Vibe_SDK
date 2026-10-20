using SiWare.Sdk;

namespace SiWare.Analysis;

/// <summary>Spectral preprocessing used before chemometric modelling.</summary>
public static class Preprocessing
{
    /// <summary>
    /// Savitzky-Golay filter: fits a polynomial of <paramref name="polynomialOrder"/> to each window and returns its value
    /// (<paramref name="derivative"/> 0) or derivative with respect to wavelength in nm.
    /// </summary>
    /// <remarks>
    /// Edges fit one polynomial to the first and last full window and evaluate it at each edge point, as SciPy's
    /// <c>savgol_filter(mode="interp")</c> does, so the output keeps the input length without padding artefacts.
    /// Derivatives assume an evenly spaced grid; uneven grids (beyond 1% of the mean step) are rejected.
    /// </remarks>
    public static Spectrum SavitzkyGolay(Spectrum spectrum, int window = 11, int polynomialOrder = 2, int derivative = 0)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        if (window < 3 || window % 2 == 0 || window > spectrum.Count)
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be odd, at least 3, and no longer than the spectrum.");
        if (polynomialOrder < 0 || polynomialOrder >= window) throw new ArgumentOutOfRangeException(nameof(polynomialOrder));
        if (derivative < 0 || derivative > polynomialOrder) throw new ArgumentOutOfRangeException(nameof(derivative));

        var n = spectrum.Count;
        var step = (spectrum.Wavelengths[n - 1] - spectrum.Wavelengths[0]) / (n - 1);
        if (derivative > 0)
            for (var i = 1; i < n; i++)
                if (Math.Abs(spectrum.Wavelengths[i] - spectrum.Wavelengths[i - 1] - step) > 0.01 * step)
                    throw new ArgumentException("Savitzky-Golay derivatives need an evenly spaced wavelength grid.", nameof(spectrum));

        var half = window / 2;
        var scale = Math.Pow(step, -derivative);
        var y = spectrum.Values;
        var result = new double[n];

        var centre = Coefficients(half, polynomialOrder, derivative, 0);
        for (var i = half; i < n - half; i++)
        {
            var sum = 0.0;
            for (var k = 0; k < window; k++) sum += centre[k] * y[i - half + k];
            result[i] = sum * scale;
        }
        for (var i = 0; i < half; i++)
        {
            var leading = Coefficients(half, polynomialOrder, derivative, i - half);
            var trailing = Coefficients(half, polynomialOrder, derivative, half - i);
            double head = 0, tail = 0;
            for (var k = 0; k < window; k++)
            {
                head += leading[k] * y[k];
                tail += trailing[k] * y[n - window + k];
            }
            result[i] = head * scale;
            result[n - 1 - i] = tail * scale;
        }
        return spectrum.WithValues(result, spectrum.Kind);
    }

    /// <summary>
    /// Weights that, applied to a window of <c>2 * half + 1</c> samples, give the <paramref name="derivative"/>-th derivative
    /// (in sample units) of the least-squares polynomial at <paramref name="position"/> samples from the window centre.
    /// </summary>
    public static double[] Coefficients(int half, int polynomialOrder, int derivative, int position)
    {
        var window = 2 * half + 1;
        var terms = polynomialOrder + 1;
        // Normal equations of the Vandermonde system J[k, j] = (k - half)^j.
        var normal = new double[terms, terms];
        for (var k = 0; k < window; k++)
        {
            var offset = k - half;
            for (var a = 0; a < terms; a++)
                for (var b = 0; b < terms; b++)
                    normal[a, b] += Math.Pow(offset, a + b);
        }
        // d^derivative/dx^derivative of x^j evaluated at the position.
        var basis = new double[terms];
        for (var j = derivative; j < terms; j++)
        {
            var factor = 1.0;
            for (var f = 0; f < derivative; f++) factor *= j - f;
            basis[j] = factor * Math.Pow(position, j - derivative);
        }
        var solved = LinearAlgebra.Solve(normal, basis);
        var weights = new double[window];
        for (var k = 0; k < window; k++)
        {
            var offset = k - half;
            for (var j = 0; j < terms; j++) weights[k] += solved[j] * Math.Pow(offset, j);
        }
        return weights;
    }

    /// <summary>
    /// Multiplicative scatter correction: regresses each spectrum on the dataset mean and removes the fitted offset and slope,
    /// cancelling particle-size and path-length scatter.
    /// </summary>
    public static SpectralDataset MultiplicativeScatterCorrection(SpectralDataset dataset, IReadOnlyList<double>? reference = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var m = dataset.FeatureCount;
        var target = reference?.ToArray() ?? MeanSpectrum(dataset);
        if (target.Length != m) throw new ArgumentException("Reference spectrum length differs from the dataset.", nameof(reference));
        var targetMean = target.Average();
        var targetVariance = target.Sum(v => (v - targetMean) * (v - targetMean));
        if (targetVariance <= 0) throw new ArgumentException("MSC needs a non-constant reference spectrum.", nameof(reference));

        return dataset.Select(spectrum =>
        {
            var mean = spectrum.Values.Average();
            var covariance = 0.0;
            for (var i = 0; i < m; i++) covariance += (target[i] - targetMean) * (spectrum.Values[i] - mean);
            var slope = covariance / targetVariance;
            if (Math.Abs(slope) < 1e-12) throw new ArgumentException("MSC slope is zero; the spectrum does not resemble the reference.");
            var offset = mean - slope * targetMean;
            var corrected = new double[m];
            for (var i = 0; i < m; i++) corrected[i] = (spectrum.Values[i] - offset) / slope;
            return spectrum.WithValues(corrected, spectrum.Kind);
        });
    }

    /// <summary>Mean of the dataset at each wavelength.</summary>
    public static double[] MeanSpectrum(SpectralDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var mean = new double[dataset.FeatureCount];
        foreach (var spectrum in dataset.Spectra)
            for (var i = 0; i < mean.Length; i++) mean[i] += spectrum.Values[i];
        for (var i = 0; i < mean.Length; i++) mean[i] /= dataset.SampleCount;
        return mean;
    }
}
