using PollenRobotics.Net.Core.Diagnostics;
using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Simulation.Robots;

namespace PollenRobotics.Net.Simulation;

/// <summary>
/// Runs one simulated robot at a fixed rate and publishes frames.
/// </summary>
/// <remarks>
/// <para>
/// The engine owns the clock, not the viewport. Ticking from a render callback would tie the
/// simulation rate to the display refresh, so a dropped frame would slow the robot down and a
/// 144 Hz monitor would speed it up. The renderer reads <see cref="LatestSnapshot"/> whenever it
/// paints and the two run independently.
/// </para>
/// <para>
/// <see cref="FrameProduced"/> fires on the engine's own thread. UI handlers must marshal to the
/// dispatcher themselves - at 50 Hz the cost of getting that wrong is a UI thread that never
/// catches up.
/// </para>
/// </remarks>
public sealed class SimulationEngine : IAsyncDisposable
{
    private readonly RobotLogSink _log;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _running;
    private Task? _loop;
    private RealtimeLoop? _realtime;

    /// <summary>The robot currently loaded.</summary>
    public ISimulatedRobot Robot { get; private set; }

    /// <summary>Tick rate in hertz.</summary>
    public double FrequencyHz { get; }

    /// <summary>True while the engine is ticking.</summary>
    public bool IsRunning => _running is { IsCancellationRequested: false } && _loop is { IsCompleted: false };

    /// <summary>The most recent frame, safe to read from any thread.</summary>
    public SimulationSnapshot LatestSnapshot { get; private set; }

    /// <summary>Where the engine writes its log lines.</summary>
    public RobotLogSink Log => _log;

    /// <summary>Measured tick rate, for the status panel.</summary>
    public double MeasuredRateHz => _realtime is { Ticks: > 0 } loop
        ? loop.Frequency - (loop.MeanJitterMs / 1000 * loop.Frequency * loop.Frequency)
        : 0;

    /// <summary>Ticks whose body overran the period.</summary>
    public long Overruns => _realtime?.Overruns ?? 0;

    /// <summary>Raised once per tick with the fresh frame, on the engine thread.</summary>
    public event Action<SimulationSnapshot>? FrameProduced;

    /// <summary>Raised when the loaded robot changes.</summary>
    public event Action<ISimulatedRobot>? RobotChanged;

    /// <summary>Raised when the engine starts or stops.</summary>
    public event Action<bool>? RunStateChanged;

    /// <summary>Creates an engine.</summary>
    /// <param name="robot">The robot to load, or null for Reachy Mini.</param>
    /// <param name="frequencyHz">Tick rate. 50 Hz matches the MicroDuck control loop.</param>
    /// <param name="log">Where to write log lines, or null for a fresh sink.</param>
    public SimulationEngine(ISimulatedRobot? robot = null, double frequencyHz = 50, RobotLogSink? log = null)
    {
        Robot = robot ?? new SimulatedReachyMini();
        FrequencyHz = frequencyHz;
        _log = log ?? new RobotLogSink();
        LatestSnapshot = Robot.Snapshot();
    }

    /// <summary>Builds the model for a robot family.</summary>
    public static ISimulatedRobot CreateRobot(RobotKind kind) => kind switch
    {
        RobotKind.ReachyMini => new SimulatedReachyMini(),
        RobotKind.MicroDuck => new SimulatedMicroDuck(),
        RobotKind.Reachy2 => new SimulatedReachy2(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown robot kind."),
    };

    /// <summary>
    /// Swaps the loaded robot.
    /// </summary>
    /// <remarks>
    /// Stops the engine first. Ticking a model that is being replaced is a race the viewport would
    /// see as one frame of the wrong robot, and the joint count changes with it.
    /// </remarks>
    public async Task LoadRobotAsync(RobotKind kind, CancellationToken cancellationToken = default)
    {
        bool wasRunning = IsRunning;

        if (wasRunning)
        {
            await StopAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            Robot = CreateRobot(kind);
            LatestSnapshot = Robot.Snapshot();
        }

        _log.Info("simulator", $"Loaded {Robot.Description.DisplayName} ({Robot.Description.JointCount} joints).");
        RobotChanged?.Invoke(Robot);

        if (wasRunning)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts ticking. Safe to call when already running.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _realtime = new RealtimeLoop(FrequencyHz);

        CancellationToken token = _running.Token;
        RealtimeLoop loop = _realtime;

        _loop = Task.Run(async () =>
        {
            await loop.RunAsync((delta, _) =>
            {
                ISimulatedRobot robot;
                lock (_gate)
                {
                    robot = Robot;
                }

                robot.Tick(delta);
                SimulationSnapshot snapshot = robot.Snapshot();
                LatestSnapshot = snapshot;
                FrameProduced?.Invoke(snapshot);

                return ValueTask.CompletedTask;
            }, token).ConfigureAwait(false);
        }, CancellationToken.None);

        _log.Info("simulator", $"Started at {FrequencyHz:0} Hz.");
        RunStateChanged?.Invoke(true);
        return Task.CompletedTask;
    }

    /// <summary>Stops ticking and waits for the loop to unwind.</summary>
    public async Task StopAsync()
    {
        if (_running is not { } running)
        {
            return;
        }

        await running.CancelAsync().ConfigureAwait(false);

        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        running.Dispose();
        _running = null;
        _loop = null;

        _log.Info("simulator", "Stopped.");
        RunStateChanged?.Invoke(false);
    }

    /// <summary>Returns the loaded robot to its power-on state.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            Robot.Reset();
            LatestSnapshot = Robot.Snapshot();
        }

        _realtime?.ResetStatistics();
        _log.Info("simulator", "Reset to power-on state.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
