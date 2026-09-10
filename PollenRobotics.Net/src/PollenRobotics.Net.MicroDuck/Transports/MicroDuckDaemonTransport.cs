using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PollenRobotics.Net.Core;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Transport;

namespace PollenRobotics.Net.MicroDuck.Transports;

/// <summary>
/// Speaks robotd's JSON-RPC contract over a Unix socket or a TCP bridge.
/// </summary>
/// <remarks>
/// <para>
/// The daemon frames JSON-RPC 2.0 as NDJSON - one object per line - which is what
/// <see cref="JsonRpcChannel"/> implements. Method names are the <c>robot.*</c> family;
/// <c>net.*</c>, <c>system.*</c> and <c>update.*</c> live in other daemons and are not this
/// transport's business.
/// </para>
/// <para>
/// Written against Pollen's published architecture and CLI documentation. The method names below
/// follow the documented <c>robot.*</c> namespace and the <c>robotctl</c> verbs that wrap them, but
/// Pollen does not publish the per-method signatures, so treat them as this SDK's best reading
/// rather than as a contract. They have not been exercised against a physical duck - see
/// PROGRESS.md.
/// </para>
/// </remarks>
public sealed class MicroDuckDaemonTransport : IMicroDuckTransport
{
    private static class Methods
    {
        public const string State = "robot.state";
        public const string Health = "robot.health";
        public const string Init = "robot.init";
        public const string Relax = "robot.relax";
        public const string Velocity = "robot.velocity";
        public const string Do = "robot.do";
        public const string Skills = "robot.skills";
        public const string Quack = "robot.quack";
        public const string RebootMotors = "robot.reboot_motors";
        public const string TofStream = "tof.stream";
    }

    private readonly MicroDuckOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private JsonRpcChannel? _channel;
    private CancellationTokenSource? _lifetime;
    private Task? _pollLoop;
    private ConnectionState _state = ConnectionState.Disconnected;
    private DateTimeOffset _lastVelocityCommand = DateTimeOffset.MinValue;
    private bool _velocityIsZero = true;
    private int _disposed;

    /// <inheritdoc />
    public string Endpoint => _options.DescribeEndpoint();

    /// <inheritdoc />
    public ConnectionState State => _state;

    /// <inheritdoc />
    public event Action<ConnectionState>? StateChanged;

    /// <inheritdoc />
    public event Action<MicroDuckState>? StateUpdated;

