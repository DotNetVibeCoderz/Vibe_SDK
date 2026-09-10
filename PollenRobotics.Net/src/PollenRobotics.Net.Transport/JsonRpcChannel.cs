using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PollenRobotics.Net.Core;

namespace PollenRobotics.Net.Transport;

/// <summary>
/// JSON-RPC 2.0 over a duplex stream, one object per line.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape MicroDuck's daemons speak: NDJSON framed by <c>LinesCodec</c> on the Rust
/// side, over a Unix socket. The framing matters - a JSON object containing a newline inside a
/// string would break the frame, so responses are written with <see cref="JsonSerializerOptions"/>
/// that never indent.
/// </para>
/// <para>
/// Notifications (a message with no <c>id</c>) are how the daemon pushes subscriptions, so the
/// receive loop has to demultiplex rather than assume every inbound line answers a pending call.
/// </para>
/// </remarks>
public sealed class JsonRpcChannel : IAsyncDisposable
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly Stream _stream;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _receiveLoop;
    private long _nextId;
    private int _disposed;

    /// <summary>Raised for every inbound notification: the method name and its parameters.</summary>
    public event Action<string, JsonNode?>? NotificationReceived;

    /// <summary>Raised when the receive loop ends, with the reason if it was an error.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>True while the receive loop is running.</summary>
    public bool IsOpen => !_receiveLoop.IsCompleted && Volatile.Read(ref _disposed) == 0;

    /// <summary>Wraps an already-connected duplex stream.</summary>
    public JsonRpcChannel(Stream stream, ILogger? logger = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _logger = logger;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_shutdown.Token));
    }

    /// <summary>Calls a method and waits for its result.</summary>
    /// <param name="method">JSON-RPC method name, e.g. <c>robot.state</c>.</param>
    /// <param name="parameters">Parameters, or null for none.</param>
    /// <param name="timeout">How long to wait. Defaults to five seconds.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<JsonNode?> InvokeAsync(
        string method,
        JsonNode? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        long id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
            };

            if (parameters is not null)
            {
                request["params"] = parameters;
            }

            await SendAsync(request, cancellationToken).ConfigureAwait(false);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));

            await using (timeoutSource.Token.Register(static state => ((TaskCompletionSource<JsonNode?>)state!).TrySetCanceled(), completion).ConfigureAwait(false))
            {
                return await completion.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RobotCommandException(
                $"'{method}' did not answer within {(timeout ?? TimeSpan.FromSeconds(5)).TotalSeconds:0.#}s.", method);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a notification, which by definition has no reply.</summary>
    public async Task NotifyAsync(string method, JsonNode? parameters = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };

        if (parameters is not null)
        {
            notification["params"] = parameters;
        }

        await SendAsync(notification, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message.ToJsonString(WireOptions) + "\n");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;

        try
        {
            using var reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    Dispatch(JsonNode.Parse(line));
                }
                catch (JsonException ex)
                {
                    // A malformed line is the daemon's problem, not a reason to tear the link down.
                    _logger?.LogWarning(ex, "Discarded a malformed JSON-RPC line.");
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
            _logger?.LogWarning(ex, "JSON-RPC receive loop ended.");
        }
        finally
        {
            var reason = failure ?? (Exception?)null;
            foreach (KeyValuePair<long, TaskCompletionSource<JsonNode?>> entry in _pending)
            {
                entry.Value.TrySetException(new RobotConnectionException(
                    "The connection closed before the call was answered.", reason ?? new IOException("Stream closed.")));
            }

            _pending.Clear();
            Closed?.Invoke(failure);
        }
    }

    private void Dispatch(JsonNode? message)
    {
        if (message is not JsonObject obj)
        {
            return;
        }

        if (obj.TryGetPropertyValue("id", out JsonNode? idNode) && idNode is not null)
        {
            long id = idNode.GetValue<long>();
            if (!_pending.TryRemove(id, out TaskCompletionSource<JsonNode?>? completion))
            {
                // A reply to a call that already timed out. Nothing to do with it.
                return;
            }

            if (obj.TryGetPropertyValue("error", out JsonNode? error) && error is not null)
            {
                string text = error["message"]?.GetValue<string>() ?? error.ToJsonString(WireOptions);
                int code = error["code"]?.GetValue<int>() ?? 0;
                completion.TrySetException(new RobotCommandException($"The robot rejected the call: {text} (code {code})."));
                return;
            }

            obj.TryGetPropertyValue("result", out JsonNode? result);
            completion.TrySetResult(result?.DeepClone());
            return;
        }

        if (obj.TryGetPropertyValue("method", out JsonNode? methodNode) && methodNode is not null)
        {
            obj.TryGetPropertyValue("params", out JsonNode? parameters);
            NotificationReceived?.Invoke(methodNode.GetValue<string>(), parameters?.DeepClone());
        }
    }

    /// <summary>Closes the channel and fails every outstanding call.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Receive loop faulted during shutdown.");
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
