using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PollenRobotics.Net.Core;

namespace PollenRobotics.Net.Ai.Chat;

/// <summary>
/// Jack The Code Bender: the assistant that writes robot applications.
/// </summary>
/// <remarks>
/// <para>
/// Wraps a Semantic Kernel chat completion service with the session model, so the UI deals in
/// <see cref="ChatSession"/> and never in kernel types. The kernel is rebuilt when
/// <see cref="Options"/> changes, because switching provider or model means a different connector.
/// </para>
/// <para>
/// Replies stream. A code-generation answer takes tens of seconds to produce in full, and a chat
/// panel that shows nothing for that long reads as broken however fast it eventually finishes.
/// </para>
/// </remarks>
public sealed class JackTheCodeBender
{
    private readonly KernelFactory _factory;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private Kernel? _kernel;
    private AiOptions _options;

    /// <summary>The name shown in the chat panel.</summary>
    public const string DisplayName = "Jack The Code Bender";

    /// <summary>The current configuration.</summary>
    public AiOptions Options
    {
        get
        {
            lock (_gate)
            {
                return _options;
            }
        }
    }

    /// <summary>True when a kernel has been built and the assistant can answer.</summary>
    public bool IsReady => _kernel is not null;

    /// <summary>Raised when the configuration changes.</summary>
    public event Action<AiOptions>? OptionsChanged;

    /// <summary>Creates the assistant.</summary>
    public JackTheCodeBender(AiOptions? options = null, KernelFactory? factory = null, ILogger<JackTheCodeBender>? logger = null)
    {
        _options = options ?? AiOptions.Default;
        _factory = factory ?? new KernelFactory();
        _logger = logger ?? NullLogger<JackTheCodeBender>.Instance;
    }

    /// <summary>Applies new settings and rebuilds the kernel.</summary>
    /// <param name="options">The new settings.</param>
    /// <param name="extraPlugins">Plugins to register alongside the built-in ones.</param>
    /// <exception cref="PollenRoboticsException">The settings are missing a credential.</exception>
    public void Configure(AiOptions options, IEnumerable<KernelPlugin>? extraPlugins = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        Kernel kernel = _factory.Build(options, extraPlugins);

        lock (_gate)
        {
            _options = options;
            _kernel = kernel;
        }

        _logger.LogInformation("Jack is using {Provider}/{Model}.", options.Provider, options.ResolvedModel);
        OptionsChanged?.Invoke(options);
    }

    /// <summary>
    /// Asks a question and streams the reply into the session.
    /// </summary>
    /// <param name="session">The conversation to append to.</param>
    /// <param name="prompt">What the user typed.</param>
    /// <param name="attachments">Files attached to this message.</param>
    /// <param name="cancellationToken">Stops generation.</param>
    /// <returns>The chunks as they arrive, so a caller that wants them can also observe the stream.</returns>
    /// <remarks>
    /// The session is updated as the stream advances, so a UI bound to it needs nothing more than
    /// to subscribe to <see cref="ChatSession.Changed"/>. Cancelling leaves whatever arrived so far
    /// in place and marks it - a half-written answer is often still useful, and silently discarding
    /// it is worse than showing that it was cut short.
    /// </remarks>
    public async IAsyncEnumerable<string> AskAsync(
        ChatSession session,
        string prompt,
        IReadOnlyList<ChatAttachment>? attachments = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        Kernel kernel = _kernel ?? throw new PollenRoboticsException(
            "Jack has not been configured yet. Call Configure with a provider and an API key first.");

        AiOptions options = Options;

        session.AddUser(prompt, attachments);

        ChatMessage reply = session.Add(new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = string.Empty,
            IsStreaming = true,
        });

        IChatCompletionService chat = kernel.GetRequiredService<IChatCompletionService>();
        ChatHistory history = BuildHistory(session, options);

        var accumulated = new StringBuilder();
        bool faulted = false;

