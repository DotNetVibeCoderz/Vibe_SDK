using System.Runtime.InteropServices;
using System.Text;
using DepthAI.Backends;
using DepthAI.Devices;
using DepthAI.Pipelines;
using DepthAI.Pipelines.Nodes;
using DepthAI.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DepthAI.Interop;

/// <summary>
/// Backend yang berbicara dengan hardware OAK sungguhan lewat shim native depthai-c.
/// </summary>
public sealed class NativeBackend(ILogger<NativeBackend>? logger = null) : IDepthAiBackend
{
    private const int MaxDevices = 32;

    private readonly ILogger _logger = logger ?? NullLogger<NativeBackend>.Instance;

    public string Name => "native";

    public bool IsAvailable => NativeRuntime.IsAvailable;

    public bool IsSimulation => false;

    public unsafe IReadOnlyList<DeviceInfo> EnumerateDevices()
    {
        EnsureAvailable();

        var buffer = new DaiDeviceInfo[MaxDevices];
        NativeRuntime.ThrowIfFailed(
            NativeMethods.ListDevices(buffer, MaxDevices, out var count),
            "Enumerasi perangkat");

        var devices = new List<DeviceInfo>(count);
        for (var i = 0; i < Math.Min(count, MaxDevices); i++)
        {
            devices.Add(Convert(ref buffer[i]));
        }

        return devices;
    }

    public async Task<IDeviceSession> OpenAsync(
        DeviceInfo device,
        DeviceOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        EnsureAvailable();

        var firmwarePath = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrEmpty(options.FirmwarePath))
            {
                firmwarePath = Marshal.StringToCoTaskMemUTF8(options.FirmwarePath);
            }

            var nativeOptions = new DaiOpenOptions
            {
                MaxUsbSpeed = (int)options.MaxUsbSpeed,
                BootTimeoutMs = (int)options.BootTimeout.TotalMilliseconds,
                FirmwarePath = firmwarePath,
            };

            // Boot perangkat bisa memakan puluhan detik; jangan menahan thread pemanggil.
            var handle = await Task.Run(
                () =>
                {
                    NativeRuntime.ThrowIfFailed(
                        NativeMethods.OpenDevice(device.SerialNumber, in nativeOptions, out var h),
                        $"Membuka perangkat {device.SerialNumber}");
                    return h;
                },
                cancellationToken);

            NativeRuntime.ThrowIfFailed(
                NativeMethods.GetCapabilities(handle, out var caps),
                "Membaca kemampuan perangkat");

            return new NativeSession(handle, device, Convert(caps), _logger);
        }
        finally
        {
            if (firmwarePath != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(firmwarePath);
            }
        }
    }

    public void Dispose() { }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new DepthAiException(
                $"Runtime native DepthAI tidak tersedia: {NativeRuntime.UnavailableReason}");
        }
    }

    private static unsafe DeviceInfo Convert(ref DaiDeviceInfo native)
    {
        fixed (byte* mxid = native.MxId)
        fixed (byte* name = native.Name)
        fixed (byte* path = native.ConnectionPath)
        fixed (byte* firmware = native.FirmwareVersion)
        {
            return new DeviceInfo
            {
                SerialNumber = ReadUtf8(mxid, 32),
                Name = ReadUtf8(name, 64),
                ConnectionPath = ReadUtf8(path, 128),
                FirmwareVersion = ReadUtf8(firmware, 32) is { Length: > 0 } fw ? fw : null,
                Protocol = (DeviceProtocol)native.Protocol,
                State = (DeviceState)native.State,
                UsbSpeed = (UsbSpeed)native.UsbSpeed,
            };
        }
    }

    private static DeviceCapabilities Convert(DaiCapabilities native) => new()
    {
        ColorCameraCount = native.ColorCameraCount,
        MonoCameraCount = native.MonoCameraCount,
        SupportsStereoDepth = native.SupportsStereoDepth != 0,
        HasImu = native.HasImu != 0,
        ShaveCores = native.ShaveCores,
    };

    private static unsafe string ReadUtf8(byte* pointer, int maxLength)
    {
        var length = 0;
        while (length < maxLength && pointer[length] != 0)
        {
            length++;
        }

        return length == 0 ? string.Empty : Encoding.UTF8.GetString(pointer, length);
    }
}

