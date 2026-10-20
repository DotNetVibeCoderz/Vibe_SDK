using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SiWare.Sdk;

/// <summary>Explicit hardware configuration; units and mode must come from the device's documentation.</summary>
public sealed record VendorBridgeOptions(string PythonExecutable, string WorkerScript, string DriverModule,
    string ScanTimeUnit, int Mode, bool ConfirmWavenumbersInInverseCentimeters,
    long? DeviceId = null, int TimeoutSeconds = 60);

/// <summary>Windows vendor-driver adapter, isolated from .NET in a CPython 3.10 process.</summary>
public sealed class VendorSpectrometer(VendorBridgeOptions options) : ISpectrometer
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private Task? drainTask;
    private long sequence;
    private long? selectedId;
    private bool disposed;
    public DeviceInfo Device { get; private set; } = new("vendor", "Si-Ware vendor driver", "Vendor / network", false);
    public bool IsConnected
    {
        get
        {
            // Status polling may overlap process cleanup after a cancellation.
            var worker = process;
            try { return worker != null && selectedId.HasValue && !worker.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsConnected) return;
            await StopAsync().ConfigureAwait(false);
            ValidateOptions();
            var start = new ProcessStartInfo(options.PythonExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var value in new[] { "-u", options.WorkerScript, "--driver", options.DriverModule,
                "--wavenumber-unit", "cm-1", "--scan-time-unit", options.ScanTimeUnit, "--mode", options.Mode.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(value);
            process = Process.Start(start) ?? throw new IOException("Could not start vendor worker.");
            // Drain without retaining vendor logs, which can contain local configuration.
            drainTask = DrainAsync(process.StandardError);
            var result = await RequestAsync(new { id = ++sequence, op = "discover" }, cancellationToken).ConfigureAwait(false);
            foreach (var device in result.EnumerateArray())
            {
                var id = device.GetProperty("id").GetInt64();
                if (options.DeviceId is { } requested && requested != id) continue;
                selectedId = id;
                Device = new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), device.GetProperty("name").GetString() ?? "Si-Ware sensor", "Vendor / network", false);
                return;
            }
            throw new IOException("No matching Si-Ware sensor discovered. Check vendor drivers, USB network configuration, and device power.");
        }
        catch { await StopAsync().ConfigureAwait(false); throw; }
        finally { gate.Release(); }
    }

    public async Task<Spectrum> AcquireAsync(MeasurementKind kind, ScanOptions scan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan); scan.Validate();
        if (kind is not (MeasurementKind.Sample or MeasurementKind.Reference))
            throw new NotSupportedException("Vendor adapter supports PSD sample and background/reference. Dark acquisition is not verified.");
        if (scan.Points is not (65 or 129 or 257 or 513 or 1024 or 2048 or 4096))
            throw new ArgumentOutOfRangeException(nameof(scan), "Vendor common-wavenumber grid is unsupported.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsConnected) throw new InvalidOperationException("Connect to a vendor device first.");
            var result = await RequestAsync(new
            {
                id = ++sequence,
                op = "acquire",
                kind = kind.ToString(),
                points = scan.Points,
                integrationMilliseconds = scan.IntegrationMilliseconds,
                averages = scan.Averages,
                deviceId = selectedId
            }, cancellationToken).ConfigureAwait(false);
            if (result.GetProperty("wavenumberUnit").GetString() != "cm-1") throw new IOException("Unexpected spectral axis unit.");
            if (result.GetProperty("deviceId").GetString() != Device.Id) throw new IOException("Worker returned a different sensor identity.");
            if (result.GetProperty("kind").GetString() != kind.ToString()) throw new IOException("Worker returned a different measurement kind.");
            var wavenumbers = result.GetProperty("wavenumbers").Deserialize<double[]>() ?? throw new IOException("Missing spectral axis.");
            var values = result.GetProperty("values").Deserialize<double[]>() ?? throw new IOException("Missing spectrum values.");
            if (wavenumbers.Length != scan.Points || values.Length != scan.Points) throw new IOException("Unexpected spectrum length.");
            var pairs = wavenumbers.Select((w, i) => (nm: 10_000_000 / w, value: values[i])).OrderBy(p => p.nm).ToArray();
            return new(Guid.NewGuid(), Device.Id, DateTimeOffset.UtcNow, kind, false,
                pairs.Select(p => p.nm).ToArray(), pairs.Select(p => p.value).ToArray(), scan);
        }
        catch { await StopAsync().ConfigureAwait(false); throw; }
        finally { gate.Release(); }
    }

    private async Task<JsonElement> RequestAsync(object request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var worker = process ?? throw new IOException("Vendor worker not running.");
        await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token).ConfigureAwait(false);
        await worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
        var line = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            var count = await worker.StandardOutput.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
            if (count == 0) throw new IOException("Vendor worker exited without a response. Check CPython 3.10 x64 and the installed module.");
            if (buffer[0] == '\n') break;
            if (line.Length >= 1_048_576) throw new IOException("Vendor response exceeds 1 MiB.");
            line.Append(buffer[0]);
        }
        using var document = JsonDocument.Parse(line.ToString());
        var root = document.RootElement;
        if (root.GetProperty("id").GetInt64() != sequence) throw new IOException("Vendor response correlation mismatch.");
        if (!root.GetProperty("ok").GetBoolean()) throw new IOException(root.GetProperty("error").GetString() ?? "Vendor request failed.");
        return root.GetProperty("result").Clone();
    }

    private void ValidateOptions()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The inspected vendor module requires Windows x64.");
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Use an x64 process.");
        foreach (var path in new[] { options.PythonExecutable, options.WorkerScript, options.DriverModule })
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new FileNotFoundException("Configure an existing absolute vendor bridge path.", path);
        if (options.ScanTimeUnit is not ("seconds" or "milliseconds") || !options.ConfirmWavenumbersInInverseCentimeters)
            throw new ArgumentException("Explicitly confirm vendor scan-time units, mode, and cm^-1 wavenumber units.");
        if (options.TimeoutSeconds is < 6 or > 600) throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be 6–600 seconds.");
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    private async Task StopAsync()
    {
        selectedId = null;
        if (process is not { } worker) return;
        process = null;
        try
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().ConfigureAwait(false);
            if (drainTask != null) await drainTask.ConfigureAwait(false);
        }
        finally { worker.Dispose(); drainTask = null; }
    }
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; await StopAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
