using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeSafeSdk;
public static class Choice { public static ChoiceSchema Create(params string[] values) { if (values is null || values.Length == 0) throw new ArgumentException("At least one choice is required.", nameof(values)); return new ChoiceSchema(values); } }
public sealed record ChoiceSchema(IReadOnlyList<string> Values, string Instructions = "What is this about?") { public object ToApiQuestion() => new { type = "choice", instructions = Instructions, criteria = Values.ToDictionary(value => value, _ => (string?)null) }; }
public sealed record NoulSchema(string Instructions, string? True = null, string? False = null) { public object ToApiQuestion() => True is null && False is null ? new { type = "noul", instructions = Instructions } : new { type = "noul", instructions = Instructions, criteria = new { @true = True, @false = False } }; }
public sealed record ScoreSchema(string Instructions, IReadOnlyList<string> Criteria) { public ScoreSchema(string instructions, params string[] criteria) : this(instructions, (IReadOnlyList<string>)criteria) { if (Criteria.Count < 2) throw new ArgumentException("Score requires at least two criteria.", nameof(criteria)); } public object ToApiQuestion() => new { type = "score", instructions = Instructions, criteria = Criteria }; }
public sealed record QuestionsSchema(IReadOnlyDictionary<string, object> Values);
public sealed class SystemOneResponse
{
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("answers")] public IReadOnlyDictionary<string, Answer> Answers { get; init; } = new Dictionary<string, Answer>();
    [JsonPropertyName("usage")] public Usage? Usage { get; init; }
    [JsonIgnore] public IReadOnlyDictionary<string, NoulAnswer> Nouls => Answers.Where(x => x.Value is NoulAnswer).ToDictionary(x => x.Key, x => (NoulAnswer)x.Value);
    [JsonIgnore] public IReadOnlyDictionary<string, ChoiceValue> Choices => Answers.Where(x => x.Value is ChoiceAnswer).ToDictionary(x => x.Key, x => { var a = (ChoiceAnswer)x.Value; return new ChoiceValue(a.Choice, a.Confidence); });
    [JsonIgnore] public IReadOnlyDictionary<string, ChoiceAnswer> ChoiceAnswers => Answers.Where(x => x.Value is ChoiceAnswer).ToDictionary(x => x.Key, x => (ChoiceAnswer)x.Value);
    [JsonIgnore] public IReadOnlyDictionary<string, ScoreAnswer> Scores => Answers.Where(x => x.Value is ScoreAnswer).ToDictionary(x => x.Key, x => (ScoreAnswer)x.Value);
    public static SystemOneResponse Parse(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement; var answers = new Dictionary<string, Answer>(StringComparer.Ordinal);
        if (root.TryGetProperty("answers", out var answerObject)) foreach (var property in answerObject.EnumerateObject()) { try { answers[property.Name] = Answer.Parse(property.Value); } catch (JsonException) { } }
        if (answers.Count == 0 && root.TryGetProperty("choices", out var choices)) foreach (var property in choices.EnumerateObject()) answers[property.Name] = new ChoiceAnswer(property.Value.GetProperty("choice").GetString() ?? "", new Dictionary<string, double>(), property.Value.TryGetProperty("confidence", out var c) ? c.GetDouble() : 1);
        var usage = root.TryGetProperty("usage", out var usageJson) ? JsonSerializer.Deserialize<Usage>(usageJson.GetRawText(), JsonDefaults.Options) : null;
        return new SystemOneResponse { Model = root.TryGetProperty("model", out var model) ? model.GetString() : null, Answers = answers, Usage = usage };
    }
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")][JsonDerivedType(typeof(NoulAnswer), "noul")][JsonDerivedType(typeof(ChoiceAnswer), "choice")][JsonDerivedType(typeof(ScoreAnswer), "score")]
public abstract record Answer(string Type)
{
    public static Answer Parse(JsonElement json) { var type = json.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null; return type switch { "noul" => new NoulAnswer(json.GetProperty("noul").GetDouble()), "choice" => new ChoiceAnswer(json.GetProperty("choice").GetString() ?? "", ReadNumberMap(json, "probabilities"), json.GetProperty("confidence").GetDouble()), "score" => new ScoreAnswer(json.GetProperty("score").GetDouble(), ReadStringMap(json, "legend"), ReadNumberMap(json, "probabilities"), json.GetProperty("confidence").GetDouble()), _ => throw new JsonException($"Unsupported TypeSafe answer type '{type}'.") }; }
    private static IReadOnlyDictionary<string, double> ReadNumberMap(JsonElement json, string name) => json.TryGetProperty(name, out var value) ? value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble()) : new Dictionary<string, double>();
    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement json, string name) => json.TryGetProperty(name, out var value) ? value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? "") : new Dictionary<string, string>();
}
public sealed record NoulAnswer(double Noul) : Answer("noul");
public sealed record ChoiceAnswer(string Choice, IReadOnlyDictionary<string, double> Probabilities, double Confidence) : Answer("choice");
public sealed record ScoreAnswer(double Score, IReadOnlyDictionary<string, string> Legend, IReadOnlyDictionary<string, double> Probabilities, double Confidence) : Answer("score");
public sealed record ChoiceValue(string Choice, double Confidence = 1.0);
public sealed record Usage([property: JsonPropertyName("input_tokens")] int InputTokens = 0, [property: JsonPropertyName("output_tokens")] int OutputTokens = 0);
public static class TypeSafeSimulator { public static SystemOneResponse Classify(object input) { var text = input.ToString()?.ToLowerInvariant() ?? ""; var category = text.Contains("charge") || text.Contains("billing") || text.Contains("bayar") ? "billing" : text.Contains("error") || text.Contains("bug") || text.Contains("technical") ? "technical" : "other"; return new() { Answers = new Dictionary<string, Answer> { ["category"] = new ChoiceAnswer(category, new Dictionary<string, double> { [category] = 1 }, 1) } }; } }
