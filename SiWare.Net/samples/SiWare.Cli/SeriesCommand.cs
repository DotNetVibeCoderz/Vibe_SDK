using System.Globalization;
using SiWare.Sdk;
using SiWare.Visualization;

/// <summary>Continuous run mode: N acquisitions started every interval, like Si-Spect's "Continuous" setting.</summary>
internal static class SeriesCommand
{
    public const string Help =
        "  series [--count 10] [--interval seconds] [--kind sample|reference] [--integration ms] [--points 257] [--averages 1]\n" +
        "         [--output dir] [--vendor-config config.json]   Continuous run; Ctrl+C stops and keeps captured spectra";

    public static async Task<int> RunAsync(ISpectrometer device, Func<string, string?> option, CancellationToken token)
    {
        var kind = option("--kind")?.ToLowerInvariant() switch
        {
            null or "sample" => MeasurementKind.Sample,
            "reference" => MeasurementKind.Reference,
            _ => throw new ArgumentException("--kind must be sample or reference.")
        };
        var scan = new ScanOptions(Int(option("--points"), 257), Int(option("--integration"), 100), Int(option("--averages"), 1));
        var interval = double.TryParse(option("--interval") ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) ? TimeSpan.FromSeconds(seconds) : throw new ArgumentException("--interval must be a number of seconds.");
        var continuous = new ContinuousOptions(Int(option("--count"), 10), interval);
        continuous.Validate();
        var directory = Path.GetFullPath(option("--output") ?? "artifacts/series");
        Directory.CreateDirectory(directory);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Continuous run: {continuous.Measurements} × {kind}, every {interval.TotalSeconds:0.###} s, {scan.Points} points"));
        var series = new List<Spectrum>();
        var cancelled = false;
        try
        {
            await foreach (var spectrum in device.AcquireSeriesAsync(kind, scan, continuous, cancellationToken: token))
            {
                series.Add(spectrum);
                var path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{kind.ToString().ToLowerInvariant()}-{series.Count:D3}.json"));
                await File.WriteAllTextAsync(path, SpectrumFile.ToJson(spectrum), CancellationToken.None);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  [{series.Count}/{continuous.Measurements}] {spectrum.CapturedAt:HH:mm:ss.fff}  mean {spectrum.Values.Average():G5}  → {Path.GetFileName(path)}"));
            }
        }
        catch (OperationCanceledException) { cancelled = true; }

        if (series.Count > 0)
        {
            SpectrumCharts.Overlay(series, $"Continuous run ({series.Count} × {kind})").Save(Path.Combine(directory, "series.png"));
            Console.WriteLine($"Saved {series.Count} spectra and series.png to {directory}");
        }
        if (cancelled) { Console.Error.WriteLine($"Stopped after {series.Count} of {continuous.Measurements} measurements."); return 2; }
        return 0;
    }

    private static int Int(string? value, int fallback) =>
        value == null ? fallback
        : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : throw new ArgumentException($"'{value}' is not an integer.");
}
