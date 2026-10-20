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
            insightModel = insight.Model
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
