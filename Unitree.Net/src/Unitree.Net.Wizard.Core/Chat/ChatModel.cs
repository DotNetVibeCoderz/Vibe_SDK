using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unitree.Net.Wizard.Core.Chat;

/// <summary>Who produced a chat message.</summary>
public enum ChatRole
{
    /// <summary>The operator.</summary>
    User,

    /// <summary>Jack.</summary>
    Assistant,

    /// <summary>A note from the wizard itself — a tool call, an error.</summary>
    System,
}

/// <summary>What kind of file is attached to a message.</summary>
public enum AttachmentKind
{
    /// <summary>An image, sent to the model as image content.</summary>
    Image,

    /// <summary>A document, whose text is included in the message.</summary>
    Document,
}

/// <summary>
/// A file attached to a chat message.
/// </summary>
/// <param name="Id">Unique identifier, also the stored file's name.</param>
/// <param name="FileName">The name the operator chose it by.</param>
/// <param name="Kind">Whether it is an image or a document.</param>
/// <param name="ContentType">MIME type.</param>
/// <param name="StoredPath">Where the wizard copied it.</param>
/// <param name="SizeBytes">Size in bytes.</param>
/// <param name="ExtractedText">
/// For documents, the text handed to the model. Null for images and for files that could not be read
/// as text.
/// </param>
public sealed record ChatAttachment(
    string Id,
    string FileName,
    AttachmentKind Kind,
    string ContentType,
    string StoredPath,
    long SizeBytes,
    string? ExtractedText)
{
    /// <summary>
    /// A URL the chat panel can render the attachment from.
    /// </summary>
    /// <remarks>
    /// A data URI rather than a file path: the WebView serves from a virtual host and will not load
    /// <c>file://</c> resources, so a path would render as a broken image.
    /// </remarks>
    public string ToDataUri()
    {
        byte[] bytes = File.ReadAllBytes(StoredPath);
        return $"data:{ContentType};base64,{Convert.ToBase64String(bytes)}";
    }
}

/// <summary>
/// One message in a conversation.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="Role">Who said it.</param>
/// <param name="Text">The message body, in Markdown.</param>
/// <param name="Timestamp">When it was created.</param>
/// <param name="Attachments">Files attached to it.</param>
/// <param name="ToolCalls">Names of tools invoked while producing it.</param>
public sealed record ChatMessage(
    string Id,
    ChatRole Role,
    string Text,
    DateTimeOffset Timestamp,
    IReadOnlyList<ChatAttachment> Attachments,
    IReadOnlyList<string> ToolCalls)
{
    /// <summary>Creates a message with no attachments.</summary>
    /// <param name="role">Who said it.</param>
    /// <param name="text">The body.</param>
    public static ChatMessage Create(ChatRole role, string text) =>
        new(Guid.NewGuid().ToString("n"), role, text, DateTimeOffset.Now, [], []);
}

/// <summary>
/// A named conversation with Jack.
/// </summary>
/// <remarks>
/// Mutations go through <see cref="Add"/>, <see cref="ReplaceLast"/> and <see cref="Reset"/>, and
/// readers take a <see cref="Snapshot"/>. That is not ceremony: a streamed reply is rewritten on
/// every chunk, from the stream's continuation thread, while the UI is enumerating the same list to
/// render it. <c>List&lt;T&gt;</c>'s indexer setter bumps the version counter, so the render throws
/// "collection was modified" partway through the answer.
/// </remarks>
public sealed class ChatSession
{
    private readonly Lock _gate = new();

    /// <summary>Unique identifier, also the persisted file's name.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("n");

    /// <summary>Display name shown in the session list.</summary>
    public string Title { get; set; } = "New chat";

    /// <summary>When the session was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>When it was last written to.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>The messages, oldest first.</summary>
    public List<ChatMessage> Messages { get; init; } = [];

    /// <summary>Whether the session has any real content.</summary>
    [JsonIgnore]
    public bool IsEmpty
    {
        get
        {
            using (_gate.EnterScope())
            {
                return Messages.Count == 0;
            }
        }
    }

    /// <summary>Takes a copy of the messages, safe to enumerate while a reply is streaming.</summary>
    public IReadOnlyList<ChatMessage> Snapshot()
    {
        using (_gate.EnterScope())
        {
            return [.. Messages];
        }
    }

    /// <summary>Appends a message and refreshes the title if it is still the default.</summary>
    /// <param name="message">The message to append.</param>
    public void Add(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        using (_gate.EnterScope())
        {
            Messages.Add(message);
            UpdatedAt = DateTimeOffset.Now;

            // The first thing the operator types names the session. A list of a dozen conversations
            // all called "New chat" is the same as no list at all.
            if (message.Role == ChatRole.User && Messages.Count(m => m.Role == ChatRole.User) == 1)
            {
                Title = Summarise(message.Text);
            }
        }
    }

    /// <summary>
    /// Replaces the last message, as a streaming reply grows.
    /// </summary>
    /// <param name="message">The message to put in its place.</param>
    public void ReplaceLast(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        using (_gate.EnterScope())
        {
            if (Messages.Count == 0)
            {
                Messages.Add(message);
            }
            else
            {
                Messages[^1] = message;
            }

            UpdatedAt = DateTimeOffset.Now;
        }
    }

    /// <summary>Discards every message but keeps the session and its identity.</summary>
    public void Reset()
    {
        using (_gate.EnterScope())
        {
            Messages.Clear();
            Title = "New chat";
            UpdatedAt = DateTimeOffset.Now;
        }
    }

