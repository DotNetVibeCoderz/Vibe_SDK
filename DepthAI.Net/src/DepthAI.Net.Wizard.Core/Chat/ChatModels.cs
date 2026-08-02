using System.Text.Json.Serialization;

namespace DepthAI.Wizard.Chat;

/// <summary>Siapa yang menulis pesan.</summary>
public enum ChatRole
{
    User,
    Assistant,
    System,
}

/// <summary>Jenis lampiran pada pesan.</summary>
public enum AttachmentKind
{
    /// <summary>Gambar; dikirim ke model sebagai konten gambar.</summary>
    Image,

    /// <summary>Dokumen; tautannya disisipkan ke dalam teks pesan.</summary>
    Document,
}

/// <summary>
/// Berkas yang dilampirkan ke sebuah pesan.
/// </summary>
public sealed record ChatAttachment
{
    public required string FileName { get; init; }

    public required AttachmentKind Kind { get; init; }

    /// <summary>Path berkas setelah disalin ke penyimpanan sesi.</summary>
    public required string StoredPath { get; init; }

    /// <summary>URL berkas; untuk lampiran lokal berupa URI <c>file://</c>.</summary>
    public required string Url { get; init; }

    public string MimeType { get; init; } = "application/octet-stream";

    public long SizeBytes { get; init; }

    /// <summary>Ukuran yang mudah dibaca, untuk chip lampiran di UI.</summary>
    [JsonIgnore]
    public string DisplaySize => SizeBytes switch
    {
        >= 1 << 20 => $"{SizeBytes / (double)(1 << 20):F1} MB",
        >= 1 << 10 => $"{SizeBytes / (double)(1 << 10):F0} KB",
        _ => $"{SizeBytes} B",
    };
}

/// <summary>Satu pesan dalam percakapan.</summary>
public sealed record ChatMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required ChatRole Role { get; init; }

    public required string Text { get; set; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public IReadOnlyList<ChatAttachment> Attachments { get; init; } = [];

    /// <summary>Nama kernel function yang dipanggil saat menghasilkan pesan ini.</summary>
    public IReadOnlyList<string> ToolCalls { get; set; } = [];

    /// <summary>Terisi bila pembuatan balasan gagal; pesan tetap disimpan agar terlihat pengguna.</summary>
    public string? Error { get; set; }

    /// <summary>True selama balasan masih dialirkan.</summary>
    [JsonIgnore]
    public bool IsStreaming { get; set; }
}

/// <summary>
/// Satu percakapan. Wizard bisa membuka beberapa sesi sekaligus; tiap sesi punya
/// riwayat dan folder lampiran sendiri.
/// </summary>
public sealed record ChatSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Judul sesi; diturunkan dari pesan pertama bila belum diganti pengguna.</summary>
    public string Title { get; set; } = "Sesi baru";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public List<ChatMessage> Messages { get; init; } = [];

    /// <summary>Model yang dipakai sesi ini; berguna saat membandingkan penyedia.</summary>
    public string? Model { get; set; }

    [JsonIgnore]
    public int MessageCount => Messages.Count(m => m.Role != ChatRole.System);

    /// <summary>Cuplikan pesan terakhir untuk daftar sesi.</summary>
    [JsonIgnore]
    public string Preview
    {
        get
        {
            var last = Messages.LastOrDefault(m => m.Role != ChatRole.System);
            if (last is null)
            {
                return "Belum ada pesan";
            }

            var text = last.Text.ReplaceLineEndings(" ").Trim();
            return text.Length <= 60 ? text : text[..60] + "…";
        }
    }

    /// <summary>
    /// Menyetel judul dari pesan pengguna pertama. Dipanggil sekali agar judul yang
    /// sudah diubah pengguna tidak tertimpa.
    /// </summary>
    public void EnsureTitleFromFirstMessage()
    {
        if (Title != "Sesi baru")
        {
            return;
        }

        var first = Messages.FirstOrDefault(m => m.Role == ChatRole.User);
        if (first is null)
        {
            return;
        }

        var text = first.Text.ReplaceLineEndings(" ").Trim();
        Title = text.Length <= 42 ? text : text[..42] + "…";
    }
}

[JsonSerializable(typeof(ChatSession))]
[JsonSerializable(typeof(List<ChatSession>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ChatJsonContext : JsonSerializerContext;
