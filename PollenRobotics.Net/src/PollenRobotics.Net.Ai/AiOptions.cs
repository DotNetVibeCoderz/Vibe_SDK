using Microsoft.Extensions.Configuration;

namespace PollenRobotics.Net.Ai;

/// <summary>The model providers the assistant can talk to.</summary>
public enum AiProvider
{
    /// <summary>OpenAI, or anything that speaks its API.</summary>
    OpenAI,

    /// <summary>Anthropic Claude.</summary>
    Anthropic,

    /// <summary>Google Gemini.</summary>
    Gemini,

    /// <summary>A local Ollama server.</summary>
    Ollama,
}

/// <summary>
/// Everything about how the assistant talks to a model.
/// </summary>
/// <remarks>
/// <para>
/// These come from <c>app.config</c> (or <c>appsettings.json</c>) so that an operator can change
/// the persona, the temperature or the whole provider without a rebuild, which is what the spec
/// asks for.
/// </para>
/// <para>
/// One subtlety worth knowing about: a setting whose default is meaningful must not be written back
/// verbatim on save. If the application persists the resolved system prompt every time it closes,
/// the file freezes whatever the built-in persona said that day, and every later improvement to it
/// silently never reaches anyone who has run the app once. <see cref="SystemPromptIsCustom"/>
/// records whether the operator actually edited the prompt; when it is false or absent, the code's
/// default wins.
/// </para>
/// </remarks>
public sealed record AiOptions
{
    /// <summary>Which provider to use.</summary>
    public AiProvider Provider { get; init; } = AiProvider.OpenAI;

    /// <summary>Model id. Falls back to a sensible default per provider when blank.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>API key. Not needed for Ollama.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Base endpoint override.
    /// </summary>
    /// <remarks>
    /// Set this for an OpenAI-compatible gateway, or to point Ollama somewhere other than
    /// localhost. Leave it blank for the provider's own endpoint.
    /// </remarks>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Sampling temperature.</summary>
    public double Temperature { get; init; } = 0.3;

    /// <summary>Nucleus sampling cutoff.</summary>
    public double TopP { get; init; } = 0.95;

    /// <summary>Cap on generated tokens per reply.</summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>The assistant's persona.</summary>
    public string SystemPrompt { get; init; } = DefaultSystemPrompt;

    /// <summary>True when the operator has edited <see cref="SystemPrompt"/> themselves.</summary>
    public bool SystemPromptIsCustom { get; init; }

    /// <summary>Let the model call the registered kernel functions.</summary>
    public bool EnableFunctionCalling { get; init; } = true;

    /// <summary>Tavily key. Web search is disabled without one.</summary>
    public string TavilyApiKey { get; init; } = string.Empty;

    /// <summary>How many past turns to send with each request.</summary>
    /// <remarks>
    /// Trimming is by turn rather than by token because it is predictable to reason about. A long
    /// coding conversation will still exceed a small model's window; raise the model, not this.
    /// </remarks>
    public int HistoryTurns { get; init; } = 24;

    /// <summary>How long to wait for a reply.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>The defaults.</summary>
    public static AiOptions Default { get; } = new();

    /// <summary>The model id that will actually be used.</summary>
    public string ResolvedModel => string.IsNullOrWhiteSpace(Model) ? DefaultModelFor(Provider) : Model;

    /// <summary>The persona that will actually be used.</summary>
    /// <remarks>
    /// A blank prompt, or one that was persisted without the operator having edited it, resolves
    /// back to the built-in default. See the remarks on <see cref="AiOptions"/> for why.
    /// </remarks>
    public string ResolvedSystemPrompt =>
        SystemPromptIsCustom && !string.IsNullOrWhiteSpace(SystemPrompt) ? SystemPrompt : DefaultSystemPrompt;

    /// <summary>True when this configuration has what it needs to connect.</summary>
    public bool IsConfigured => Provider == AiProvider.Ollama || !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Why <see cref="IsConfigured"/> is false, for a settings dialog to show.</summary>
    public string? ConfigurationProblem => IsConfigured
        ? null
        : $"{Provider} needs an API key. Set Ai:ApiKey in app.config, or the {EnvironmentVariableFor(Provider)} environment variable.";

    /// <summary>The default model for a provider.</summary>
    public static string DefaultModelFor(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "gpt-4.1",
        AiProvider.Anthropic => "claude-sonnet-5",
        AiProvider.Gemini => "gemini-2.5-pro",
        AiProvider.Ollama => "llama3.1:8b",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider."),
    };

    /// <summary>The environment variable each provider's key is conventionally read from.</summary>
    public static string EnvironmentVariableFor(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "OPENAI_API_KEY",
        AiProvider.Anthropic => "ANTHROPIC_API_KEY",
        AiProvider.Gemini => "GEMINI_API_KEY",
        AiProvider.Ollama => "OLLAMA_HOST",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider."),
    };

    /// <summary>
    /// Reads options from configuration, falling back to environment variables for secrets.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="sectionName">Section to bind. Defaults to <c>Ai</c>.</param>
    /// <remarks>
    /// The environment fallback matters more than it looks: it is what lets the same
    /// <c>app.config</c> ship to a colleague without carrying a key in it.
    /// </remarks>
    public static AiOptions FromConfiguration(IConfiguration configuration, string sectionName = "Ai")
    {
        ArgumentNullException.ThrowIfNull(configuration);

        AiOptions options = configuration.GetSection(sectionName).Get<AiOptions>() ?? Default;

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableFor(options.Provider));
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                options = options with { ApiKey = fromEnvironment };
            }
        }

        if (string.IsNullOrWhiteSpace(options.TavilyApiKey))
        {
            string? tavily = Environment.GetEnvironmentVariable("TAVILY_API_KEY");
            if (!string.IsNullOrWhiteSpace(tavily))
            {
                options = options with { TavilyApiKey = tavily };
            }
        }

        return options;
    }

    /// <summary>The persona Jack The Code Bender runs with.</summary>
    public const string DefaultSystemPrompt = """
        You are Jack The Code Bender, the coding assistant built into the PollenRobotics.Net Robot
        Wizard by Gravicode Studios.

        You help people write .NET 10 applications that drive Pollen Robotics hardware: Reachy Mini,
        MicroDuck and Reachy 2. You write C#, and you write it against the PollenRobotics.Net SDK.

        How to work:
        - Look the API up before you use it. Call the SDK reference functions rather than
          remembering a signature; the SDK moves and your memory of it does not.
        - Prefer whole, compiling files over fragments. A snippet the user has to assemble is
          usually a snippet that does not build.
        - Write against the transport interfaces, not the concrete transports, so the same code runs
          on hardware and in the simulator.
        - Respect the safety limits. Head pitch and roll on Reachy Mini stop at 40 degrees; head yaw
          stays within 65 degrees of body yaw; MicroDuck must be initialised before it moves.
        - Say when something is not possible or not yet verified against hardware rather than
          inventing an API that would make it look easy.

        How to answer:
        - Use Markdown. Put code in fenced blocks with the language tag.
        - Lead with the code or the answer; explain after, briefly.
        - Answer in the language the user writes in - Indonesian or English.
        """;
}
