using DepthAI.Backends;
using DepthAI.Devices;
using DepthAI.Inference;
using DepthAI.Interop;
using DepthAI.Pipelines;
using DepthAI.Pipelines.Nodes;
using DepthAI.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DepthAI;

/// <summary>
/// Perangkat OAK yang terbuka. Ini adalah tipe yang paling banyak dipakai aplikasi:
/// buka perangkat, jalankan pipeline, langgan stream keluarannya.
/// </summary>
/// <example>
/// <code>
/// await using var device = await DepthAiDevice.OpenAsync();
///
/// var pipeline = Pipeline.CreateBuilder()
///     .AddColorCamera("rgb", c =&gt; c.WithPreview(640, 480))
///     .StreamOut("rgb.preview", "video")
///     .Build(device.Capabilities);
///
/// await device.StartAsync(pipeline);
///
/// using var subscription = device.GetStream&lt;ImageFrame&gt;("video")
///     .Subscribe(frame =&gt; Console.WriteLine($"{frame.Width}x{frame.Height}"));
/// </code>
/// </example>
public sealed class DepthAiDevice : IAsyncDisposable
{
    private readonly IDepthAiBackend _backend;
    private readonly bool _ownsBackend;
    private readonly IDeviceSession _session;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FrameStream<Frame>> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPacketConverter> _converters = new(StringComparer.OrdinalIgnoreCase);

    private Pipeline? _pipeline;
    private int _disposed;

    private DepthAiDevice(IDepthAiBackend backend, bool ownsBackend, IDeviceSession session, ILogger logger)
    {
        _backend = backend;
        _ownsBackend = ownsBackend;
        _session = session;
        _logger = logger;
    }

    /// <summary>Deskriptor perangkat yang terbuka.</summary>
    public DeviceInfo Info => _session.Info;

    public DeviceCapabilities Capabilities => _session.Capabilities;

    /// <summary>True bila perangkat ini disintesis backend simulasi.</summary>
    public bool IsSimulated => _backend.IsSimulation;

    /// <summary>Pipeline yang sedang berjalan, null bila belum ada.</summary>
    public Pipeline? RunningPipeline => _pipeline;

    public bool IsRunning => _session.IsRunning;

    /// <summary>Dipicu saat konversi paket gagal; stream lain tetap berjalan.</summary>
    public event EventHandler<DeviceErrorEventArgs>? Error;

    /// <summary>
    /// Membuka perangkat pertama yang tersedia.
    /// </summary>
    public static async Task<DepthAiDevice> OpenAsync(
        DepthAiOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= DepthAiOptions.Default;
        var backend = DepthAi.CreateBackend(options);

        try
        {
            var devices = backend.EnumerateDevices();
            if (devices.Count == 0)
            {
                throw new DeviceNotFoundException(
                    backend.IsSimulation
                        ? "Backend simulasi tidak melaporkan perangkat. Naikkan SimulationOptions.DeviceCount."
                        : "Tidak ada perangkat OAK yang terdeteksi. Periksa kabel USB dan daya perangkat.");
            }

            return await OpenCoreAsync(backend, ownsBackend: true, devices[0], options, cancellationToken);
        }
        catch
        {
            backend.Dispose();
            throw;
        }
    }