/// <summary>
/// Sesi perangkat native. Menjalankan satu thread polling khusus yang menarik paket dari
/// antrean native dan meneruskannya ke lapisan managed.
/// </summary>
internal sealed class NativeSession(
    IntPtr handle,
    DeviceInfo info,
    DeviceCapabilities capabilities,
    ILogger logger) : IDeviceSession
{
    private const int PollTimeoutMs = 100;

    private readonly Lock _gate = new();
    private IntPtr _handle = handle;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    public DeviceInfo Info { get; } = info;

    public DeviceCapabilities Capabilities { get; } = capabilities;

    public bool IsRunning => _pollTask is { IsCompleted: false };

    public Task StartAsync(Pipeline pipeline, Action<DevicePacket> onPacket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(onPacket);
        EnsureOpen();

        if (IsRunning)
        {
            throw new InvalidOperationException("Pipeline sudah berjalan pada perangkat ini.");
        }

        pipeline.Validate(Capabilities).ThrowIfInvalid();

        // Payload model harus diunggah sebelum pipeline mulai, karena node NN
        // merujuk model berdasarkan nama di dalam JSON pipeline.
        foreach (var node in pipeline.Nodes.OfType<NeuralNetworkNode>())
        {
            if (node.Model is { } model)
            {
                NativeRuntime.ThrowIfFailed(
                    NativeMethods.UploadModel(_handle, model.Name, model.Payload.Span, model.Payload.Length),
                    $"Mengunggah model '{model.Name}'");
            }
        }

        NativeRuntime.ThrowIfFailed(
            NativeMethods.StartPipeline(_handle, pipeline.ToJson(indented: false)),
            "Memulai pipeline");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollCts = cts;
        _pollTask = Task.Factory.StartNew(
            () => PollLoop(onPacket, cts.Token),
            cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var cts = Interlocked.Exchange(ref _pollCts, null);
        var task = Interlocked.Exchange(ref _pollTask, null);

        if (cts is not null)
        {
            await cts.CancelAsync();
        }

        if (task is not null)
        {
            try
            {
                await task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Pembatalan adalah cara normal loop polling berhenti.
            }
        }

        cts?.Dispose();

        if (_handle != IntPtr.Zero)
        {
            var status = NativeMethods.StopPipeline(_handle);
            if (status != (int)DaiStatus.Ok)
            {
                logger.LogWarning("Menghentikan pipeline mengembalikan {Status}: {Error}",
                    status, NativeRuntime.GetLastError());
            }
        }
    }

    public DeviceTelemetry ReadTelemetry()
    {
        EnsureOpen();

        if (NativeMethods.GetTelemetry(_handle, out var native) != (int)DaiStatus.Ok)
        {
            return DeviceTelemetry.Empty;
        }

        return new DeviceTelemetry
        {
            ChipTemperatureCelsius = native.ChipTemperatureCelsius,
            LeonCssUsagePercent = native.LeonCssUsagePercent,
            LeonMssUsagePercent = native.LeonMssUsagePercent,
            DdrUsedBytes = native.DdrUsedBytes,
            DdrTotalBytes = native.DdrTotalBytes,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);

        lock (_gate)
        {
            if (_handle != IntPtr.Zero)
            {
                var status = NativeMethods.CloseDevice(_handle);
                if (status != (int)DaiStatus.Ok)
                {
                    logger.LogWarning("Menutup perangkat mengembalikan {Status}.", status);
                }

                _handle = IntPtr.Zero;
            }
        }
    }

    private unsafe void PollLoop(Action<DevicePacket> onPacket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = NativeMethods.PollPacket(_handle, out var native, PollTimeoutMs);

            if (status == (int)DaiStatus.Timeout)
            {
                continue;
            }

            if (status != (int)DaiStatus.Ok)
            {
                logger.LogError("Polling paket gagal: {Error}", NativeRuntime.GetLastError());
                break;
            }

            try
            {
                onPacket(ConvertPacket(ref native));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Gagal memproses paket dari perangkat.");
            }
            finally
            {
                // Buffer native harus dikembalikan walau pemrosesan gagal, jika tidak
                // pool perangkat akan habis dan stream berhenti diam-diam.
                if (native.NativeHandle != IntPtr.Zero
                    && NativeMethods.ReleasePacket(_handle, native.NativeHandle) != (int)DaiStatus.Ok)
                {
                    logger.LogWarning("Gagal mengembalikan buffer paket ke pool native: {Error}",
                        NativeRuntime.GetLastError());
                }
            }
        }
    }

    /// <summary>
    /// Menyalin payload native ke memori managed. Salinan wajib: buffer native
    /// dikembalikan ke pool perangkat begitu poll selesai.
    /// </summary>
    private static unsafe DevicePacket ConvertPacket(ref DaiPacket native)
    {
        var length = (int)Math.Min(native.DataLength, int.MaxValue);
        var payload = new byte[length];

        if (native.Data != IntPtr.Zero && length > 0)
        {
            new ReadOnlySpan<byte>((void*)native.Data, length).CopyTo(payload);
        }

        string streamName;
        fixed (byte* name = native.StreamName)
        {
            var nameLength = 0;
            while (nameLength < 64 && name[nameLength] != 0)
            {
                nameLength++;
            }

            streamName = nameLength == 0 ? string.Empty : Encoding.UTF8.GetString(name, nameLength);
        }

        return new DevicePacket
        {
            StreamName = streamName,
            Kind = (DaiPacketType)native.Type switch
            {
                DaiPacketType.Depth => PacketKind.Depth,
                DaiPacketType.NeuralTensors => PacketKind.NeuralTensors,
                DaiPacketType.Encoded => PacketKind.Encoded,
                DaiPacketType.Imu => PacketKind.Imu,
                _ => PacketKind.Image,
            },
            Payload = payload,
            Width = native.Width,
            Height = native.Height,
            Format = (PixelFormat)native.PixelFormat,
            SequenceNumber = native.SequenceNumber,
            DeviceTimestamp = TimeSpan.FromTicks(native.TimestampNanos / 100),
        };
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
    }
}
