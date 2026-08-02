using System.Text.Json;

namespace DepthAI.Wizard.Chat;

/// <summary>
/// Penyimpanan sesi chat di disk.
/// </summary>
/// <remarks>
/// Tiap sesi adalah satu berkas JSON di bawah folder data aplikasi, dengan
/// subfolder lampiran di sebelahnya. Satu berkas per sesi dipilih supaya sesi yang
/// rusak tidak menjatuhkan seluruh riwayat, dan menghapus sesi cukup menghapus folder.
/// </remarks>
public sealed class ChatSessionStore
{
    private readonly string _root;

    public ChatSessionStore(string? rootDirectory = null)
    {
        _root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GravicodeStudios",
            "JackTheCodeBender",
            "sessions");

        Directory.CreateDirectory(_root);
    }

    /// <summary>Folder tempat seluruh sesi disimpan.</summary>
    public string RootDirectory => _root;

    /// <summary>Memuat semua sesi, terbaru lebih dulu.</summary>
    public async Task<List<ChatSession>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var sessions = new List<ChatSession>();

        foreach (var file in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var stream = File.OpenRead(file);
                var session = await JsonSerializer.DeserializeAsync(
                    stream, ChatJsonContext.Default.ChatSession, cancellationToken);

                if (session is not null)
                {
                    sessions.Add(session);
                }
            }
            catch (JsonException)
            {
                // Satu berkas rusak tidak boleh menghalangi pemuatan sesi lain.
            }
        }

        return [.. sessions.OrderByDescending(s => s.UpdatedAt)];
    }

    /// <summary>Menyimpan satu sesi.</summary>
    public async Task SaveAsync(ChatSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.UpdatedAt = DateTimeOffset.Now;

        var directory = DirectoryFor(session.Id);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "session.json");
        var temporary = path + ".tmp";

        // Ditulis ke berkas sementara lalu dipindahkan, supaya penyimpanan yang
        // terputus tidak meninggalkan JSON setengah jadi.
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(
                stream, session, ChatJsonContext.Default.ChatSession, cancellationToken);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Menghapus sesi beserta lampirannya.</summary>
    public void Delete(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var directory = DirectoryFor(sessionId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Menyalin berkas ke penyimpanan sesi dan mengembalikan deskriptor lampirannya.
    /// </summary>
    /// <remarks>
    /// Berkasnya disalin, bukan dirujuk di tempat asal: pengguna sering melampirkan
    /// berkas dari Downloads lalu menghapusnya, dan riwayat chat tidak boleh ikut rusak.
    /// </remarks>
    public async Task<ChatAttachment> AddAttachmentAsync(
        string sessionId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Lampiran tidak ditemukan: {sourcePath}", sourcePath);
        }

        var directory = Path.Combine(DirectoryFor(sessionId), "attachments");
        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(sourcePath);
        var target = Path.Combine(directory, fileName);

        // Nama yang bentrok diberi akhiran, bukan ditimpa.
        var counter = 1;
        while (File.Exists(target))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            target = Path.Combine(directory, $"{stem}_{counter++}{extension}");
        }

        await using (var source = File.OpenRead(sourcePath))
        await using (var destination = File.Create(target))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        var info = new FileInfo(target);
        var mimeType = GuessMimeType(target);

        return new ChatAttachment
        {
            FileName = Path.GetFileName(target),
            Kind = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? AttachmentKind.Image
                : AttachmentKind.Document,
            StoredPath = target,
            Url = new Uri(target).AbsoluteUri,
            MimeType = mimeType,
            SizeBytes = info.Length,
        };
    }

    private string DirectoryFor(string sessionId) => Path.Combine(_root, sessionId);

    private static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".pdf" => "application/pdf",
        ".md" => "text/markdown",
        ".txt" or ".log" => "text/plain",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".cs" => "text/x-csharp",
        ".xml" or ".csproj" or ".axaml" => "application/xml",
        _ => "application/octet-stream",
    };
}
