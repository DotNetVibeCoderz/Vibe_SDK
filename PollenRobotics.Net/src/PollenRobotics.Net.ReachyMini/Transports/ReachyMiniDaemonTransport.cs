using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PollenRobotics.Net.Core;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.ReachyMini.Moves;
using PollenRobotics.Net.Transport;

namespace PollenRobotics.Net.ReachyMini.Transports;

/// <summary>
/// Talks to the Reachy Mini daemon over its REST API and command WebSocket.
/// </summary>
/// <remarks>
/// <para>
/// The daemon is the server and this is a client: commands go out on the WebSocket, reads come back
/// over REST, and state arrives on whichever of the two the daemon build supports. That split is
/// why <see cref="GetStateAsync"/> can serve a cached push or issue a fetch without the caller
/// noticing.
/// </para>
/// <para>
/// Commands are serialised as externally tagged JSON - <c>{"SetFullTargetCmd": {...}}</c> - which
/// is what serde produces for the Rust command enum the daemon deserialises into. The names in
/// <see cref="Commands"/> are the variant names and must match exactly; an unknown variant is
/// dropped by the daemon without an error, so a typo here looks like a robot that ignores you.
/// </para>
/// <para>
/// This transport is written against Pollen's published SDK and daemon documentation. It has not
/// been exercised against a physical Reachy Mini - see PROGRESS.md before relying on it.
/// </para>
/// </remarks>
public sealed class ReachyMiniDaemonTransport : IReachyMiniTransport
{
    /// <summary>The daemon command variant names, as serde spells them.</summary>
    private static class Commands
    {
        public const string SetFullTarget = "SetFullTargetCmd";
        public const string SetTorque = "SetTorqueCmd";
        public const string SetGravityCompensation = "SetGravityCompensationCmd";
        public const string SetAutomaticBodyYaw = "SetAutomaticBodyYawCmd";
        public const string SetHeadTracking = "SetHeadTrackingCmd";
        public const string SetWobbling = "SetWobblingCmd";
        public const string StartRecording = "StartRecordingCmd";
        public const string StopRecording = "StopRecordingCmd";
        public const string Goto = "GotoTaskRequest";
        public const string CancelMove = "CancelMoveCmd";
    }

    private readonly ReachyMiniOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private HttpJsonClient? _http;
    private JsonWebSocketChannel? _socket;
    private CancellationTokenSource? _lifetime;
    private Task? _pollLoop;
    private ReachyMiniState _lastState = ReachyMiniState.Empty;
    private ConnectionState _state = ConnectionState.Disconnected;
    private string _endpoint;
    private int _disposed;

    /// <inheritdoc />
    public string Endpoint => _endpoint;

    /// <inheritdoc />
    public ConnectionState State => _state;

    /// <inheritdoc />
    public event Action<ConnectionState>? StateChanged;

    /// <inheritdoc />
    public event Action<ReachyMiniState>? StateUpdated;

    /// <summary>Creates a transport for the given options.</summary>
    public ReachyMiniDaemonTransport(ReachyMiniOptions? options = null, ILogger<ReachyMiniDaemonTransport>? logger = null)
    {
        _options = options ?? ReachyMiniOptions.Default;
        _logger = logger ?? NullLogger<ReachyMiniDaemonTransport>.Instance;
        _endpoint = _options.BaseAddress.ToString();
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == ConnectionState.Connected)
            {
                return;
            }

            SetState(ConnectionState.Connecting);

            ReachyMiniOptions resolved = await ResolveHostAsync(cancellationToken).ConfigureAwait(false);
            _endpoint = resolved.BaseAddress.ToString();

            _http = new HttpJsonClient(resolved.BaseAddress, resolved.RequestTimeout);
            _socket = await JsonWebSocketChannel.ConnectAsync(resolved.WebSocketAddress, _logger, cancellationToken).ConfigureAwait(false);
            _socket.MessageReceived += OnSocketMessage;
            _socket.Closed += OnSocketClosed;

            _lifetime = new CancellationTokenSource();
            _pollLoop = Task.Run(() => PollLoopAsync(_lifetime.Token), CancellationToken.None);

            SetState(ConnectionState.Connected);
            _logger.LogInformation("Connected to the Reachy Mini daemon at {Endpoint}.", _endpoint);

