namespace Unitree.Net.Core;

/// <summary>
/// Base type for every error the SDK raises deliberately.
/// </summary>
public class UnitreeException : Exception
{
    /// <summary>Creates an instance with a message.</summary>
    public UnitreeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an instance with a message and inner cause.</summary>
    public UnitreeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The DDS transport could not be established or was lost.
/// </summary>
public sealed class UnitreeConnectionException : UnitreeException
{
    /// <summary>Creates an instance with a message.</summary>
    public UnitreeConnectionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an instance with a message and inner cause.</summary>
    public UnitreeConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A command was rejected because it would have exceeded a configured safety limit.
/// </summary>
/// <remarks>
/// Safety violations are thrown, never logged-and-ignored. A silently clamped torque command is
/// indistinguishable from a working one until the robot behaves unexpectedly under load.
/// </remarks>
public sealed class SafetyViolationException : UnitreeException
{
    /// <summary>Creates an instance describing which limit was breached.</summary>
    /// <param name="limitName">The name of the violated limit, e.g. <c>MaxTorque</c>.</param>
    /// <param name="requested">The value that was requested.</param>
    /// <param name="limit">The configured ceiling.</param>
    public SafetyViolationException(string limitName, double requested, double limit)
        : base($"Safety limit '{limitName}' violated: requested {requested:0.###}, limit {limit:0.###}.")
    {
        LimitName = limitName;
        Requested = requested;
        Limit = limit;
    }

    /// <summary>The name of the violated limit.</summary>
    public string LimitName { get; }

    /// <summary>The requested value.</summary>
    public double Requested { get; }

    /// <summary>The configured ceiling.</summary>
    public double Limit { get; }
}

/// <summary>
/// The robot returned a non-zero status code for a service request.
/// </summary>
public sealed class UnitreeServiceException : UnitreeException
{
    /// <summary>Creates an instance for a failed service call.</summary>
    /// <param name="apiId">The Unitree API identifier that was invoked.</param>
    /// <param name="statusCode">The status code the robot returned.</param>
    public UnitreeServiceException(long apiId, int statusCode)
        : base($"Unitree API {apiId} failed with status code {statusCode}.")
    {
        ApiId = apiId;
        StatusCode = statusCode;
    }

    /// <summary>The API identifier that was invoked.</summary>
    public long ApiId { get; }

    /// <summary>The status code the robot returned. Zero means success.</summary>
    public int StatusCode { get; }
}

/// <summary>
/// A message could not be encoded to or decoded from CDR.
/// </summary>
public sealed class CdrFormatException : UnitreeException
{
    /// <summary>Creates an instance with a message.</summary>
    public CdrFormatException(string message)
        : base(message)
    {
    }
}
