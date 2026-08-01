using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;
using Unitree.Net.Dds.Transports;

namespace Unitree.Net.Dds;

/// <summary>
/// A pure-managed UDP multicast transport with no native dependency.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is not RTPS.</strong> It carries CDR payloads inside Unitree.Net's own framing, so it can
/// talk to another Unitree.Net process — a simulator, a bridge, a recorded-trace replayer — but not to
/// robot firmware. Use <c>CycloneNative</c> for that.
/// </para>
/// <para>
/// The frame is: a four-byte magic, a version byte, a two-byte topic length, the UTF-8 topic name, then
/// the CDR payload. Topic names travel on the wire because a single multicast group carries every topic;
/// that costs a few dozen bytes per frame and removes the need for per-topic port allocation.
/// </para>
/// </remarks>
public sealed class ManagedMulticastTransport : IDdsTransport
{
    /// <summary>Frame magic: ASCII <c>UNET</c>.</summary>
    private static ReadOnlySpan<byte> FrameMagic => "UNET"u8;

    private const byte ProtocolVersion = 1;
    private const int HeaderSize = 4 + 1 + 2;
    private const int MaxDatagramSize = 65507;

    private readonly UnitreeOptions _options;
    private readonly ILogger<ManagedMulticastTransport> _logger;
    private readonly SubscriptionRegistry _registry = new();
    private readonly IPEndPoint _groupEndPoint;

    private Socket? _sendSocket;
    private Socket? _receiveSocket;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private long _publishedCount;
    private long _receivedCount;
    private long _malformedFrameCount;
    private bool _disposed;

    /// <summary>Creates a transport from <paramref name="options"/>.</summary>
    public ManagedMulticastTransport(UnitreeOptions options, ILogger<ManagedMulticastTransport>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _logger = logger ?? NullLogger<ManagedMulticastTransport>.Instance;
        _groupEndPoint = new IPEndPoint(IPAddress.Parse(options.MulticastAddress), options.MulticastPort);
    }

    /// <inheritdoc />
    public string Name => $"managed-multicast[{_options.MulticastAddress}:{_options.MulticastPort}]";

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <summary>Total frames sent.</summary>
    public long PublishedCount => Interlocked.Read(ref _publishedCount);

    /// <summary>Total frames received and dispatched.</summary>
    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    /// <summary>Frames discarded because the header was not recognised.</summary>
    public long MalformedFrameCount => Interlocked.Read(ref _malformedFrameCount);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        IPAddress localAddress = ResolveLocalAddress();

        try
        {
            _sendSocket = CreateSendSocket(localAddress);
            _receiveSocket = CreateReceiveSocket(localAddress);
        }
        catch (SocketException ex)
        {
            throw new UnitreeConnectionException(
                $"Failed to open multicast sockets on {localAddress} for group {_options.MulticastAddress}:{_options.MulticastPort}. " +
                "On a corporate network, multicast is frequently filtered; see docs/dds-networking.md.",
                ex);
        }

