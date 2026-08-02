using DepthAI.Wizard.Ai.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace DepthAI.Wizard.Ai;

/// <summary>
/// Merakit <see cref="Kernel"/> Semantic Kernel sesuai penyedia yang dipilih dan
/// mendaftarkan seluruh plugin.
/// </summary>
public static class KernelFactory
{
    /// <summary>
    /// Membuat kernel untuk konfigurasi tertentu.
    /// </summary>
    /// <param name="settings">Penyedia, model, dan kredensial.</param>
    /// <param name="workspace">Konteks proyek yang dipakai plugin penulis berkas.</param>
    /// <param name="httpClient">
    /// HttpClient bersama untuk plugin web dan konektor Anthropic. Sediakan dari
    /// IHttpClientFactory bila memungkinkan, supaya koneksi dipakai ulang.
    /// </param>
    public static Kernel Create(
        AssistantSettings settings,
        IWorkspaceContext workspace,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(workspace);

        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(settings.MissingConfiguration);
        }

        var builder = Kernel.CreateBuilder();

        switch (settings.Provider)
        {
            case AiProvider.OpenAI:
                builder.AddOpenAIChatCompletion(settings.Model, settings.ApiKey);
                break;

            case AiProvider.Anthropic:
                // Semantic Kernel belum punya konektor Anthropic resmi, jadi
                // implementasi kita didaftarkan langsung sebagai layanan.
                builder.Services.AddSingleton<IChatCompletionService>(
                    _ => new AnthropicChatCompletionService(
                        settings.ApiKey, settings.Model, settings.Endpoint, httpClient));
                break;

            case AiProvider.Gemini:
                builder.AddGoogleAIGeminiChatCompletion(settings.Model, settings.ApiKey);
                break;

            case AiProvider.Ollama:
                builder.AddOpenAIChatCompletion(
                    settings.Model,
                    // Ollama memaparkan API yang kompatibel OpenAI; kuncinya tidak diperiksa
                    // tapi klien menolak string kosong, jadi diberi nilai penanda.
                    apiKey: string.IsNullOrWhiteSpace(settings.ApiKey) ? "ollama" : settings.ApiKey,
                    endpoint: new Uri(settings.Endpoint ?? "http://localhost:11434/v1"));
                break;

            default:
                throw new NotSupportedException($"Penyedia {settings.Provider} belum didukung.");
        }

        var kernel = builder.Build();
        RegisterPlugins(kernel, settings, workspace, httpClient);

        return kernel;
    }

    /// <summary>Mendaftarkan kernel function bawaan wizard.</summary>
    private static void RegisterPlugins(
        Kernel kernel,
        AssistantSettings settings,
        IWorkspaceContext workspace,
        HttpClient? httpClient)
    {
        kernel.Plugins.AddFromObject(new TimePlugin(), "time");
        kernel.Plugins.AddFromObject(new MathPlugin(), "math");
        kernel.Plugins.AddFromObject(new DepthAiPlugin(workspace), "depthai");

        var http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        kernel.Plugins.AddFromObject(new WebPlugin(http, settings.TavilyApiKey), "web");
    }

    /// <summary>
    /// Menyusun setelan eksekusi untuk penyedia yang dipilih, termasuk mengaktifkan
    /// pemanggilan fungsi otomatis.
    /// </summary>
    public static PromptExecutionSettings CreateExecutionSettings(AssistantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.Provider switch
        {
            AiProvider.Anthropic => new PromptExecutionSettings
            {
                // Konektor Anthropic membaca nilainya dari ExtensionData dan menangani
                // putaran tool sendiri.
                ExtensionData = new Dictionary<string, object>
                {
                    ["temperature"] = settings.Temperature,
                    ["max_tokens"] = settings.MaxTokens,
                },
            },

            AiProvider.Gemini => new GeminiPromptExecutionSettings
            {
                Temperature = settings.Temperature,
                MaxTokens = settings.MaxTokens,
                ToolCallBehavior = settings.EnableFunctionCalling
                    ? GeminiToolCallBehavior.AutoInvokeKernelFunctions
                    : null,
            },

            _ => new OpenAIPromptExecutionSettings
            {
                Temperature = settings.Temperature,
                MaxTokens = settings.MaxTokens,
                FunctionChoiceBehavior = settings.EnableFunctionCalling
                    ? FunctionChoiceBehavior.Auto()
                    : FunctionChoiceBehavior.None(),
            },
        };
    }
}
