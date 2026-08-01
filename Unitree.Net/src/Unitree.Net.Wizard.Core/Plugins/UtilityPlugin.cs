using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;

namespace Unitree.Net.Wizard.Core.Plugins;

/// <summary>
/// Date, time and arithmetic — the things a language model is worst at doing in its head.
/// </summary>
/// <remarks>
/// Every function returns prose rather than a bare value. A model that receives "42" has to guess what
/// it means; one that receives "sqrt(1764) = 42" can quote it back correctly.
/// </remarks>
public sealed class UtilityPlugin
{
    /// <summary>Reports the current local date and time.</summary>
    /// <param name="timeZone">
    /// An IANA or Windows time-zone identifier such as <c>Asia/Jakarta</c>. Empty uses the machine's
    /// own zone.
    /// </param>
    [KernelFunction("get_current_time")]
    [Description("Gets the current date and time. Use this instead of guessing; you have no clock.")]
    public string GetCurrentTime(
        [Description("Optional time zone id, e.g. 'Asia/Jakarta' or 'UTC'. Leave empty for local time.")]
        string timeZone = "")
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string zoneName = TimeZoneInfo.Local.DisplayName;

        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            try
            {
                TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
                now = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, zone);
                zoneName = zone.DisplayName;
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return $"No time zone called '{timeZone}'. Try an IANA id such as 'Asia/Jakarta'.";
            }
        }

        return $"{now:dddd, d MMMM yyyy HH:mm:ss} ({zoneName}), ISO 8601 {now:O}.";
    }

    /// <summary>Reports the interval between two dates.</summary>
    /// <param name="from">Start date.</param>
    /// <param name="to">End date.</param>
    [KernelFunction("date_difference")]
    [Description("Calculates the number of days and hours between two dates.")]
    public string DateDifference(
        [Description("The earlier date, ISO 8601 or any common format.")] string from,
        [Description("The later date, ISO 8601 or any common format.")] string to)
    {
        if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, out DateTimeOffset start))
        {
            return $"Could not read '{from}' as a date.";
        }

        if (!DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, out DateTimeOffset end))
        {
            return $"Could not read '{to}' as a date.";
        }

        TimeSpan span = end - start;
        return $"{span.TotalDays:0.##} days ({span.Days} days, {span.Hours} hours, {span.Minutes} minutes) from {start:d} to {end:d}.";
    }

    /// <summary>Evaluates an arithmetic expression.</summary>
    /// <param name="expression">The expression to evaluate.</param>
    [KernelFunction("calculate")]
    [Description(
        "Evaluates an arithmetic expression. Supports + - * / % ^, parentheses, and the functions " +
        "sqrt, abs, sin, cos, tan, asin, acos, atan, log, log10, exp, floor, ceil, round, min, max, " +
        "plus the constants pi and e. Use this for any calculation rather than doing it yourself.")]
    public string Calculate(
        [Description("The expression, for example '(0.213 + 0.213) * cos(0.8)'.")] string expression)
    {
        try
        {
            double value = ExpressionEvaluator.Evaluate(expression);

            return double.IsNaN(value) || double.IsInfinity(value)
                ? $"{expression} is undefined ({value})."
                : $"{expression} = {value.ToString("0.##########", CultureInfo.InvariantCulture)}";
        }
        catch (FormatException exception)
        {
            return $"Could not evaluate '{expression}': {exception.Message}";
        }
    }

    /// <summary>Converts between units common in robotics.</summary>
    /// <param name="value">The value to convert.</param>
    /// <param name="from">Source unit.</param>
    /// <param name="to">Target unit.</param>
    [KernelFunction("convert_units")]
    [Description(
        "Converts between units used in robot work: rad, deg, m, cm, mm, m/s, km/h, kg, g, N, Nm, " +
        "C, F, Hz, ms, s.")]
    public string ConvertUnits(
        [Description("The numeric value.")] double value,
        [Description("The unit it is in, e.g. 'rad'.")] string from,
        [Description("The unit to convert to, e.g. 'deg'.")] string to)
    {
        string source = from.Trim().ToLowerInvariant();
        string target = to.Trim().ToLowerInvariant();

        double? result = (source, target) switch
        {
            ("rad", "deg") => value * 180 / Math.PI,
            ("deg", "rad") => value * Math.PI / 180,
            ("m", "cm") => value * 100,
            ("cm", "m") => value / 100,
            ("m", "mm") => value * 1000,
            ("mm", "m") => value / 1000,
            ("m/s", "km/h") => value * 3.6,
            ("km/h", "m/s") => value / 3.6,
            ("kg", "g") => value * 1000,
            ("g", "kg") => value / 1000,
            ("c", "f") => (value * 9 / 5) + 32,
            ("f", "c") => (value - 32) * 5 / 9,
            ("hz", "ms") => value > 0 ? 1000 / value : null,
            ("ms", "hz") => value > 0 ? 1000 / value : null,
            ("s", "ms") => value * 1000,
            ("ms", "s") => value / 1000,
            _ when source == target => value,
            _ => null,
        };

        return result is { } converted
            ? $"{value.ToString("0.######", CultureInfo.InvariantCulture)} {from} = " +
              $"{converted.ToString("0.######", CultureInfo.InvariantCulture)} {to}"
            : $"No conversion from '{from}' to '{to}' is defined.";
    }
}