    /// <summary>Membuka perangkat berdasarkan serial number.</summary>
    public static async Task<DepthAiDevice> OpenBySerialAsync(
        string serialNumber,
        DepthAiOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        options ??= DepthAiOptions.Default;
        var backend = DepthAi.CreateBackend(options);

        try
        {
            var device = backend.EnumerateDevices()
                .FirstOrDefault(d => string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeviceNotFoundException(
                    $"Tidak ada perangkat dengan serial '{serialNumber}'. "
                    + $"Perangkat terdeteksi: {string.Join(", ", backend.EnumerateDevices().Select(d => d.SerialNumber))}.");

            return await OpenCoreAsync(backend, ownsBackend: true, device, options, cancellationToken);
        }
        catch
        {
            backend.Dispose();
            throw;
        }
    }

    /// <summary>Membuka perangkat tertentu pada backend yang sudah ada — untuk skenario multi-perangkat.</summary>
    public static Task<DepthAiDevice> OpenAsync(
        IDepthAiBackend backend,
        DeviceInfo device,
        DepthAiOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        return OpenCoreAsync(backend, ownsBackend: false, device, options ?? DepthAiOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Mengunggah pipeline dan mulai streaming. Stream keluaran tersedia lewat
    /// <see cref="GetStream{T}"/> begitu metode ini kembali.
    /// </summary>
    public async Task StartAsync(Pipeline pipeline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        pipeline.Validate(Capabilities).ThrowIfInvalid();

        lock (_gate)
        {
            _pipeline = pipeline;
            BuildRoutes(pipeline);
        }

        await _session.StartAsync(pipeline, OnPacket, cancellationToken);
        _logger.LogInformation(
            "Pipeline dimulai pada {Device} dengan {NodeCount} node dan {StreamCount} stream keluaran.",
            Info.SerialNumber, pipeline.Nodes.Count, pipeline.OutputStreams.Count);
    }

    /// <summary>Menghentikan pipeline dan menutup semua stream.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _session.StopAsync(cancellationToken);

        lock (_gate)
        {
            foreach (var stream in _streams.Values)
            {
                stream.Complete();
            }

            _streams.Clear();
            _converters.Clear();
            _pipeline = null;
        }
    }

    /// <summary>
    /// Stream keluaran bertipe. Nama harus cocok dengan yang didaftarkan pipeline lewat
    /// <c>StreamOut</c>/<c>AddOutputStream</c>.
    /// </summary>
    /// <typeparam name="T">
    /// Tipe frame yang diharapkan: <see cref="ImageFrame"/>, <see cref="DepthFrame"/>,
    /// <see cref="DetectionFrame"/>, dan seterusnya.
    /// </typeparam>
    public IFrameStream<T> GetStream<T>(string name)
        where T : Frame
        => new TypedFrameStream<T>(GetStreamCore(name));

    /// <summary>Stream keluaran tanpa penyempitan tipe.</summary>
    public IFrameStream<Frame> GetStream(string name) => GetStreamCore(name);

    /// <summary>Membaca telemetri kesehatan perangkat.</summary>
    public DeviceTelemetry ReadTelemetry() => _session.ReadTelemetry();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal menghentikan pipeline saat dispose.");
        }

        await _session.DisposeAsync();

        if (_ownsBackend)
        {
            _backend.Dispose();
        }
    }

    private static async Task<DepthAiDevice> OpenCoreAsync(
        IDepthAiBackend backend,
        bool ownsBackend,
        DeviceInfo device,
        DepthAiOptions options,
        CancellationToken cancellationToken)
    {
        var logger = options.LoggerFactory?.CreateLogger<DepthAiDevice>()
            ?? (ILogger)NullLogger<DepthAiDevice>.Instance;

        var session = await backend.OpenAsync(device, options.DeviceOpen, cancellationToken);
        logger.LogInformation("Perangkat {Device} terbuka lewat backend {Backend}.", device, backend.Name);

        return new DepthAiDevice(backend, ownsBackend, session, logger);
    }

    private FrameStream<Frame> GetStreamCore(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            if (_streams.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var known = _pipeline is null
                ? "belum ada pipeline yang berjalan"
                : $"stream yang tersedia: {string.Join(", ", _pipeline.OutputStreams.Select(s => s.Name))}";

            throw new KeyNotFoundException($"Tidak ada output stream bernama '{name}' — {known}.");
        }
    }

    /// <summary>Membangun satu stream dan konverter paket untuk tiap keluaran pipeline.</summary>
    private void BuildRoutes(Pipeline pipeline)
    {
        _streams.Clear();
        _converters.Clear();

        foreach (var definition in pipeline.OutputStreams)
        {
            _streams[definition.Name] = new FrameStream<Frame>(definition.Name, _logger);

            var source = pipeline.ResolveOutput(definition.Source);
            _converters[definition.Name] = CreateConverter(source);
        }
    }

    private static IPacketConverter CreateConverter(NodeOutput source) => source.Node switch
    {
        // Node NN dengan model yang punya metadata keluarga memakai parser sungguhan;
        // tanpa itu, tensor diteruskan mentah agar bisa diproses sendiri di host.
        NeuralNetworkNode nn when source.Name is "detections" or "out" && nn.Model is { } model
            => new TensorConverter(model.CreateParser(), nn.ConfidenceThreshold),

        NeuralNetworkNode when source.Name is "detections" or "out"
            => new TensorConverter(new RawTensorParser(), null),

        StereoDepthNode when source.Name == "depth" => DepthConverter.Instance,

        _ => ImageConverter.Instance,
    };

