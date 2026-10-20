namespace SiWare.Ai;

/// <summary>Model output for a set of facts. Always advisory: it is generated text, not a measurement.</summary>
public sealed record Interpretation(string Text, string Provider, string Model, string Language, DateTimeOffset CreatedAt)
{
    public const string DisclaimerEnglish = "AI-generated, advisory only. Verify against reference methods before acting on it.";
    public const string DisclaimerIndonesian = "Dihasilkan AI, hanya sebagai saran. Verifikasi dengan metode acuan sebelum digunakan.";
    public bool IsAiGenerated => true;
    public string Disclaimer => Language == "id" ? DisclaimerIndonesian : DisclaimerEnglish;
}

/// <summary>Turns deterministic facts (see <see cref="SpectrumFacts"/>) into a short natural-language interpretation.</summary>
public interface ISpectrumInterpreter
{
    Task<Interpretation> InterpretAsync(string facts, string language = "en", CancellationToken cancellationToken = default);
}

/// <summary>The instruction shared by every interpreter, so providers are compared on equal terms.</summary>
public static class InterpretationPrompt
{
    public const int MaxFactsLength = 8_000;

    public static string System(string language) =>
        "You are a near-infrared (NIR) spectroscopy assistant for the SiWare.NET SDK. " +
        "Use only the facts given. Do not invent measurements, sample identities, or concentrations. " +
        "If the data is SIMULATED, say so in the first sentence. " +
        "Give: (1) a one-sentence summary, (2) tentative band assignments with typical NIR origins (for example O-H, C-H, N-H overtones and combinations), " +
        (language == "id" ? "hedged with 'mungkin' or 'kemungkinan', " : "hedged with 'may' or 'likely', ") +
        "(3) one or two concrete next steps. Keep it under 160 words, plain text, no headings or lists. " +
        (language == "id" ? "Answer entirely in Bahasa Indonesia; keep chemical notation such as O-H as is." : "Answer in English.");

    public static string User(string facts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(facts);
        if (facts.Length > MaxFactsLength) throw new ArgumentException($"Facts exceed {MaxFactsLength} characters.", nameof(facts));
        return "Facts computed by SiWare.NET:\n" + facts.Trim();
    }

    public static string NormalizeLanguage(string? language) => language?.Trim().ToLowerInvariant() is "id" or "id-id" or "indonesian" ? "id" : "en";

    /// <summary>Removes reasoning blocks some models emit before the answer.</summary>
    public static string Clean(string text)
    {
        var cleaned = text;
        int start;
        while ((start = cleaned.IndexOf("<think>", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = cleaned.IndexOf("</think>", start, StringComparison.OrdinalIgnoreCase);
            cleaned = end < 0 ? cleaned[..start] : cleaned[..start] + cleaned[(end + "</think>".Length)..];
        }
        return cleaned.Trim();
    }
}
