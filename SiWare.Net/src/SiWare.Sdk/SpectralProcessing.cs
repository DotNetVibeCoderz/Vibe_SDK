namespace SiWare.Sdk;

public static class SpectralProcessing
{
    public static Spectrum Absorbance(Spectrum sample, Spectrum dark, Spectrum reference)
    {
        if (sample.Kind != MeasurementKind.Sample || dark.Kind != MeasurementKind.Dark || reference.Kind != MeasurementKind.Reference)
            throw new ArgumentException("Expected sample, dark, and reference spectra respectively.");
        Match(sample, dark); Match(sample, reference);
        var result = new double[sample.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var numerator = sample.Values[i] - dark.Values[i];
            var denominator = reference.Values[i] - dark.Values[i];
            if (numerator <= 0 || denominator <= 0 || !double.IsFinite(numerator) || !double.IsFinite(denominator))
                throw new ArgumentException($"Invalid optical calibration at point {i}.");
            // Avoid overflow/underflow of the intensity ratio itself.
            result[i] = Math.Log10(denominator) - Math.Log10(numerator);
            if (!double.IsFinite(result[i])) throw new ArgumentException("Absorbance exceeds numeric range.");
        }
        return sample.WithValues(result, MeasurementKind.Absorbance);
    }

    public static Spectrum StandardNormalVariate(Spectrum spectrum)
    {
        var scale = spectrum.Values.Max(Math.Abs);
        if (scale == 0 || spectrum.Values.All(value => value == spectrum.Values[0]))
            throw new ArgumentException("SNV requires nonconstant data.");
        var sum = 0.0;
        for (var i = 0; i < spectrum.Count; i++) sum += spectrum.Values[i] / scale;
        var mean = sum / spectrum.Count;
        var variance = 0.0;
        for (var i = 0; i < spectrum.Count; i++)
        {
            var delta = spectrum.Values[i] / scale - mean;
            variance += delta * delta;
        }
        var deviation = Math.Sqrt(variance / (spectrum.Count - 1));
        if (!double.IsFinite(deviation) || deviation <= 0) throw new ArgumentException("SNV requires nonconstant, numerically bounded data.");
        return spectrum.WithValues(spectrum.Values.Select(v => (v / scale - mean) / deviation).ToArray(), MeasurementKind.Normalized);
    }

    public static Spectrum Smooth(Spectrum spectrum, int window = 5)
    {
        if (window < 1 || window % 2 == 0 || window > spectrum.Count) throw new ArgumentOutOfRangeException(nameof(window));
        if (window == 1) return spectrum.WithValues(spectrum.Values.ToArray(), spectrum.Kind);
        var result = new double[spectrum.Count];
        var scale = spectrum.Values.Max(Math.Abs);
        if (scale == 0) return spectrum.WithValues(result, spectrum.Kind);
        var radius = window / 2;
        var left = 0;
        var right = radius + 1;
        var sum = 0.0;
        for (var i = 0; i < right; i++) sum += spectrum.Values[i] / scale;
        for (var i = 0; i < result.Length; i++)
        {
            var nextLeft = Math.Max(0, i - radius);
            var nextRight = Math.Min(result.Length, i + radius + 1);
            while (left < nextLeft) sum -= spectrum.Values[left++] / scale;
            while (right < nextRight) sum += spectrum.Values[right++] / scale;
            result[i] = Math.Clamp(sum / (right - left), -1.0, 1.0) * scale;
        }
        return spectrum.WithValues(result, spectrum.Kind);
    }

    public static void Match(Spectrum a, Spectrum b)
    {
        if (a.DeviceId != b.DeviceId || a.IsSimulated != b.IsSimulated || a.Count != b.Count)
            throw new ArgumentException("Calibration must use the same device, provenance, and wavelength grid.");
        for (var i = 0; i < a.Count; i++)
            if (Math.Abs(a.Wavelengths[i] - b.Wavelengths[i]) > 1e-8)
                throw new ArgumentException("Calibration wavelength grids differ.");
    }
}
