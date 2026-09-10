using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PollenRobotics.Net.Core;

namespace PollenRobotics.Net.Transport;

/// <summary>
/// A JSON message channel over a client WebSocket.
/// </summary>
/// <remarks>
/// <para>
/// The Reachy Mini daemon takes commands on a WebSocket and pushes state back over the same socket.
/// Commands are fire-and-forget - the daemon does not acknowledge a <c>SetTargetCmd</c> - so this
/// channel is a send queue plus a receive event, not a request/response pair.
/// </para>
/// <para>
/// Sends are serialised through a lock because <see cref="ClientWebSocket"/> permits exactly one
/// outstanding send. Two control loops writing concurrently is not a race that produces interleaved
/// frames; it is an <see cref="InvalidOperationException"/> that kills the socket.
/// </para>
/// </remarks>
public sealed class JsonWebSocketChannel : IAsyncDisposable
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly ClientWebSocket _socket;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _receiveLoop;
    private int _disposed;

    /// <summary>Raised for every inbound JSON message.</summary>
    public event Action<JsonNode>? MessageReceived;

    /// <summary>Raised when the socket closes, carrying the failure if there was one.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>True while the socket is open.</summary>
    public bool IsOpen => _socket.State == WebSocketState.Open;

    private JsonWebSocketChannel(ClientWebSocket socket, ILogger? logger)
    {
        _socket = socket;
        _logger = logger;
    }

    /// <summary>Connects to <paramref name="uri"/> and starts pumping messages.</summary>
    public static async Task<JsonWebSocketChannel> ConnectAsync(Uri uri, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new RobotConnectionException($"Could not open a WebSocket to {uri}.", ex);
        }

        var channel = new JsonWebSocketChannel(socket, logger);
        channel._receiveLoop = Task.Run(() => channel.ReceiveLoopAsync(channel._shutdown.Token), CancellationToken.None);
        return channel;
    }

    /// <summary>Sends one JSON message.</summary>
    public async Task SendAsync(JsonNode message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        byte[] payload = Encoding.UTF8.GetBytes(message.ToJsonString(WireOptions));

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                throw new RobotConnectionException($"The WebSocket is {_socket.State}, not Open.");
            }

            await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new RobotConnectionException("Sending on the WebSocket failed.", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            var message = new ArrayBufferWriter<byte>(16 * 1024);

            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                message.Clear();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (message.WrittenCount == 0)
                {
                    continue;
                }

                try
                {
                    JsonNode? node = JsonNode.Parse(message.WrittenSpan);
                    if (node is not null)
                    {
                        MessageReceived?.Invoke(node);
                    }
                }
                catch (JsonException ex)
                {
                    _logger?.LogWarning(ex, "Discarded a malformed WebSocket message.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            failure = ex;
            _logger?.LogWarning(ex, "WebSocket receive loop ended.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Closed?.Invoke(failure);
        }
    }

    /// <summary>Closes the socket.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client shutdown", closeTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Closing the WebSocket failed; disposing anyway.");
        }

        if (_receiveLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Receive loop faulted during shutdown.");
            }
        }

        _shutdown.Dispose();
        _sendLock.Dispose();
        _socket.Dispose();
    }
}
