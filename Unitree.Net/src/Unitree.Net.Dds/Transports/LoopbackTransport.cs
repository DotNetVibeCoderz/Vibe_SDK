using Unitree.Net.Dds.Transports;

namespace Unitree.Net.Dds;

/// <summary>
/// An in-process transport that delivers published payloads straight to local subscribers.
/// </summary>
/// <remarks>
/// <para>
/// Intended for unit tests and for simulating a robot in the same process: publish a
/// <c>rt/lowstate</c> payload and a subscriber in the same process sees it immediately, with no
/// sockets, no network configuration and no timing variability.
/// </para>
/// <para>
/// Delivery is synchronous on the publishing thread. A slow handler therefore blocks the publisher,
/// which is the opposite of how the real transports behave — keep test handlers cheap.
/// </para>
/// </remarks>
public sealed class LoopbackTransport : IDdsTransport
{
    private readonly SubscriptionRegistry _registry = new();
    private long _publishedCount;
    private bool _disposed;

    /// <inheritdoc />
    public string Name => "loopback";

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <summary>Total payloads published, regardless of whether anyone was subscribed.</summary>
    public long PublishedCount => Interlocked.Read(ref _publishedCount);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsRunning = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Publish(string topic, ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _publishedCount);
        _registry.Dispatch(topic, payload);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(string topic, DdsPayloadHandler handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(handler);
        return _registry.Add(topic, handler);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        IsRunning = false;
        return ValueTask.CompletedTask;
    }
}
