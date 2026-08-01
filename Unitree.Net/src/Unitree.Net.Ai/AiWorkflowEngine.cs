using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Unitree.Net.Control;
using Unitree.Net.Sensors;

namespace Unitree.Net.Ai;

/// <summary>
/// A natural-language interface to a robot, backed by Semantic Kernel.
/// </summary>
/// <remarks>
/// <para>
/// Wires the configured provider to the robot plugins and keeps a bounded conversation history. What the
/// model is allowed to do is decided entirely by <see cref="AiOptions"/>: with the defaults it can read
/// telemetry and explain what it sees, but the motion plugin is not even registered.
/// </para>
/// <para>
/// This is a supervisory interface, not a control loop. Language-model latency is measured in seconds;
/// nothing here belongs anywhere near a balance controller.
/// </para>
/// </remarks>
public sealed class AiWorkflowEngine
{
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chat;
    private readonly AiOptions _options;
    private readonly PromptExecutionSettings _executionSettings;
    private readonly ChatHistory _history;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    /// <summary>
    /// Creates an engine bound to <paramref name="robot"/>.
    /// </summary>
    /// <param name="robot">The robot the model may observe and, if permitted, command.</param>
    /// <param name="telemetry">Telemetry source for the read-only plugin.</param>
    /// <param name="options">Provider and permission configuration.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public AiWorkflowEngine(
        UnitreeRobot robot,
        TelemetryHub telemetry,
        AiOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(robot);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        ILoggerFactory loggers = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = loggers.CreateLogger<AiWorkflowEngine>();

        _kernel = KernelFactory.Create(options, loggers);
        _executionSettings = KernelFactory.CreateExecutionSettings(options);

        var telemetryPlugin = new RobotTelemetryPlugin(robot, telemetry);
        _kernel.Plugins.AddFromObject(telemetryPlugin, "RobotTelemetry");

        if (options.ExposeMotionFunctions)
        {
            var motionPlugin = new RobotMotionPlugin(
                robot,
                telemetryPlugin,
                loggers.CreateLogger<RobotMotionPlugin>());

            _kernel.Plugins.AddFromObject(motionPlugin, "RobotMotion");

            _logger.LogWarning(
                "Motion functions are exposed to the language model. Automatic invocation is {State}.",
                options.AllowAutomaticFunctionCalling ? "ENABLED" : "disabled");
        }

        _chat = _kernel.GetRequiredService<IChatCompletionService>();
        _history = new ChatHistory();
        _history.AddSystemMessage(BuildSystemPrompt(robot, options));

        _logger.LogInformation(
            "AI workflow engine ready: {Provider} / {Model}.",
            options.Provider,
            options.GetEffectiveModelId());
    }

    /// <summary>The underlying kernel, for registering additional plugins.</summary>
    public Kernel Kernel => _kernel;

    /// <summary>Number of exchanges retained, excluding the system prompt.</summary>
    public int HistoryLength => _history.Count - 1;

    /// <summary>
    /// Sends a message and returns the model's reply.
    /// </summary>
    /// <param name="message">The user's message.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Turns are serialised. Concurrent calls would interleave writes into the shared history and produce
    /// a transcript neither caller asked for.
    /// </remarks>
    public async Task<string> AskAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _history.AddUserMessage(message);
            TrimHistory();

            Microsoft.SemanticKernel.ChatMessageContent reply = await _chat
                .GetChatMessageContentAsync(_history, _executionSettings, _kernel, cancellationToken)
                .ConfigureAwait(false);

            string content = reply.Content ?? string.Empty;
            _history.AddAssistantMessage(content);
            return content;
        }
        finally
        {
            _turnLock.Release();
        }
    }

    /// <summary>
    /// Sends a message and streams the reply as it is generated.
    /// </summary>
    /// <param name="message">The user's message.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async IAsyncEnumerable<string> AskStreamingAsync(
        string message,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        var accumulated = new System.Text.StringBuilder();

        try
        {
            _history.AddUserMessage(message);
            TrimHistory();

            await foreach (StreamingChatMessageContent chunk in _chat
                .GetStreamingChatMessageContentsAsync(_history, _executionSettings, _kernel, cancellationToken)
                .ConfigureAwait(false))
            {
                if (string.IsNullOrEmpty(chunk.Content))
                {
                    continue;
                }

                accumulated.Append(chunk.Content);
                yield return chunk.Content;
            }

            _history.AddAssistantMessage(accumulated.ToString());
        }
        finally
        {
            _turnLock.Release();
        }
    }

    /// <summary>Clears the conversation, keeping the system prompt.</summary>
    public void ResetConversation()
    {
        Microsoft.SemanticKernel.ChatMessageContent systemMessage = _history[0];
        _history.Clear();
        _history.Add(systemMessage);
    }

    /// <summary>
    /// Drops the oldest exchanges once the history exceeds the configured limit.
    /// </summary>
    /// <remarks>
    /// Index 0 is always preserved — it holds the system prompt, and losing it would silently remove
    /// every safety instruction the model was given.
    /// </remarks>
    private void TrimHistory()
    {
        int maxMessages = (_options.MaxHistoryTurns * 2) + 1;

        while (_history.Count > maxMessages)
        {
            _history.RemoveAt(1);
        }
    }

    private static string BuildSystemPrompt(UnitreeRobot robot, AiOptions options)
    {
        var prompt = new System.Text.StringBuilder();

        prompt.AppendLine("You are an operations assistant for a Unitree robot.");
        prompt.AppendLine($"The robot is a {robot.Model}.");
        prompt.AppendLine();
        prompt.AppendLine("Guidelines:");
        prompt.AppendLine("- Read the robot's actual telemetry before answering questions about its state. Never guess.");
        prompt.AppendLine("- Report numbers as measured, with units. Do not round away detail that matters for diagnosis.");
        prompt.AppendLine("- If telemetry is unavailable, say so rather than inferring.");

        if (options.ExposeMotionFunctions)
        {
            prompt.AppendLine();
            prompt.AppendLine("You can command physical motion. This robot weighs tens of kilograms and moves in a real space:");
            prompt.AppendLine("- Call check_ready_to_move before any movement command.");
            prompt.AppendLine("- Make the robot stand before asking it to walk.");
            prompt.AppendLine("- Prefer several short movements over one long one, checking state between them.");
            prompt.AppendLine("- If the user's intent is ambiguous, ask rather than moving.");
            prompt.AppendLine("- Movement distances and angles are open-loop and approximate. Say so when reporting them.");
        }
        else
        {
            prompt.AppendLine("- You cannot command motion. If asked to move the robot, explain that motion control is disabled.");
        }

        return prompt.ToString();
    }
}
