namespace PollenRobotics.Net.Core.Robots;

/// <summary>Where a robot connection is in its lifecycle.</summary>
public enum ConnectionState
{
    /// <summary>No transport is open.</summary>
    Disconnected,

    /// <summary>A connection attempt is in flight.</summary>
    Connecting,

    /// <summary>Connected and exchanging state.</summary>
    Connected,

    /// <summary>The link dropped and the transport is retrying.</summary>
    Reconnecting,

    /// <summary>The connection failed in a way retrying will not fix.</summary>
    Faulted,
}