            if (_options.AutomaticBodyYaw)
            {
                await SetAutomaticBodyYawAsync(true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            SetState(ConnectionState.Faulted);
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Picks the host to dial.
    /// </summary>
    /// <remarks>
    /// In <see cref="ReachyMiniConnectionMode.Auto"/> the local daemon wins if it answers, because
    /// a Lite plugged into this machine and a Wireless on the same network both being present is a
    /// normal development setup and the cable is the one you meant.
    /// </remarks>
    private async Task<ReachyMiniOptions> ResolveHostAsync(CancellationToken cancellationToken)
    {
        if (_options.ConnectionMode != ReachyMiniConnectionMode.Auto)
        {
            return _options;
        }

        ReachyMiniOptions local = _options with { ConnectionMode = ReachyMiniConnectionMode.LocalhostOnly };
        using var probe = new HttpJsonClient(local.BaseAddress, TimeSpan.FromMilliseconds(700));

        if (await probe.PingAsync("status", cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("A daemon answered on localhost; using it.");
            return local;
        }

        _logger.LogDebug("No daemon on localhost; falling back to {Host}.", _options.NetworkHost);
        return _options with { ConnectionMode = ReachyMiniConnectionMode.Network };
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TeardownAsync().ConfigureAwait(false);
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task TeardownAsync()
    {
        if (_lifetime is { } lifetime)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        if (_pollLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "State poll loop faulted during shutdown.");
            }
        }

        if (_socket is { } socket)
        {
            socket.MessageReceived -= OnSocketMessage;
            socket.Closed -= OnSocketClosed;
            await socket.DisposeAsync().ConfigureAwait(false);
        }

        _http?.Dispose();
        _lifetime?.Dispose();

        _socket = null;
        _http = null;
        _pollLoop = null;
        _lifetime = null;
    }

    /// <inheritdoc />
    public Task SetTargetAsync(ReachyMiniTarget target, CancellationToken cancellationToken = default)
    {
        if (target.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var payload = new JsonObject();
        AddTargetFields(payload, target);
        return SendCommandAsync(Commands.SetFullTarget, payload, cancellationToken);
    }

    /// <inheritdoc />
    public Task GotoTargetAsync(ReachyMiniTarget target, TimeSpan duration, InterpolationMethod method, CancellationToken cancellationToken = default)
    {
        if (target.IsEmpty)
        {
            return Task.CompletedTask;
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A goto cannot take negative time.");
        }

        var payload = new JsonObject
        {
            ["duration"] = duration.TotalSeconds,
            ["method"] = method.ToWireValue(),
        };

        AddTargetFields(payload, target);
        return SendCommandAsync(Commands.Goto, payload, cancellationToken);
    }

    /// <inheritdoc />
    public Task CancelMoveAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(Commands.CancelMove, new JsonObject(), cancellationToken);

    /// <inheritdoc />
    public async Task<ReachyMiniState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        HttpJsonClient http = RequireHttp();

        JsonNode? status = await http.GetAsync("status", cancellationToken).ConfigureAwait(false);
        ReachyMiniState state = ParseState(status);

        // /status is the daemon's own summary; on builds that do not carry the pose in it, fill the
        // gap from the dedicated endpoints rather than reporting an identity pose as if it were real.
        if (state.HeadPose == Pose.Identity && status?["head"] is null)
        {
            JsonNode? pose = await http.GetAsync("head_pose", cancellationToken).ConfigureAwait(false);
            if (ReadMatrix(pose) is { } head)
            {
                state = state with { HeadPose = head };
            }
        }

        Publish(state);
        return state;
    }

    /// <inheritdoc />
    public Task SetMotorModeAsync(MotorMode mode, CancellationToken cancellationToken = default) => mode switch
    {
        MotorMode.Enabled => SetTorqueAsync(true, null, cancellationToken),
        MotorMode.Disabled => SetTorqueAsync(false, null, cancellationToken),
        MotorMode.GravityCompensation => SendCommandAsync(
            Commands.SetGravityCompensation, new JsonObject { ["enabled"] = true }, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown motor mode."),
    };

    /// <inheritdoc />
    public Task SetTorqueAsync(bool enabled, IReadOnlyList<string>? motorIds = null, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject { ["on"] = enabled };

        if (motorIds is { Count: > 0 })
        {
            var ids = new JsonArray();
            foreach (string id in motorIds)
            {
                ids.Add(id);
            }

            payload["ids"] = ids;
        }

        return SendCommandAsync(Commands.SetTorque, payload, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetHeadTrackingAsync(bool enabled, double weight = 1.0, CancellationToken cancellationToken = default)
    {
        if (weight is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Tracking weight blends in [0, 1].");
        }

        return SendCommandAsync(
            Commands.SetHeadTracking,
            new JsonObject { ["enabled"] = enabled, ["weight"] = weight },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<FaceTarget> GetTrackedFaceAsync(CancellationToken cancellationToken = default)
    {
        JsonNode? node = await RequireHttp().GetAsync("tracked_face", cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            return FaceTarget.None;
        }

        return new FaceTarget(
            node["detected"]?.GetValue<bool>() ?? false,
            node["x"]?.GetValue<double>() ?? 0,
            node["y"]?.GetValue<double>() ?? 0,
            Angle.FromRadians(node["roll"]?.GetValue<double>() ?? 0));
    }

    /// <inheritdoc />
    public Task SetWobblingAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendCommandAsync(Commands.SetWobbling, new JsonObject { ["enabled"] = enabled }, cancellationToken);

    /// <inheritdoc />
    public Task SetAutomaticBodyYawAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendCommandAsync(Commands.SetAutomaticBodyYaw, new JsonObject { ["enabled"] = enabled }, cancellationToken);

    /// <inheritdoc />
    public async Task<ImuReading?> GetImuAsync(CancellationToken cancellationToken = default)
    {
        JsonNode? node;
        try
        {
            node = await RequireHttp().GetAsync("imu", cancellationToken).ConfigureAwait(false);
        }
        catch (RobotCommandException)
        {
            // A Lite has no IMU and the endpoint 404s. That is a fact about the hardware, not a fault.
            return null;
        }

        if (node is null)
        {
            return null;
        }

        return new ImuReading(
            ReadTriple(node["accelerometer"]),
            ReadTriple(node["gyroscope"]),
            ReadQuaternion(node["quaternion"]),
            node["temperature"]?.GetValue<double>() ?? double.NaN);

        static (double X, double Y, double Z) ReadTriple(JsonNode? array) => array is JsonArray { Count: >= 3 } a
            ? (a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>())
            : (0, 0, 0);

        static (double W, double X, double Y, double Z) ReadQuaternion(JsonNode? array) => array is JsonArray { Count: >= 4 } a
            ? (a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), a[3]!.GetValue<double>())
            : (1, 0, 0, 0);
    }

    /// <inheritdoc />
    public Task StartRecordingAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(Commands.StartRecording, new JsonObject(), cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MoveFrame>> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(Commands.StopRecording, new JsonObject(), cancellationToken).ConfigureAwait(false);

        JsonNode? recorded = await RequireHttp().GetAsync("recorded_data", cancellationToken).ConfigureAwait(false);
        if (recorded is not JsonArray frames)
        {
            return [];
        }

        var result = new List<MoveFrame>(frames.Count);
        foreach (JsonNode? frame in frames)
        {
            if (frame is null)
            {
                continue;
            }

            result.Add(new MoveFrame(
                frame["time"]?.GetValue<double>() ?? 0,
                ReadMatrix(frame["head"]),
                ReadAntennas(frame["antennas"]),
                frame["body_yaw"] is { } yaw ? Angle.FromRadians(yaw.GetValue<double>()) : null));
        }

        return result;
    }

    private async Task SendCommandAsync(string command, JsonObject payload, CancellationToken cancellationToken)
    {
        JsonWebSocketChannel socket = _socket
            ?? throw new RobotConnectionException("Not connected. Call ConnectAsync first.");

        // Externally tagged: the variant name is the single key of the envelope.
        var envelope = new JsonObject { [command] = payload };
        await socket.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    private HttpJsonClient RequireHttp() =>
        _http ?? throw new RobotConnectionException("Not connected. Call ConnectAsync first.");

    private static void AddTargetFields(JsonObject payload, ReachyMiniTarget target)
    {
        if (target.Head is { } head)
        {
            var matrix = new JsonArray();
            foreach (double value in head.ToRowMajor())
            {
                matrix.Add(value);
            }

            payload["head"] = matrix;
        }

        if (target.Antennas is { } antennas)
        {
            // Right first, then left - the order the daemon uses in both directions.
            payload["antennas"] = new JsonArray(antennas.Right.Radians, antennas.Left.Radians);
        }

        if (target.BodyYaw is { } bodyYaw)
        {
            payload["body_yaw"] = bodyYaw.Radians;
        }
    }

    private void OnSocketMessage(JsonNode message)
    {
        // The daemon pushes state on the same socket commands go out on. Anything without a head
        // field is some other notification and is not our business here.
        if (message["head"] is null && message["motor_mode"] is null)
        {
            return;
        }

        Publish(ParseState(message));
    }

    private void OnSocketClosed(Exception? failure)
    {
        if (Volatile.Read(ref _disposed) != 0 || _state == ConnectionState.Disconnected)
        {
            return;
        }

        _logger.LogWarning("The daemon WebSocket closed{Reason}.", failure is null ? string.Empty : $": {failure.Message}");
        SetState(_options.Reconnect.Enabled ? ConnectionState.Reconnecting : ConnectionState.Faulted);
    }

    private static ReachyMiniState ParseState(JsonNode? node)
    {
        if (node is null)
        {
            return ReachyMiniState.Empty;
        }

        double[] jointPositions = node["head_joint_positions"] is JsonArray joints
            ? [.. joints.Select(j => j?.GetValue<double>() ?? 0)]
            : [];

        (Angle Right, Angle Left) antennas = ReadAntennas(node["antennas"])
            ?? ReadAntennas(node["antennas_joint_positions"])
            ?? (Angle.Zero, Angle.Zero);

        return new ReachyMiniState(
            ReadMatrix(node["head"]) ?? Pose.Identity,
            antennas,
            Angle.FromRadians(node["body_yaw"]?.GetValue<double>() ?? 0),
            jointPositions,
            MotorModeExtensions.ParseMotorMode(node["motor_mode"]?.GetValue<string>()),
            node["is_move_running"]?.GetValue<bool>() ?? false,
            DateTimeOffset.UtcNow);
    }

    private static Pose? ReadMatrix(JsonNode? node)
    {
        if (node is not JsonArray { Count: 16 } array)
        {
            return null;
        }

        Span<double> values = stackalloc double[16];
        for (int i = 0; i < 16; i++)
        {
            values[i] = array[i]?.GetValue<double>() ?? 0;
        }

        return Pose.FromRowMajor(values);
    }

    private static (Angle Right, Angle Left)? ReadAntennas(JsonNode? node) => node is JsonArray { Count: 2 } array
        ? (Angle.FromRadians(array[0]?.GetValue<double>() ?? 0), Angle.FromRadians(array[1]?.GetValue<double>() ?? 0))
        : null;

    private void Publish(ReachyMiniState state)
    {
        _lastState = state;
        StateUpdated?.Invoke(state);
    }

    private void SetState(ConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>
    /// Polls state while the daemon is not pushing it, and drives reconnection.
    /// </summary>
    /// <remarks>
    /// A daemon that pushes state keeps <c>_lastState.Timestamp</c> fresh, and the poll then costs
    /// nothing because it skips. Polling unconditionally would double the state traffic on exactly
    /// the builds that need it least.
    /// </remarks>
    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        int attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_state == ConnectionState.Reconnecting)
                {
                    attempt++;
                    if (_options.Reconnect.MaxAttempts is { } max && attempt > max)
                    {
                        _logger.LogError("Giving up after {Attempts} reconnection attempts.", max);
                        SetState(ConnectionState.Faulted);
                        return;
                    }

                    await Task.Delay(_options.Reconnect.DelayFor(attempt), cancellationToken).ConfigureAwait(false);
                    await ReconnectSocketAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                attempt = 0;
                await Task.Delay(_options.StatePollInterval, cancellationToken).ConfigureAwait(false);

                bool stale = DateTimeOffset.UtcNow - _lastState.Timestamp > _options.StatePollInterval;
                if (_state == ConnectionState.Connected && stale)
                {
                    await GetStateAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "State poll failed; will retry.");
                if (_state == ConnectionState.Connected && _options.Reconnect.Enabled)
                {
                    SetState(ConnectionState.Reconnecting);
                }
            }
        }
    }

    private async Task ReconnectSocketAsync(CancellationToken cancellationToken)
    {
        if (_socket is { } old)
        {
            old.MessageReceived -= OnSocketMessage;
            old.Closed -= OnSocketClosed;
            await old.DisposeAsync().ConfigureAwait(false);
            _socket = null;
        }

        JsonWebSocketChannel socket = await JsonWebSocketChannel
            .ConnectAsync(_options.WebSocketAddress, _logger, cancellationToken).ConfigureAwait(false);

        socket.MessageReceived += OnSocketMessage;
        socket.Closed += OnSocketClosed;
        _socket = socket;

        SetState(ConnectionState.Connected);
        _logger.LogInformation("Reconnected to the daemon at {Endpoint}.", _endpoint);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await TeardownAsync().ConfigureAwait(false);
        SetState(ConnectionState.Disconnected);
        _connectLock.Dispose();
    }
}
