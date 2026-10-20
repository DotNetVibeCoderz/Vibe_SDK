using SiWare.Ai;
using SiWare.Sdk;

namespace SiWare.Workbench;

/// <summary>
/// Optional AI interpretation of a spectrum. Configured from the environment only, so keys never reach the browser:
/// <c>SIWARE_LLM_BASE_URL</c> + <c>SIWARE_LLM_API_KEY</c> + <c>SIWARE_LLM_MODEL</c> for any OpenAI-compatible API,
/// otherwise <c>HF_TOKEN</c> for the Hugging Face router (model from <c>SIWARE_LLM_MODEL</c> when set).
/// </summary>
public sealed class InsightService(HttpClient http)
{
    private readonly ChatCompletionsOptions? options = Configure();

    public bool IsAvailable => options != null;
    public string? Model => options?.Model;

    public async Task<object> ExplainAsync(Spectrum spectrum, string? language, CancellationToken token)
    {
        if (options == null)
            throw new InvalidOperationException("AI insight is not configured. Start the Workbench with HF_TOKEN, or SIWARE_LLM_BASE_URL, SIWARE_LLM_API_KEY, and SIWARE_LLM_MODEL.");
        var facts = SpectrumFacts.Describe(spectrum);
        var result = await new ChatCompletionsInterpreter(http, options).InterpretAsync(facts, language ?? "en", token);
        return new { result.Text, result.Provider, result.Model, result.Disclaimer, result.IsAiGenerated, Facts = facts, SpectrumId = spectrum.Id };
    }

    private static ChatCompletionsOptions? Configure()
    {
        var model = Environment.GetEnvironmentVariable("SIWARE_LLM_MODEL");
        var baseUrl = Environment.GetEnvironmentVariable("SIWARE_LLM_BASE_URL");
        var key = Environment.GetEnvironmentVariable("SIWARE_LLM_API_KEY");
        if (baseUrl != null && key != null && model != null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            return new(uri, model, key, uri.Host);
        var token = Environment.GetEnvironmentVariable("HF_TOKEN") ?? Environment.GetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN");
        return token == null ? null : ChatCompletionsOptions.HuggingFaceRouter(model, token);
    }
}

public sealed record InsightRequest(string? Language = "en");
