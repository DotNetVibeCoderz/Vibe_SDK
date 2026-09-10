namespace PollenRobotics.Net.Core.Safety;

/// <summary>
/// How the SDK reacts when a command falls outside a joint's declared limits.
/// </summary>
/// <remarks>
/// The Python SDK clamps silently. That is friendly for a REPL and dangerous for a control loop,
/// because a servo that has been quietly clamped for ten minutes looks exactly like one that is
/// tracking correctly. This SDK throws by default and clamps only when asked, so a limit violation
/// is a bug report rather than a mystery.
/// </remarks>
public sealed record RobotSafetyOptions
{
    /// <summary>Clamp out-of-range commands instead of throwing. Defaults to false.</summary>
    public bool ClampInsteadOfThrow { get; init; }

    /// <summary>Emit a warning through the logger whenever a command is clamped. Defaults to true.</summary>
    public bool WarnOnClamp { get; init; } = true;

    /// <summary>Enforce declared joint limits at all. Turning this off is for bench work only.</summary>
    public bool EnforceJointLimits { get; init; } = true;

    /// <summary>Largest commanded joint step accepted in a single tick, or null for no check.</summary>
    public double? MaxJointStepRadians { get; init; }

    /// <summary>The defaults: enforce limits, throw on violation.</summary>
    public static RobotSafetyOptions Default { get; } = new();

    /// <summary>The forgiving profile the Python SDK uses: clamp and carry on.</summary>
    public static RobotSafetyOptions Permissive { get; } = new() { ClampInsteadOfThrow = true };
}
