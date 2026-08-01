using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Messages;
using Unitree.Net.Messages.Go;

namespace Unitree.Net.Control;

/// <summary>
/// The primary entry point: one connected robot, with its control surfaces and telemetry.
/// </summary>
/// <remarks>
/// <para>
/// Control surfaces are created lazily. Merely reading telemetry never creates a low-level publisher,
/// which matters because the existence of a <c>rt/lowcmd</c> writer is itself visible to the robot.
/// </para>
/// <para>
/// Only one instance may own a given robot; Unitree firmware does not arbitrate between hosts.
/// </para>
/// </remarks>
public sealed class UnitreeRobot : IRobotConnection
{
    private readonly IDdsParticipant _participant;
    private readonly UnitreeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<UnitreeRobot> _logger;
    private readonly Lock _stateLock = new();

    private IDdsSubscriber<LowState>? _lowStateSubscriber;
    private IDdsSubscriber<SportModeState>? _sportStateSubscriber;
    private SportClient? _sport;
    private LowLevelController? _lowLevel;
    private MotionSwitcherClient? _motionSwitcher;
    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _disposed;

    /// <summary>Creates a robot over <paramref name="participant"/>.</summary>
    /// <param name="participant">A started or startable DDS participant.</param>
    /// <param name="options">Connection and safety configuration.</param>
    /// <param name="loggerFactory">Logger factory for the control surfaces.</param>
    public UnitreeRobot(IDdsParticipant participant, UnitreeOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _participant = participant;
        _options = options;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<UnitreeRobot>();
    }

    /// <inheritdoc />
    public RobotModel Model => _options.Model;

    /// <inheritdoc />
    public ConnectionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>The configuration this robot was created with.</summary>
    public UnitreeOptions Options => _options;

    /// <summary>The underlying DDS participant, for advanced use.</summary>
    public IDdsParticipant Participant => _participant;

    /// <summary>High-level locomotion control.</summary>
    public SportClient Sport => _sport ??= new SportClient(
        _participant,
        _options.Safety,
        _options.RequestTimeout,
        _loggerFactory.CreateLogger<SportClient>());

    /// <summary>The on-board motion controller switch.</summary>
    public MotionSwitcherClient MotionSwitcher => _motionSwitcher ??= new MotionSwitcherClient(
        _participant,
        _options.RequestTimeout,
        _loggerFactory.CreateLogger<MotionSwitcherClient>());

    /// <summary>
    /// Direct joint control.
    /// </summary>
    /// <remarks>
    /// Accessing this property creates a <c>rt/lowcmd</c> publisher but does not start the control loop
    /// or release the sport service. Use <see cref="BeginLowLevelSessionAsync"/> for the full sequence.
    /// </remarks>
    public LowLevelController LowLevel => _lowLevel ??= new LowLevelController(
        _participant,
        _options,
        _loggerFactory.CreateLogger<LowLevelController>());

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        TransitionTo(ConnectionState.Connecting);

        await _participant.StartAsync(cancellationToken).ConfigureAwait(false);

        _lowStateSubscriber ??= _participant.CreateSubscriber<LowState>(
            Topics.LowState,
            _options.TelemetryQueueCapacity);

        _sportStateSubscriber ??= _participant.CreateSubscriber<SportModeState>(
            Topics.SportModeState,
            _options.TelemetryQueueCapacity);

        // Waiting for real telemetry rather than just a successful socket bind is the difference between
        // "the transport started" and "the robot is actually there". Multicast can succeed at every layer
        // and still deliver nothing.
        bool observed = await WaitForFirstStateAsync(cancellationToken).ConfigureAwait(false);

        if (!observed)
        {
            TransitionTo(ConnectionState.Faulted, "No telemetry received.");

            throw new UnitreeConnectionException(
                $"No telemetry arrived within {_options.ConnectTimeout.TotalSeconds:0.#} s on transport '{_participant.Transport.Name}'. " +
                "Check that the robot is powered, that Unitree:NetworkInterface names the robot-facing NIC, " +
                "and that multicast is not being filtered. See docs/dds-networking.md.");
        }

