using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.SemanticKernel;
using TypeSafeAppGen.Workspace;
using TypeSafeSdk;

namespace TypeSafeAppGen.Ai.Plugins;

/// <summary>Perhitungan matematika yang tepat, supaya Jack tidak menebak angka.</summary>
public sealed class MathPlugin
{
    [KernelFunction("calculate"), Description("Evaluate a math expression exactly. Supports + - * / % ^, parentheses, pi, e, and sqrt abs sin cos tan asin acos atan ln log log2 exp floor ceil round min max pow. Angles in radians.")]
    public string Calculate([Description("Expression, for example (1920/1080)*720 or sqrt(2)^3.")] string expression)
    {
        try
        {
            var value = ExpressionEvaluator.Evaluate(expression);
            return value.ToString("G15", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or DivideByZeroException or OverflowException)
        {
            return "Error: " + ex.Message;
        }
    }

    [KernelFunction("statistics"), Description("Compute count, sum, mean, median, min, max, and standard deviation of a list of numbers.")]
    public string Statistics([Description("Numbers separated by commas or spaces.")] string numbers)
    {
        var values = numbers.Split([',', ' ', ';', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (double?)d : null)
            .ToList();
        if (values.Count == 0 || values.Any(v => v is null)) return "Error: provide numbers only, using '.' as the decimal separator.";
        var sorted = values.Select(v => v!.Value).Order().ToArray();
        var mean = sorted.Average();
        var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        var std = Math.Sqrt(sorted.Sum(v => (v - mean) * (v - mean)) / sorted.Length);
        return string.Create(CultureInfo.InvariantCulture, $"count={sorted.Length} sum={sorted.Sum():G15} mean={mean:G15} median={median:G15} min={sorted[0]:G15} max={sorted[^1]:G15} stddev={std:G15}");
    }
}

/// <summary>Tanggal dan waktu saat ini serta aritmetika tanggal.</summary>
public sealed class DateTimePlugin
{
    [KernelFunction("get_date_time"), Description("Get the current date and time, optionally in another time zone (IANA id like Asia/Jakarta or Windows id).")]
    public string GetDateTime([Description("Time zone id; empty for the user's local zone.")] string timeZone = "")
    {
        var now = DateTimeOffset.Now;
        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            try { now = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timeZone)); }
            catch (TimeZoneNotFoundException) { return $"Unknown time zone '{timeZone}'."; }
        }
        return $"{now:yyyy-MM-dd HH:mm:ss zzz} ({now.DayOfWeek}, ISO week {ISOWeek.GetWeekOfYear(now.DateTime)}) · {(string.IsNullOrWhiteSpace(timeZone) ? TimeZoneInfo.Local.Id : timeZone)}";
    }

    [KernelFunction("date_difference"), Description("Days, hours, and business days between two dates (yyyy-MM-dd or ISO 8601).")]
    public string DateDifference(string from, string to)
    {
        if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var start)
            || !DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var end))
            return "Error: use yyyy-MM-dd or ISO 8601 dates.";
        var span = end - start;
        var businessDays = 0;
        for (var d = start.Date; d < end.Date; d = d.AddDays(1))
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday) businessDays++;
        return $"{span.TotalDays:0.##} days ({span.TotalHours:0.#} hours), {businessDays} business days.";
    }

    [KernelFunction("add_to_date"), Description("Add days, months, or years (negative to subtract) to a date.")]
    public string AddToDate(string date, int days = 0, int months = 0, int years = 0)
    {
        if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) return "Error: use yyyy-MM-dd.";
        var result = value.AddYears(years).AddMonths(months).AddDays(days);
        return $"{result:yyyy-MM-dd} ({result.DayOfWeek})";
    }
}

/// <summary>Pengetahuan TypeSafe SDK: referensi pemakaian yang benar dan klasifikasi via simulator.</summary>
public sealed class TypeSafeSdkPlugin
{
    private static readonly TypeSafeClient Simulator = new(new TypeSafeOptions { Simulator = true });

    [KernelFunction("typesafe_sdk_reference"), Description("Get the correct usage of the TypeSafe .NET SDK (package Gravicode.TypeSafeSdk) before generating code that classifies, scores, or decides with it.")]
    public string Reference() =>
        """
        Package: <PackageReference Include="Gravicode.TypeSafeSdk" Version="1.1.0" />   namespace: TypeSafeSdk
        Client:  await using var client = new TypeSafeClient(TypeSafeOptions.FromEnvironment());   // reads TYPESAFE_API_KEY
                 new TypeSafeOptions { Simulator = true }   // offline, no key; use for demos and tests
        Single choice (question named "category"):
                 var r = await client.SystemOneAsync(new { document = text }, Choice.Create("billing", "technical", "other"));
                 r.Choices["category"].Choice; r.ChoiceAnswers["category"].Probabilities; .Confidence
        Choice with criteria descriptions (improves accuracy):
                 Choice.Create(new Dictionary<string, string?> { ["billing"] = "payments, refunds", ["technical"] = "bugs, errors" }, "Which team?")
        Several named questions in one call:
                 var questions = new Dictionary<string, object> {
                     ["team"] = Choice.Create("a", "b"),
                     ["urgency"] = new ScoreSchema("How urgent?", "low", "medium", "high"),
                     ["is_spam"] = new NoulSchema("Is this spam?") };
                 var r = await client.SystemOneAsync(state, questions, model: null, CancellationToken.None);
                 r.Scores["urgency"].Score / .NearestLabel;  r.Nouls["is_spam"].Noul (0..1)
        Errors: catch TypeSafeApiException (base) or specific TypeSafeAuthenticationException, TypeSafeRateLimitException.
        Never serialize a schema record yourself; pass it to SystemOneAsync.
        """;

    [KernelFunction("typesafe_classify"), Description("Classify a text into one of the given labels with the offline TypeSafe simulator. Returns the label and the probability of each.")]
    public async Task<string> ClassifyAsync(
        [Description("Text to classify.")] string text,
        [Description("Comma-separated labels.")] string labels)
    {
        var values = labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length < 2) return "Provide at least two labels.";
        var response = await Simulator.SystemOneAsync(new { text }, Choice.Create(values));
        var answer = response.ChoiceAnswers["category"];
        var builder = new StringBuilder($"{answer.Choice} (confidence {answer.Confidence:P0}, simulator)\n");
        foreach (var (label, p) in answer.Probabilities.OrderByDescending(x => x.Value)) builder.AppendLine($"  {label}: {p:P1}");
        return builder.ToString();
    }
}
