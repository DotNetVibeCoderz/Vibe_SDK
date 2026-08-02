using System.Diagnostics;
using DepthAI.Backends;
using DepthAI.Devices;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Pipelines.Nodes;
using DepthAI.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DepthAI.Simulation;

/// <summary>Opsi backend simulasi.</summary>
public sealed record SimulationOptions
{
    /// <summary>Jumlah perangkat virtual yang dilaporkan.</summary>
    public int DeviceCount { get; init; } = 1;

    /// <summary>Seed adegan; seed sama menghasilkan urutan frame yang identik.</summary>
    public int Seed { get; init; } = 1337;

    /// <summary>Model perangkat virtual yang ditiru.</summary>
    public string DeviceName { get; init; } = "OAK-D-Pro (Simulated)";

    public static SimulationOptions Default { get; } = new();
}

/// <summary>
/// Backend yang menghasilkan data sintetis alih-alih berbicara dengan hardware.
/// </summary>
/// <remarks>
/// Ini bukan sekadar stub: paket yang dihasilkan melewati jalur kode yang sama persis
/// dengan data hardware — termasuk parser inferensi sungguhan, yang diberi tensor dalam
/// tata letak asli MobileNet-SSD atau YOLO sesuai metadata model. Artinya sample, template,
/// dan test benar-benar menguji SDK, bukan versi mainannya.
/// </remarks>
public sealed class SimulationBackend(
    SimulationOptions? options = null,
    ILogger<SimulationBackend>? logger = null) : IDepthAiBackend
{
    private readonly SimulationOptions _options = options ?? SimulationOptions.Default;
    private readonly ILogger _logger = logger ?? NullLogger<SimulationBackend>.Instance;

    public string Name => "simulation";

    public bool IsAvailable => true;

    public bool IsSimulation => true;

    public IReadOnlyList<DeviceInfo> EnumerateDevices()
    {
        var devices = new List<DeviceInfo>(_options.DeviceCount);

        for (var i = 0; i < _options.DeviceCount; i++)
        {
            devices.Add(new DeviceInfo
            {
                SerialNumber = $"SIM{i:D4}{_options.Seed:X4}",
                Name = _options.DeviceCount == 1 ? _options.DeviceName : $"{_options.DeviceName} #{i + 1}",
                ConnectionPath = $"sim://{i}",
                Protocol = DeviceProtocol.Usb,
                State = DeviceState.Available,
                FirmwareVersion = "simulated-3.0.0",
                UsbSpeed = UsbSpeed.SuperPlus,
                IsSimulated = true,
                Capabilities = new DeviceCapabilities
                {
                    ColorCameraCount = 1,
                    MonoCameraCount = 2,
                    SupportsStereoDepth = true,
                    HasImu = true,
                    ShaveCores = 16,
                    Sensors = new Dictionary<CameraSocket, string>
                    {
                        [CameraSocket.CamA] = "IMX378",
                        [CameraSocket.CamB] = "OV9282",
                        [CameraSocket.CamC] = "OV9282",
                    },
                },
            });
        }

        return devices;
    }

    public Task<IDeviceSession> OpenAsync(
        DeviceInfo device,
        DeviceOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        _logger.LogInformation("Membuka perangkat simulasi {Serial}.", device.SerialNumber);
        return Task.FromResult<IDeviceSession>(new SimulationSession(device, _options, _logger));
    }

    public void Dispose() { }
}

/// <summary>Sesi simulasi yang menjalankan loop pembangkitan frame.</summary>
internal sealed class SimulationSession(DeviceInfo info, SimulationOptions options, ILogger logger) : IDeviceSession
{
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DeviceInfo Info { get; } = info;

    public DeviceCapabilities Capabilities => Info.Capabilities;

    public bool IsRunning => _loop is { IsCompleted: false };

    public Task StartAsync(Pipeline pipeline, Action<DevicePacket> onPacket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(onPacket);

        if (IsRunning)
        {
            throw new InvalidOperationException("Pipeline sudah berjalan pada perangkat ini.");
        }

        pipeline.Validate(Capabilities).ThrowIfInvalid();

        var plan = SimulationPlan.Build(pipeline, options.Seed);
        _clock.Restart();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cts = cts;
        _loop = Task.Factory.StartNew(
            () => RunAsync(plan, onPacket, cts.Token),
            cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
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
                await loop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Jalur berhenti yang normal.
            }
        }

        cts?.Dispose();
        _clock.Stop();
    }

    public DeviceTelemetry ReadTelemetry() => new()
    {
        // Suhu naik perlahan lalu mendatar, meniru pemanasan perangkat sungguhan.
        ChipTemperatureCelsius = 38f + (float)Math.Min(14, _clock.Elapsed.TotalSeconds * 0.4),
        LeonCssUsagePercent = 22f,
        LeonMssUsagePercent = 31f,
        DdrUsedBytes = 140L * 1024 * 1024,
        DdrTotalBytes = 512L * 1024 * 1024,
    };

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    private async Task RunAsync(SimulationPlan plan, Action<DevicePacket> onPacket, CancellationToken cancellationToken)
    {
        var period = TimeSpan.FromSeconds(1.0 / Math.Max(1, plan.Fps));
        using var timer = new PeriodicTimer(period);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                plan.Scene.Advance();
                var timestamp = _clock.Elapsed;

                foreach (var packet in plan.GeneratePackets(timestamp))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    onPacket(packet);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Berhenti normal.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loop simulasi berhenti karena error.");
        }
    }
}
