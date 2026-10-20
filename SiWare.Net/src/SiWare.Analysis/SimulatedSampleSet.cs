using SiWare.Sdk;

namespace SiWare.Analysis;

/// <summary>
/// Deterministic synthetic NIR absorbance spectra with a known moisture content, for exercising PCA and PLS without
/// laboratory data. Every spectrum is marked <see cref="Spectrum.IsSimulated"/>; this is a teaching and test fixture,
/// not a model of any real material.
/// </summary>
/// <remarks>
/// Moisture drives the O-H bands near 1450 and 1940 nm. A protein-like interferent near 2050/2180 nm varies
/// independently, and per-sample baseline offset, multiplicative scatter, and noise imitate real sample presentation,
/// so PLS needs several components. Moisture also changes overall absorbance, so MSC removes part of the signal here:
/// compare RMSECV with and without each preprocessing step instead of applying them by habit.
/// </remarks>
public static class SimulatedSampleSet
{
    public const string PropertyName = "moisture_percent";

    public static SpectralDataset Generate(int samples = 60, int points = 257, int seed = 7)
    {
        if (samples < 4 || samples > 10_000) throw new ArgumentOutOfRangeException(nameof(samples));
        if (points < 16 || points > 4096) throw new ArgumentOutOfRangeException(nameof(points));
        var random = new Random(seed);
        var wavelengths = new double[points];
        for (var i = 0; i < points; i++) wavelengths[i] = 1350 + 1200.0 * i / (points - 1);

        var spectra = new Spectrum[samples];
        var moisture = new double[samples];
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var settings = new ScanOptions(points, IntegrationMilliseconds: 100, Averages: 3);
        for (var s = 0; s < samples; s++)
        {
            moisture[s] = Math.Round(5 + 20 * random.NextDouble(), 2);
            var protein = 0.5 + random.NextDouble();
            var offset = 0.05 * (random.NextDouble() - 0.5);
            var scatter = 0.9 + 0.2 * random.NextDouble();
            var values = new double[points];
            for (var i = 0; i < points; i++)
            {
                var nm = wavelengths[i];
                var water = moisture[s] / 25 * (0.30 * Band(nm, 1450, 45) + 0.75 * Band(nm, 1940, 70));
                var interferent = protein * (0.18 * Band(nm, 2050, 60) + 0.22 * Band(nm, 2180, 55));
                var matrix = 0.15 + 0.10 * Band(nm, 2310, 80) + 0.00008 * (nm - 1350);
                values[i] = scatter * (matrix + water + interferent) + offset + 0.002 * Gaussian(random);
            }
            spectra[s] = new(Guid.NewGuid(), "SIM-001", start.AddMinutes(s), MeasurementKind.Absorbance, true, wavelengths, values, settings);
        }
        return new(spectra, moisture, PropertyName);
    }

    private static double Band(double nm, double centre, double width) => Math.Exp(-Math.Pow((nm - centre) / width, 2));

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
