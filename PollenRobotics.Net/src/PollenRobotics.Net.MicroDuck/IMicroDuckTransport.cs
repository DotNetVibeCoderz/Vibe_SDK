using PollenRobotics.Net.Core.Robots;

namespace PollenRobotics.Net.MicroDuck;

/// <summary>
/// The wire operations a MicroDuck client needs.
/// </summary>
/// <remarks>
/// Mirrors the <c>robot.*</c> half of the daemon's JSON-RPC contract. The <c>net.*</c>,
/// <c>system.*</c> and <c>update.*</c> families belong to configd and updaterd and are deliberately
/// out of scope for a control SDK - use <c>robotctl</c> for those.
/// </remarks>
public interface IMicroDuckTransport : IAsyncDisposable
{
    /// <summary>Where this transport points, for display.</summary>
    string Endpoint { get; }

    /// <summary>Current connection state.</summary>
    ConnectionState State { get; }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event Action<ConnectionState>? StateChanged;

    /// <summary>Raised whenever a fresh state snapshot arrives.</summary>
    event Action<MicroDuckState>? StateUpdated;

    /// <summary>Opens the link.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the link.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the current state.</summary>
    Task<MicroDuckState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the daemon health report.</summary>
    Task<MicroDuckHealth> GetHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>Powers the servos and ramps to the home pose.</summary>
    Task InitAsync(CancellationToken cancellationToken = default);

    /// <summary>Cuts servo power. The duck collapses - make sure it is somewhere it can.</summary>
    Task RelaxAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a velocity intent to the active locomotion policy.</summary>
    Task SetVelocityAsync(DuckVelocity velocity, CancellationToken cancellationToken = default);

    /// <summary>Runs one action slot to completion.</summary>
    Task PerformAsync(DuckActionSlot slot, CancellationToken cancellationToken = default);

    /// <summary>Runs a named skill, which is a policy registered under a custom name.</summary>
    Task PerformSkillAsync(string skillName, CancellationToken cancellationToken = default);

    /// <summary>Lists the slots and skills the daemon currently has loaded.</summary>
    Task<IReadOnlyList<string>> ListSkillsAsync(CancellationToken cancellationToken = default);

    /// <summary>Plays the duck's voice signature.</summary>
    Task QuackAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one frame from the time-of-flight sensor, or null when there is no sensor.</summary>
    Task<TofFrame?> ReadTimeOfFlightAsync(CancellationToken cancellationToken = default);

    /// <summary>Reboots servos - all of them, or the listed ids.</summary>
    Task RebootMotorsAsync(IReadOnlyList<int>? servoIds = null, CancellationToken cancellationToken = default);
}
