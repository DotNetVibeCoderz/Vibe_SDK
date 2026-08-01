using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Unitree.Net.Ai;
using Unitree.Net.Wizard.Core.Plugins;
using Unitree.Net.Wizard.Core.Projects;

namespace Unitree.Net.Wizard.Core.Chat;

/// <summary>
/// Jack The Code Bender — the wizard's coding assistant.
/// </summary>
/// <remarks>
/// <para>
/// A thin layer over Semantic Kernel that owns the plugin set, turns a stored
/// <see cref="ChatSession"/> into a <see cref="ChatHistory"/>, and streams replies back. The
/// conversation of record is the session, not the history: the history is rebuilt from it on every
/// turn so that deleting a message, resetting a session or switching between them all behave the
/// obvious way.
/// </para>
/// <para>
/// Rebuilding costs a little CPU per turn and buys the absence of a whole class of bug where the UI
/// and the model disagree about what was said.
/// </para>
/// </remarks>
public sealed class JackAssistant : IDisposable
{
    private readonly WizardSettings _settings;
    private readonly WebPlugin _web;
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    private Kernel? _kernel;
    private bool _disposed;

    /// <summary>Creates the assistant.</summary>
    /// <param name="settings">Provider, persona and tool configuration.</param>
    /// <param name="projects">Used by the project plugin to enumerate files.</param>
    /// <param name="currentProject">Returns the open project, or null when none is open.</param>
    /// <param name="fileChanged">Called when Jack writes a file, so the editor can refresh.</param>
    /// <param name="loggerFactory">Optional logging for the kernel.</param>
    public JackAssistant(
        WizardSettings settings,
        ProjectService projects,
        Func<WizardProject?> currentProject,
        Action<string, string>? fileChanged = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(currentProject);

        _settings = settings;
        _web = new WebPlugin(settings.TavilyApiKey);

        Projects = projects;
        CurrentProject = currentProject;
        FileChanged = fileChanged;
        LoggerFactory = loggerFactory;
    }

    /// <summary>Used by the project plugin.</summary>
    private ProjectService Projects { get; }

    /// <summary>Returns the open project.</summary>
    private Func<WizardProject?> CurrentProject { get; }

    /// <summary>Called when Jack writes a file.</summary>
    private Action<string, string>? FileChanged { get; }

    /// <summary>Optional kernel logging.</summary>
    private ILoggerFactory? LoggerFactory { get; }

    /// <summary>The last error from building the kernel, or null if it built.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether a kernel could be built from the current settings.</summary>
    public bool IsReady => TryGetKernel() is not null;

    /// <summary>
    /// Discards the cached kernel so the next turn picks up changed settings.
    /// </summary>
    /// <remarks>
    /// Called when the settings dialog is saved. Without it, changing the provider appears to do
    /// nothing until the application is restarted.
    /// </remarks>
    public void InvalidateKernel()
    {
        _kernel = null;
        LastError = null;
    }

    /// <summary>
    /// Streams a reply to <paramref name="session"/>'s latest message.
    /// </summary>
    /// <param name="session">The conversation. Its last message must be from the user.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>Chunks of the reply as they arrive.</returns>
    /// <remarks>
    /// Turns are serialised. Two concurrent turns on one session would interleave their writes into
    /// the same message list and produce a conversation that never happened.
    /// </remarks>
    public async IAsyncEnumerable<string> StreamReplyAsync(
        ChatSession session,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        Kernel? kernel = TryGetKernel();

        if (kernel is null)
        {
            yield return LastError ?? "Jack is not configured. Open Settings and choose a provider.";
            yield break;
        }

        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            IChatCompletionService chat = kernel.GetRequiredService<IChatCompletionService>();
            ChatHistory history = BuildHistory(session);
            PromptExecutionSettings execution = KernelFactory.CreateExecutionSettings(_settings.ToAiOptions());

            IAsyncEnumerable<StreamingChatMessageContent> stream = chat.GetStreamingChatMessageContentsAsync(
                history, execution, kernel, cancellationToken);

            await foreach (StreamingChatMessageContent chunk in stream.ConfigureAwait(false))
            {
                if (chunk.Content is { Length: > 0 } content)
                {
                    yield return content;
                }
            }
        }
        finally
        {
            _turnLock.Release();
        }
    }

    /// <summary>
    /// Converts a stored session into the history the model sees.
    /// </summary>
    /// <remarks>
    /// The system prompt is always index 0 and is never trimmed. Losing it would silently remove
    /// every instruction Jack was given, including the ones about not claiming things were tested on
    /// hardware — and the failure would look like the model simply becoming careless.
    /// </remarks>
    private ChatHistory BuildHistory(ChatSession session)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(_settings.SystemPrompt);

        IEnumerable<ChatMessage> messages = session.Snapshot()
            .Where(message => message.Role != ChatRole.System)
            .TakeLast(Math.Max(2, _settings.MaxHistoryTurns * 2));

        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.Assistant)
            {
                history.AddAssistantMessage(message.Text);
                continue;
            }

            var items = new ChatMessageContentItemCollection();
            string text = message.Text;

            foreach (ChatAttachment attachment in message.Attachments)
            {
                if (attachment.Kind == AttachmentKind.Image)
                {
                    // Images go as image content so a vision model can actually look at them. Passing
                    // the file name as text would tell it only that a file exists.
                    try
                    {
                        items.Add(new ImageContent(
                            File.ReadAllBytes(attachment.StoredPath), attachment.ContentType));
                    }
                    catch (IOException)
                    {
                        text += $"\n\n[Attached image '{attachment.FileName}' could not be read.]";
                    }
                }
                else if (attachment.ExtractedText is { Length: > 0 } document)
                {
                    text += $"\n\n--- attached file: {attachment.FileName} ---\n{document}";
                }
                else
                {
                    text += $"\n\n[Attached '{attachment.FileName}' " +
                            $"({attachment.SizeBytes:N0} bytes) — not readable as text.]";
                }
            }

            items.Insert(0, new TextContent(text));
            history.AddUserMessage(items);
        }

        return history;
    }

    private Kernel? TryGetKernel()
    {
        if (_kernel is not null)
        {
            return _kernel;
        }

        try
        {
            Kernel kernel = KernelFactory.Create(_settings.ToAiOptions(), LoggerFactory);

            kernel.Plugins.AddFromObject(new UtilityPlugin(), "Utility");
            kernel.Plugins.AddFromObject(_web, "Web");
            kernel.Plugins.AddFromObject(new ProjectPlugin(CurrentProject, Projects, FileChanged), "Project");
            kernel.Plugins.AddFromObject(new SdkPlugin(CurrentProject, FileChanged), "Sdk");

            _kernel = kernel;
            LastError = null;
            return kernel;
        }
        catch (Exception exception)
        {
            // A missing API key is the normal case here, not an exceptional one — the operator has
            // simply not filled it in yet. Report it as guidance rather than as a crash.
            LastError = $"Jack could not start: {exception.Message}";
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _web.Dispose();
        _turnLock.Dispose();
    }
}
