using PollenRobotics.Net.Core.Connectivity;
using PollenRobotics.Net.Core.Safety;

namespace PollenRobotics.Net.MicroDuck;

/// <summary>How to reach robotd.</summary>
public enum MicroDuckEndpointKind
{
    /// <summary>
    /// A Unix domain socket, the daemon's native transport.
    /// </summary>
    /// <remarks>
    /// Only reachable from the duck itself, or through an SSH-forwarded socket. Windows has
    /// supported <c>AF_UNIX</c> since Windows 10 1803, so the forwarded case works from a
    /// development machine too.
    /// </remarks>
    UnixSocket,

    /// <summary>
    /// A TCP endpoint, for a bridge that forwards the same NDJSON frames over the network.
    /// </summary>
    /// <remarks>
    /// robotd does not listen on TCP itself. Use this against <c>socat</c>, an SSH tunnel or the
    /// simulator, which speaks the same protocol.
    /// </remarks>
    Tcp,
}

/// <summary>Settings for a MicroDuck connection.</summary>
public sealed record MicroDuckOptions
{
    /// <summary>Which transport to use.</summary>
    public MicroDuckEndpointKind EndpointKind { get; init; } = MicroDuckEndpointKind.UnixSocket;

    /// <summary>The socket path when <see cref="EndpointKind"/> is a Unix socket.</summary>
    public string SocketPath { get; init; } = "/run/robotd.sock";

    /// <summary>Host when using TCP.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>Port when using TCP.</summary>
    public int Port { get; init; } = 7654;

    /// <summary>How long a single JSON-RPC call may take.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How often to poll state.
    /// </summary>
    /// <remarks>
    /// The control loop runs at 50 Hz; polling faster than that returns the same frame twice and
    /// costs a round trip to learn nothing.
    /// </remarks>
    public TimeSpan StatePollInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Reconnect behaviour.</summary>
    public ReconnectPolicy Reconnect { get; init; } = ReconnectPolicy.Default;

    /// <summary>Limit policy.</summary>
    public RobotSafetyOptions Safety { get; init; } = RobotSafetyOptions.Default;

    /// <summary>
    /// Stop the duck automatically when velocity commands stop arriving.
    /// </summary>
    /// <remarks>
    /// The daemon holds the last velocity indefinitely, so a client that crashes mid-stride leaves
    /// the duck walking into whatever is in front of it. With this set, the client sends a zero
    /// velocity once this long has passed with no new command.
    /// </remarks>
    public TimeSpan? VelocityWatchdog { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The defaults: the daemon socket on the robot itself.</summary>
    public static MicroDuckOptions Default { get; } = new();

    /// <summary>Settings for a TCP bridge or the simulator.</summary>
    public static MicroDuckOptions ForTcp(string host, int port = 7654) => new()
    {
        EndpointKind = MicroDuckEndpointKind.Tcp,
        Host = host,
        Port = port,
    };

    /// <summary>Settings for a Unix socket at a non-default path, such as an SSH-forwarded one.</summary>
    public static MicroDuckOptions ForSocket(string path) => new()
    {
        EndpointKind = MicroDuckEndpointKind.UnixSocket,
        SocketPath = path,
    };

    /// <summary>A display string for the endpoint.</summary>
    public string DescribeEndpoint() => EndpointKind == MicroDuckEndpointKind.UnixSocket
        ? SocketPath
        : $"tcp://{Host}:{Port}";
}
