namespace SiWare.Sdk;

public sealed record DeviceInfo(string Id, string Name, string Transport, bool IsSimulated);

public sealed record ScanOptions(int Points = 257, int IntegrationMilliseconds = 100, int Averages = 1)
{
    public void Validate()
    {
        if (Points is < 2 or > 4096) throw new ArgumentOutOfRangeException(nameof(Points), "Use 2–4096 points.");
        if (IntegrationMilliseconds is < 1 or > 60_000) throw new ArgumentOutOfRangeException(nameof(IntegrationMilliseconds));
        if (Averages is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(Averages));
    }
}

/// <summary>Owns an acquisition connection; implementations serialize operations and support cancellation.</summary>
public interface ISpectrometer : IAsyncDisposable
{
    DeviceInfo Device { get; }
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<Spectrum> AcquireAsync(MeasurementKind kind, ScanOptions options, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Deterministic synthetic optical signal for software development, not a hardware emulator.</summary>
public sealed class SimulatedSpectrometer(int seed = 42) : ISpectrometer
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Random random = new(seed);
    private bool disposed;
    public DeviceInfo Device { get; } = new("SIM-001", "SiWare optical simulator", "Simulation", true);
    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(disposed, this); IsConnected = true; }
        finally { gate.Release(); }
    }

    public async Task<Spectrum> AcquireAsync(MeasurementKind kind, ScanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (kind is not (MeasurementKind.Sample or MeasurementKind.Dark or MeasurementKind.Reference))
            throw new ArgumentException("Acquire sample, dark, or reference; derive other kinds with processing.", nameof(kind));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsConnected) throw new InvalidOperationException("Connect the spectrometer before acquisition.");
            var x = new double[options.Points];
            var y = new double[options.Points];
            for (var scan = 0; scan < options.Averages; scan++)
            {
                await Task.Delay(options.IntegrationMilliseconds, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < x.Length; i++)
                {
                    var nm = x[i] = 1350 + 1200.0 * i / (x.Length - 1);
                    var lamp = 8000 + 3000 * Math.Exp(-Math.Pow((nm - 1900) / 600, 2));
                    var absorption = 0.12 + 0.32 * Math.Exp(-Math.Pow((nm - 1450) / 55, 2))
                        + 0.65 * Math.Exp(-Math.Pow((nm - 1940) / 90, 2))
                        + 0.22 * Math.Exp(-Math.Pow((nm - 2310) / 65, 2));
                    var intensity = kind switch
                    {
                        MeasurementKind.Dark => 50,
                        MeasurementKind.Reference => lamp + 50,
                        _ => lamp * Math.Pow(10, -absorption) + 50
                    };
                    y[i] += (intensity + (random.NextDouble() - 0.5) * 4) / options.Averages;
                }
            }
            return new(Guid.NewGuid(), Device.Id, DateTimeOffset.UtcNow, kind, true, x, y, options);
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { IsConnected = false; }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { IsConnected = false; disposed = true; }
        finally { gate.Release(); }
    }
}
