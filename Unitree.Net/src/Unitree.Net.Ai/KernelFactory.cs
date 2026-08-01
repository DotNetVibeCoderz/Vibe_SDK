using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Unitree.Net.Core;

namespace Unitree.Net.Ai;

/// <summary>
/// Builds a Semantic Kernel configured for the selected provider.
/// </summary>
/// <remarks>
/// <para>
/// The four providers reach the kernel by two different routes. OpenAI, Gemini and Ollama have first-party
/// Semantic Kernel connectors. Anthropic does not have a stable one, so its client is adapted through
/// <see cref="IChatClient"/> — the <c>Microsoft.Extensions.AI</c> abstraction that Semantic Kernel also
/// speaks. Both routes produce an <see cref="IChatCompletionService"/>, so nothing downstream can tell
/// which was used.
/// </para>
/// </remarks>
public static class KernelFactory
{
    /// <summary>
    /// Creates a kernel for <paramref name="options"/>.
    /// </summary>
    /// <param name="options">Provider selection and model settings.</param>
    /// <param name="loggerFactory">Logger factory shared with the kernel's own logging.</param>
    /// <exception cref="OptionsValidationFailure">The configuration is incomplete.</exception>
    public static Kernel Create(AiOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        IKernelBuilder builder = Kernel.CreateBuilder();

        if (loggerFactory is not null)
        {
            builder.Services.AddSingleton(loggerFactory);
        }

        string modelId = options.GetEffectiveModelId();

        switch (options.Provider)
        {
            case LlmProvider.OpenAI:
                ConfigureOpenAI(builder, options, modelId);
                break;

            case LlmProvider.Anthropic:
                ConfigureAnthropic(builder, options, modelId);
                break;

            case LlmProvider.Gemini:
                builder.AddGoogleAIGeminiChatCompletion(modelId, options.ApiKey, GoogleAIVersion.V1_Beta);
                break;

            case LlmProvider.Ollama:
                builder.AddOllamaChatCompletion(modelId, options.GetEffectiveEndpoint()!);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.Provider,
                    "Unknown LLM provider.");
        }

        return builder.Build();
    }

    /// <summary>
    /// Builds execution settings from <paramref name="options"/>.
    /// </summary>
    /// <remarks>
    /// Function-calling behaviour is decided here and nowhere else. When automatic invocation is
    /// disabled the model may still *propose* a call, but Semantic Kernel will not execute it — the
    /// application decides. That distinction is the difference between an assistant that suggests
    /// walking forward and one that simply does it.
    /// </remarks>
    public static PromptExecutionSettings CreateExecutionSettings(AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        FunctionChoiceBehavior? behaviour = options.AllowAutomaticFunctionCalling
            ? FunctionChoiceBehavior.Auto()
            : FunctionChoiceBehavior.Auto(autoInvoke: false);

        return options.Provider switch
        {
            LlmProvider.OpenAI => new OpenAIPromptExecutionSettings
            {
                ModelId = options.GetEffectiveModelId(),
                Temperature = options.Temperature,
                MaxTokens = options.MaxTokens,
                FunctionChoiceBehavior = behaviour,
            },

            LlmProvider.Gemini => new GeminiPromptExecutionSettings
            {
                ModelId = options.GetEffectiveModelId(),
                Temperature = options.Temperature,
                MaxTokens = options.MaxTokens,
                FunctionChoiceBehavior = behaviour,
            },

            _ => new PromptExecutionSettings
            {
                ModelId = options.GetEffectiveModelId(),
                FunctionChoiceBehavior = behaviour,
                ExtensionData = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["temperature"] = options.Temperature,
                    ["max_tokens"] = options.MaxTokens,
                },
            },
        };
    }

    private static void ConfigureOpenAI(IKernelBuilder builder, AiOptions options, string modelId)
    {
        Uri? endpoint = options.GetEffectiveEndpoint();

        if (endpoint is null)
        {
            builder.AddOpenAIChatCompletion(modelId, options.ApiKey);
            return;
        }

        // A configured endpoint means an OpenAI-compatible gateway — Azure OpenAI, a local vLLM server,
        // an enterprise proxy. The OpenAI connector handles all of them through the same client.
        builder.AddOpenAIChatCompletion(modelId, endpoint, options.ApiKey);
    }

    private static void ConfigureAnthropic(IKernelBuilder builder, AiOptions options, string modelId)
    {
        builder.Services.AddSingleton<IChatCompletionService>(serviceProvider =>
        {
            var anthropic = new AnthropicClient(new APIAuthentication(options.ApiKey));

            // MessagesEndpoint implements IChatClient explicitly, so the cast is required rather than
            // incidental — the interface members are not visible on the concrete type.
            var chatClient = (IChatClient)anthropic.Messages;

            ILoggerFactory loggers =
                serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;

            IChatClient configured = new ChatClientBuilder(chatClient)
                .UseLogging(loggers)
                .UseFunctionInvocation(loggers)
                .Build(serviceProvider);

            return configured.AsChatCompletionService(serviceProvider);
        });

        builder.Services.AddSingleton(new AnthropicModelMarker(modelId));
    }

    /// <summary>
    /// Records which Anthropic model a kernel was built for.
    /// </summary>
    /// <remarks>
    /// The Anthropic client takes its model from the per-request options rather than from construction,
    /// so the choice has to travel with the kernel to be visible for diagnostics.
    /// </remarks>
    internal sealed record AnthropicModelMarker(string ModelId);
}
