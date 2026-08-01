using System.Threading.Channels;
using Unitree.Net.Core;

namespace Unitree.Net.Dds;

/// <summary>
/// The raw byte-level transport beneath the typed DDS layer.
/// </summary>
/// <remarks>
/// Splitting transport from typing is what lets the same control and telemetry code run against real
/// Cyclone DDS, a managed multicast link, or an in-process loopback used by tests.
/// </remarks>
public interface IDdsTransport : IAsyncDisposable
{
    /// <summary>A short name for the transport, used in logs and diagnostics.</summary>
    string Name { get; }

    /// <summary>Whether the transport is started and able to carry traffic.</summary>
    bool IsRunning { get; }

    /// <summary>Starts the transport.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the transport without disposing it.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Publishes a payload on <paramref name="topic"/>.</summary>
    /// <param name="topic">The DDS topic name.</param>
    /// <param name="payload">The encoded CDR payload, including the encapsulation header.</param>
    void Publish(string topic, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Registers a handler for payloads arriving on <paramref name="topic"/>.
    /// </summary>
    /// <returns>A token that unsubscribes when disposed.</returns>
    /// <remarks>
    /// The handler runs on the transport's receive path. It must return quickly; anything slow belongs
    /// behind the bounded channel that <see cref="IDdsSubscriber{T}"/> provides.
    /// </remarks>
    IDisposable Subscribe(string topic, DdsPayloadHandler handler);
}

/// <summary>
/// Receives a raw payload from a transport.
/// </summary>
/// <param name="payload">The encoded CDR payload. Only valid for the duration of the call.</param>
public delegate void DdsPayloadHandler(ReadOnlySpan<byte> payload);

/// <summary>
/// Publishes typed messages on a DDS topic.
/// </summary>
/// <typeparam name="T">The message type.</typeparam>
public interface IDdsPublisher<T> : IDisposable
    where T : ICdrSerializable<T>
{
    /// <summary>The topic being published to.</summary>
    string Topic { get; }

    /// <summary>Total messages published.</summary>
    long PublishedCount { get; }

    /// <summary>Encodes and publishes <paramref name="message"/>.</summary>
    void Publish(in T message);
}

/// <summary>
/// Receives typed messages from a DDS topic.
/// </summary>
/// <typeparam name="T">The message type.</typeparam>
/// <remarks>
/// Two consumption models are offered and both stay live at once: <see cref="Reader"/> for
/// backpressure-aware streaming, and <see cref="TryGetLatest"/> for control loops that only ever want
/// the most recent sample and should never process a backlog.
/// </remarks>
public interface IDdsSubscriber<T> : IDisposable
    where T : ICdrSerializable<T>
{
    /// <summary>The topic being subscribed to.</summary>
    string Topic { get; }

    /// <summary>Total messages successfully decoded.</summary>
    long ReceivedCount { get; }

    /// <summary>
    /// Messages dropped because <see cref="Reader"/> was full.
    /// </summary>
    /// <remarks>A non-zero and rising value means the consumer cannot keep up with the robot.</remarks>
    long DroppedCount { get; }

    /// <summary>Messages discarded because they failed to decode.</summary>
    long MalformedCount { get; }

    /// <summary>When the most recent message arrived, or <see langword="null"/> if none has.</summary>
    DateTimeOffset? LastReceivedAt { get; }

    /// <summary>A bounded channel of decoded messages, oldest-dropped when full.</summary>
    ChannelReader<T> Reader { get; }

    /// <summary>
    /// Gets the most recently received message without consuming from <see cref="Reader"/>.
    /// </summary>
    /// <returns><see langword="false"/> if no message has arrived yet.</returns>
    bool TryGetLatest(out T message);
}

/// <summary>
/// Creates typed publishers and subscribers over a transport.
/// </summary>
public interface IDdsParticipant : IAsyncDisposable
{
    /// <summary>The underlying transport.</summary>
    IDdsTransport Transport { get; }

    /// <summary>Starts the participant and its transport.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a publisher for <paramref name="topic"/>.</summary>
    IDdsPublisher<T> CreatePublisher<T>(string topic)
        where T : ICdrSerializable<T>;

    /// <summary>Creates a subscriber for <paramref name="topic"/>.</summary>
    /// <param name="topic">The DDS topic name.</param>
    /// <param name="queueCapacity">Bounded capacity of the message channel.</param>
    IDdsSubscriber<T> CreateSubscriber<T>(string topic, int queueCapacity = 256)
        where T : ICdrSerializable<T>;
}