    /// <summary>Creates a transport.</summary>
    public MicroDuckDaemonTransport(MicroDuckOptions? options = null, ILogger<MicroDuckDaemonTransport>? logger = null)
    {
        _options = options ?? MicroDuckOptions.Default;
        _logger = logger ?? NullLogger<MicroDuckDaemonTransport>.Instance;
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

            NetworkStream stream = await OpenStreamAsync(cancellationToken).ConfigureAwait(false);
            _channel = new JsonRpcChannel(stream, _logger);
            _channel.Closed += OnChannelClosed;
            _channel.NotificationReceived += OnNotification;

            _lifetime = new CancellationTokenSource();
            _pollLoop = Task.Run(() => PollLoopAsync(_lifetime.Token), CancellationToken.None);

            SetState(ConnectionState.Connected);
            _logger.LogInformation("Connected to robotd at {Endpoint}.", Endpoint);
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

    private async Task<NetworkStream> OpenStreamAsync(CancellationToken cancellationToken)
    {
        if (_options.EndpointKind == MicroDuckEndpointKind.Tcp)
        {
            var tcp = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await tcp.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(tcp, ownsSocket: true);
            }
            catch (Exception ex)
            {
                tcp.Dispose();
                throw new RobotConnectionException($"Could not reach a robotd bridge at {Endpoint}.", ex);
            }
        }

        var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await unix.ConnectAsync(new UnixDomainSocketEndPoint(_options.SocketPath), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(unix, ownsSocket: true);
        }
        catch (SocketException ex)
        {
            unix.Dispose();

            // Two very different problems produce a failure here and they need different advice.
            throw new RobotConnectionException(
                File.Exists(_options.SocketPath)
                    ? $"The socket at {_options.SocketPath} exists but refused the connection. " +
                      "robotd gates mutating calls on uid/gid - check that this user is in allow_uids or allow_gids."
                    : $"No socket at {_options.SocketPath}. Either robotd is not running, or this code is not on the duck " +
                      "and needs an SSH-forwarded socket or a TCP bridge (see MicroDuckOptions.ForTcp).",
                ex);
        }
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
                _logger.LogDebug(ex, "Poll loop faulted during shutdown.");
            }
        }

        if (_channel is { } channel)
        {
            channel.Closed -= OnChannelClosed;
            channel.NotificationReceived -= OnNotification;
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime?.Dispose();
        _channel = null;
        _pollLoop = null;
        _lifetime = null;
    }

    /// <inheritdoc />
    public async Task<MicroDuckState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        JsonNode? result = await InvokeAsync(Methods.State, null, cancellationToken).ConfigureAwait(false);
        MicroDuckState state = ParseState(result);
        StateUpdated?.Invoke(state);
        return state;
    }

    /// <inheritdoc />
    public async Task<MicroDuckHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        JsonNode? result = await InvokeAsync(Methods.Health, null, cancellationToken).ConfigureAwait(false);

        return new MicroDuckHealth(
            result?["healthy"]?.GetValue<bool>() ?? false,
            result?["version"]?.GetValue<string>() ?? "unknown",
            result?["loop_rate_hz"]?.GetValue<double>() ?? 0,
            ReadIntArray(result?["failed_servos"]),
            ReadStringArray(result?["warnings"]));
    }

    /// <inheritdoc />
    public Task InitAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(Methods.Init, null, cancellationToken);

    /// <inheritdoc />
    public Task RelaxAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(Methods.Relax, new JsonObject { ["confirm"] = true }, cancellationToken);

    /// <inheritdoc />
    public async Task SetVelocityAsync(DuckVelocity velocity, CancellationToken cancellationToken = default)
    {
        DuckVelocity clamped = velocity.Clamped();

        var parameters = new JsonObject
        {
            ["vx"] = clamped.ForwardMetersPerSecond,
            ["vy"] = clamped.LateralMetersPerSecond,
            ["wz"] = clamped.YawRadiansPerSecond,
        };

        await InvokeAsync(Methods.Velocity, parameters, cancellationToken).ConfigureAwait(false);

        _lastVelocityCommand = DateTimeOffset.UtcNow;
        _velocityIsZero = clamped == DuckVelocity.Zero;
    }

    /// <inheritdoc />
    public Task PerformAsync(DuckActionSlot slot, CancellationToken cancellationToken = default) =>
        InvokeAsync(Methods.Do, new JsonObject { ["skill"] = slot.ToWireValue() }, cancellationToken,
            // An action runs to completion on the robot, and a ground pick or a roulade is several
            // seconds long. The default request timeout would abandon it halfway.
            timeout: TimeSpan.FromSeconds(20));

    /// <inheritdoc />
    public Task PerformSkillAsync(string skillName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        return InvokeAsync(Methods.Do, new JsonObject { ["skill"] = skillName }, cancellationToken,
            timeout: TimeSpan.FromSeconds(20));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListSkillsAsync(CancellationToken cancellationToken = default)
    {
        JsonNode? result = await InvokeAsync(Methods.Skills, null, cancellationToken).ConfigureAwait(false);
        return ReadStringArray(result as JsonArray ?? result?["skills"]);
    }

    /// <inheritdoc />
    public Task QuackAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(Methods.Quack, null, cancellationToken);

    /// <inheritdoc />
    public async Task<TofFrame?> ReadTimeOfFlightAsync(CancellationToken cancellationToken = default)
    {
        JsonNode? result;
        try
        {
            result = await InvokeAsync(Methods.TofStream, new JsonObject { ["once"] = true }, cancellationToken).ConfigureAwait(false);
        }
        catch (RobotCommandException)
        {
            // tofd is optional hardware; its absence is not a fault.
            return null;
        }

        JsonNode? distances = result?["distances"] ?? result?["matrix"];
        if (distances is not JsonArray array || array.Count < TofFrame.Size * TofFrame.Size)
        {
            return null;
        }

        double[] values = new double[TofFrame.Size * TofFrame.Size];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = array[i]?.GetValue<double>() ?? double.NaN;
        }

        return new TofFrame(values, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public Task RebootMotorsAsync(IReadOnlyList<int>? servoIds = null, CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject();

        if (servoIds is { Count: > 0 })
        {
            var ids = new JsonArray();
            foreach (int id in servoIds)
            {
                ids.Add(id);
            }

            parameters["ids"] = ids;
        }

        return InvokeAsync(Methods.RebootMotors, parameters, cancellationToken, timeout: TimeSpan.FromSeconds(15));
    }

    private async Task<JsonNode?> InvokeAsync(string method, JsonNode? parameters, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        JsonRpcChannel channel = _channel
            ?? throw new RobotConnectionException("Not connected. Call ConnectAsync first.");

        return await channel.InvokeAsync(method, parameters, timeout ?? _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static MicroDuckState ParseState(JsonNode? node)
    {
        if (node is null)
        {
            return MicroDuckState.Empty;
        }

        return new MicroDuckState(
            ReadDoubleArray(node["joint_positions"] ?? node["positions"]),
            ReadDoubleArray(node["joint_velocities"] ?? node["velocities"]),
            ReadQuaternion(node["orientation"] ?? node["quaternion"]),
            ReadTriple(node["gyroscope"] ?? node["angular_rate"]),
            ReadTriple(node["accelerometer"] ?? node["acceleration"]),
            node["active_slot"]?.GetValue<string>() is { Length: > 0 } slot ? TryParseSlot(slot) : null,
            node["fallen"]?.GetValue<bool>() ?? false,
            node["battery_volts"]?.GetValue<double>() ?? double.NaN,
            node["loop_rate_hz"]?.GetValue<double>() ?? 0,
            DateTimeOffset.UtcNow);

        static DuckActionSlot? TryParseSlot(string value)
        {
            try
            {
                return DuckActionSlotExtensions.ParseSlot(value);
            }
            catch (ArgumentException)
            {
                // A custom skill occupies a slot under its own name. Not knowing it is fine.
                return null;
            }
        }
    }

    private static double[] ReadDoubleArray(JsonNode? node) => node is JsonArray array
        ? [.. array.Select(v => v?.GetValue<double>() ?? 0)]
        : [];

    private static int[] ReadIntArray(JsonNode? node) => node is JsonArray array
        ? [.. array.Select(v => v?.GetValue<int>() ?? 0)]
        : [];

    private static string[] ReadStringArray(JsonNode? node) => node is JsonArray array
        ? [.. array.Select(v => v?.GetValue<string>() ?? string.Empty).Where(s => s.Length > 0)]
        : [];

    private static (double X, double Y, double Z) ReadTriple(JsonNode? node) => node is JsonArray { Count: >= 3 } a
        ? (a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>())
        : (0, 0, 0);

    private static (double W, double X, double Y, double Z) ReadQuaternion(JsonNode? node) => node is JsonArray { Count: >= 4 } a
        ? (a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), a[3]!.GetValue<double>())
        : (1, 0, 0, 0);

    private void OnNotification(string method, JsonNode? parameters)
    {
        // The daemon streams state as a notification on subscriptions; take it when it comes rather
        // than waiting for the next poll.
        if (method is Methods.State or "robot.state_update" && parameters is not null)
        {
            StateUpdated?.Invoke(ParseState(parameters));
        }
    }

    private void OnChannelClosed(Exception? failure)
    {
        if (Volatile.Read(ref _disposed) != 0 || _state == ConnectionState.Disconnected)
        {
            return;
        }

        _logger.LogWarning("The robotd link closed{Reason}.", failure is null ? string.Empty : $": {failure.Message}");
        SetState(_options.Reconnect.Enabled ? ConnectionState.Reconnecting : ConnectionState.Faulted);
    }

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
                    await ReopenAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                attempt = 0;
                await Task.Delay(_options.StatePollInterval, cancellationToken).ConfigureAwait(false);

                if (_state != ConnectionState.Connected)
                {
                    continue;
                }

                await GetStateAsync(cancellationToken).ConfigureAwait(false);
                await ApplyVelocityWatchdogAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Sends a zero velocity when the application has gone quiet.
    /// </summary>
    /// <remarks>
    /// The watchdog measures from the last command this transport <b>sent</b>, not from the last
    /// distinct value. A caller holding one velocity for a second - which every teleoperation loop
    /// does - keeps sending, so it never trips; a caller that has crashed stops, and it does.
    /// </remarks>
    private async Task ApplyVelocityWatchdogAsync(CancellationToken cancellationToken)
    {
        if (_options.VelocityWatchdog is not { } window || _velocityIsZero || _lastVelocityCommand == DateTimeOffset.MinValue)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - _lastVelocityCommand < window)
        {
            return;
        }

        _logger.LogWarning("No velocity command for {Window:0} ms; stopping the duck.", window.TotalMilliseconds);
        await SetVelocityAsync(DuckVelocity.Zero, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReopenAsync(CancellationToken cancellationToken)
    {
        if (_channel is { } old)
        {
            old.Closed -= OnChannelClosed;
            old.NotificationReceived -= OnNotification;
            await old.DisposeAsync().ConfigureAwait(false);
            _channel = null;
        }

        NetworkStream stream = await OpenStreamAsync(cancellationToken).ConfigureAwait(false);
        var channel = new JsonRpcChannel(stream, _logger);
        channel.Closed += OnChannelClosed;
        channel.NotificationReceived += OnNotification;
        _channel = channel;

        SetState(ConnectionState.Connected);
        _logger.LogInformation("Reconnected to robotd at {Endpoint}.", Endpoint);
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
