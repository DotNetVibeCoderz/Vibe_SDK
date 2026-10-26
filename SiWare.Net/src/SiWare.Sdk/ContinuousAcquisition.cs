using System.Runtime.CompilerServices;

namespace SiWare.Sdk;

/// <summary>
/// Settings for a continuous run: <paramref name="Measurements"/> acquisitions started every <paramref name="Interval"/>,
/// like the Si-Spect app's "Continuous" run mode. The interval is measured start to start; when an acquisition takes longer
/// than the interval, the next one starts immediately rather than catching up with a burst.
/// </summary>
public sealed record ContinuousOptions(int Measurements, TimeSpan Interval)
{
    public const int MaxMeasurements = 10_000;
    public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(1);

    public void Validate()
    {
        if (Measurements is < 1 or > MaxMeasurements)
            throw new ArgumentOutOfRangeException(nameof(Measurements), $"Use 1–{MaxMeasurements} measurements.");
        if (Interval < TimeSpan.Zero || Interval > MaxInterval)
            throw new ArgumentOutOfRangeException(nameof(Interval), "Use an interval between 0 and 1 hour.");
    }
}

public static class ContinuousAcquisition
{
    /// <summary>
    /// Acquires a series of spectra, yielding each one as soon as it is captured. Cancelling stops the series; spectra
    /// already yielded remain valid. Pass <paramref name="timeProvider"/> to control waiting in tests.
    /// </summary>
    public static async IAsyncEnumerable<Spectrum> AcquireSeriesAsync(this ISpectrometer device, MeasurementKind kind,
        ScanOptions scan, ContinuousOptions continuous, TimeProvider? timeProvider = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(continuous);
        scan.Validate();
        continuous.Validate();
        var time = timeProvider ?? TimeProvider.System;
        var start = time.GetTimestamp();
        for (var i = 0; i < continuous.Measurements; i++)
        {
            var due = continuous.Interval * i - time.GetElapsedTime(start);
            if (due > TimeSpan.Zero) await Task.Delay(due, time, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return await device.AcquireAsync(kind, scan, cancellationToken).ConfigureAwait(false);
        }
    }
}
