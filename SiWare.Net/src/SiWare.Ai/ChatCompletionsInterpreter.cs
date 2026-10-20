using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiWare.Ai;

/// <summary>Endpoint settings for an OpenAI-compatible <c>/chat/completions</c> API.</summary>
/// <param name="BaseUrl">API root ending before <c>/chat/completions</c>, e.g. <c>https://router.huggingface.co/v1</c>.</param>
/// <param name="Model">Model id as the provider names it, e.g. <c>Qwen/Qwen2.5-7B-Instruct</c>.</param>
/// <param name="ApiKey">Bearer token. Supply it at runtime (environment variable or secret store), never in source.</param>
/// <param name="Provider">Label recorded in <see cref="Interpretation.Provider"/> and error messages.</param>
/// <param name="MaxTokens">Upper bound on generated tokens. Reasoning models spend part of it before answering, so keep headroom.</param>
/// <param name="Temperature">Low values keep the answer close to the facts.</param>
/// <param name="Timeout">Whole-request timeout; 90 seconds when null.</param>
public sealed record ChatCompletionsOptions(Uri BaseUrl, string Model, string ApiKey, string Provider = "openai-compatible",
    int MaxTokens = 1200, double Temperature = 0.2, TimeSpan? Timeout = null)
{
    public const string HuggingFaceRouterUrl = "https://router.huggingface.co/v1";
    public const string DefaultHuggingFaceModel = "meta-llama/Llama-3.1-8B-Instruct";

    /// <summary>
    /// Hugging Face Inference Providers router. The token comes from <paramref name="token"/>, else <c>HF_TOKEN</c>,
    /// else <c>HUGGING_FACE_HUB_TOKEN</c> — the same variables HF.Net uses for Hub downloads.
    /// </summary>
    public static ChatCompletionsOptions HuggingFaceRouter(string? model = null, string? token = null) =>
        new(new Uri(HuggingFaceRouterUrl), model ?? DefaultHuggingFaceModel,
            token ?? Environment.GetEnvironmentVariable("HF_TOKEN") ?? Environment.GetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN")
            ?? throw new InvalidOperationException("Set HF_TOKEN to a Hugging Face access token with Inference Providers permission."),
            "huggingface");

    public override string ToString() => $"{Provider}:{Model} @ {BaseUrl}"; // Never print the key.
}

/// <summary>
/// Calls any OpenAI-compatible chat completions endpoint: the Hugging Face router, Azure OpenAI's v1 API, DeepSeek,
/// or a local server. The caller owns the <see cref="HttpClient"/>.
/// </summary>
public sealed class ChatCompletionsInterpreter(HttpClient http, ChatCompletionsOptions options) : ISpectrumInterpreter
{
    private const int MaxErrorLength = 300;

    public async Task<Interpretation> InterpretAsync(string facts, string language = "en", CancellationToken cancellationToken = default)
    {
        language = InterpretationPrompt.NormalizeLanguage(language);
        var body = new ChatRequest(options.Model,
            [new("system", InterpretationPrompt.System(language)), new("user", InterpretationPrompt.User(facts))],
            options.MaxTokens, options.Temperature);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUrl.ToString().TrimEnd('/') + "/chat/completions"))
        {
            Content = JsonContent.Create(body, ChatJsonContext.Default.ChatRequest)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout ?? TimeSpan.FromSeconds(90));
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{options.Provider} returned {(int)response.StatusCode}: {Truncate(ErrorMessage(text))}", null, response.StatusCode);

        ChatResponse? parsed;
        try { parsed = JsonSerializer.Deserialize(text, ChatJsonContext.Default.ChatResponse); }
        catch (JsonException ex) { throw new IOException($"{options.Provider} returned malformed JSON.", ex); }
        var choice = parsed?.Choices?.FirstOrDefault();
        var content = choice?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new IOException(choice?.FinishReason == "length"
                ? $"{options.Provider} used all {options.MaxTokens} tokens before answering (reasoning models think first); raise MaxTokens."
                : $"{options.Provider} returned no message content.");
        return new(InterpretationPrompt.Clean(content), options.Provider, parsed!.Model ?? options.Model, language, DateTimeOffset.UtcNow);
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.TryGetProperty("error", out var e) ? e : document.RootElement;
            if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? body;
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)) return message.GetString() ?? body;
        }
        catch (JsonException) { }
        return body;
    }

    private static string Truncate(string text) => text.Length <= MaxErrorLength ? text : text[..MaxErrorLength] + "…";
}

internal sealed record ChatMessage(string Role, string Content);
internal sealed record ChatRequest(string Model, ChatMessage[] Messages,
    [property: JsonPropertyName("max_tokens")] int MaxTokens, double Temperature);
internal sealed record ChatResponse(string? Model, ChatChoice[]? Choices);
internal sealed record ChatChoice(ChatMessage? Message, [property: JsonPropertyName("finish_reason")] string? FinishReason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatResponse))]
internal sealed partial class ChatJsonContext : JsonSerializerContext;
