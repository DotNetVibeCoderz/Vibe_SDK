using PollenRobotics.Net.Core.Connectivity;
using PollenRobotics.Net.Core.Safety;

namespace PollenRobotics.Net.ReachyMini;

/// <summary>How the client should reach the daemon.</summary>
public enum ReachyMiniConnectionMode
{
    /// <summary>
    /// Probe localhost first, then the mDNS name. Matches the Python SDK's auto-detection.
    /// </summary>
    Auto,

    /// <summary>Only ever talk to a daemon on this machine. Reachy Mini Lite over USB.</summary>
    LocalhostOnly,

    /// <summary>Talk to a daemon over the network. Reachy Mini Wireless.</summary>
    Network,
}

/// <summary>
/// Settings for <see cref="ReachyMiniClient"/>.
/// </summary>
/// <remarks>
/// The defaults describe a Reachy Mini Lite plugged into the machine running this code, which is
/// the setup most people start with.
/// </remarks>
public sealed record ReachyMiniOptions
{
    /// <summary>Daemon host. Ignored when <see cref="ConnectionMode"/> resolves it.</summary>
    public string Host { get; init; } = "localhost";

    /// <summary>
    /// Daemon port. The documented default is 8000 for both the REST API and the WebSocket.
    /// </summary>
    public int Port { get; init; } = 8000;

    /// <summary>How to pick a host.</summary>
    public ReachyMiniConnectionMode ConnectionMode { get; init; } = ReachyMiniConnectionMode.Auto;

    /// <summary>
    /// The mDNS name a wireless Reachy Mini advertises, tried when localhost does not answer.
    /// </summary>
    public string NetworkHost { get; init; } = "reachy-mini.local";

    /// <summary>
    /// Path the command WebSocket is served on, relative to the daemon root.
    /// </summary>
    /// <remarks>
    /// Pollen documents the daemon as exposing "a REST API and WebSocket at http://host:8000" but
    /// does not name the WebSocket path in the public docs, so this is settable. If a daemon build
    /// moves it, change this rather than patching the transport.
    /// </remarks>
    public string WebSocketPath { get; init; } = "/ws";

    /// <summary>How long a REST call may take.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often to poll state when the daemon is not pushing it.
    /// </summary>
    /// <remarks>
    /// 500 ms matches the JavaScript SDK's polling fallback. Anything mirroring the robot in real
    /// time - a 3D view, a "has the move finished" watcher - should subscribe to the push stream
    /// instead of tightening this.
    /// </remarks>
    public TimeSpan StatePollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Reconnect behaviour after a dropped link.</summary>
    public ReconnectPolicy Reconnect { get; init; } = ReconnectPolicy.Default;

    /// <summary>What to do about out-of-range commands.</summary>
    public RobotSafetyOptions Safety { get; init; } = RobotSafetyOptions.Default;

    /// <summary>
    /// Let the daemon rotate the body to follow large head yaws.
    /// </summary>
    /// <remarks>
    /// Head yaw may only differ from body yaw by 65 degrees. With this on, the daemon turns the
    /// body to keep that constraint satisfied instead of clamping the head short of where it was
    /// asked to look.
    /// </remarks>
    public bool AutomaticBodyYaw { get; init; } = true;

    /// <summary>The default settings.</summary>
    public static ReachyMiniOptions Default { get; } = new();

    /// <summary>Settings pointed at a wireless robot by host name or address.</summary>
    public static ReachyMiniOptions ForNetwork(string host) => new()
    {
        Host = host,
        ConnectionMode = ReachyMiniConnectionMode.Network,
    };

    /// <summary>The daemon base address these settings resolve to.</summary>
    public Uri BaseAddress => new($"http://{ResolvedHost}:{Port}/");

    /// <summary>The WebSocket address these settings resolve to.</summary>
    public Uri WebSocketAddress => new($"ws://{ResolvedHost}:{Port}{WebSocketPath}");

    /// <summary>The host that will actually be dialled.</summary>
    public string ResolvedHost => ConnectionMode switch
    {
        ReachyMiniConnectionMode.LocalhostOnly => "localhost",
        ReachyMiniConnectionMode.Network => Host == "localhost" ? NetworkHost : Host,
        _ => Host,
    };
}
