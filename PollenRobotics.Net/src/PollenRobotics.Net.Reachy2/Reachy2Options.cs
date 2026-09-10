using PollenRobotics.Net.Core.Connectivity;
using PollenRobotics.Net.Core.Safety;

namespace PollenRobotics.Net.Reachy2;

/// <summary>Settings for a Reachy 2 connection.</summary>
public sealed record Reachy2Options
{
    /// <summary>Robot host name or address.</summary>
    public string Host { get; init; } = "localhost";

    /// <summary>
    /// gRPC port. Reachy 2's SDK server listens on 50051.
    /// </summary>
    public int Port { get; init; } = 50051;

    /// <summary>
    /// Use TLS.
    /// </summary>
    /// <remarks>
    /// Off by default because the SDK server serves plaintext h2c on the robot's own network. Note
    /// that <see cref="Grpc.Net.Client.GrpcChannel"/> needs
    /// <c>System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport</c> for that, which this SDK
    /// sets on the channel it builds.
    /// </remarks>
    public bool UseTls { get; init; }

    /// <summary>How long a unary call may take.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often to pull a state snapshot when nothing is streaming.
    /// </summary>
    /// <remarks>
    /// The SDK server also offers <c>StreamReachyState</c>, which this client prefers when it is
    /// available; the poll is the fallback.
    /// </remarks>
    public TimeSpan StatePollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Frequency the state stream is requested at, in hertz.</summary>
    public double StateStreamFrequencyHz { get; init; } = 30;

    /// <summary>Reconnect behaviour.</summary>
    public ReconnectPolicy Reconnect { get; init; } = ReconnectPolicy.Default;

    /// <summary>Limit policy for joint commands issued through this SDK.</summary>
    public RobotSafetyOptions Safety { get; init; } = RobotSafetyOptions.Default;

    /// <summary>The defaults, pointed at localhost.</summary>
    public static Reachy2Options Default { get; } = new();

    /// <summary>Settings for a robot at a given address.</summary>
    public static Reachy2Options ForHost(string host) => new() { Host = host };

    /// <summary>The gRPC address these settings resolve to.</summary>
    public Uri Address => new($"{(UseTls ? "https" : "http")}://{Host}:{Port}");
}
