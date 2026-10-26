using SiWare.Sdk;

namespace SiWare.Workbench;

public sealed class WorkbenchSession(ISpectrometer device, InsightService insight) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<Guid, Spectrum> history = [];
    private readonly Queue<Guid> order = new();
    private CancellationTokenSource? active;
    private Spectrum? dark;
    private Spectrum? reference;
    private SeriesProgress? series;
    public object Status()
    {
        lock (sync) return new
        {
            connected = device.IsConnected,
            busy = active != null,
            device = device.Device,
            calibration = new { dark = dark != null, reference = reference != null },
            historyCount = history.Count,
            nativeAvailable = NativeDsp.IsAvailable,
            supportsDark = device.Device.IsSimulated,
            insightAvailable = insight.IsAvailable,
            insightModel = insight.Model,
            series
        };
    }
    public async Task<object> ConnectAsync(CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("An acquisition is already running.");
        try { await device.ConnectAsync(token); return Status(); }
        finally { gate.Release(); }
    }
    public async Task<object> DisconnectAsync(CancellationToken token)
    {
        Cancel();
        await gate.WaitAsync(token);
        try
        {
            await device.DisconnectAsync(token);
            lock (sync) { dark = null; reference = null; }
            return Status();
        }
        finally { gate.Release(); }
    }
    public async Task<Spectrum> AcquireAsync(AcquireRequest request, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("An acquisition is already running.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(3));
        lock (sync) active = cancellation;
        try
        {
            var spectrum = await device.AcquireAsync(request.Kind,
                new(request.Points, request.IntegrationMilliseconds, request.Averages), cancellation.Token);
            lock (sync)
            {
                if (request.Kind == MeasurementKind.Dark) dark = spectrum;
                if (request.Kind == MeasurementKind.Reference) reference = spectrum;
                Add(spectrum);
            }
            return spectrum;
        }
        finally { lock (sync) active = null; gate.Release(); }
    }
    /// <summary>
    /// Starts a continuous run in the background and returns immediately. Each spectrum joins the history as it arrives;
    /// progress is reported by <see cref="Status"/>, and <see cref="Cancel"/> stops the run.
    /// </summary>
    public object StartSeries(SeriesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scan = new ScanOptions(request.Points, request.IntegrationMilliseconds, request.Averages);
        scan.Validate();
        var continuous = new ContinuousOptions(request.Measurements, TimeSpan.FromSeconds(request.IntervalSeconds));
        continuous.Validate();
        if (!device.IsConnected) throw new InvalidOperationException("Connect the instrument first.");
        if (!gate.Wait(0)) throw new InvalidOperationException("An acquisition is already running.");
        var cancellation = new CancellationTokenSource();
        lock (sync)
        {
            active = cancellation;
            series = new(true, 0, continuous.Measurements, request.IntervalSeconds, null);
        }
        _ = Task.Run(() => RunSeriesAsync(scan, continuous, cancellation));
        return Status();
    }

    private async Task RunSeriesAsync(ScanOptions scan, ContinuousOptions continuous, CancellationTokenSource cancellation)
    {
        string? error = null;
        try
        {
            await foreach (var spectrum in device.AcquireSeriesAsync(MeasurementKind.Sample, scan, continuous, cancellationToken: cancellation.Token))
                lock (sync)
                {
                    Add(spectrum);
                    series = series! with { Completed = series.Completed + 1 };
                }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException) { error = ex.Message; }
        finally
        {
            lock (sync)
            {
                series = series! with { Running = false, Error = error };
                active = null;
            }
            cancellation.Dispose();
            gate.Release();
        }
    }

    public void Cancel() { lock (sync) active?.Cancel(); }
    public Spectrum Get(Guid id) { lock (sync) return history.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException("Spectrum not found or expired from the 100-scan session."); }
    public object[] History()
    {
        lock (sync) return order.Reverse().Select(id => history[id]).Select(s => (object)new
        { s.Id, s.Kind, s.CapturedAt, points = s.Count, s.IsSimulated }).ToArray();
    }
    public Spectrum Process(Guid id, ProcessRequest request)
    {
        lock (sync)
        {
            var source = Get(id);
            var result = request.Operation switch
            {
                // Devices without a dark scan (Si-Ware FT sensors) use reference-only absorbance.
                "absorbance" => device.Device.IsSimulated
                    ? SpectralProcessing.Absorbance(source,
                        dark ?? throw new InvalidOperationException("Acquire a dark scan first."),
                        reference ?? throw new InvalidOperationException("Acquire a reference scan first."))
                    : SpectralProcessing.Absorbance(source, reference ?? throw new InvalidOperationException("Acquire a reference scan first.")),
                "snv" => NativeDsp.IsAvailable ? NativeDsp.StandardNormalVariate(source) : SpectralProcessing.StandardNormalVariate(source),
                "smooth" => NativeDsp.IsAvailable ? NativeDsp.Smooth(source, request.Window) : SpectralProcessing.Smooth(source, request.Window),
                _ => throw new ArgumentException("Unknown processing operation.")
            };
            Add(result); return result;
        }
    }
    private void Add(Spectrum spectrum)
    {
        history.Add(spectrum.Id, spectrum); order.Enqueue(spectrum.Id);
        while (order.Count > 100) history.Remove(order.Dequeue());
    }
    public async ValueTask DisposeAsync() { Cancel(); await device.DisposeAsync(); }
}
public sealed record AcquireRequest(MeasurementKind Kind = MeasurementKind.Sample, int Points = 257, int IntegrationMilliseconds = 100, int Averages = 1);
public sealed record ProcessRequest(string Operation, int Window = 5);
public sealed record SeriesRequest(int Measurements = 10, double IntervalSeconds = 5, int Points = 257, int IntegrationMilliseconds = 100, int Averages = 1);
public sealed record SeriesProgress(bool Running, int Completed, int Total, double IntervalSeconds, string? Error);
