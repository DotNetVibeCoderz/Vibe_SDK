using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Messages;

namespace Unitree.Net.Interop;

/// <summary>
/// A wire-compatible DDS transport backed by Cyclone DDS through the native shim.
/// </summary>
/// <remarks>
/// <para>
/// This is the transport to use against real hardware. It requires the <c>unitree_net_native</c> shared
/// library on the load path; see <c>native/README.md</c>.
/// </para>
/// <para>
/// Because Unitree firmware permits only one controlling SDK instance per robot, creating a second
/// transport against the same robot will produce undefined behaviour rather than a clean error. Treat
/// the transport as a process-wide singleton.
/// </para>
/// </remarks>
public sealed class CycloneDdsTransport : IDdsTransport
{
    private readonly UnitreeOptions _options;
    private readonly ILogger<CycloneDdsTransport> _logger;
    private readonly ConcurrentDictionary<string, int> _writerHandles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReaderRegistration> _readers = new(StringComparer.Ordinal);
    private readonly Lock _lifecycleLock = new();

    // The delegate must be rooted for as long as native code can invoke it. Letting it be collected
    // produces a callback into freed memory, which surfaces as a process-level crash with no managed
    // stack — one of the harder interop failures to diagnose after the fact.
    private readonly NativeMessageCallback _callbackDelegate;
    private readonly nint _callbackPointer;

    private bool _initialised;
    private bool _disposed;

    /// <summary>Creates a transport from <paramref name="options"/>.</summary>
    public unsafe CycloneDdsTransport(UnitreeOptions options, ILogger<CycloneDdsTransport>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _logger = logger ?? NullLogger<CycloneDdsTransport>.Instance;

        _callbackDelegate = OnNativeMessage;
        _callbackPointer = Marshal.GetFunctionPointerForDelegate(_callbackDelegate);
    }

    /// <inheritdoc />
    public string Name => $"cyclone-native[domain={_options.DomainId}]";

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <summary>Gets the native shim version, or an empty string when the library is unavailable.</summary>
    public static string GetNativeVersion()
    {
        try
        {
            unsafe
            {
                return NativeMethods.ReadUtf8(NativeMethods.Version());
            }
        }
        catch (DllNotFoundException)
        {
            return string.Empty;
        }
        catch (EntryPointNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>Whether the native shim can be loaded in this process.</summary>
    public static bool IsNativeLibraryAvailable() => GetNativeVersion().Length > 0;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            string? interfaceName = string.IsNullOrWhiteSpace(_options.NetworkInterface)
                ? null
                : _options.NetworkInterface;

            int status;

            try
            {
                status = NativeMethods.Init(_options.DomainId, interfaceName);
            }
            catch (DllNotFoundException ex)
            {
                throw new UnitreeConnectionException(
                    $"The native library '{NativeMethods.LibraryName}' could not be loaded. Build it as described in " +
                    "native/README.md, or switch Unitree:Transport to ManagedMulticast for host-only development.",
                    ex);
            }

            ThrowIfFailed(status, "initialise the DDS participant");

            _initialised = true;
            IsRunning = true;
        }

        _logger.LogInformation(
            "Cyclone DDS transport started on domain {DomainId} via interface '{Interface}' (shim {Version}).",
            _options.DomainId,
            string.IsNullOrWhiteSpace(_options.NetworkInterface) ? "<auto>" : _options.NetworkInterface,
            GetNativeVersion());

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
            {
                return Task.CompletedTask;
            }

            IsRunning = false;

            foreach (ReaderRegistration registration in _readers.Values)
            {
                NativeMethods.DestroyEndpoint(registration.Handle);
            }

            _readers.Clear();

            foreach (int handle in _writerHandles.Values)
            {
                NativeMethods.DestroyEndpoint(handle);
            }

            _writerHandles.Clear();

            if (_initialised)
            {
                NativeMethods.Shutdown();
                _initialised = false;
            }
        }

