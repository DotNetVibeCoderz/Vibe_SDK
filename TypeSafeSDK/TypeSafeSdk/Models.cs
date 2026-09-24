using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeSafeSdk;

/// <summary>Factory pertanyaan bertipe <c>choice</c>.</summary>
public static class Choice
{
    /// <summary>Membuat schema choice dari daftar label.</summary>
    public static ChoiceSchema Create(params string[] values)
    {
        if (values is null || values.Length == 0) throw new ArgumentException("At least one choice is required.", nameof(values));
        return new ChoiceSchema(values);
    }

    /// <summary>Membuat schema choice dengan deskripsi per label, sejajar dengan <c>criteria</c> Python SDK.</summary>
    public static ChoiceSchema Create(IReadOnlyDictionary<string, string?> criteria, string instructions = "What is this about?")
    {
        if (criteria is null || criteria.Count == 0) throw new ArgumentException("At least one choice is required.", nameof(criteria));
        return new ChoiceSchema([.. criteria.Keys], instructions) { Descriptions = criteria };
    }
}

/// <summary>Pertanyaan pilihan berlabel. Menghasilkan <c>{ type: "choice", instructions, criteria }</c>.</summary>
public sealed record ChoiceSchema(IReadOnlyList<string> Values, string Instructions = "What is this about?") : IQuestionSchema
{
    /// <summary>Deskripsi opsional per label; nilai null berarti label tanpa penjelasan.</summary>
    public IReadOnlyDictionary<string, string?>? Descriptions { get; init; }

    /// <summary>Menyalin schema dengan instruksi berbeda.</summary>
    public ChoiceSchema WithInstructions(string instructions) => this with { Instructions = instructions };

    public object ToApiQuestion() => new
    {
        type = "choice",
        instructions = Instructions,
        criteria = Values.ToDictionary(value => value, value => Descriptions is not null && Descriptions.TryGetValue(value, out var text) ? text : null)
    };
}

/// <summary>Pertanyaan biner bernilai kontinu 0..1. Menghasilkan <c>{ type: "noul", ... }</c>.</summary>
public sealed record NoulSchema(string Instructions, string? True = null, string? False = null) : IQuestionSchema
{
    public object ToApiQuestion() => True is null && False is null
        ? new { type = "noul", instructions = Instructions }
        : new { type = "noul", instructions = Instructions, criteria = new { @true = True, @false = False } };
}

/// <summary>Pertanyaan skala berurutan. Menghasilkan <c>{ type: "score", instructions, criteria }</c>.</summary>
public sealed record ScoreSchema(string Instructions, IReadOnlyList<string> Criteria) : IQuestionSchema
{
    public ScoreSchema(string instructions, params string[] criteria) : this(instructions, (IReadOnlyList<string>)criteria)
    {
        if (Criteria.Count < 2) throw new ArgumentException("Score requires at least two criteria.", nameof(criteria));
    }

    public object ToApiQuestion() => new { type = "score", instructions = Instructions, criteria = Criteria };
}

/// <summary>Kontrak bersama seluruh schema pertanyaan agar bisa dinormalisasi client.</summary>
public interface IQuestionSchema
{
    /// <summary>Mengubah schema menjadi objek pertanyaan sesuai kontrak wire API.</summary>
    object ToApiQuestion();
}

/// <summary>Kumpulan pertanyaan bernama yang siap dikirim ke API.</summary>
public sealed record QuestionsSchema(IReadOnlyDictionary<string, object> Values);

/// <summary>Normalisasi berbagai bentuk input pertanyaan menjadi payload wire API.</summary>
public static class QuestionPayload
{
    /// <summary>
    /// Menerima <see cref="IQuestionSchema"/> tunggal, <see cref="QuestionsSchema"/>, dictionary berisi
    /// schema atau payload mentah, lalu mengembalikan dictionary yang sudah berbentuk kontrak API.
    /// </summary>
    public static object Normalize(object questions) => questions switch
    {
        null => throw new ArgumentNullException(nameof(questions)),
        IQuestionSchema schema => new Dictionary<string, object> { ["category"] = schema.ToApiQuestion() },
        QuestionsSchema typed => NormalizeMap(typed.Values),
        IReadOnlyDictionary<string, object> map => NormalizeMap(map),
        IDictionary<string, object> map => NormalizeMap(map),
        _ => questions
    };

