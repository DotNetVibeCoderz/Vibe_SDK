using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using TypeSafeAppGen.Config;

namespace TypeSafeAppGen.Ai;

/// <summary>Kesalahan konfigurasi yang bisa diperbaiki pengguna di Settings.</summary>
public sealed class LlmConfigurationException(string message) : Exception(message);

/// <summary>Membangun <see cref="IKernelBuilder"/> dan execution settings untuk penyedia LLM yang dipilih.</summary>
public static class LlmFactory
{
    public static string DisplayName(LlmProvider provider) => provider switch
    {
        LlmProvider.AzureOpenAI => "Azure OpenAI",
        _ => provider.ToString(),
    };

    public static bool RequiresApiKey(LlmProvider provider) => provider != LlmProvider.Ollama;

    /// <summary>Validasi yang bisa dicek sebelum mengirim request, dengan pesan yang menunjukkan cara memperbaikinya.</summary>
    public static string? Validate(LlmProvider provider, ProviderProfile profile)
    {
        var name = DisplayName(provider);
        if (string.IsNullOrWhiteSpace(profile.Model)) return $"Choose a model for {name} in Settings.";
        if (RequiresApiKey(provider) && !profile.HasApiKey) return $"Add your {name} API key in Settings → Models to talk to Jack.";
        if (provider == LlmProvider.AzureOpenAI && !IsHttpUrl(profile.Endpoint)) return "Set your Azure OpenAI endpoint (https://<resource>.openai.azure.com/) in Settings → Models.";
        if (provider == LlmProvider.Ollama && !IsHttpUrl(profile.Endpoint)) return "Set the Ollama endpoint (for example http://localhost:11434) in Settings → Models.";
        if (provider is LlmProvider.OpenAI or LlmProvider.Claude && profile.Endpoint.Length > 0 && !IsHttpUrl(profile.Endpoint))
            return $"The {name} endpoint must be an http(s) URL, or empty for the default.";
        return null;
    }

    public static IKernelBuilder CreateBuilder(LlmProvider provider, ProviderProfile profile, HttpClient http, int maxOutputTokens = 16000)
    {
        if (Validate(provider, profile) is { } problem) throw new LlmConfigurationException(problem);
        var builder = Kernel.CreateBuilder();
        var model = profile.Model.Trim();
        var key = profile.ApiKey.Trim();
        var endpoint = profile.Endpoint.Trim();

        switch (provider)
        {
            case LlmProvider.OpenAI:
                // Endpoint kustom memungkinkan layanan kompatibel OpenAI (DeepSeek, Groq, LM Studio, vLLM).
                if (endpoint.Length == 0 || endpoint.StartsWith("https://api.openai.com", StringComparison.OrdinalIgnoreCase))
                    builder.AddOpenAIChatCompletion(model, key, httpClient: http);
                else
                    builder.AddOpenAIChatCompletion(model, new Uri(endpoint), key, httpClient: http);
                break;
            case LlmProvider.AzureOpenAI:
                builder.AddAzureOpenAIChatCompletion(model, endpoint, key, httpClient: http);
                break;
            case LlmProvider.Claude:
                // SDK resmi Anthropic, dibungkus sebagai IChatCompletionService lewat Microsoft.Extensions.AI.
                var anthropic = new AnthropicClient { ApiKey = key, HttpClient = http };
                if (endpoint.Length > 0) anthropic = new AnthropicClient { ApiKey = key, HttpClient = http, BaseUrl = endpoint };
                builder.Services.AddSingleton<IChatCompletionService>(anthropic.AsIChatClient(model, maxOutputTokens).AsChatCompletionService());
                break;
            case LlmProvider.Gemini:
                builder.AddGoogleAIGeminiChatCompletion(model, key, httpClient: http);
                break;
            case LlmProvider.Ollama:
                builder.AddOllamaChatCompletion(model, new Uri(endpoint));
                break;
        }
        return builder;
    }

    public static PromptExecutionSettings CreateSettings(LlmProvider provider, string model, AppConfig config)
    {
        var temperature = SupportsTemperature(provider, model) ? config.Temperature : (double?)null;
        var auto = FunctionChoiceBehavior.Auto();
        return provider switch
        {
            LlmProvider.OpenAI => new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = auto, Temperature = temperature },
            LlmProvider.AzureOpenAI => new AzureOpenAIPromptExecutionSettings { FunctionChoiceBehavior = auto, Temperature = temperature },
            LlmProvider.Gemini => new GeminiPromptExecutionSettings
            {
                ToolCallBehavior = GeminiToolCallBehavior.AutoInvokeKernelFunctions,
                Temperature = temperature,
                MaxTokens = config.MaxOutputTokens,
            },
            LlmProvider.Ollama => new OllamaPromptExecutionSettings { FunctionChoiceBehavior = auto, Temperature = (float?)temperature },
            _ => new PromptExecutionSettings
            {
                FunctionChoiceBehavior = auto,
                ExtensionData = temperature is { } t
                    ? new Dictionary<string, object> { ["max_tokens"] = config.MaxOutputTokens, ["temperature"] = t }
                    : new Dictionary<string, object> { ["max_tokens"] = config.MaxOutputTokens },
            },
        };
    }

    /// <summary>
    /// Model penalaran (OpenAI gpt-5/o-series, Claude generasi 4.7 ke atas) menolak parameter temperature,
    /// jadi parameter itu tidak dikirim untuk model tersebut.
    /// </summary>
    public static bool SupportsTemperature(LlmProvider provider, string model)
    {
        var m = model.ToLowerInvariant();
        return provider switch
        {
            LlmProvider.OpenAI or LlmProvider.AzureOpenAI => !(m.StartsWith("gpt-5") || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4")),
            LlmProvider.Claude => m.Contains("haiku-4-5") || m.Contains("-4-6") || m.Contains("-4-5") || m.Contains("claude-3"),
            _ => true,
        };
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !value.Contains('<');
}