        _logger.LogInformation("Cyclone DDS transport stopped.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public unsafe void Publish(string topic, ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsRunning)
        {
            throw new UnitreeConnectionException("Cannot publish: the Cyclone DDS transport is not running.");
        }

        int handle = _writerHandles.GetOrAdd(topic, CreateWriter);

        fixed (byte* data = payload)
        {
            int status = NativeMethods.Write(handle, data, payload.Length);
            ThrowIfFailed(status, $"publish {payload.Length} bytes on '{topic}'");
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(string topic, DdsPayloadHandler handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(handler);

        if (!IsRunning)
        {
            throw new UnitreeConnectionException("Cannot subscribe: the Cyclone DDS transport is not running.");
        }

        ReaderRegistration registration = _readers.GetOrAdd(topic, CreateReader);
        registration.Add(handler);

        return new SubscriptionToken(registration, handler);
    }

    private int CreateWriter(string topic)
    {
        string typeName = ResolveTypeName(topic);
        int status = NativeMethods.CreateWriter(topic, typeName, out int handle);
        ThrowIfFailed(status, $"create a writer for '{topic}' ({typeName})");
        _logger.LogDebug("Created native writer {Handle} for {Topic} ({TypeName}).", handle, topic, typeName);
        return handle;
    }

    private ReaderRegistration CreateReader(string topic)
    {
        string typeName = ResolveTypeName(topic);
        var registration = new ReaderRegistration(topic);

        // The user-data pointer carries the topic identity back through the callback. A GCHandle keeps
        // the registration alive independently of the dictionary, so a concurrent Stop cannot free it
        // while native code is mid-dispatch.
        registration.SelfHandle = GCHandle.Alloc(registration, GCHandleType.Normal);
        nint userData = GCHandle.ToIntPtr(registration.SelfHandle);

        int status = NativeMethods.CreateReader(topic, typeName, _callbackPointer, userData, out int handle);

        if (status != (int)NativeStatus.Ok)
        {
            registration.SelfHandle.Free();
            ThrowIfFailed(status, $"create a reader for '{topic}' ({typeName})");
        }

        registration.Handle = handle;
        _logger.LogDebug("Created native reader {Handle} for {Topic} ({TypeName}).", handle, topic, typeName);
        return registration;
    }

    private unsafe void OnNativeMessage(byte* topic, byte* data, int length, nint userData)
    {
        // Nothing may escape this method as an exception: unwinding into the Cyclone DDS listener thread
        // is undefined behaviour and takes down the process.
        try
        {
            if (userData == 0 || data is null || length <= 0)
            {
                return;
            }

            var gcHandle = GCHandle.FromIntPtr(userData);

            if (gcHandle.Target is not ReaderRegistration registration)
            {
                return;
            }

            registration.Dispatch(new ReadOnlySpan<byte>(data, length));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native message callback threw and was suppressed.");
        }
    }

    /// <summary>
    /// Maps a topic name to the DDS type name the shim must register for it.
    /// </summary>
    private string ResolveTypeName(string topic) => topic switch
    {
        Topics.LowCommand => TopicResolver.GetLowCommandTypeName(_options.Model),
        Topics.LowState or Topics.LowStateLowFrequency => TopicResolver.GetLowStateTypeName(_options.Model),
        Topics.SportModeState or Topics.SportModeStateLowFrequency => "unitree_go::msg::dds_::SportModeState_",
        Topics.WirelessController => "unitree_go::msg::dds_::WirelessController_",
        Topics.LidarCloud => "sensor_msgs::msg::dds_::PointCloud2_",
        Topics.LidarImu => "sensor_msgs::msg::dds_::Imu_",
        _ when topic.EndsWith("/request", StringComparison.Ordinal) => "unitree_api::msg::dds_::Request_",
        _ when topic.EndsWith("/response", StringComparison.Ordinal) => "unitree_api::msg::dds_::Response_",
        _ => throw new UnitreeException(
            $"No DDS type is registered for topic '{topic}'. Add it to CycloneDdsTransport.ResolveTypeName " +
            "and to the descriptor registry in the native shim."),
    };

    private static void ThrowIfFailed(int status, string operation)
    {
        if (status == (int)NativeStatus.Ok)
        {
            return;
        }

        string detail;

        unsafe
        {
            detail = NativeMethods.ReadUtf8(NativeMethods.LastError());
        }

        string message = $"Failed to {operation}: {(NativeStatus)status}";
        throw new UnitreeConnectionException(
            detail.Length > 0 ? $"{message} — {detail}" : message + ".");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
    }

    /// <summary>
    /// One native reader plus the managed handlers fanned out from it.
    /// </summary>
    private sealed class ReaderRegistration(string topic)
    {
        private readonly Lock _lock = new();
        private DdsPayloadHandler[] _handlers = [];

        internal string Topic { get; } = topic;

        internal int Handle { get; set; }

        internal GCHandle SelfHandle { get; set; }

        internal void Add(DdsPayloadHandler handler)
        {
            lock (_lock)
            {
                _handlers = [.. _handlers, handler];
            }
        }

        internal void Remove(DdsPayloadHandler handler)
        {
            lock (_lock)
            {
                _handlers = [.. _handlers.Where(h => h != handler)];
            }
        }

        internal void Dispatch(ReadOnlySpan<byte> payload)
        {
            DdsPayloadHandler[] handlers;

            lock (_lock)
            {
                handlers = _handlers;
            }

            foreach (DdsPayloadHandler handler in handlers)
            {
                handler(payload);
            }
        }
    }

    private sealed class SubscriptionToken(ReaderRegistration registration, DdsPayloadHandler handler) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registration.Remove(handler);
        }
    }
}
