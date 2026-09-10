using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PollenRobotics.Net.Core.Connectivity;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.MicroDuck.Transports;

namespace PollenRobotics.Net.MicroDuck;

/// <summary>
/// MicroDuck: a 25 cm biped that walks on reinforcement-learning policies.
/// </summary>
/// <remarks>
/// <para>
/// The mental model is different from the Reachy robots. You do not command joint angles - the
/// policy does that at 50 Hz. You send intents: a velocity for the walk policy to track, or an
/// action slot to run to completion. Reaching past that to the servos means replacing the policy,
/// which is <c>robotctl policy load</c> territory rather than an SDK call.
/// </para>
/// <para>
/// The duck must be initialised before it will do anything: <see cref="InitAsync"/> powers the
/// servos and ramps to the home pose. Commanding a relaxed duck is silently ignored.
/// </para>
/// </remarks>
public sealed class MicroDuckClient : IRobotClient
{
    private readonly IMicroDuckTransport _transport;
    private readonly bool _ownsTransport;
    private readonly ILogger _logger;

    /// <inheritdoc />
    public RobotKind Kind => RobotKind.MicroDuck;

    /// <inheritdoc />
    public RobotDescription Description => RobotCatalog.MicroDuck;

    /// <inheritdoc />
    public ConnectionState State => _transport.State;

    /// <inheritdoc />
    public string Endpoint => _transport.Endpoint;

    /// <inheritdoc />
    public event Action<ConnectionState>? StateChanged;

    /// <summary>Raised whenever a fresh state snapshot arrives, at up to the 50 Hz loop rate.</summary>
    public event Action<MicroDuckState>? StateUpdated;

    /// <summary>The transport in use.</summary>
    public IMicroDuckTransport Transport => _transport;

    /// <summary>The most recent state seen, without a round trip.</summary>
    public MicroDuckState LastState { get; private set; } = MicroDuckState.Empty;

    /// <summary>Wraps an existing transport.</summary>
    public MicroDuckClient(IMicroDuckTransport transport, ILogger<MicroDuckClient>? logger = null, bool ownsTransport = false)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ownsTransport = ownsTransport;
        _logger = logger ?? NullLogger<MicroDuckClient>.Instance;

