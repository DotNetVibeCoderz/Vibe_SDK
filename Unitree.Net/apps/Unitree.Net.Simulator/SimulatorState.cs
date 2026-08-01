using Unitree.Net.Core;
using Unitree.Net.Simulation;

namespace Unitree.Net.Simulator;

/// <summary>
/// Application state shared between the Blazor UI and the simulation.
/// </summary>
/// <remarks>
/// <para>
/// This exists to decouple the two rates. The simulation runs at 500 Hz; a UI that re-rendered on
/// every tick would spend all of its time in diffing and none of it drawing. Instead the state is
/// sampled on two timers — a fast one for the viewport pose and a slow one for the panels — and the
/// UI is only ever told about the sample.
/// </para>
/// </remarks>
public sealed class SimulatorState : IAsyncDisposable
{
    /// <summary>Pose updates per second pushed to the 3D viewport.</summary>
    private const int PoseRateHz = 50;

    /// <summary>Panel refreshes per second. Text that changes faster than this is unreadable anyway.</summary>
    private const int PanelRateHz = 5;

    private readonly SimulationHost _host;
    private readonly SimulationLog _log;
    private readonly Timer _poseTimer;
    private readonly Timer _panelTimer;

    private RobotModel _selectedModel = RobotModel.Go2;
    private SimulationSnapshot? _snapshot;
    private SimulationStatistics _statistics;
    private bool _disposed;

    /// <summary>Creates the shared state.</summary>
    /// <param name="host">The simulation host to drive.</param>
    /// <param name="log">The log both the simulation and the UI write to.</param>
    public SimulatorState(SimulationHost host, SimulationLog log)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(log);

        _host = host;
        _log = log;

        _poseTimer = new Timer(_ => SamplePose(), null, Timeout.Infinite, Timeout.Infinite);
        _panelTimer = new Timer(_ => SamplePanels(), null, Timeout.Infinite, Timeout.Infinite);

        _log.Info("app", "Unitree.Net Simulator ready. Pick a platform and press Start.");
    }

    /// <summary>Raised roughly <see cref="PoseRateHz"/> times a second while running.</summary>
    public event Action<SimulationSnapshot>? PoseSampled;

    /// <summary>Raised roughly <see cref="PanelRateHz"/> times a second, and on every state change.</summary>
    public event Action? Changed;

    /// <summary>The log panel's backing store.</summary>
    public SimulationLog Log => _log;

    /// <summary>The most recent simulation sample, or <see langword="null"/> before the first run.</summary>
    public SimulationSnapshot? Snapshot => _snapshot;

    /// <summary>The most recent transport and loop counters.</summary>
    public SimulationStatistics Statistics => _statistics;

    /// <summary>Whether the simulation is publishing.</summary>
    public bool IsRunning => _host.IsRunning;

    /// <summary>Settings the next run will use.</summary>
    public SimulationOptions Options { get; } = new();

    /// <summary>The rig for the selected platform, used to build the viewport.</summary>
    public RobotRig Rig { get; private set; } = RobotRig.For(RobotModel.Go2);

    /// <summary>Which platform is selected. Changing it while running is refused.</summary>
    public RobotModel SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (_selectedModel == value || _host.IsRunning)
            {
                return;
            }

            _selectedModel = value;
            Rig = RobotRig.For(value);
            _snapshot = null;
            _log.Info("model", $"Selected {Rig.DisplayName} — {Rig.Summary}");
            Changed?.Invoke();
        }
    }

    /// <summary>Forward speed command in metres per second.</summary>
    public float CommandForward { get; private set; }

    /// <summary>Lateral speed command in metres per second, left positive.</summary>
    public float CommandLateral { get; private set; }

    /// <summary>Yaw rate command in radians per second, counter-clockwise positive.</summary>
    public float CommandYaw { get; private set; }

    /// <summary>Starts the simulation.</summary>
    public async Task StartAsync()
    {
        if (_host.IsRunning)
        {
            return;
        }

        Options.Model = _selectedModel;

        try
        {
            await _host.StartAsync(Options).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Error("app", $"Start failed: {exception.Message}");
            Changed?.Invoke();
            return;
        }

        _poseTimer.Change(0, 1000 / PoseRateHz);
        _panelTimer.Change(0, 1000 / PanelRateHz);
        Changed?.Invoke();
    }

    /// <summary>Stops the simulation and parks the timers.</summary>
    public async Task StopAsync()
    {
        if (!_host.IsRunning)
        {
            return;
        }

        _poseTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _panelTimer.Change(Timeout.Infinite, Timeout.Infinite);

        await _host.StopAsync().ConfigureAwait(false);

        SetCommand(0f, 0f, 0f);
        _statistics = _host.GetStatistics();
        Changed?.Invoke();
    }

    /// <summary>Sets the commanded velocity.</summary>
    /// <param name="forward">Forward speed in metres per second.</param>
    /// <param name="lateral">Left-positive strafe speed in metres per second.</param>
    /// <param name="yawRate">Counter-clockwise yaw rate in radians per second.</param>
    public void SetCommand(float forward, float lateral, float yawRate)
    {
        CommandForward = forward;
        CommandLateral = lateral;
        CommandYaw = yawRate;

        _host.Robot.Command = new SimulatedVelocity(forward, lateral, yawRate);
        Changed?.Invoke();
    }

    /// <summary>Stands the robot up.</summary>
    public void StandUp()
    {
        _host.Robot.StandUp();
        _log.Info("control", "Stand up.");
        Changed?.Invoke();
    }

    /// <summary>Lies the robot down and cancels motion.</summary>
    public void StandDown()
    {
        _host.Robot.StandDown();
        SetCommand(0f, 0f, 0f);
        _log.Info("control", "Stand down.");
    }

    /// <summary>Overrides the battery charge, for exercising low-battery handling downstream.</summary>
    /// <param name="stateOfCharge">Charge to set, 0–100.</param>
    public void SetBattery(float stateOfCharge)
    {
        _host.Robot.SetBatterySoc(stateOfCharge);
        _log.Info("control", $"Battery forced to {stateOfCharge:0}%.");
        Changed?.Invoke();
    }

    private void SamplePose()
    {
        if (!_host.IsRunning)
        {
            return;
        }

        SimulationSnapshot snapshot = _host.Robot.Capture();
        _snapshot = snapshot;
        PoseSampled?.Invoke(snapshot);
    }

    private void SamplePanels()
    {
        _statistics = _host.GetStatistics();
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _poseTimer.DisposeAsync().ConfigureAwait(false);
        await _panelTimer.DisposeAsync().ConfigureAwait(false);
        await _host.DisposeAsync().ConfigureAwait(false);
    }
}