    private static Dictionary<string, object> NormalizeMap(IEnumerable<KeyValuePair<string, object>> map)
        => map.ToDictionary(pair => pair.Key, pair => pair.Value is IQuestionSchema schema ? schema.ToApiQuestion() : pair.Value, StringComparer.Ordinal);
}

/// <summary>Response endpoint System One.</summary>
public sealed record SystemOneResponse
{
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("answers")] public IReadOnlyDictionary<string, Answer> Answers { get; init; } = new Dictionary<string, Answer>();
    [JsonPropertyName("usage")] public Usage? Usage { get; init; }
    /// <summary>Request id dari header response, diisi client untuk korelasi observability.</summary>
    [JsonIgnore] public string? RequestId { get; init; }

    [JsonIgnore] public IReadOnlyDictionary<string, NoulAnswer> Nouls => Filter<NoulAnswer>();
    [JsonIgnore] public IReadOnlyDictionary<string, ChoiceValue> Choices => Answers.Where(x => x.Value is ChoiceAnswer)
        .ToDictionary(x => x.Key, x => { var answer = (ChoiceAnswer)x.Value; return new ChoiceValue(answer.Choice, answer.Confidence); });
    [JsonIgnore] public IReadOnlyDictionary<string, ChoiceAnswer> ChoiceAnswers => Filter<ChoiceAnswer>();
    [JsonIgnore] public IReadOnlyDictionary<string, ScoreAnswer> Scores => Filter<ScoreAnswer>();

    private IReadOnlyDictionary<string, T> Filter<T>() where T : Answer
        => Answers.Where(x => x.Value is T).ToDictionary(x => x.Key, x => (T)x.Value, StringComparer.Ordinal);

    /// <summary>Mem-parsing body JSON response. Answer bertipe tidak dikenal diabaikan agar forward-compatible.</summary>
    public static SystemOneResponse Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var answers = new Dictionary<string, Answer>(StringComparer.Ordinal);
        if (root.TryGetProperty("answers", out var answerObject) && answerObject.ValueKind == JsonValueKind.Object)
            foreach (var property in answerObject.EnumerateObject())
            { try { answers[property.Name] = Answer.Parse(property.Value); } catch (JsonException) { } }
        if (answers.Count == 0 && root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Object)
            foreach (var property in choices.EnumerateObject())
                answers[property.Name] = new ChoiceAnswer(
                    property.Value.TryGetProperty("choice", out var choice) ? choice.GetString() ?? "" : "",
                    new Dictionary<string, double>(),
                    property.Value.TryGetProperty("confidence", out var confidence) ? confidence.GetDouble() : 1);
        var usage = root.TryGetProperty("usage", out var usageJson) && usageJson.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Usage>(usageJson.GetRawText(), JsonDefaults.Options) : null;
        return new SystemOneResponse
        {
            Model = root.TryGetProperty("model", out var model) ? model.GetString() : null,
            Answers = answers,
            Usage = usage
        };
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoulAnswer), "noul")]
[JsonDerivedType(typeof(ChoiceAnswer), "choice")]
[JsonDerivedType(typeof(ScoreAnswer), "score")]
public abstract record Answer(string Type)
{
    public static Answer Parse(JsonElement json)
    {
        var type = json.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;
        return type switch
        {
            "noul" => new NoulAnswer(json.GetProperty("noul").GetDouble()),
            "choice" => new ChoiceAnswer(json.GetProperty("choice").GetString() ?? "", ReadNumberMap(json, "probabilities"), ReadConfidence(json)),
            "score" => new ScoreAnswer(json.GetProperty("score").GetDouble(), ReadStringMap(json, "legend"), ReadNumberMap(json, "probabilities"), ReadConfidence(json)),
            _ => throw new JsonException($"Unsupported TypeSafe answer type '{type}'.")
        };
    }

    private static double ReadConfidence(JsonElement json) => json.TryGetProperty("confidence", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 1;
    private static IReadOnlyDictionary<string, double> ReadNumberMap(JsonElement json, string name)
        => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble()) : new Dictionary<string, double>();
    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement json, string name)
        => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() ?? "" : x.Value.GetRawText())
            : new Dictionary<string, string>();
}

/// <summary>Jawaban biner kontinu pada rentang 0..1.</summary>
public sealed record NoulAnswer(double Noul) : Answer("noul")
{
    /// <summary>Pembacaan boolean pada ambang 0,5.</summary>
    public bool IsTrue => Noul >= 0.5;
}