        TransitionTo(ConnectionState.Connected);
        _logger.LogInformation("Connected to {Model} via {Transport}.", Model, _participant.Transport.Name);
    }

    /// <summary>
    /// Prepares the robot for direct joint control and starts the control loop.
    /// </summary>
    /// <param name="settleDelay">How long to wait after releasing the sport service.</param>
    /// <param name="cancellationToken">Cancels the sequence.</param>
    /// <returns>The started low-level controller.</returns>
    /// <remarks>
    /// Performs the full sequence that low-level control requires: release the on-board motion
    /// controller, let the robot settle, then start publishing. Skipping the release step is the most
    /// common reason low-level commands appear to do nothing.
    /// </remarks>
    public async Task<LowLevelController> BeginLowLevelSessionAsync(
        TimeSpan? settleDelay = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State != ConnectionState.Connected)
        {
            throw new InvalidOperationException(
                $"The robot must be connected before starting a low-level session; current state is {State}.");
        }

        bool released = await MotionSwitcher.EnsureReleasedAsync(cancellationToken).ConfigureAwait(false);

        if (released)
        {
            await Task.Delay(settleDelay ?? TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        LowLevelController controller = LowLevel;
        controller.SetAllDamping();
        controller.Start();

        _logger.LogWarning(
            "Low-level session started. The safety envelope is the only thing between application code and the motors.");

        return controller;
    }

    /// <summary>Gets the most recent low-level state.</summary>
    public bool TryGetLowState(out LowState state)
    {
        if (_lowStateSubscriber is null)
        {
            state = default;
            return false;
        }

        return _lowStateSubscriber.TryGetLatest(out state);
    }

    /// <summary>Gets the most recent high-level locomotion state.</summary>
    public bool TryGetSportState(out SportModeState state)
    {
        if (_sportStateSubscriber is null)
        {
            state = default;
            return false;
        }

        return _sportStateSubscriber.TryGetLatest(out state);
    }

    /// <summary>
    /// Refreshes <see cref="State"/> from telemetry freshness.
    /// </summary>
    /// <remarks>Call periodically; the connection does not poll on its own.</remarks>
    public ConnectionState RefreshConnectionState()
    {
        DateTimeOffset? lastLow = _lowStateSubscriber?.LastReceivedAt;
        DateTimeOffset? lastSport = _sportStateSubscriber?.LastReceivedAt;
        DateTimeOffset? mostRecent = (lastLow, lastSport) switch
        {
            (null, null) => null,
            (not null, null) => lastLow,
            (null, not null) => lastSport,
            _ => lastLow > lastSport ? lastLow : lastSport,
        };

        if (mostRecent is null)
        {
            return State;
        }

        TimeSpan age = DateTimeOffset.UtcNow - mostRecent.Value;

        // A generous multiple of the state timeout: the low-level controller reacts to staleness in
        // milliseconds, whereas this is a coarse health signal for dashboards and health checks.
        ConnectionState target = age > _options.Safety.StateTimeout * 10
            ? ConnectionState.Stale
            : ConnectionState.Connected;

        if (target != State)
        {
            TransitionTo(target, $"Telemetry age {age.TotalMilliseconds:0} ms.");
        }

        return target;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _lowLevel?.Stop();

        if (_sport is not null)
        {
            try
            {
                await _sport.DampAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Disconnecting must not throw just because the robot has already gone away.
                _logger.LogWarning(ex, "Could not damp the robot during disconnect.");
            }
        }

        await _participant.Transport.StopAsync(cancellationToken).ConfigureAwait(false);
        TransitionTo(ConnectionState.Disconnected);
    }

    private async Task<bool> WaitForFirstStateAsync(CancellationToken cancellationToken)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(_options.ConnectTimeout.TotalSeconds * Stopwatch.Frequency);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (_lowStateSubscriber?.ReceivedCount > 0 || _sportStateSubscriber?.ReceivedCount > 0)
            {
                return true;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private void TransitionTo(ConnectionState next, string? reason = null)
    {
        ConnectionState previous;

        lock (_stateLock)
        {
            if (_state == next)
            {
                return;
            }

            previous = _state;
            _state = next;
        }

        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(previous, next, reason));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _lowLevel?.Dispose();
        _sport?.Dispose();
        _motionSwitcher?.Dispose();
        _lowStateSubscriber?.Dispose();
        _sportStateSubscriber?.Dispose();

        await _participant.DisposeAsync().ConfigureAwait(false);
    }
}