        _transport.StateChanged += OnTransportStateChanged;
        _transport.StateUpdated += OnTransportStateUpdated;
    }

    /// <summary>Builds a client wired to a real robotd.</summary>
    public static MicroDuckClient Connect(MicroDuckOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        MicroDuckOptions resolved = options ?? MicroDuckOptions.Default;
        var transport = new MicroDuckDaemonTransport(resolved, loggerFactory?.CreateLogger<MicroDuckDaemonTransport>());
        return new MicroDuckClient(transport, loggerFactory?.CreateLogger<MicroDuckClient>(), ownsTransport: true);
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default) => _transport.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => _transport.DisconnectAsync(cancellationToken);

    /// <summary>Powers the servos and ramps to the home pose. Nothing else works until this runs.</summary>
    public async Task InitAsync(CancellationToken cancellationToken = default)
    {
        await _transport.InitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("MicroDuck initialised and standing.");
    }

    /// <summary>Cuts servo power. The duck collapses where it stands.</summary>
    public Task RelaxAsync(CancellationToken cancellationToken = default) => _transport.RelaxAsync(cancellationToken);

    /// <inheritdoc />
    public Task EnableMotorsAsync(CancellationToken cancellationToken = default) => InitAsync(cancellationToken);

    /// <inheritdoc />
    public Task DisableMotorsAsync(CancellationToken cancellationToken = default) => RelaxAsync(cancellationToken);

    /// <summary>Sends one velocity intent to the walk policy.</summary>
    public Task DriveAsync(DuckVelocity velocity, CancellationToken cancellationToken = default) =>
        _transport.SetVelocityAsync(velocity, cancellationToken);

    /// <summary>Sends one velocity intent, in the units most call sites have to hand.</summary>
    public Task DriveAsync(double forward, double lateral = 0, double turnDegreesPerSecond = 0, CancellationToken cancellationToken = default) =>
        DriveAsync(new DuckVelocity(forward, lateral, Angle.FromDegrees(turnDegreesPerSecond).Radians), cancellationToken);

    /// <summary>Stops the duck.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default) =>
        DriveAsync(DuckVelocity.Zero, cancellationToken);

    /// <summary>
    /// Holds a velocity for a fixed time, resending it at the loop rate, then stops.
    /// </summary>
    /// <remarks>
    /// The daemon holds whatever it was last told, so a one-shot command walks forever. Keeping the
    /// stream alive for a bounded window and then zeroing is what "walk forward for two seconds"
    /// actually means. The stop runs even if the caller cancels, because a cancelled walk that
    /// leaves the duck walking is worse than no walk at all.
    /// </remarks>
    public async Task DriveForAsync(DuckVelocity velocity, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var loop = new RealtimeLoop(50);
        TimeSpan elapsed = TimeSpan.Zero;

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await loop.RunAsync(async (delta, token) =>
            {
                elapsed += delta;
                if (elapsed >= duration)
                {
                    await window.CancelAsync().ConfigureAwait(false);
                    return;
                }

                await _transport.SetVelocityAsync(velocity, token).ConfigureAwait(false);
            }, window.Token).ConfigureAwait(false);
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _transport.SetVelocityAsync(DuckVelocity.Zero, stopTimeout.Token).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Runs one action slot to completion.</summary>
    public Task PerformAsync(DuckActionSlot slot, CancellationToken cancellationToken = default) =>
        _transport.PerformAsync(slot, cancellationToken);

    /// <summary>Runs a named custom skill.</summary>
    public Task PerformSkillAsync(string skillName, CancellationToken cancellationToken = default) =>
        _transport.PerformSkillAsync(skillName, cancellationToken);

    /// <summary>Sits down.</summary>
    public Task SitAsync(CancellationToken cancellationToken = default) =>
        PerformAsync(DuckActionSlot.SitStand, cancellationToken);

    /// <summary>Stands up, including from a fall.</summary>
    public Task StandUpAsync(CancellationToken cancellationToken = default) =>
        PerformAsync(DuckActionSlot.Stand, cancellationToken);

    /// <summary>Rolls over and recovers - the move to run after a tip-over.</summary>
    public Task RecoverAsync(CancellationToken cancellationToken = default) =>
        PerformAsync(DuckActionSlot.Roulade, cancellationToken);

    /// <summary>Picks up whatever is in front of the beak.</summary>
    public Task PickAsync(CancellationToken cancellationToken = default) =>
        PerformAsync(DuckActionSlot.GroundPick, cancellationToken);

    /// <summary>Kicks.</summary>
    public Task KickAsync(bool leftFoot = true, CancellationToken cancellationToken = default) =>
        PerformAsync(leftFoot ? DuckActionSlot.KickLeft : DuckActionSlot.KickRight, cancellationToken);

    /// <summary>Plays the duck's voice signature.</summary>
    public Task QuackAsync(CancellationToken cancellationToken = default) => _transport.QuackAsync(cancellationToken);

    /// <summary>Lists the slots and skills the daemon has loaded.</summary>
    public Task<IReadOnlyList<string>> ListSkillsAsync(CancellationToken cancellationToken = default) =>
        _transport.ListSkillsAsync(cancellationToken);

    /// <summary>Reads the daemon health report.</summary>
    public Task<MicroDuckHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        _transport.GetHealthAsync(cancellationToken);

    /// <summary>Reads one frame from the time-of-flight sensor, or null when there is none.</summary>
    public Task<TofFrame?> ReadTimeOfFlightAsync(CancellationToken cancellationToken = default) =>
        _transport.ReadTimeOfFlightAsync(cancellationToken);

    /// <summary>Fetches a fresh state snapshot.</summary>
    public async Task<MicroDuckState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        MicroDuckState state = await _transport.GetStateAsync(cancellationToken).ConfigureAwait(false);
        LastState = state;
        return state;
    }

    /// <inheritdoc />
    public async Task<double[]> GetJointPositionsAsync(CancellationToken cancellationToken = default)
    {
        MicroDuckState state = await GetStateAsync(cancellationToken).ConfigureAwait(false);

        double[] joints = new double[Description.JointCount];
        for (int i = 0; i < Math.Min(state.JointPositions.Count, joints.Length); i++)
        {
            joints[i] = state.JointPositions[i];
        }

        return joints;
    }

    /// <summary>
    /// Watches for a fall and runs the recovery move when one happens.
    /// </summary>
    /// <remarks>
    /// Runs until cancelled. Intended to be started once alongside an application's main logic:
    /// a duck that has fallen over ignores velocity commands, so without this the application looks
    /// hung rather than tipped over.
    /// </remarks>
    public async Task RunFallRecoveryAsync(CancellationToken cancellationToken)
    {
        var loop = new RealtimeLoop(5);

        await loop.RunAsync(async (_, token) =>
        {
            MicroDuckState state = await GetStateAsync(token).ConfigureAwait(false);
            if (!state.IsFallen)
            {
                return;
            }

            _logger.LogWarning("MicroDuck is down; running the recovery move.");
            await RecoverAsync(token).ConfigureAwait(false);
            await StandUpAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private void OnTransportStateChanged(ConnectionState state) => StateChanged?.Invoke(state);

    private void OnTransportStateUpdated(MicroDuckState state)
    {
        LastState = state;
        StateUpdated?.Invoke(state);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _transport.StateChanged -= OnTransportStateChanged;
        _transport.StateUpdated -= OnTransportStateUpdated;

        if (_ownsTransport)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
