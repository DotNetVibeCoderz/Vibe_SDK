namespace Unitree.Net.Core;

/// <summary>
/// Lifecycle state of a robot link.
/// </summary>
public enum ConnectionState
{
    /// <summary>No transport has been created.</summary>
    Disconnected,

    /// <summary>The transport exists but no state message has arrived yet.</summary>
    Connecting,

    /// <summary>State messages are arriving within the configured timeout.</summary>
    Connected,

    /// <summary>The transport exists but state has gone stale.</summary>
    Stale,

    /// <summary>The link failed and will not recover without a reconnect.</summary>
    Faulted,
}

/// <summary>
/// A live link to one robot.
/// </summary>
/// <remarks>
/// Only one SDK instance may own a robot at a time — Unitree's firmware does not arbitrate between
/// multiple controlling hosts. Treat this as an exclusive resource for the process lifetime.
/// </remarks>
public interface IRobotConnection : IAsyncDisposable
{
    /// <summary>The robot platform this connection targets.</summary>
    RobotModel Model { get; }

    /// <summary>The current link state.</summary>
    ConnectionState State { get; }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Establishes the transport and waits for the first state message.
    /// </summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <exception cref="UnitreeConnectionException">The robot did not respond within the configured timeout.</exception>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Tears the transport down without disposing the connection object.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Payload for <see cref="IRobotConnection.StateChanged"/>.
/// </summary>
/// <param name="previousState">The state being left.</param>
/// <param name="currentState">The state being entered.</param>
/// <param name="reason">A human-readable explanation, or <see langword="null"/> for routine transitions.</param>
public sealed class ConnectionStateChangedEventArgs(
    ConnectionState previousState,
    ConnectionState currentState,
    string? reason = null) : EventArgs
{
    /// <summary>The state being left.</summary>
    public ConnectionState PreviousState { get; } = previousState;

    /// <summary>The state being entered.</summary>
    public ConnectionState CurrentState { get; } = currentState;

    /// <summary>A human-readable explanation, if any.</summary>
    public string? Reason { get; } = reason;
}

/// <summary>
/// Marks a type that can be serialised to and from DDS CDR.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
/// <remarks>
/// Implemented as a static abstract interface so serialisation dispatches without virtual calls or
/// boxing, which matters on the 500 Hz publish path.
/// </remarks>
public interface ICdrSerializable<TSelf>
    where TSelf : ICdrSerializable<TSelf>
{
    /// <summary>The fully qualified DDS type name, e.g. <c>unitree_go::msg::dds_::LowCmd_</c>.</summary>
    static abstract string DdsTypeName { get; }

    /// <summary>
    /// An upper bound on the encoded size in bytes, used to size stack or pooled buffers.
    /// </summary>
    static abstract int MaxSerializedSize { get; }

    /// <summary>Writes this value into <paramref name="destination"/>.</summary>
    /// <param name="destination">Buffer of at least <c>MaxSerializedSize</c> bytes.</param>
    /// <returns>The number of bytes written.</returns>
    int Serialize(Span<byte> destination);

    /// <summary>Reads a value from <paramref name="source"/>.</summary>
    /// <param name="source">The encoded payload, including the CDR encapsulation header.</param>
    static abstract TSelf Deserialize(ReadOnlySpan<byte> source);
}
