using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PollenRobotics.Net.Ai.Plugins;
using PollenRobotics.Net.Core;

namespace PollenRobotics.Net.Ai;

/// <summary>
/// Builds a Semantic Kernel wired to whichever provider the configuration names.
/// </summary>
/// <remarks>
/// <para>
/// Semantic Kernel ships first-party connectors for OpenAI, Gemini and Ollama. Anthropic has no
/// official connector, so it comes in through <c>Microsoft.Extensions.AI</c>: <c>Anthropic.SDK</c>
/// exposes an <see cref="IChatClient"/>, and Semantic Kernel can consume any of those as a chat
/// completion service. That route supports streaming and tool calls, so the four providers behave
/// the same from the caller's side.
/// </para>
/// <para>
/// The kernel is built per session rather than shared. A kernel carries its plugin collection, and
/// the robot-control plugin is bound to one connected robot - sharing a kernel between two chat
/// sessions would let one session drive the other's robot.
/// </para>
/// </remarks>
public sealed class KernelFactory(ILoggerFactory? loggerFactory = null, IHttpClientFactory? httpClientFactory = null)
{
    private readonly ILoggerFactory? _loggerFactory = loggerFactory;
    private readonly IHttpClientFactory? _httpClientFactory = httpClientFactory;

    /// <summary>
    /// Builds a kernel for the given options.
    /// </summary>
    /// <param name="options">Provider, model and credentials.</param>
    /// <param name="plugins">Extra plugins to register beyond the built-in ones.</param>
    /// <exception cref="PollenRoboticsException">The options are missing a credential.</exception>
    public Kernel Build(AiOptions options, IEnumerable<KernelPlugin>? plugins = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured)
        {
            throw new PollenRoboticsException(options.ConfigurationProblem!);
        }

        IKernelBuilder builder = Kernel.CreateBuilder();

        if (_loggerFactory is not null)
        {
            builder.Services.AddSingleton(_loggerFactory);
        }

        switch (options.Provider)
        {
            case AiProvider.OpenAI:
                // The endpoint overload is a separate method rather than an optional parameter, so
                // an OpenAI-compatible gateway and OpenAI proper take different calls.
                if (ParseEndpoint(options.Endpoint) is { } openAiEndpoint)
                {
                    builder.AddOpenAIChatCompletion(
                        modelId: options.ResolvedModel,
                        endpoint: openAiEndpoint,
                        apiKey: options.ApiKey,
                        httpClient: CreateHttpClient(options));
                }
                else
                {
                    builder.AddOpenAIChatCompletion(
                        modelId: options.ResolvedModel,
                        apiKey: options.ApiKey,
                        httpClient: CreateHttpClient(options));
                }

                break;

            case AiProvider.Gemini:
                builder.AddGoogleAIGeminiChatCompletion(
                    modelId: options.ResolvedModel,
                    apiKey: options.ApiKey,
                    httpClient: CreateHttpClient(options));
                break;

            case AiProvider.Ollama:
                builder.AddOllamaChatCompletion(
                    modelId: options.ResolvedModel,
                    endpoint: ParseEndpoint(options.Endpoint) ?? new Uri("http://localhost:11434"));
                break;

            case AiProvider.Anthropic:
                builder.Services.AddSingleton<IChatCompletionService>(_ =>
                {
                    var client = new AnthropicClient(options.ApiKey);

                    // FunctionInvokingChatClient is what turns a tool call into an actual method
                    // call. Without it the model asks for a function and nothing answers, which
                    // presents as an assistant that describes what it would do and never does it.
                    IChatClient chat = client.Messages
                        .AsBuilder()
                        .UseFunctionInvocation(_loggerFactory)
                        .Build();

                    return chat.AsChatCompletionService();
                });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Provider, "Unknown provider.");
        }

        Kernel kernel = builder.Build();

        foreach (KernelPlugin plugin in BuiltInPlugins(options))
        {
            kernel.Plugins.Add(plugin);
        }

        if (plugins is not null)
        {
            foreach (KernelPlugin plugin in plugins)
            {
                kernel.Plugins.Add(plugin);
            }
        }

        return kernel;
    }

    /// <summary>
    /// The plugins every session gets.
    /// </summary>
    /// <remarks>
    /// Web search is only registered when a Tavily key is present. Registering it without one would
    /// advertise a tool to the model that fails on every call, and a model that has been told it
    /// can search will keep trying.
    /// </remarks>
    private IEnumerable<KernelPlugin> BuiltInPlugins(AiOptions options)
    {
        HttpClient http = CreateHttpClient(options) ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        yield return KernelPluginFactory.CreateFromObject(new ClockPlugin(), "Clock");
        yield return KernelPluginFactory.CreateFromObject(new MathPlugin(), "Math");
        yield return KernelPluginFactory.CreateFromObject(new WebContentPlugin(http), "Web");
        yield return KernelPluginFactory.CreateFromObject(new SdkReferencePlugin(), "SdkReference");
        yield return KernelPluginFactory.CreateFromObject(new CodeGenerationPlugin(), "CodeGen");

        if (!string.IsNullOrWhiteSpace(options.TavilyApiKey))
        {
            yield return KernelPluginFactory.CreateFromObject(new TavilySearchPlugin(options.TavilyApiKey, http), "Search");
        }
    }

    /// <summary>The execution settings matching these options, for a chat completion call.</summary>
    public static PromptExecutionSettings ExecutionSettings(AiOptions options)
    {
        var settings = new PromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["temperature"] = options.Temperature,
                ["top_p"] = options.TopP,
                ["max_tokens"] = options.MaxTokens,
            },
        };

        if (options.EnableFunctionCalling)
        {
            settings.FunctionChoiceBehavior = FunctionChoiceBehavior.Auto();
        }

        return settings;
    }

    private HttpClient? CreateHttpClient(AiOptions options)
    {
        HttpClient? client = _httpClientFactory?.CreateClient("pollen-ai");

        if (client is null)
        {
            return new HttpClient { Timeout = options.RequestTimeout };
        }

        client.Timeout = options.RequestTimeout;
        return client;
    }

    private static Uri? ParseEndpoint(string endpoint) =>
        string.IsNullOrWhiteSpace(endpoint) ? null : new Uri(endpoint);
}
