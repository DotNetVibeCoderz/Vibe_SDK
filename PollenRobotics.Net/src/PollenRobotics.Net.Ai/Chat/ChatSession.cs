using System.Text.Json;
using System.Text.Json.Serialization;

namespace PollenRobotics.Net.Ai.Chat;

/// <summary>Who said it.</summary>
public enum ChatRole
{
    /// <summary>The persona, sent once at the top of every request.</summary>
    System,

    /// <summary>The person.</summary>
    User,

    /// <summary>Jack.</summary>
    Assistant,

    /// <summary>A note the UI shows but the model never sees - errors, cancellations.</summary>
    Note,
}

/// <summary>What kind of file was attached.</summary>
public enum AttachmentKind
{
    /// <summary>An image, sent to the model as image content.</summary>
    Image,

    /// <summary>Anything else, referenced by link in the message text.</summary>
    Document,
}

/// <summary>
/// A file attached to a message.
/// </summary>
/// <remarks>
/// Images and documents take different routes on purpose, as the spec asks: an image is uploaded
/// and its URL becomes image content the model can actually see, while a document is uploaded and
/// its link is appended to the message text. Sending a 40-page PDF as inline content would blow the
/// context window; sending an image as a link would mean the model never looks at it.
/// </remarks>
public sealed record ChatAttachment
{
    /// <summary>Stable id.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>File name as the user sees it.</summary>
    public required string FileName { get; init; }

    /// <summary>Image or document.</summary>
    public required AttachmentKind Kind { get; init; }

    /// <summary>Where the file now lives - an http(s) URL, or a local path.</summary>
    public required string Uri { get; init; }

    /// <summary>MIME type, when known.</summary>
    public string? MediaType { get; init; }

    /// <summary>Size in bytes, when known.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>A one-line description for the message bubble.</summary>
    public string Describe() => SizeBytes is { } bytes
        ? $"{FileName} ({FormatSize(bytes)})"
        : FileName;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}

/// <summary>One message in a conversation.</summary>
public sealed record ChatMessage
{
    /// <summary>Stable id.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Who said it.</summary>
    public required ChatRole Role { get; init; }

    /// <summary>The text, in Markdown.</summary>
    public required string Content { get; init; }

    /// <summary>When it was said.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>Files attached to it.</summary>
    public IReadOnlyList<ChatAttachment> Attachments { get; init; } = [];

    /// <summary>Names of kernel functions the assistant called while producing this.</summary>
    public IReadOnlyList<string> ToolCalls { get; init; } = [];

    /// <summary>True while the assistant is still streaming this message.</summary>
    [JsonIgnore]
    public bool IsStreaming { get; init; }

    /// <summary>
    /// The message text as it goes to the model, with document links appended.
    /// </summary>
    /// <remarks>
    /// Documents become links in the text so the model can decide to fetch them with
    /// <c>read_file_from_url</c>. Images are not mentioned here - they travel as image content.
    /// </remarks>
    public string ToModelText()
    {
        List<ChatAttachment> documents = [.. Attachments.Where(a => a.Kind == AttachmentKind.Document)];

        if (documents.Count == 0)
        {
            return Content;
        }

        var builder = new System.Text.StringBuilder(Content);
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("Attached files:");

        foreach (ChatAttachment document in documents)
        {
            builder.AppendLine($"- {document.FileName}: {document.Uri}");
        }

        return builder.ToString();
    }
}

/// <summary>
/// One conversation.
/// </summary>
/// <remarks>
/// <para>
/// Mutated from a background thread while a UI renders it, so every read hands out a snapshot. A
/// <see cref="List{T}"/> indexer assignment bumps the collection's version counter, which means
/// rewriting a streaming message from a continuation throws "collection was modified" in the middle
/// of the render - not on the thread doing the writing, and not with a stack trace that points at
/// it.
/// </para>
/// </remarks>
public sealed class ChatSession
{
    private readonly Lock _gate = new();
    private readonly List<ChatMessage> _messages = [];

    /// <summary>Stable id.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Display name, shown in the session list.</summary>
    public string Title { get; set; } = "New chat";

    /// <summary>When the session was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.Now;

    /// <summary>Number of messages.</summary>
    public int MessageCount
    {
        get
        {
            lock (_gate)
            {
                return _messages.Count;
            }
        }
    }

    /// <summary>Raised whenever the message list changes.</summary>
    public event Action<ChatSession>? Changed;

