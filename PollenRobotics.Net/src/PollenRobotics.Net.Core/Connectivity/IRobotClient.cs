using PollenRobotics.Net.Core.Robots;

namespace PollenRobotics.Net.Core.Connectivity;

/// <summary>
/// The behaviour every robot client shares, whatever it is talking to.
/// </summary>
/// <remarks>
/// The gallery, the simulator and the wizard all drive robots they cannot name at compile time -
/// the user picks one from a combo box. This is the surface they bind against; anything
/// robot-specific lives on the concrete client.
/// </remarks>
public interface IRobotClient : IAsyncDisposable
{
    /// <summary>Which family of robot this client drives.</summary>
    RobotKind Kind { get; }

    /// <summary>The joint model, available before connecting.</summary>
    RobotDescription Description { get; }

    /// <summary>Current connection state.</summary>
    ConnectionState State { get; }

    /// <summary>Where the client is pointed, for display: a host, a socket path, or "simulation".</summary>
    string Endpoint { get; }

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event Action<ConnectionState>? StateChanged;

    /// <summary>Opens the connection. Safe to call when already connected.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the connection without disposing the client.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the present joint positions in radians, in wire order.</summary>
    Task<double[]> GetJointPositionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Energises the actuators and holds position.</summary>
    Task EnableMotorsAsync(CancellationToken cancellationToken = default);

    /// <summary>Cuts actuator power. On most of these robots that means the robot goes limp.</summary>
    Task DisableMotorsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Retry behaviour for a transport whose link has dropped.</summary>
public sealed record ReconnectPolicy
{
    /// <summary>Reconnect automatically after an unexpected drop.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Delay before the first retry.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Ceiling on the backoff.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Growth factor applied to the delay after each failed attempt.</summary>
    public double BackoffFactor { get; init; } = 2.0;

    /// <summary>Attempts before giving up and faulting, or null to retry forever.</summary>
    public int? MaxAttempts { get; init; }

    /// <summary>The defaults.</summary>
    public static ReconnectPolicy Default { get; } = new();

    /// <summary>Never reconnect; a dropped link faults immediately.</summary>
    public static ReconnectPolicy None { get; } = new() { Enabled = false };

    /// <summary>
    /// The delay before attempt <paramref name="attempt"/>, counting from one.
    /// </summary>
    /// <remarks>
    /// Jittered by up to 20%. Without it, a room full of robots that lost the same access point
    /// reconnects in lockstep and knocks it over again.
    /// </remarks>
    public TimeSpan DelayFor(int attempt)
    {
        double scaled = InitialDelay.TotalMilliseconds * Math.Pow(BackoffFactor, Math.Max(0, attempt - 1));
        double capped = Math.Min(scaled, MaxDelay.TotalMilliseconds);
        double jitter = capped * 0.2 * Random.Shared.NextDouble();
        return TimeSpan.FromMilliseconds(capped - jitter);
    }
}