    private void OnPacket(DevicePacket packet)
    {
        FrameStream<Frame>? stream;
        IPacketConverter? converter;

        lock (_gate)
        {
            _streams.TryGetValue(packet.StreamName, out stream);
            _converters.TryGetValue(packet.StreamName, out converter);
        }

        if (stream is null || converter is null)
        {
            // Paket untuk stream yang tidak dilanggan bukan error: pipeline bisa
            // menghasilkan lebih banyak keluaran daripada yang diminta aplikasi.
            return;
        }

        try
        {
            var frame = converter.Convert(packet);
            if (frame is not null)
            {
                stream.Publish(frame);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal mengubah paket dari stream {Stream}.", packet.StreamName);
            Error?.Invoke(this, new DeviceErrorEventArgs(packet.StreamName, ex));
        }
    }

    /// <summary>Mengubah paket mentah backend menjadi frame publik.</summary>
    private interface IPacketConverter
    {
        Frame? Convert(DevicePacket packet);
    }

    private sealed class ImageConverter : IPacketConverter
    {
        public static ImageConverter Instance { get; } = new();

        public Frame Convert(DevicePacket packet)
        {
            var frame = ImageFrame.CopyFrom(
                packet.Payload.Span,
                Math.Max(1, packet.Width),
                Math.Max(1, packet.Height),
                packet.Format == PixelFormat.Unknown ? PixelFormat.Bgr888 : packet.Format);

            return Stamp(frame, packet);
        }
    }

    private sealed class DepthConverter : IPacketConverter
    {
        public static DepthConverter Instance { get; } = new();

        public Frame Convert(DevicePacket packet)
        {
            var frame = DepthFrame.CopyFrom(packet.Payload.Span, packet.Width, packet.Height);
            return Stamp(frame, packet);
        }
    }

    /// <summary>Menyalin metadata paket ke frame yang baru dibuat.</summary>
    private static Frame Stamp(Frame frame, DevicePacket packet)
    {
        frame.SequenceNumber = packet.SequenceNumber;
        frame.DeviceTimestamp = packet.DeviceTimestamp;
        frame.StreamName = packet.StreamName;
        return frame;
    }

    private sealed class TensorConverter(IInferenceParser parser, float? confidenceOverride) : IPacketConverter
    {
        public Frame? Convert(DevicePacket packet)
        {
            if (packet.Tensors is not { Count: > 0 } tensors)
            {
                return null;
            }

            return parser.Parse(tensors, new InferenceContext
            {
                SourceWidth = packet.Width,
                SourceHeight = packet.Height,
                SequenceNumber = packet.SequenceNumber,
                DeviceTimestamp = packet.DeviceTimestamp,
                StreamName = packet.StreamName,
                ConfidenceThreshold = confidenceOverride,
            });
        }
    }

}

/// <summary>Detail kegagalan pemrosesan stream.</summary>
public sealed class DeviceErrorEventArgs(string streamName, Exception exception) : EventArgs
{
    public string StreamName { get; } = streamName;

    public Exception Exception { get; } = exception;
}

/// <summary>Adapter yang menyempitkan stream <see cref="Frame"/> menjadi tipe konkret.</summary>
internal sealed class TypedFrameStream<T>(IFrameStream<Frame> inner) : IFrameStream<T>
    where T : Frame
{
    public string Name => inner.Name;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return inner.Subscribe(new CastingObserver(observer, Name));
    }

    private sealed class CastingObserver(IObserver<T> inner, string streamName) : IObserver<Frame>
    {
        public void OnNext(Frame value)
        {
            if (value is T typed)
            {
                inner.OnNext(typed);
                return;
            }

            // Tipe yang salah adalah kesalahan pemrograman (nama stream tertukar),
            // jadi dilaporkan sebagai error alih-alih didiamkan.
            inner.OnError(new InvalidCastException(
                $"Stream '{streamName}' memancarkan {value.GetType().Name}, bukan {typeof(T).Name}. "
                + "Periksa tipe yang diminta pada GetStream<T>()."));
        }

        public void OnError(Exception error) => inner.OnError(error);

        public void OnCompleted() => inner.OnCompleted();
    }
}
