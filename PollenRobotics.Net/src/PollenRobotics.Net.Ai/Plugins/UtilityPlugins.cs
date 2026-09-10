using System.ComponentModel;
using System.Data;
using System.Globalization;
using Microsoft.SemanticKernel;

namespace PollenRobotics.Net.Ai.Plugins;

/// <summary>
/// Date and time, which a model has no reliable access to on its own.
/// </summary>
/// <remarks>
/// Worth having even when it looks trivial. A model asked to "schedule the patrol for tomorrow"
/// will otherwise answer from its training cutoff and be confidently months out.
/// </remarks>
public sealed class ClockPlugin
{
    private readonly TimeProvider _time;

    /// <summary>Creates the plugin.</summary>
    /// <param name="timeProvider">Clock to read. Defaults to the system clock.</param>
    public ClockPlugin(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <summary>Today's date.</summary>
    [KernelFunction("get_current_date")]
    [Description("Gets today's date in the local time zone, as ISO-8601 (yyyy-MM-dd).")]
    public string GetCurrentDate() => _time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The current time.</summary>
    [KernelFunction("get_current_time")]
    [Description("Gets the current local date and time as an ISO-8601 timestamp with offset.")]
    public string GetCurrentTime() => _time.GetLocalNow().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>The current UTC time.</summary>
    [KernelFunction("get_utc_time")]
    [Description("Gets the current UTC date and time as an ISO-8601 timestamp.")]
    public string GetUtcTime() => _time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Shifts a date by a number of days.</summary>
    [KernelFunction("add_days")]
    [Description("Adds a number of days to an ISO-8601 date and returns the result as yyyy-MM-dd. Negative values go backwards.")]
    public string AddDays(
        [Description("Starting date, yyyy-MM-dd.")] string date,
        [Description("Days to add. May be negative.")] int days)
    {
        if (!DateOnly.TryParse(date, CultureInfo.InvariantCulture, out DateOnly parsed))
        {
            return $"Error: '{date}' is not a yyyy-MM-dd date.";
        }

        return parsed.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>Days between two dates.</summary>
    [KernelFunction("days_between")]
    [Description("Counts the days from one ISO-8601 date to another. Negative when the second is earlier.")]
    public string DaysBetween(
        [Description("First date, yyyy-MM-dd.")] string from,
        [Description("Second date, yyyy-MM-dd.")] string to)
    {
        if (!DateOnly.TryParse(from, CultureInfo.InvariantCulture, out DateOnly start) ||
            !DateOnly.TryParse(to, CultureInfo.InvariantCulture, out DateOnly end))
        {
            return "Error: both dates must be yyyy-MM-dd.";
        }

        return (end.DayNumber - start.DayNumber).ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Arithmetic, which language models are famously unreliable at.
/// </summary>
/// <remarks>
/// <see cref="DataTable.Compute"/> does the evaluation. It handles the arithmetic a robotics
/// conversation actually produces - unit conversions, gear ratios, duty cycles - and it cannot
/// call out to anything, which is the property that matters when the expression came from a model.
/// It does not do trigonometry, so those have their own functions.
/// </remarks>
public sealed class MathPlugin
{
    /// <summary>Evaluates an arithmetic expression.</summary>
    [KernelFunction("calculate")]
    [Description("Evaluates an arithmetic expression such as (12.5 * 3) / 4 + 7. Supports + - * / % and parentheses.")]
    public string Calculate([Description("The expression to evaluate.")] string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return "Error: the expression is empty.";
        }

        try
        {
            object result = new DataTable().Compute(expression, filter: null);
            return Convert.ToDouble(result, CultureInfo.InvariantCulture).ToString("G15", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is EvaluateException or SyntaxErrorException or InvalidCastException or FormatException or OverflowException)
        {
            return $"Error: could not evaluate '{expression}' ({ex.Message}).";
        }
    }

    /// <summary>Converts degrees to radians.</summary>
    [KernelFunction("degrees_to_radians")]
    [Description("Converts an angle in degrees to radians. The SDK takes radians on the wire and degrees in most helpers.")]
    public string DegreesToRadians([Description("Angle in degrees.")] double degrees) =>
        (degrees * Math.PI / 180.0).ToString("G15", CultureInfo.InvariantCulture);

    /// <summary>Converts radians to degrees.</summary>
    [KernelFunction("radians_to_degrees")]
    [Description("Converts an angle in radians to degrees.")]
    public string RadiansToDegrees([Description("Angle in radians.")] double radians) =>
        (radians * 180.0 / Math.PI).ToString("G15", CultureInfo.InvariantCulture);

    /// <summary>Trigonometry and roots.</summary>
    [KernelFunction("math_function")]
    [Description("Applies a named function to a value. Supported: sin, cos, tan, asin, acos, atan, sqrt, abs, exp, log, log10. Angles are in radians.")]
    public string Apply(
        [Description("Function name.")] string function,
        [Description("Input value.")] double value)
    {
        double result = function.Trim().ToLowerInvariant() switch
        {
            "sin" => Math.Sin(value),
            "cos" => Math.Cos(value),
            "tan" => Math.Tan(value),
            "asin" => Math.Asin(value),
            "acos" => Math.Acos(value),
            "atan" => Math.Atan(value),
            "sqrt" => Math.Sqrt(value),
            "abs" => Math.Abs(value),
            "exp" => Math.Exp(value),
            "log" => Math.Log(value),
            "log10" => Math.Log10(value),
            _ => double.NaN,
        };

        return double.IsNaN(result)
            ? $"Error: '{function}' is not a supported function, or the input is outside its domain."
            : result.ToString("G15", CultureInfo.InvariantCulture);
    }
}