/// <summary>
/// A small recursive-descent evaluator for arithmetic expressions.
/// </summary>
/// <remarks>
/// Hand-written rather than delegating to <c>DataTable.Compute</c>, which cannot do square roots or
/// trigonometry and reports failures as an untyped exception with an unhelpful message. Everything a
/// model is likely to ask for during robot work is here instead.
/// </remarks>
internal static class ExpressionEvaluator
{
    /// <summary>Evaluates <paramref name="expression"/>.</summary>
    /// <param name="expression">The expression text.</param>
    /// <exception cref="FormatException">The expression could not be parsed.</exception>
    internal static double Evaluate(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        int position = 0;
        double value = ParseExpression(expression, ref position);

        SkipWhitespace(expression, ref position);

        if (position < expression.Length)
        {
            throw new FormatException($"unexpected '{expression[position]}' at position {position}");
        }

        return value;
    }

    private static double ParseExpression(string text, ref int position)
    {
        double left = ParseTerm(text, ref position);

        while (true)
        {
            SkipWhitespace(text, ref position);

            if (position >= text.Length)
            {
                return left;
            }

            char op = text[position];

            if (op is not ('+' or '-'))
            {
                return left;
            }

            position++;
            double right = ParseTerm(text, ref position);
            left = op == '+' ? left + right : left - right;
        }
    }

    private static double ParseTerm(string text, ref int position)
    {
        double left = ParsePower(text, ref position);

        while (true)
        {
            SkipWhitespace(text, ref position);

            if (position >= text.Length)
            {
                return left;
            }

            char op = text[position];

            if (op is not ('*' or '/' or '%'))
            {
                return left;
            }

            position++;
            double right = ParsePower(text, ref position);

            left = op switch
            {
                '*' => left * right,
                '/' => left / right,
                _ => left % right,
            };
        }
    }

    private static double ParsePower(string text, ref int position)
    {
        double baseValue = ParseUnary(text, ref position);
        SkipWhitespace(text, ref position);

        if (position < text.Length && text[position] == '^')
        {
            position++;
            // Right-associative: 2^3^2 is 2^(3^2), which is what every calculator does.
            return Math.Pow(baseValue, ParsePower(text, ref position));
        }

        return baseValue;
    }

    private static double ParseUnary(string text, ref int position)
    {
        SkipWhitespace(text, ref position);

        if (position < text.Length && text[position] == '-')
        {
            position++;
            return -ParseUnary(text, ref position);
        }

        if (position < text.Length && text[position] == '+')
        {
            position++;
        }

        return ParsePrimary(text, ref position);
    }

    private static double ParsePrimary(string text, ref int position)
    {
        SkipWhitespace(text, ref position);

        if (position >= text.Length)
        {
            throw new FormatException("expression ended early");
        }

        if (text[position] == '(')
        {
            position++;
            double value = ParseExpression(text, ref position);
            Expect(text, ref position, ')');
            return value;
        }

        if (char.IsAsciiLetter(text[position]))
        {
            int start = position;

            while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_'))
            {
                position++;
            }

            string name = text[start..position].ToLowerInvariant();
            SkipWhitespace(text, ref position);

            if (position < text.Length && text[position] == '(')
            {
                position++;
                double first = ParseExpression(text, ref position);
                SkipWhitespace(text, ref position);

                if (position < text.Length && text[position] == ',')
                {
                    position++;
                    double second = ParseExpression(text, ref position);
                    Expect(text, ref position, ')');
                    return ApplyBinary(name, first, second);
                }

                Expect(text, ref position, ')');
                return ApplyUnary(name, first);
            }

            return name switch
            {
                "pi" => Math.PI,
                "e" => Math.E,
                "tau" => Math.Tau,
                _ => throw new FormatException($"unknown name '{name}'"),
            };
        }

        int numberStart = position;

        while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] == '.'))
        {
            position++;
        }

        // Exponent notation, so 1.5e-3 parses as one number rather than as 1.5 times an unknown name.
        if (position < text.Length && (text[position] == 'e' || text[position] == 'E')
            && position + 1 < text.Length
            && (char.IsAsciiDigit(text[position + 1]) || text[position + 1] is '+' or '-'))
        {
            position += 2;

            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
            }
        }

        if (numberStart == position)
        {
            throw new FormatException($"unexpected '{text[position]}' at position {position}");
        }

        return double.Parse(text[numberStart..position], CultureInfo.InvariantCulture);
    }

    private static double ApplyUnary(string name, double value) => name switch
    {
        "sqrt" => Math.Sqrt(value),
        "abs" => Math.Abs(value),
        "sin" => Math.Sin(value),
        "cos" => Math.Cos(value),
        "tan" => Math.Tan(value),
        "asin" => Math.Asin(value),
        "acos" => Math.Acos(value),
        "atan" => Math.Atan(value),
        "log" => Math.Log(value),
        "log10" => Math.Log10(value),
        "exp" => Math.Exp(value),
        "floor" => Math.Floor(value),
        "ceil" or "ceiling" => Math.Ceiling(value),
        "round" => Math.Round(value),
        "deg" => value * 180 / Math.PI,
        "rad" => value * Math.PI / 180,
        _ => throw new FormatException($"unknown function '{name}'"),
    };

    private static double ApplyBinary(string name, double left, double right) => name switch
    {
        "min" => Math.Min(left, right),
        "max" => Math.Max(left, right),
        "pow" => Math.Pow(left, right),
        "atan2" => Math.Atan2(left, right),
        "round" => Math.Round(left, (int)right),
        _ => throw new FormatException($"'{name}' does not take two arguments"),
    };

    private static void Expect(string text, ref int position, char expected)
    {
        SkipWhitespace(text, ref position);

        if (position >= text.Length || text[position] != expected)
        {
            throw new FormatException($"expected '{expected}' at position {position}");
        }

        position++;
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }
}
