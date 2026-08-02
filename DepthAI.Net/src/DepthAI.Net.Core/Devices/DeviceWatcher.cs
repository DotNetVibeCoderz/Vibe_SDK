using DepthAI.Backends;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DepthAI.Devices;

/// <summary>Data kejadian hotplug.</summary>
public sealed class DeviceEventArgs(DeviceInfo device) : EventArgs
{
    public DeviceInfo Device { get; } = device;
}

/// <summary>
/// Memantau perangkat OAK yang dicolok dan dicabut, lalu melaporkannya sebagai event .NET.
/// </summary>
/// <remarks>
/// Deteksi dilakukan dengan polling terjadwal, bukan notifikasi OS. depthai-core tidak
/// memaparkan sinyal hotplug lintas platform, dan enumerasi USB per platform akan
/// menambah tiga jalur kode berbeda demi latensi yang tidak dibutuhkan aplikasi vision.
/// Ubah <see cref="PollInterval"/> bila perlu lebih responsif.
/// </remarks>
public sealed class DeviceWatcher : IAsyncDisposable
{
    private readonly IDepthAiBackend _backend;
    private readonly bool _ownsBackend;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private Dictionary<string, DeviceInfo> _known = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DeviceWatcher(DepthAiOptions? options = null)
        : this(DepthAi.CreateBackend(options), ownsBackend: true, options?.LoggerFactory)
    {
    }

    public DeviceWatcher(IDepthAiBackend backend, bool ownsBackend = false, ILoggerFactory? loggerFactory = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _ownsBackend = ownsBackend;
        _logger = loggerFactory?.CreateLogger<DeviceWatcher>() ?? (ILogger)NullLogger<DeviceWatcher>.Instance;
    }

    /// <summary>Jeda antar pemindaian.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Perangkat baru terdeteksi.</summary>
    public event EventHandler<DeviceEventArgs>? DeviceConnected;

    /// <summary>Perangkat yang sebelumnya terlihat kini hilang.</summary>
    public event EventHandler<DeviceEventArgs>? DeviceDisconnected;

    /// <summary>Perangkat masih ada tapi statusnya berubah, misal dari Available ke InUse.</summary>
    public event EventHandler<DeviceEventArgs>? DeviceStateChanged;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Snapshot perangkat yang saat ini diketahui.</summary>
    public IReadOnlyCollection<DeviceInfo> KnownDevices
    {
        get
        {
            lock (_gate)
            {
                return [.. _known.Values];
            }
        }
    }

    /// <summary>
    /// Mulai memantau. Perangkat yang sudah terpasang saat ini juga dilaporkan lewat
    /// <see cref="DeviceConnected"/>, sehingga pemanggil tidak perlu menangani
    /// enumerasi awal secara terpisah.
    /// </summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _loop = Task.Run(() => RunAsync(cts.Token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        var loop = Interlocked.Exchange(ref _loop, null);

        if (cts is not null)
        {
            await cts.CancelAsync();
        }

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Jalur berhenti yang normal.
            }
        }

        cts?.Dispose();
    }

    /// <summary>Memindai sekali sekarang, tanpa menunggu tick berikutnya.</summary>
    public void ScanNow() => Scan();

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        if (_ownsBackend)
        {
            _backend.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        Scan();

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Scan();
            }
        }
        catch (OperationCanceledException)
        {
            // Jalur berhenti yang normal.
        }
    }

    private void Scan()
    {
        IReadOnlyList<DeviceInfo> current;

        try
        {
            current = _backend.EnumerateDevices();
        }
        catch (Exception ex)
        {
            // Enumerasi bisa gagal sementara saat perangkat sedang boot; jangan
            // matikan pemantauan karena satu pemindaian yang gagal.
            _logger.LogWarning(ex, "Pemindaian perangkat gagal; akan dicoba lagi pada tick berikutnya.");
            return;
        }

        var snapshot = current.ToDictionary(static d => d.SerialNumber, StringComparer.OrdinalIgnoreCase);

        List<DeviceInfo> connected = [];
        List<DeviceInfo> disconnected = [];
        List<DeviceInfo> changed = [];

        lock (_gate)
        {
            foreach (var (serial, device) in snapshot)
            {
                if (!_known.TryGetValue(serial, out var previous))
                {
                    connected.Add(device);
                }
                else if (previous.State != device.State)
                {
                    changed.Add(device);
                }
            }

            foreach (var (serial, device) in _known)
            {
                if (!snapshot.ContainsKey(serial))
                {
                    disconnected.Add(device);
                }
            }

            _known = snapshot;
        }

        // Event dipicu di luar lock supaya handler yang lambat tidak menahan pemindaian.
        foreach (var device in connected)
        {
            _logger.LogInformation("Perangkat terhubung: {Device}", device);
            DeviceConnected?.Invoke(this, new DeviceEventArgs(device));
        }

        foreach (var device in changed)
        {
            DeviceStateChanged?.Invoke(this, new DeviceEventArgs(device));
        }

        foreach (var device in disconnected)
        {
            _logger.LogInformation("Perangkat terputus: {Device}", device);
            DeviceDisconnected?.Invoke(this, new DeviceEventArgs(device));
        }
    }
}
