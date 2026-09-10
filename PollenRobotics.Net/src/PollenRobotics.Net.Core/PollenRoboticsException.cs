namespace PollenRobotics.Net.Core;

/// <summary>Base type for every error this SDK raises deliberately.</summary>
public class PollenRoboticsException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PollenRoboticsException(string message) : base(message) { }

    /// <summary>Creates the exception with an inner cause.</summary>
    public PollenRoboticsException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown when the SDK cannot reach, or loses, a robot.</summary>
public sealed class RobotConnectionException : PollenRoboticsException
{
    /// <summary>Creates the exception.</summary>
    public RobotConnectionException(string message) : base(message) { }

    /// <summary>Creates the exception with an inner cause.</summary>
    public RobotConnectionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown when the robot rejects a command or replies with an error.</summary>
public sealed class RobotCommandException : PollenRoboticsException
{
    /// <summary>The command that failed, as named on the wire.</summary>
    public string? Command { get; }

    /// <summary>Creates the exception.</summary>
    public RobotCommandException(string message, string? command = null) : base(message) => Command = command;
}