        _receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCancellation.Token), CancellationToken.None);

        IsRunning = true;
        _logger.LogInformation(
            "Managed multicast transport started on {LocalAddress}, group {Group}:{Port}.",
            localAddress,
            _options.MulticastAddress,
            _options.MulticastPort);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;

        if (_receiveCancellation is not null)
        {
            await _receiveCancellation.CancelAsync().ConfigureAwait(false);
        }

        // Closing the socket is what actually unblocks a pending ReceiveFromAsync; cancellation alone
        // leaves the loop parked in the kernel until a datagram happens to arrive.
        _receiveSocket?.Close();
        _sendSocket?.Close();

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                _logger.LogDebug("Receive loop did not drain cleanly during stop.");
            }
        }

        _receiveTask = null;
        _receiveCancellation?.Dispose();
        _receiveCancellation = null;
        _receiveSocket?.Dispose();
        _receiveSocket = null;
        _sendSocket?.Dispose();
        _sendSocket = null;

        _logger.LogInformation("Managed multicast transport stopped.");
    }

    /// <inheritdoc />
    public void Publish(string topic, ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Socket? socket = _sendSocket;

        if (socket is null || !IsRunning)
        {
            throw new UnitreeConnectionException("Cannot publish: the multicast transport is not running.");
        }

        int topicByteCount = Encoding.UTF8.GetByteCount(topic);
        int frameSize = HeaderSize + topicByteCount + payload.Length;

        if (frameSize > MaxDatagramSize)
        {
            throw new UnitreeException(
                $"Frame for topic '{topic}' is {frameSize} bytes, over the {MaxDatagramSize}-byte datagram limit. " +
                "Large payloads such as point clouds need the native transport, which fragments them.");
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(frameSize);

        try
        {
            Span<byte> frame = rented.AsSpan(0, frameSize);
            FrameMagic.CopyTo(frame);
            frame[4] = ProtocolVersion;
            BinaryPrimitives.WriteUInt16LittleEndian(frame[5..], (ushort)topicByteCount);
            Encoding.UTF8.GetBytes(topic, frame[HeaderSize..]);
            payload.CopyTo(frame[(HeaderSize + topicByteCount)..]);

            socket.SendTo(frame, SocketFlags.None, _groupEndPoint);
            Interlocked.Increment(ref _publishedCount);
        }
        catch (SocketException ex)
        {
            throw new UnitreeConnectionException($"Failed to send on topic '{topic}'.", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(string topic, DdsPayloadHandler handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(handler);
        return _registry.Add(topic, handler);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        Socket socket = _receiveSocket!;
        byte[] buffer = GC.AllocateArray<byte>(MaxDatagramSize, pinned: true);
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            int received;

            try
            {
                SocketReceiveFromResult result =
                    await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken)
                        .ConfigureAwait(false);
                received = result.ReceivedBytes;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Multicast receive failed; continuing.");
                continue;
            }

            DispatchFrame(buffer.AsSpan(0, received));
        }
    }

    private void DispatchFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderSize ||
            !frame[..4].SequenceEqual(FrameMagic) ||
            frame[4] != ProtocolVersion)
        {
            Interlocked.Increment(ref _malformedFrameCount);
            return;
        }

        int topicLength = BinaryPrimitives.ReadUInt16LittleEndian(frame[5..]);

        if (frame.Length < HeaderSize + topicLength)
        {
            Interlocked.Increment(ref _malformedFrameCount);
            return;
        }

        string topic = Encoding.UTF8.GetString(frame.Slice(HeaderSize, topicLength));

        // Skip the dictionary lookup and the string allocation cost for topics nobody wants. On a busy
        // group this discards the majority of frames before any decoding work happens.
        if (!_registry.HasSubscribers(topic))
        {
            return;
        }

        ReadOnlySpan<byte> payload = frame[(HeaderSize + topicLength)..];

        try
        {
            _registry.Dispatch(topic, payload);
            Interlocked.Increment(ref _receivedCount);
        }
        catch (Exception ex)
        {
            // A throwing handler must not take down the receive loop for every other topic.
            _logger.LogError(ex, "Handler for topic {Topic} threw.", topic);
        }
    }

    private Socket CreateSendSocket(IPAddress localAddress)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(localAddress, 0));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, _options.MulticastTimeToLive);

        // Loopback on: multiple Unitree.Net processes on one host (a simulator and a controller, say)
        // must be able to see each other's traffic.
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        return socket;
    }

    private Socket CreateReceiveSocket(IPAddress localAddress)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, _options.MulticastPort));
        socket.SetSocketOption(
            SocketOptionLevel.IP,
            SocketOptionName.AddMembership,
            new MulticastOption(_groupEndPoint.Address, localAddress));
        return socket;
    }

    /// <summary>
    /// Resolves the local IPv4 address to bind, from the configured interface name.
    /// </summary>
    /// <remarks>
    /// When no interface is configured this picks the first operational non-loopback IPv4 interface.
    /// That is a guess, and on a machine with both a robot link and a corporate LAN it is frequently the
    /// wrong one — which is why the choice is logged.
    /// </remarks>
    private IPAddress ResolveLocalAddress()
    {
        if (!string.IsNullOrWhiteSpace(_options.NetworkInterface))
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(nic.Name, _options.NetworkInterface, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(nic.Description, _options.NetworkInterface, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return unicast.Address;
                    }
                }
            }

            throw new UnitreeConnectionException(
                $"Network interface '{_options.NetworkInterface}' was not found, or has no IPv4 address. " +
                $"Available: {string.Join(", ", NetworkInterface.GetAllNetworkInterfaces().Select(n => n.Name))}.");
        }

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                !nic.SupportsMulticast)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    _logger.LogWarning(
                        "No network interface configured; auto-selected {Interface} ({Address}). Set Unitree:NetworkInterface to make this deterministic.",
                        nic.Name,
                        unicast.Address);
                    return unicast.Address;
                }
            }
        }

        _logger.LogWarning("No multicast-capable interface found; falling back to loopback.");
        return IPAddress.Loopback;
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
}