/// <summary>Label terpilih beserta distribusi probabilitasnya.</summary>
public sealed record ChoiceAnswer(string Choice, IReadOnlyDictionary<string, double> Probabilities, double Confidence) : Answer("choice");

/// <summary>Skor harapan pada skala berurutan beserta legend dan distribusinya.</summary>
public sealed record ScoreAnswer(double Score, IReadOnlyDictionary<string, string> Legend, IReadOnlyDictionary<string, double> Probabilities, double Confidence) : Answer("score")
{
    /// <summary>Legend dengan kunci integer, sejajar dengan <c>legend: dict[int, str]</c> Python SDK.</summary>
    public IReadOnlyDictionary<int, string> LegendByLevel => Legend
        .Where(pair => int.TryParse(pair.Key, out _)).ToDictionary(pair => int.Parse(pair.Key), pair => pair.Value);

    /// <summary>Label legend terdekat dengan skor yang dikembalikan.</summary>
    public string? NearestLabel => LegendByLevel.Count == 0 ? null
        : LegendByLevel.OrderBy(pair => Math.Abs(pair.Key - Score)).First().Value;
}

/// <summary>Ringkasan label terpilih untuk akses cepat.</summary>
public sealed record ChoiceValue(string Choice, double Confidence = 1.0);

/// <summary>Pemakaian token. Null berarti API tidak melaporkan angka tersebut.</summary>
public sealed record Usage(
    [property: JsonPropertyName("input_tokens")] int? InputTokens = null,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens = null)
{
    /// <summary>Total token bila kedua nilai tersedia.</summary>
    public int? TotalTokens => InputTokens is null && OutputTokens is null ? null : (InputTokens ?? 0) + (OutputTokens ?? 0);
}

/// <summary>
/// Simulator lokal deterministik. Menjawab pertanyaan choice, noul, dan score tanpa jaringan
/// sehingga demo, unit test, dan pipeline CI berjalan tanpa API key.
/// </summary>
public static class TypeSafeSimulator
{
    private static readonly Dictionary<string, string[]> Lexicon = new(StringComparer.OrdinalIgnoreCase)
    {
        ["billing"] = ["charge", "charged", "invoice", "refund", "payment", "billing", "subscription", "bayar", "tagihan", "faktur"],
        ["technical"] = ["error", "bug", "crash", "broken", "technical", "exception", "timeout", "gagal", "rusak", "galat"],
        ["urgent"] = ["urgent", "immediately", "asap", "critical", "emergency", "segera", "darurat"],
        ["positive"] = ["great", "love", "excellent", "thanks", "happy", "bagus", "senang"],
        ["negative"] = ["angry", "terrible", "awful", "hate", "worst", "buruk", "marah", "kecewa"]
    };

    private static readonly string[] FallbackLabels = ["other", "lainnya", "unknown", "none", "neutral", "netral"];

    /// <summary>Klasifikasi tiket klasik billing/technical/other.</summary>
    public static SystemOneResponse Classify(object input)
        => Answer(input, new Dictionary<string, object> { ["category"] = Choice.Create("billing", "technical", "other").ToApiQuestion() }, TypeSafeConstants.DefaultModel);

    /// <summary>Menjawab seluruh pertanyaan pada payload yang sudah dinormalisasi.</summary>
    public static SystemOneResponse Answer(object state, object questions, string? model = null)
    {
        var text = Flatten(state);
        var answers = new Dictionary<string, Answer>(StringComparer.Ordinal);
        foreach (var (name, question) in EnumerateQuestions(questions))
        {
            var answer = Simulate(text, question);
            if (answer is not null) answers[name] = answer;
        }
        return new SystemOneResponse
        {
            Model = model ?? TypeSafeConstants.DefaultModel,
            Answers = answers,
            Usage = new Usage(Math.Max(1, text.Length / 4), answers.Count * 8)
        };
    }

