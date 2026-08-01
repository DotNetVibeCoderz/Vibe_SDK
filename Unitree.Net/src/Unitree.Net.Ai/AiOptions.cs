using Unitree.Net.Core;

namespace Unitree.Net.Ai;

/// <summary>
/// Which large-language-model provider backs the workflow engine.
/// </summary>
public enum LlmProvider
{
    /// <summary>OpenAI or an Azure OpenAI deployment.</summary>
    OpenAI,

    /// <summary>Anthropic Claude.</summary>
    Anthropic,

    /// <summary>Google Gemini.</summary>
    Gemini,

    /// <summary>A local Ollama server.</summary>
    Ollama,
}

/// <summary>
/// Configuration for the AI workflow engine.
/// </summary>
/// <remarks>
/// Bind from configuration section <c>Unitree:Ai</c>. API keys belong in user secrets or environment
/// variables, never in <c>appsettings.json</c>.
/// </remarks>
public sealed class AiOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Unitree:Ai";

    /// <summary>Which provider to use.</summary>
    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

    /// <summary>
    /// Model identifier.
    /// </summary>
    /// <remarks>Left empty, a sensible current default for the chosen provider is used.</remarks>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>API key. Ignored for Ollama.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Endpoint override, used for Azure OpenAI and for a non-default Ollama host.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Sampling temperature.</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>Maximum tokens to generate per turn.</summary>
    public int MaxTokens { get; set; } = 1024;

    /// <summary>
    /// Whether the model may invoke robot plugin functions on its own.
    /// </summary>
    /// <remarks>
    /// Defaults to disabled. Automatic invocation means a language model can command physical motion
    /// without a human in the loop; that should be an explicit decision, taken with the robot's
    /// surroundings in mind.
    /// </remarks>
    public bool AllowAutomaticFunctionCalling { get; set; }

    /// <summary>
    /// Whether motion functions are exposed to the model at all.
    /// </summary>
    /// <remarks>
    /// With this off, the model can read telemetry and answer questions but cannot move the robot —
    /// a useful configuration for a diagnostic assistant.
    /// </remarks>
    public bool ExposeMotionFunctions { get; set; }

    /// <summary>Maximum conversation turns retained as context.</summary>
    public int MaxHistoryTurns { get; set; } = 20;

    /// <summary>Gets the effective model identifier, resolving the provider default when unset.</summary>
    public string GetEffectiveModelId() => string.IsNullOrWhiteSpace(ModelId)
        ? Provider switch
        {
            LlmProvider.OpenAI => "gpt-4o-mini",
            LlmProvider.Anthropic => "claude-sonnet-4-5",
            LlmProvider.Gemini => "gemini-2.0-flash",
            LlmProvider.Ollama => "llama3.2",
            _ => throw new ArgumentOutOfRangeException(nameof(Provider), Provider, "Unknown provider."),
        }
        : ModelId;

    /// <summary>Gets the effective endpoint, resolving the Ollama default when unset.</summary>
    public Uri? GetEffectiveEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(Endpoint))
        {
            return new Uri(Endpoint);
        }

        return Provider == LlmProvider.Ollama ? new Uri("http://localhost:11434") : null;
    }

    /// <summary>
    /// Validates that the configuration can produce a working client.
    /// </summary>
    /// <exception cref="OptionsValidationFailure">A required setting is missing.</exception>
    public void Validate()
    {
        bool requiresKey = Provider is LlmProvider.OpenAI or LlmProvider.Anthropic or LlmProvider.Gemini;

        if (requiresKey && string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new OptionsValidationFailure(
                $"{SectionName}:ApiKey is required for the {Provider} provider. " +
                "Supply it through user secrets or an environment variable rather than appsettings.json.");
        }

        if (!string.IsNullOrWhiteSpace(Endpoint) && !Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
        {
            throw new OptionsValidationFailure($"{SectionName}:Endpoint '{Endpoint}' is not a valid absolute URI.");
        }

        if (MaxTokens < 1)
        {
            throw new OptionsValidationFailure($"{SectionName}:MaxTokens must be at least 1.");
        }

        if (Temperature is < 0 or > 2)
        {
            throw new OptionsValidationFailure($"{SectionName}:Temperature must be between 0 and 2.");
        }
    }
}
