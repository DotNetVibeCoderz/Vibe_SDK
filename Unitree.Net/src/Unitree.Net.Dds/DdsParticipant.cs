using System.Buffers;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;

namespace Unitree.Net.Dds;

/// <summary>
/// Creates typed publishers and subscribers over an <see cref="IDdsTransport"/>.
/// </summary>
public sealed class DdsParticipant : IDdsParticipant
{
    private readonly ILogger<DdsParticipant> _logger;
    private readonly List<IDisposable> _endpoints = [];
    private readonly Lock _endpointLock = new();
    private bool _disposed;

    /// <summary>Creates a participant over <paramref name="transport"/>.</summary>
    public DdsParticipant(IDdsTransport transport, ILogger<DdsParticipant>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        Transport = transport;
        _logger = logger ?? NullLogger<DdsParticipant>.Instance;
    }

    /// <inheritdoc />
    public IDdsTransport Transport { get; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        Transport.StartAsync(cancellationToken);

    /// <inheritdoc />
    public IDdsPublisher<T> CreatePublisher<T>(string topic)
        where T : ICdrSerializable<T>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        var publisher = new DdsPublisher<T>(Transport, topic);
        Track(publisher);
        _logger.LogDebug("Created publisher for {Topic} ({TypeName}).", topic, T.DdsTypeName);
        return publisher;
    }

    /// <inheritdoc />
    public IDdsSubscriber<T> CreateSubscriber<T>(string topic, int queueCapacity = 256)
        where T : ICdrSerializable<T>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);

        var subscriber = new DdsSubscriber<T>(Transport, topic, queueCapacity, _logger);
        Track(subscriber);
        _logger.LogDebug("Created subscriber for {Topic} ({TypeName}).", topic, T.DdsTypeName);
        return subscriber;
    }

    private void Track(IDisposable endpoint)
    {
        lock (_endpointLock)
        {
            _endpoints.Add(endpoint);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        IDisposable[] endpoints;
        lock (_endpointLock)
        {
            endpoints = [.. _endpoints];
            _endpoints.Clear();
        }

        foreach (IDisposable endpoint in endpoints)
        {
            endpoint.Dispose();
        }

        await Transport.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Encodes messages to CDR and hands them to a transport.
/// </summary>
/// <typeparam name="T">The message type.</typeparam>
internal sealed class DdsPublisher<T> : IDdsPublisher<T>
    where T : ICdrSerializable<T>
{
    // Above this size, renting from the pool beats a stack allocation. The threshold is well under the
    // default 1 MB thread stack, so the common Unitree messages (LowCmd at 816 bytes) stay on the stack.
    private const int StackAllocThreshold = 2048;

    private readonly IDdsTransport _transport;
    private long _publishedCount;
    private bool _disposed;

    internal DdsPublisher(IDdsTransport transport, string topic)
    {
        _transport = transport;
        Topic = topic;
    }

    public string Topic { get; }

    public long PublishedCount => Interlocked.Read(ref _publishedCount);

    public void Publish(in T message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int maxSize = T.MaxSerializedSize;

        if (maxSize <= StackAllocThreshold)
        {
            Span<byte> buffer = stackalloc byte[StackAllocThreshold];
            int written = message.Serialize(buffer);
            _transport.Publish(Topic, buffer[..written]);
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(maxSize);
            try
            {
                int written = message.Serialize(rented);
                _transport.Publish(Topic, rented.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        Interlocked.Increment(ref _publishedCount);
    }

    public void Dispose() => _disposed = true;
}

/// <summary>
/// Decodes CDR payloads from a transport into typed messages.
/// </summary>
/// <typeparam name="T">The message type.</typeparam>
internal sealed class DdsSubscriber<T> : IDdsSubscriber<T>
    where T : ICdrSerializable<T>
{
    private readonly Channel<T> _channel;
    private readonly IDisposable _subscription;
    private readonly ILogger _logger;
    private readonly Lock _latestLock = new();
    private readonly int _capacity;

    private T _latest = default!;
    private bool _hasLatest;
    private long _receivedCount;
    private long _droppedCount;
    private long _malformedCount;
    private long _lastReceivedTicks;
    private bool _disposed;

    internal DdsSubscriber(IDdsTransport transport, string topic, int queueCapacity, ILogger logger)
    {
        Topic = topic;
        _logger = logger;
        _capacity = queueCapacity;

        // DropOldest, not Wait: a control loop that stalls must never apply backpressure onto the
        // receive path, because that would delay every other topic sharing the transport.
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });

        _subscription = transport.Subscribe(topic, OnPayload);
    }

    public string Topic { get; }

    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public long MalformedCount => Interlocked.Read(ref _malformedCount);

    public DateTimeOffset? LastReceivedAt
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastReceivedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public ChannelReader<T> Reader => _channel.Reader;

    public bool TryGetLatest(out T message)
    {
        lock (_latestLock)
        {
            message = _latest;
            return _hasLatest;
        }
    }

    private void OnPayload(ReadOnlySpan<byte> payload)
    {
        T message;

        try
        {
            message = T.Deserialize(payload);
        }
        catch (Exception ex) when (ex is CdrFormatException or ArgumentException)
        {
            // Malformed frames are counted rather than thrown: on a shared multicast group, traffic from
            // an unrelated publisher on the same topic name is a configuration problem, not a crash.
            Interlocked.Increment(ref _malformedCount);
            _logger.LogDebug(ex, "Discarded malformed payload on {Topic}.", Topic);
            return;
        }

        lock (_latestLock)
        {
            _latest = message;
            _hasLatest = true;
        }

        Interlocked.Increment(ref _receivedCount);
        Interlocked.Exchange(ref _lastReceivedTicks, DateTimeOffset.UtcNow.UtcTicks);

        // A DropOldest channel always accepts the write, so the return value cannot report loss.
        // Sampling the depth first is what makes DroppedCount a real signal that a consumer is behind.
        if (_channel.Reader.Count >= _capacity)
        {
            Interlocked.Increment(ref _droppedCount);
        }

        _channel.Writer.TryWrite(message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription.Dispose();
        _channel.Writer.TryComplete();
    }
}