    private static IEnumerable<(string Name, JsonElement Question)> EnumerateQuestions(object questions)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(questions, JsonDefaults.Options));
        if (document.RootElement.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in document.RootElement.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Object) yield return (property.Name, property.Value.Clone());
    }

    private static Answer? Simulate(string text, JsonElement question)
    {
        var type = question.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;
        var instructions = question.TryGetProperty("instructions", out var instructionProperty) && instructionProperty.ValueKind == JsonValueKind.String
            ? instructionProperty.GetString() ?? "" : "";
        return type switch
        {
            "choice" => SimulateChoice(text, question),
            "noul" => SimulateNoul(text, instructions, question),
            "score" => SimulateScore(text, question),
            _ => null
        };
    }

    private static Answer? SimulateChoice(string text, JsonElement question)
    {
        var labels = ReadCriteriaLabels(question);
        if (labels.Count == 0) return null;
        var vocabulary = Vocabulary(labels.Select(label => label.Label + " " + (label.Description ?? "")));
        var scores = labels.ToDictionary(label => label.Label, label => Affinity(text, label.Label, label.Description, vocabulary), StringComparer.Ordinal);
        if (scores.Values.All(value => value <= 0))
        {
            // Tidak ada sinyal leksikal sama sekali. Simulator tidak menebak: distribusi dibiarkan
            // rata sehingga confidence rendah jujur menyatakan bahwa jawaban ini tidak bisa diandalkan.
            var abstain = labels.FirstOrDefault(label => FallbackLabels.Contains(label.Label, StringComparer.OrdinalIgnoreCase)).Label ?? labels[0].Label;
            return new ChoiceAnswer(abstain, Normalize(scores), Math.Round(1.0 / labels.Count, 4));
        }
        var probabilities = Normalize(scores);
        var winner = probabilities.OrderByDescending(pair => pair.Value).ThenBy(pair => labels.FindIndex(label => label.Label == pair.Key)).First();
        return new ChoiceAnswer(winner.Key, probabilities, Math.Round(winner.Value, 4));
    }

    /// <summary>
    /// Menilai pertanyaan noul dengan membandingkan bobot sisi true dan sisi false, ditambah
    /// sinyal konsep (urgensi, komitmen, kontrol eksperimen, dan lain-lain) yang dipicu oleh
    /// kata kunci pada instruksi. Bila tidak ada sinyal apa pun, simulator mengembalikan 0,5
    /// sebagai pernyataan jujur bahwa ia tidak tahu, bukan tebakan.
    /// </summary>
    private static Answer SimulateNoul(string text, string instructions, JsonElement question)
    {
        var (trueText, falseText) = ReadNoulCriteria(question);
        var positive = string.Join(' ', new[] { trueText, instructions }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var negative = falseText ?? "";
        var vocabulary = Vocabulary([positive, negative]);

        var yes = Affinity(text, positive, null, vocabulary) + ConceptScore(text, positive);
        var no = Affinity(text, negative, null, vocabulary);
        if (yes + no <= 0) return new NoulAnswer(0.5);

        var ratio = yes / (yes + no);
        return new NoulAnswer(Math.Round(0.08 + 0.84 * ratio, 4));
    }

    private static (string? True, string? False) ReadNoulCriteria(JsonElement question)
    {
        if (!question.TryGetProperty("criteria", out var criteria) || criteria.ValueKind != JsonValueKind.Object) return (null, null);
        var yes = criteria.TryGetProperty("true", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var no = criteria.TryGetProperty("false", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        return (yes, no);
    }

    /// <summary>
    /// Sinyal konsep: kata kunci pada instruksi memilih indikator yang dicari di dalam teks.
    /// Dua sinyal terakhir dihitung, bukan dicocokkan: nama diri dan kepadatan kata panjang.
    /// </summary>
    private static double ConceptScore(string text, string instructions)
    {
        double score = 0;
        foreach (var (concept, indicators) in ConceptLexicon)
        {
            if (!instructions.Contains(concept, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var indicator in indicators)
                if (text.Contains(indicator, StringComparison.OrdinalIgnoreCase)) score += 4;
        }
        if (Mentions(instructions, "named", "responsible", "owner")) score += ProperNouns(text) * 4;
        if (Mentions(instructions, "advanced", "reading level", "too hard", "readable")) score += LongWordRatio(text) > 0.16 ? 8 : 0;
        return score;
    }

    private static bool Mentions(string instructions, params string[] keywords)
        => keywords.Any(keyword => instructions.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>Menghitung kata berhuruf kapital yang bukan awal kalimat, sebagai perkiraan nama diri.</summary>
    private static int ProperNouns(string text)
    {
        var words = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        for (var index = 1; index < words.Length; index++)
        {
            var word = words[index].Trim('.', ',', ';', ':', '"', '\'', '(', ')');
            if (word.Length > 1 && char.IsUpper(word[0]) && word.Skip(1).All(char.IsLower)
                && !words[index - 1].EndsWith('.')) count++;
        }
        return count;
    }

    /// <summary>Proporsi kata panjang, perkiraan kasar tingkat kesulitan bacaan.</summary>
    private static double LongWordRatio(string text)
    {
        var words = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? 0 : (double)words.Count(word => word.Trim('.', ',').Length >= 10) / words.Length;
    }

    private static readonly Dictionary<string, string[]> ConceptLexicon = new(StringComparer.OrdinalIgnoreCase)
    {
        ["urgent"] = ["urgent", "asap", "immediately", "critical", "emergency", "never arrived", "still waiting", "segera", "darurat"],
        ["within one hour"] = ["urgent", "asap", "immediately", "critical", "never arrived", "twice"],
        ["abusive"] = ["useless", "uninstall", "idiot", "stupid", "trash", "shut up", "never come back", "hate"],
        ["harassment"] = ["useless", "uninstall", "idiot", "stupid", "trash", "shut up", "never come back"],
        ["commitment"] = ["will ", "shall ", "akan ", "before ", "deadline", "due ", "send", "deliver", "submit", "book ", "prepare"],
        ["act on"] = ["will ", "shall ", "before ", "deadline", "send", "deliver", "submit", "book "],
        ["control"] = ["control", "placebo", "comparison", "versus", " arm", "randomly assigned", "randomised"],
        ["comparison group"] = ["control", "placebo", "comparison", "versus", " arm"],
        ["stuck"] = ["keep getting", "cannot", "can't", "stuck", "confused", "is the", "why "],
        ["maintenance"] = ["falling", "rising", "drift", "degrad", "wear", "vibration", "leak"],
        ["revision"] = ["i think", "good and bad", "maybe", "it was", "mostly"],
        ["wake"] = ["critical", "fatal", "sigma", "emergency", "breach", "outage"],
        ["right now"] = ["critical", "fatal", "sigma", "emergency", "breach"],
        ["closing a lane"] = ["blocking", "blocked", "lane", "obstruct", "leaking"]
    };

    private static Answer? SimulateScore(string text, JsonElement question)
    {
        if (!question.TryGetProperty("criteria", out var criteria) || criteria.ValueKind != JsonValueKind.Array) return null;
        var levels = criteria.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText()).ToList();
        if (levels.Count < 2) return null;
        var vocabulary = Vocabulary(levels);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var index = 0; index < levels.Count; index++) scores[index.ToString()] = Affinity(text, levels[index], null, vocabulary);
        if (scores.Values.All(value => value <= 0))
        {
            var intensity = Affinity(text, "urgent", null, null) + Affinity(text, "negative", null, null);
            var chosen = Math.Clamp((int)Math.Round(intensity / 2.0 * (levels.Count - 1)), 0, levels.Count - 1);
            scores[chosen.ToString()] = 1;
        }
        var probabilities = Normalize(scores);
        var expected = probabilities.Sum(pair => int.Parse(pair.Key) * pair.Value);
        var legend = levels.Select((label, index) => (index, label)).ToDictionary(pair => pair.index.ToString(), pair => pair.label);
        return new ScoreAnswer(Math.Round(expected, 4), legend, probabilities, Math.Round(probabilities.Values.Max(), 4));
    }

    private static List<(string Label, string? Description)> ReadCriteriaLabels(JsonElement question)
    {
        var labels = new List<(string, string?)>();
        if (!question.TryGetProperty("criteria", out var criteria)) return labels;
        if (criteria.ValueKind == JsonValueKind.Object)
            foreach (var property in criteria.EnumerateObject())
                labels.Add((property.Name, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null));
        else if (criteria.ValueKind == JsonValueKind.Array)
            foreach (var item in criteria.EnumerateArray())
                labels.Add((item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText(), null));
        return labels;
    }

    /// <summary>
    /// Menghitung kedekatan teks dengan satu kriteria. Token yang muncul di banyak kriteria
    /// (misalnya kata "player" pada seluruh deskripsi) diberi bobot kecil agar distribusi tidak
    /// menjadi rata, mirip pembobotan inverse document frequency.
    /// </summary>
    private static double Affinity(string text, string label, string? description, IReadOnlyDictionary<string, int>? vocabulary)
    {
        double score = 0;
        foreach (var token in Tokenize(label + " " + (description ?? "")))
        {
            if (!Affirms(text, token)) continue;
            score += vocabulary is null ? 3 : Weight(vocabulary, token);
        }
        if (Lexicon.TryGetValue(label, out var keywords))
            foreach (var keyword in keywords)
                if (Affirms(text, keyword)) score += 4;
        return score;
    }

    private static readonly string[] Negators = ["no ", "not ", "never ", "without ", "tidak ", "tanpa ", "bukan "];

    /// <summary>
    /// Mencari token di dalam teks tetapi mengabaikan kemunculan yang dinegasikan, seperti
    /// "no nesting" atau "tanpa kontrol", agar penyebutan justru tidak menaikkan skor kriteria.
    /// </summary>
    private static bool Affirms(string text, string token)
    {
        var index = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var prefix = text[Math.Max(0, index - 16)..index];
            if (!Negators.Any(negator => prefix.Contains(negator, StringComparison.OrdinalIgnoreCase))) return true;
            index = index + 1 >= text.Length ? -1 : text.IndexOf(token, index + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private const string VocabularySizeKey = "\u0000criteria-count";

    private static double Weight(IReadOnlyDictionary<string, int> vocabulary, string token)
    {
        var total = vocabulary.GetValueOrDefault(VocabularySizeKey, 1);
        var frequency = vocabulary.GetValueOrDefault(token, 1);
        if (total <= 1) return 3;
        if (frequency >= total) return 0;                    // muncul di semua kriteria: tidak membedakan apa pun
        return frequency <= 1 ? 3 : 3.0 / frequency;         // makin umum sebuah token, makin kecil bobotnya
    }

    /// <summary>Menghitung berapa kriteria yang memuat tiap token.</summary>
    private static IReadOnlyDictionary<string, int> Vocabulary(IEnumerable<string> criteria)
    {
        var frequencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var criterion in criteria)
        {
            count++;
            foreach (var token in Tokenize(criterion)) frequencies[token] = frequencies.GetValueOrDefault(token) + 1;
        }
        frequencies[VocabularySizeKey] = count;
        return frequencies;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "that", "with", "from", "into", "about", "your", "their", "there", "which", "when", "what",
        "have", "been", "must", "should", "would", "could", "does", "only", "some", "such", "than", "then",
        "they", "them", "were", "will", "used", "using", "more", "most", "very", "over", "without", "rather",
        "yang", "untuk", "dengan", "pada", "atau", "dari", "adalah", "akan", "tidak", "sudah", "bisa"
    };

    private static IEnumerable<string> Tokenize(string value) => value
        .Split([' ', '\t', '\n', '\r', ',', '.', ';', ':', '/', '-', '_', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(token => token.Length > 3).Select(token => token.ToLowerInvariant())
        .Where(token => !StopWords.Contains(token)).Distinct();

    private static IReadOnlyDictionary<string, double> Normalize(IReadOnlyDictionary<string, double> scores)
    {
        const double smoothing = 0.35;
        var total = scores.Values.Sum() + smoothing * scores.Count;
        return scores.ToDictionary(pair => pair.Key, pair => Math.Round((pair.Value + smoothing) / total, 4), StringComparer.Ordinal);
    }

    /// <summary>
    /// Mengambil teks yang bisa dinilai dari state. Hanya nilai string dan angka yang diambil,
    /// nama field diabaikan agar kunci seperti <c>player_line</c> tidak ikut dicocokkan.
    /// </summary>
    private static string Flatten(object? state)
    {
        if (state is null) return "";
        if (state is string text) return text;
        var builder = new System.Text.StringBuilder();
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(state, JsonDefaults.Options));
            Collect(document.RootElement, builder);
        }
        catch (JsonException) { return state.ToString() ?? ""; }
        return builder.ToString();
    }

    private static void Collect(JsonElement element, System.Text.StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String: builder.Append(element.GetString()).Append(' '); break;
            case JsonValueKind.Number: builder.Append(element.GetRawText()).Append(' '); break;
            case JsonValueKind.Object: foreach (var property in element.EnumerateObject()) Collect(property.Value, builder); break;
            case JsonValueKind.Array: foreach (var item in element.EnumerateArray()) Collect(item, builder); break;
        }
    }
}