    private static string Summarise(string text)
    {
        string collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (collapsed.Length == 0)
        {
            return "New chat";
        }

        return collapsed.Length <= 42 ? collapsed : collapsed[..41].TrimEnd() + "…";
    }
}

/// <summary>
/// Stores chat sessions and their attachments on disk.
/// </summary>
/// <remarks>
/// One JSON file per session under the wizard's application-data folder. Separate files rather than
/// one index because a corrupt write then costs a single conversation instead of all of them.
/// </remarks>
public sealed class ChatSessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _sessionDirectory;
    private readonly string _attachmentDirectory;

    /// <summary>Creates a store rooted at <paramref name="rootPath"/>.</summary>
    /// <param name="rootPath">
    /// Folder to store under. Defaults to <c>%APPDATA%\Unitree.Net.Wizard</c> when null or empty.
    /// </param>
    public ChatSessionStore(string? rootPath = null)
    {
        string root = string.IsNullOrWhiteSpace(rootPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Unitree.Net.Wizard")
            : rootPath;

        _sessionDirectory = Path.Combine(root, "sessions");
        _attachmentDirectory = Path.Combine(root, "attachments");

        Directory.CreateDirectory(_sessionDirectory);
        Directory.CreateDirectory(_attachmentDirectory);
    }

    /// <summary>Loads every stored session, most recently updated first.</summary>
    public IReadOnlyList<ChatSession> LoadAll()
    {
        var sessions = new List<ChatSession>();

        foreach (string path in Directory.EnumerateFiles(_sessionDirectory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(path), SerializerOptions) is { } session)
                {
                    sessions.Add(session);
                }
            }
            catch (JsonException)
            {
                // A session that will not parse is skipped rather than fatal. Losing one conversation
                // is annoying; refusing to open the application is worse.
            }
        }

        return [.. sessions.OrderByDescending(session => session.UpdatedAt)];
    }

    /// <summary>Writes a session to disk.</summary>
    /// <param name="session">The session to save.</param>
    public void Save(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Serialising the live session would enumerate the message list, which a streaming reply may
        // be rewriting. The snapshot is the same defence the UI uses.
        var copy = new ChatSession
        {
            Id = session.Id,
            Title = session.Title,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            Messages = [.. session.Snapshot()],
        };

        File.WriteAllText(
            Path.Combine(_sessionDirectory, $"{session.Id}.json"),
            JsonSerializer.Serialize(copy, SerializerOptions));
    }

    /// <summary>Deletes a session and forgets its attachments.</summary>
    /// <param name="session">The session to delete.</param>
    public void Delete(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        string path = Path.Combine(_sessionDirectory, $"{session.Id}.json");

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        foreach (ChatAttachment attachment in session.Snapshot().SelectMany(message => message.Attachments))
        {
            if (File.Exists(attachment.StoredPath))
            {
                File.Delete(attachment.StoredPath);
            }
        }
    }

    /// <summary>
    /// Copies an uploaded file into the store.
    /// </summary>
    /// <param name="fileName">The operator's file name.</param>
    /// <param name="content">The file's bytes.</param>
    /// <returns>The stored attachment.</returns>
    public async Task<ChatAttachment> StoreAttachmentAsync(string fileName, Stream content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        string id = Guid.NewGuid().ToString("n");
        string extension = Path.GetExtension(fileName);
        string storedPath = Path.Combine(_attachmentDirectory, id + extension);

        await using (FileStream target = File.Create(storedPath))
        {
            await content.CopyToAsync(target).ConfigureAwait(false);
        }

        var info = new FileInfo(storedPath);
        (AttachmentKind kind, string contentType) = Classify(extension);

        string? text = null;

        if (kind == AttachmentKind.Document)
        {
            text = await ReadAsTextAsync(storedPath).ConfigureAwait(false);
        }

        return new ChatAttachment(id, fileName, kind, contentType, storedPath, info.Length, text);
    }

    private static (AttachmentKind Kind, string ContentType) Classify(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => (AttachmentKind.Image, "image/png"),
            ".jpg" or ".jpeg" => (AttachmentKind.Image, "image/jpeg"),
            ".gif" => (AttachmentKind.Image, "image/gif"),
            ".webp" => (AttachmentKind.Image, "image/webp"),
            ".bmp" => (AttachmentKind.Image, "image/bmp"),
            ".svg" => (AttachmentKind.Image, "image/svg+xml"),
            ".json" => (AttachmentKind.Document, "application/json"),
            ".md" => (AttachmentKind.Document, "text/markdown"),
            ".cs" or ".txt" or ".log" or ".csv" or ".yml" or ".yaml" or ".xml" or ".csproj" =>
                (AttachmentKind.Document, "text/plain"),
            ".pdf" => (AttachmentKind.Document, "application/pdf"),
            _ => (AttachmentKind.Document, "application/octet-stream"),
        };

    /// <summary>
    /// Reads a document as text, or reports why it could not be.
    /// </summary>
    /// <remarks>
    /// Truncated at 200 kB. A large log pasted in whole would consume the whole context window and
    /// push out the conversation that gives it meaning.
    /// </remarks>
    private static async Task<string?> ReadAsTextAsync(string path)
    {
        const int MaxCharacters = 200_000;

        try
        {
            string text = await File.ReadAllTextAsync(path).ConfigureAwait(false);

            // A null byte in the first block means this is not text, whatever the extension says.
            if (text.AsSpan(0, Math.Min(text.Length, 2048)).Contains('\0'))
            {
                return null;
            }

            return text.Length <= MaxCharacters
                ? text
                : text[..MaxCharacters] + $"\n\n… truncated, {text.Length - MaxCharacters:N0} more characters.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
