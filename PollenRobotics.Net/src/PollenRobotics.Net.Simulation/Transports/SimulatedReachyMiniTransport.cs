using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.ReachyMini;
using PollenRobotics.Net.ReachyMini.Moves;
using PollenRobotics.Net.Simulation.Robots;

namespace PollenRobotics.Net.Simulation.Transports;

/// <summary>
/// An in-process transport that drives a <see cref="SimulatedReachyMini"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes "the same code runs on the robot and in the simulator" true rather than
/// aspirational: <see cref="ReachyMiniClient"/> talks to <see cref="IReachyMiniTransport"/> and does
/// not know which side of that interface it is on. Every gallery page, sample and wizard template
/// swaps between them by changing one line.
/// </para>
/// <para>
/// It deliberately keeps the asynchrony. Everything here could return a completed task
/// synchronously, and then code that works in the simulator would deadlock the first time it met a
/// real network round trip. The small artificial latency exists for the same reason.
/// </para>
/// </remarks>
public sealed class SimulatedReachyMiniTransport : IReachyMiniTransport
{
    private readonly SimulatedReachyMini _robot;
    private readonly SimulationEngine? _engine;
    private readonly TimeSpan _latency;
    private ConnectionState _state = ConnectionState.Disconnected;
    private int _disposed;

    /// <inheritdoc />
    public string Endpoint => "simulation://reachy-mini";

    /// <inheritdoc />
    public ConnectionState State => _state;

    /// <inheritdoc />
    public event Action<ConnectionState>? StateChanged;

    /// <inheritdoc />
    public event Action<ReachyMiniState>? StateUpdated;

    /// <summary>The model being driven.</summary>
    public SimulatedReachyMini Robot => _robot;

    /// <summary>Wraps a model, optionally subscribing to an engine's frames.</summary>
    /// <param name="robot">The model to drive.</param>
    /// <param name="engine">
    /// When given, state updates are published from the engine's tick instead of only on request,
    /// which is what a daemon pushing its pose stream looks like.
    /// </param>
    /// <param name="simulatedLatency">
    /// Artificial round-trip delay. A few milliseconds by default so that code which accidentally
    /// depends on a command completing before the next line runs fails here rather than on hardware.
    /// </param>
    public SimulatedReachyMiniTransport(
        SimulatedReachyMini? robot = null,
        SimulationEngine? engine = null,
        TimeSpan? simulatedLatency = null)
    {
        _robot = robot ?? (engine?.Robot as SimulatedReachyMini) ?? new SimulatedReachyMini();
        _engine = engine;
        _latency = simulatedLatency ?? TimeSpan.FromMilliseconds(2);

        if (_engine is not null)
        {
            _engine.FrameProduced += OnFrame;
        }
    }

    /// <summary>Builds a transport with its own engine already running.</summary>
    public static async Task<SimulatedReachyMiniTransport> StartAsync(CancellationToken cancellationToken = default)
    {
        var robot = new SimulatedReachyMini();
        var engine = new SimulationEngine(robot);
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        return new SimulatedReachyMiniTransport(robot, engine);
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Connecting);
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        SetState(ConnectionState.Connected);
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SetTargetAsync(ReachyMiniTarget target, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        _robot.SetTarget(target);
        Publish();
    }

    /// <inheritdoc />
    public async Task GotoTargetAsync(ReachyMiniTarget target, TimeSpan duration, InterpolationMethod method, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        _robot.StartGoto(target, duration, method);
        Publish();
    }

    /// <inheritdoc />
    public async Task CancelMoveAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        _robot.CancelMove();
        Publish();
    }

    /// <inheritdoc />
    public async Task<ReachyMiniState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        ReachyMiniState state = _robot.State();
        StateUpdated?.Invoke(state);
        return state;
    }

    /// <inheritdoc />
    public async Task SetMotorModeAsync(MotorMode mode, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        _robot.SetMotorMode(mode);
        Publish();
    }

    /// <inheritdoc />
    public Task SetTorqueAsync(bool enabled, IReadOnlyList<string>? motorIds = null, CancellationToken cancellationToken = default) =>
        SetMotorModeAsync(enabled ? MotorMode.Enabled : MotorMode.Disabled, cancellationToken);

    /// <inheritdoc />
    public async Task SetHeadTrackingAsync(bool enabled, double weight = 1, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        _robot.SetHeadTracking(enabled, weight);
    }

    /// <inheritdoc />
    public async Task<FaceTarget> GetTrackedFaceAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        return _robot.HeadTracking ? _robot.SimulatedFace : FaceTarget.None;
    }

    /// <inheritdoc />
    public async Task SetWobblingAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        _robot.SetWobbling(enabled);
    }

    /// <inheritdoc />
    public async Task SetAutomaticBodyYawAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        _robot.AutomaticBodyYaw = enabled;
    }

    /// <summary>
    /// Always null: the simulated robot models a Lite, which has no IMU.
    /// </summary>
    /// <remarks>
    /// Returning plausible numbers here would let an application depend on an IMU that half the
    /// Reachy Mini fleet does not have.
    /// </remarks>
    public Task<ImuReading?> GetImuAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ImuReading?>(null);

    /// <inheritdoc />
    public Task StartRecordingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<MoveFrame>> StopRecordingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MoveFrame>>([]);

    private void OnFrame(SimulationSnapshot snapshot)
    {
        if (_state == ConnectionState.Connected)
        {
            StateUpdated?.Invoke(_robot.State());
        }
    }

    private void Publish() => StateUpdated?.Invoke(_robot.State());

    private void RequireConnected()
    {
        if (_state != ConnectionState.Connected)
        {
            throw new Core.RobotConnectionException("Not connected. Call ConnectAsync first.");
        }
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
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        if (_engine is not null)
        {
            _engine.FrameProduced -= OnFrame;
        }

        SetState(ConnectionState.Disconnected);
        return ValueTask.CompletedTask;
    }
}