    /// <summary>A snapshot of the messages, oldest first.</summary>
    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (_gate)
        {
            return [.. _messages];
        }
    }

    /// <summary>Appends a message.</summary>
    public ChatMessage Add(ChatMessage message)
    {
        lock (_gate)
        {
            _messages.Add(message);
            UpdatedAt = DateTimeOffset.Now;

            // The first thing the user says makes a better title than "New chat", and nobody
            // renames these by hand.
            if (message.Role == ChatRole.User && _messages.Count(m => m.Role == ChatRole.User) == 1)
            {
                Title = Summarise(message.Content);
            }
        }

        Changed?.Invoke(this);
        return message;
    }

    /// <summary>Appends a user message.</summary>
    public ChatMessage AddUser(string content, IReadOnlyList<ChatAttachment>? attachments = null) =>
        Add(new ChatMessage { Role = ChatRole.User, Content = content, Attachments = attachments ?? [] });

    /// <summary>Appends a note the model will never see.</summary>
    public ChatMessage AddNote(string content) =>
        Add(new ChatMessage { Role = ChatRole.Note, Content = content });

    /// <summary>Replaces a message in place, for streaming.</summary>
    public void Replace(string messageId, ChatMessage updated)
    {
        lock (_gate)
        {
            int index = _messages.FindIndex(m => m.Id == messageId);
            if (index < 0)
            {
                return;
            }

            _messages[index] = updated;
            UpdatedAt = DateTimeOffset.Now;
        }

        Changed?.Invoke(this);
    }

    /// <summary>Empties the conversation but keeps the session.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _messages.Clear();
            Title = "New chat";
            UpdatedAt = DateTimeOffset.Now;
        }

        Changed?.Invoke(this);
    }

    /// <summary>
    /// The last <paramref name="turns"/> messages the model should see.
    /// </summary>
    /// <remarks>
    /// Notes are dropped - they are UI chrome, and feeding "connection lost" back to the model as
    /// though someone said it produces confused replies.
    /// </remarks>
    public IReadOnlyList<ChatMessage> HistoryForModel(int turns)
    {
        lock (_gate)
        {
            return [.. _messages.Where(m => m.Role is ChatRole.User or ChatRole.Assistant).TakeLast(turns * 2)];
        }
    }

    private static string Summarise(string content)
    {
        string flattened = content.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= 48 ? flattened : flattened[..48].TrimEnd() + "...";
    }
}

/// <summary>
/// The set of conversations, persisted between runs.
/// </summary>
/// <remarks>
/// Sessions live in one JSON file rather than a database. This is a desktop tool with a few dozen
/// conversations at most; a file is inspectable, portable and one less thing to install.
/// </remarks>
public sealed class ChatSessionStore
{
    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();
    private readonly List<ChatSession> _sessions = [];
    private readonly string _path;

    /// <summary>Raised when a session is created, deleted or reordered.</summary>
    public event Action? SessionsChanged;

    /// <summary>The session currently selected.</summary>
    public ChatSession? Active { get; private set; }

    /// <summary>Creates a store backed by a file.</summary>
    /// <param name="path">Where to persist. Defaults to the per-user application data folder.</param>
    public ChatSessionStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GravicodeStudios",
            "PollenRoboticsWizard",
            "chat-sessions.json");
    }

    /// <summary>All sessions, newest first.</summary>
    public IReadOnlyList<ChatSession> Sessions()
    {
        lock (_gate)
        {
            return [.. _sessions.OrderByDescending(s => s.UpdatedAt)];
        }
    }

    /// <summary>Creates a session and selects it.</summary>
    public ChatSession Create(string? title = null)
    {
        var session = new ChatSession();

        if (!string.IsNullOrWhiteSpace(title))
        {
            session.Title = title;
        }

        lock (_gate)
        {
            _sessions.Add(session);
            Active = session;
        }

        SessionsChanged?.Invoke();
        return session;
    }

    /// <summary>Selects a session by id.</summary>
    public ChatSession? Select(string id)
    {
        lock (_gate)
        {
            Active = _sessions.FirstOrDefault(s => s.Id == id) ?? Active;
            return Active;
        }
    }

    /// <summary>Deletes a session.</summary>
    public bool Delete(string id)
    {
        bool removed;

        lock (_gate)
        {
            int index = _sessions.FindIndex(s => s.Id == id);
            if (index < 0)
            {
                return false;
            }

            _sessions.RemoveAt(index);
            removed = true;

            if (Active?.Id == id)
            {
                Active = _sessions.Count > 0 ? _sessions[^1] : null;
            }
        }

        if (removed)
        {
            SessionsChanged?.Invoke();
        }

        return removed;
    }

    /// <summary>The active session, creating one if there is none.</summary>
    public ChatSession EnsureActive() => Active ?? Create();

    /// <summary>Writes the sessions to disk.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        PersistedSession[] payload;

        lock (_gate)
        {
            payload = [.. _sessions.Select(PersistedSession.From)];
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Written to a temporary file and moved into place. A crash halfway through a direct write
        // leaves a truncated file that fails to parse on next start, taking every conversation with
        // it.
        string temporary = _path + ".tmp";
        await using (FileStream stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, payload, FileOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>Reads the sessions from disk. Missing or corrupt files start empty.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            await using FileStream stream = File.OpenRead(_path);
            PersistedSession[]? payload = await JsonSerializer
                .DeserializeAsync<PersistedSession[]>(stream, FileOptions, cancellationToken).ConfigureAwait(false);

            if (payload is null)
            {
                return;
            }

            lock (_gate)
            {
                _sessions.Clear();
                _sessions.AddRange(payload.Select(p => p.ToSession()));
                Active = _sessions.LastOrDefault();
            }

            SessionsChanged?.Invoke();
        }
        catch (JsonException)
        {
            // A corrupt file should not stop the application starting. The user loses history,
            // which is bad; they would otherwise lose the tool, which is worse.
        }
    }

    private sealed record PersistedSession(string Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, ChatMessage[] Messages)
    {
        public static PersistedSession From(ChatSession session) =>
            new(session.Id, session.Title, session.CreatedAt, session.UpdatedAt, [.. session.Snapshot()]);

        public ChatSession ToSession()
        {
            var session = new ChatSession { Id = Id, Title = Title, CreatedAt = CreatedAt };

            foreach (ChatMessage message in Messages)
            {
                session.Add(message with { IsStreaming = false });
            }

            session.Title = Title;
            return session;
        }
    }
}
