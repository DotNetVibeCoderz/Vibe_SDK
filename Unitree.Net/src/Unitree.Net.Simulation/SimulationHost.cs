using System.Diagnostics;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Messages;
using Unitree.Net.Messages.Go;

namespace Unitree.Net.Simulation;

/// <summary>
/// Settings for a simulation run.
/// </summary>
public sealed class SimulationOptions
{
    /// <summary>Which platform to simulate.</summary>
    public RobotModel Model { get; set; } = RobotModel.Go2;

    /// <summary>Multicast group the simulated robot publishes to.</summary>
    public string MulticastAddress { get; set; } = "239.255.0.1";

    /// <summary>Multicast port.</summary>
    public int MulticastPort { get; set; } = 7447;

    /// <summary>Network interface address to bind, or empty for the default route.</summary>
    public string NetworkInterface { get; set; } = string.Empty;

    /// <summary>Rate at which low-level state is published, in hertz.</summary>
    /// <remarks>
    /// Defaults to the 500 Hz real firmware uses. Matching it exercises a consumer's queueing and drop
    /// behaviour honestly; a slower stream hides backpressure bugs until hardware arrives.
    /// </remarks>
    public int LowStateRateHz { get; set; } = 500;

    /// <summary>Divisor applied to <see cref="LowStateRateHz"/> for locomotion state.</summary>
    public int SportStateDivisor { get; set; } = 10;
}

/// <summary>
/// Live counters for a running simulation.
/// </summary>
/// <param name="IsRunning">Whether the loop is publishing.</param>
/// <param name="LowStateCount">Low-level state messages published.</param>
/// <param name="SportStateCount">Locomotion state messages published.</param>
/// <param name="PublishFailureCount">Publishes that threw.</param>
/// <param name="MeanJitterMicroseconds">Mean loop jitter.</param>
/// <param name="MaxJitterMicroseconds">Worst loop jitter seen.</param>
/// <param name="TransportName">Name of the transport in use.</param>
/// <param name="UptimeSeconds">Seconds since the run started.</param>
public readonly record struct SimulationStatistics(
    bool IsRunning,
    long LowStateCount,
    long SportStateCount,
    long PublishFailureCount,
    double MeanJitterMicroseconds,
    double MaxJitterMicroseconds,
    string TransportName,
    double UptimeSeconds);

/// <summary>
/// Runs a <see cref="SimulatedRobot"/> and publishes its telemetry over a real SDK transport.
/// </summary>
/// <remarks>
/// <para>
/// Because the messages go out through the same publishers the SDK uses everywhere else, a consumer
/// cannot tell this apart from a robot: the CLI connects, the dashboard fills in, health checks go
/// green. That is the whole point — it makes every layer above the transport developable without
/// hardware.
/// </para>
/// <para>
/// Humanoids publish locomotion state only. Their low-level messages belong to the <c>unitree_hg</c>
/// IDL, which this SDK does not implement yet; emitting a quadruped-shaped <c>LowState</c> for a G1
/// would be worse than emitting nothing, because it would look like it worked.
/// </para>
/// </remarks>
public sealed class SimulationHost : IAsyncDisposable
{
    private readonly SimulationLog _log;

    private SimulationOptions _options = new();
    private SimulatedRobot _robot = new(RobotModel.Go2);
    private ManagedMulticastTransport? _transport;
    private DdsParticipant? _participant;
    private IDdsPublisher<LowState>? _lowStatePublisher;
    private IDdsPublisher<SportModeState>? _sportPublisher;
    private SimulatedServiceHub? _services;
    private RealtimeLoop? _loop;

    private long _startTimestamp;
    private long _tick;
    private long _lowStateCount;
    private long _sportStateCount;
    private long _publishFailureCount;
    private bool _publishesLowState;
    private bool _disposed;

