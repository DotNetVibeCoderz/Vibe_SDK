using PollenRobotics.Net.Core;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.MicroDuck;
using PollenRobotics.Net.Simulation.Robots;

namespace PollenRobotics.Net.Simulation.Transports;

/// <summary>
/// An in-process transport that drives a <see cref="SimulatedMicroDuck"/>.
/// </summary>
/// <remarks>
/// Action slots block for their scripted duration, as they do on the robot, so a sequence of
/// actions written as consecutive awaits plays in order. Returning immediately would let an
/// application queue four actions in a single tick and appear to work.
/// </remarks>
public sealed class SimulatedMicroDuckTransport : IMicroDuckTransport
{
    private readonly SimulatedMicroDuck _robot;
    private readonly SimulationEngine? _engine;
    private readonly TimeSpan _latency;
    private ConnectionState _state = ConnectionState.Disconnected;
    private int _disposed;

    /// <inheritdoc />
    public string Endpoint => "simulation://microduck";

    /// <inheritdoc />
    public ConnectionState State => _state;

    /// <inheritdoc />
    public event Action<ConnectionState>? StateChanged;

    /// <inheritdoc />
    public event Action<MicroDuckState>? StateUpdated;

    /// <summary>The model being driven.</summary>
    public SimulatedMicroDuck Robot => _robot;

    /// <summary>Wraps a model, optionally subscribing to an engine's frames.</summary>
    public SimulatedMicroDuckTransport(
        SimulatedMicroDuck? robot = null,
        SimulationEngine? engine = null,
        TimeSpan? simulatedLatency = null)
    {
        _robot = robot ?? (engine?.Robot as SimulatedMicroDuck) ?? new SimulatedMicroDuck();
        _engine = engine;
        _latency = simulatedLatency ?? TimeSpan.FromMilliseconds(2);

        if (_engine is not null)
        {
            _engine.FrameProduced += OnFrame;
        }
    }

    /// <summary>Builds a transport with its own engine already running.</summary>
    public static async Task<SimulatedMicroDuckTransport> StartAsync(CancellationToken cancellationToken = default)
    {
        var robot = new SimulatedMicroDuck();
        var engine = new SimulationEngine(robot);
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        return new SimulatedMicroDuckTransport(robot, engine);
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
    public async Task<MicroDuckState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        MicroDuckState state = _robot.State();
        StateUpdated?.Invoke(state);
        return state;
    }

    /// <inheritdoc />
    public async Task<MicroDuckHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        if (_robot.BatteryVolts < 7.0)
        {
            warnings.Add($"Battery low: {_robot.BatteryVolts:0.00} V.");
        }

        if (!_robot.IsInitialised)
        {
            warnings.Add("Servos are not powered. Call InitAsync.");
        }

        return new MicroDuckHealth(_robot.BatteryVolts > 6.5, $"simulated-{SdkInfo.Version}", 50, [], warnings);
    }

    /// <inheritdoc />
    public async Task InitAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();

        // The real thing ramps to the home pose over about a second; making the call return
        // immediately would hide the fact that init takes time.
        await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);
        _robot.Init();
    }

    /// <inheritdoc />
    public async Task RelaxAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        _robot.Relax();
    }

    /// <inheritdoc />
    public async Task SetVelocityAsync(DuckVelocity velocity, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        _robot.SetVelocity(velocity);
    }

    /// <inheritdoc />
    public async Task PerformAsync(DuckActionSlot slot, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        _robot.Perform(slot);

        // Block for the action's duration so consecutive awaits sequence correctly.
        while (_robot.ActiveSlot is not null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task PerformSkillAsync(string skillName, CancellationToken cancellationToken = default)
    {
        // A custom skill maps onto its slot when the name matches one; otherwise it is a no-op the
        // same way an unloaded skill is on the robot.
        try
        {
            return PerformAsync(DuckActionSlotExtensions.ParseSlot(skillName), cancellationToken);
        }
        catch (ArgumentException)
        {
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListSkillsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            [.. Enum.GetValues<DuckActionSlot>().Select(s => s.ToWireValue())]);

    /// <inheritdoc />
    public async Task QuackAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        _engine?.Log.Info("microduck", "Quack.");
    }

    /// <inheritdoc />
    public async Task<TofFrame?> ReadTimeOfFlightAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        return _robot.ReadTimeOfFlight();
    }

    /// <inheritdoc />
    public async Task RebootMotorsAsync(IReadOnlyList<int>? servoIds = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        _robot.Init();
    }

    private void OnFrame(SimulationSnapshot snapshot)
    {
        if (_state == ConnectionState.Connected)
        {
            StateUpdated?.Invoke(_robot.State());
        }
    }

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