        IAsyncEnumerator<StreamingChatMessageContent> stream = chat
            .GetStreamingChatMessageContentsAsync(history, KernelFactory.ExecutionSettings(options), kernel, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                string? chunk;

                try
                {
                    if (!await stream.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    chunk = stream.Current.Content;
                }
                catch (OperationCanceledException)
                {
                    session.Replace(reply.Id, reply with
                    {
                        Content = accumulated.Append("\n\n_[stopped]_").ToString(),
                        IsStreaming = false,
                    });

                    yield break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Generation failed.");
                    faulted = true;

                    session.Replace(reply.Id, reply with
                    {
                        Content = accumulated.Length > 0 ? accumulated.ToString() : string.Empty,
                        IsStreaming = false,
                    });

                    session.AddNote($"{options.Provider} returned an error: {ex.Message}");
                    yield break;
                }

                if (string.IsNullOrEmpty(chunk))
                {
                    continue;
                }

                accumulated.Append(chunk);
                session.Replace(reply.Id, reply with { Content = accumulated.ToString(), IsStreaming = true });
                yield return chunk;
            }
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);

            if (!faulted)
            {
                session.Replace(reply.Id, reply with { Content = accumulated.ToString(), IsStreaming = false });
            }
        }
    }

    /// <summary>Asks a question and waits for the whole reply.</summary>
    public async Task<string> AskCompleteAsync(
        ChatSession session,
        string prompt,
        IReadOnlyList<ChatAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();

        await foreach (string chunk in AskAsync(session, prompt, attachments, cancellationToken).ConfigureAwait(false))
        {
            builder.Append(chunk);
        }

        return builder.ToString();
    }

    /// <summary>
    /// A one-shot question that does not touch a session, for internal use by the editor.
    /// </summary>
    /// <remarks>
    /// Used for things like "explain this compiler error" where the answer belongs in a tooltip
    /// rather than in the conversation.
    /// </remarks>
    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        Kernel kernel = _kernel ?? throw new PollenRoboticsException("Jack has not been configured yet.");
        AiOptions options = Options;

        var history = new ChatHistory(options.ResolvedSystemPrompt);
        history.AddUserMessage(prompt);

        IChatCompletionService chat = kernel.GetRequiredService<IChatCompletionService>();

        IReadOnlyList<Microsoft.SemanticKernel.ChatMessageContent> result = await chat
            .GetChatMessageContentsAsync(history, KernelFactory.ExecutionSettings(options), kernel, cancellationToken)
            .ConfigureAwait(false);

        return result.Count > 0 ? result[0].Content ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// Builds the request history from the session.
    /// </summary>
    /// <remarks>
    /// Images attached to a user message become image content so the model can see them; documents
    /// were already folded into the text by <see cref="ChatMessage.ToModelText"/>. A local file path
    /// is skipped rather than sent - the model cannot open one, and pretending otherwise produces
    /// an answer about an image it never saw.
    /// </remarks>
    private static ChatHistory BuildHistory(ChatSession session, AiOptions options)
    {
        var history = new ChatHistory(options.ResolvedSystemPrompt);

        foreach (ChatMessage message in session.HistoryForModel(options.HistoryTurns))
        {
            if (message.Role == ChatRole.Assistant)
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    history.AddAssistantMessage(message.Content);
                }

                continue;
            }

            ChatAttachment[] images = [.. message.Attachments
                .Where(a => a.Kind == AttachmentKind.Image && Uri.TryCreate(a.Uri, UriKind.Absolute, out Uri? uri)
                            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))];

            if (images.Length == 0)
            {
                history.AddUserMessage(message.ToModelText());
                continue;
            }

            var items = new ChatMessageContentItemCollection { new TextContent(message.ToModelText()) };

            foreach (ChatAttachment image in images)
            {
                items.Add(new ImageContent(new Uri(image.Uri)));
            }

            history.AddUserMessage(items);
        }

        return history;
    }
}