    /// <summary>Creates a host writing to <paramref name="log"/>.</summary>
    /// <param name="log">Log to report lifecycle and failures to.</param>
    public SimulationHost(SimulationLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Raised after each simulation tick that produced a published message.</summary>
    /// <remarks>Fires on the control loop thread. Do no work of consequence in the handler.</remarks>
    public event EventHandler? Ticked;

    /// <summary>Whether the loop is currently publishing.</summary>
    public bool IsRunning => _loop is not null;

    /// <summary>The robot being simulated. Replaced when the model changes.</summary>
    public SimulatedRobot Robot => _robot;

    /// <summary>The options the current or next run uses.</summary>
    public SimulationOptions Options => _options;

    /// <summary>
    /// Starts publishing.
    /// </summary>
    /// <param name="options">Settings for this run.</param>
    /// <param name="cancellationToken">Cancels transport startup.</param>
    /// <exception cref="InvalidOperationException">A run is already in progress.</exception>
    public async Task StartAsync(SimulationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            throw new InvalidOperationException("The simulation is already running. Stop it first.");
        }

        _options = options;

        // Starts resting, like a robot that has just been powered on. Standing it up here would let
        // an application skip StandUp and BalanceStand and still drive — which works in the simulator
        // and then fails silently on hardware, the exact trap the simulator exists to expose.
        _robot = new SimulatedRobot(options.Model);

        _lowStateCount = 0;
        _sportStateCount = 0;
        _publishFailureCount = 0;
        _tick = 0;

        _publishesLowState = RobotModelInfo.IsQuadruped(options.Model);

        var unitreeOptions = new UnitreeOptions
        {
            Model = options.Model,
            Transport = DdsTransportKind.ManagedMulticast,
            MulticastAddress = options.MulticastAddress,
            MulticastPort = options.MulticastPort,
            NetworkInterface = options.NetworkInterface,
        };

        try
        {
            _transport = new ManagedMulticastTransport(unitreeOptions);
            _participant = new DdsParticipant(_transport);
            await _participant.StartAsync(cancellationToken).ConfigureAwait(false);

            _sportPublisher = _participant.CreatePublisher<SportModeState>(Topics.SportModeState);

            if (_publishesLowState)
            {
                _lowStatePublisher = _participant.CreatePublisher<LowState>(Topics.LowState);
            }

            // Without these the simulator publishes telemetry and answers nothing, so every
            // application that commands motion connects, reads state, and then times out on its first
            // StandUpAsync with "Service 'sport' did not respond".
            _services = new SimulatedServiceHub(_participant, _robot, _log);
        }
        catch (Exception exception)
        {
            _log.Error("transport", $"Could not start: {exception.Message}");
            await TearDownAsync().ConfigureAwait(false);
            throw;
        }

        _log.Info("transport", $"Publishing on {options.MulticastAddress}:{options.MulticastPort} via {_transport.Name}.");
        _log.Info("model", $"{RobotRig.For(options.Model).DisplayName} — {_robot.Rig.JointCount} joints.");
        _log.Info("service", "Answering sport, motion_switcher and robot_state requests.");

        if (_publishesLowState)
        {
            _log.Info("topics", $"{Topics.LowState} at {options.LowStateRateHz} Hz, {Topics.SportModeState} at {options.LowStateRateHz / options.SportStateDivisor} Hz.");
        }
        else
        {
            _log.Warning(
                "topics",
                $"{Topics.SportModeState} only. Humanoid low-level state needs the unitree_hg IDL, which this SDK does not implement yet.");
        }

        _startTimestamp = Stopwatch.GetTimestamp();
        _loop = new RealtimeLoop(options.LowStateRateHz, OnTick);
        _loop.Start();

        _log.Info("loop", $"Control loop started at {options.LowStateRateHz} Hz.");
    }

    /// <summary>Stops publishing and releases the transport.</summary>
    public async Task StopAsync()
    {
        if (_loop is null)
        {
            return;
        }

        RealtimeLoop loop = _loop;
        _loop = null;
        loop.Stop();

        LoopStatistics stats = loop.Statistics;
        loop.Dispose();

        _log.Info(
            "loop",
            $"Stopped after {stats.TickCount:N0} ticks — mean jitter {stats.MeanJitterMicroseconds:0} µs, max {stats.MaxJitterMicroseconds:0} µs, {stats.OverrunCount:N0} overruns.");

        await TearDownAsync().ConfigureAwait(false);
        _log.Info("transport", $"Released. Published {_lowStateCount:N0} low-state and {_sportStateCount:N0} sport-state messages.");
    }

    /// <summary>Reads the current counters.</summary>
    public SimulationStatistics GetStatistics()
    {
        LoopStatistics loopStats = _loop?.Statistics ?? default;

        return new SimulationStatistics(
            IsRunning,
            Interlocked.Read(ref _lowStateCount),
            Interlocked.Read(ref _sportStateCount),
            Interlocked.Read(ref _publishFailureCount),
            loopStats.MeanJitterMicroseconds,
            loopStats.MaxJitterMicroseconds,
            _transport?.Name ?? "none",
            IsRunning ? Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds : 0);
    }

    private void OnTick(in ControlTickContext context)
    {
        SimulatedRobot robot = _robot;
        robot.Advance(context.DeltaSeconds);

        try
        {
            if (_publishesLowState && _lowStatePublisher is { } lowState)
            {
                uint tickMilliseconds = (uint)Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
                lowState.Publish(robot.BuildLowState(tickMilliseconds));
                Interlocked.Increment(ref _lowStateCount);
            }

            // Locomotion state is supervisory, not a control input, so it goes out an order of
            // magnitude slower — matching what the real robot does.
            if (_tick % _options.SportStateDivisor == 0 && _sportPublisher is { } sport)
            {
                sport.Publish(robot.BuildSportModeState());
                Interlocked.Increment(ref _sportStateCount);
            }
        }
        catch (Exception exception)
        {
            // A publish failure must not tear down the loop: a transient socket error would otherwise
            // stop the whole simulation, and the log entry is more useful than a dead run.
            if (Interlocked.Increment(ref _publishFailureCount) == 1)
            {
                _log.Error("transport", $"Publish failed: {exception.Message}. Further failures are counted, not logged.");
            }
        }

        _tick++;
        Ticked?.Invoke(this, EventArgs.Empty);
    }

    private async Task TearDownAsync()
    {
        if (_services is not null)
        {
            // Before the participant, so the pumps stop reading a subscriber that is being disposed.
            await _services.DisposeAsync().ConfigureAwait(false);
            _services = null;
        }

        _lowStatePublisher = null;
        _sportPublisher = null;

        if (_participant is not null)
        {
            await _participant.DisposeAsync().ConfigureAwait(false);
            _participant = null;
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
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
        await StopAsync().ConfigureAwait(false);
    }
}
