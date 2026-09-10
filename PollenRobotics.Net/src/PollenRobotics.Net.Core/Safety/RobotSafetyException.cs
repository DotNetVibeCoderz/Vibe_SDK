namespace PollenRobotics.Net.Core.Safety;

/// <summary>Thrown when a command would take a joint outside its declared limits.</summary>
public sealed class RobotSafetyException : PollenRoboticsException
{
    /// <summary>The joint that would have been violated.</summary>
    public string JointName { get; }

    /// <summary>The value that was requested, in radians.</summary>
    public double Requested { get; }

    /// <summary>The lower limit, in radians.</summary>
    public double Lower { get; }

    /// <summary>The upper limit, in radians.</summary>
    public double Upper { get; }

    /// <summary>Creates the exception.</summary>
    public RobotSafetyException(string jointName, double requested, double lower, double upper)
        : base($"Joint '{jointName}' limited to [{Deg(lower):0.#}, {Deg(upper):0.#}] deg, commanded {Deg(requested):0.#} deg. " +
               "Set RobotSafetyOptions.ClampInsteadOfThrow to clamp instead.")
    {
        JointName = jointName;
        Requested = requested;
        Lower = lower;
        Upper = upper;
    }

    private static double Deg(double radians) => radians * (180.0 / Math.PI);
}
