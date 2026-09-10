namespace PollenRobotics.Net.Core.Realtime;

/// <summary>
/// The easing curves the Reachy Mini daemon accepts on <c>goto_target</c>.
/// </summary>
/// <remarks>
/// The names are the wire values. <see cref="Interpolation.ToWireValue"/> is the only place that
/// mapping lives - the daemon rejects an unknown method outright, and a typo here would look like a
/// movement that simply never happens.
/// </remarks>
public enum InterpolationMethod
{
    /// <summary>Constant velocity. Starts and stops abruptly.</summary>
    Linear,

    /// <summary>Minimum-jerk profile. The daemon default and the right choice for most motion.</summary>
    MinJerk,

    /// <summary>Smoothstep acceleration and deceleration.</summary>
    EaseInOut,

    /// <summary>Overshoots slightly then settles. Reads as playful rather than mechanical.</summary>
    Cartoon,
}

/// <summary>Evaluates the easing curves in <see cref="InterpolationMethod"/>.</summary>
public static class Interpolation
{
    /// <summary>The wire spelling the daemon expects.</summary>
    public static string ToWireValue(this InterpolationMethod method) => method switch
    {
        InterpolationMethod.Linear => "linear",
        InterpolationMethod.MinJerk => "minjerk",
        InterpolationMethod.EaseInOut => "ease_in_out",
        InterpolationMethod.Cartoon => "cartoon",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown interpolation method."),
    };

    /// <summary>Parses a wire spelling back into the enum.</summary>
    public static InterpolationMethod Parse(string wireValue) => wireValue switch
    {
        "linear" => InterpolationMethod.Linear,
        "minjerk" => InterpolationMethod.MinJerk,
        "ease_in_out" => InterpolationMethod.EaseInOut,
        "cartoon" => InterpolationMethod.Cartoon,
        _ => throw new ArgumentException($"Unknown interpolation method '{wireValue}'.", nameof(wireValue)),
    };

    /// <summary>
    /// Maps normalised time in [0, 1] onto normalised progress. Every curve satisfies f(0)=0 and
    /// f(1)=1 so that a trajectory always arrives exactly where it was asked to.
    /// </summary>
    public static double Evaluate(InterpolationMethod method, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return method switch
        {
            InterpolationMethod.Linear => t,
            // 10t^3 - 15t^4 + 6t^5: zero velocity and zero acceleration at both ends.
            InterpolationMethod.MinJerk => t * t * t * ((t * ((6 * t) - 15)) + 10),
            InterpolationMethod.EaseInOut => t * t * (3 - (2 * t)),
            InterpolationMethod.Cartoon => Cartoon(t),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown interpolation method."),
        };
    }

    // A back-eased overshoot. The constant is the usual 1.70158, which peaks about 10% past target.
    private static double Cartoon(double t)
    {
        const double Overshoot = 1.70158;
        const double Scaled = Overshoot * 1.525;

        if (t < 0.5)
        {
            double u = 2 * t;
            return u * u * (((Scaled + 1) * u) - Scaled) / 2;
        }

        double v = (2 * t) - 2;
        return ((v * v * (((Scaled + 1) * v) + Scaled)) + 2) / 2;
    }

    /// <summary>Interpolates between two joint vectors, writing the result into <paramref name="destination"/>.</summary>
    public static void Blend(InterpolationMethod method, ReadOnlySpan<double> from, ReadOnlySpan<double> to, double t, Span<double> destination)
    {
        if (from.Length != to.Length || to.Length != destination.Length)
        {
            throw new ArgumentException("from, to and destination must be the same length.");
        }

        double alpha = Evaluate(method, t);
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = from[i] + ((to[i] - from[i]) * alpha);
        }
    }
}
