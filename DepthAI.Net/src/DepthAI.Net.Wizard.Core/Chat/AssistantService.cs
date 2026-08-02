using System.Runtime.CompilerServices;
using System.Text;
using DepthAI.Wizard.Ai;
using DepthAI.Wizard.Ai.Plugins;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace DepthAI.Wizard.Chat;

/// <summary>
/// Menghubungkan sesi chat dengan Semantic Kernel: menyusun riwayat, mengirim
/// permintaan, dan mengalirkan balasan.
/// </summary>
public sealed class AssistantService : IDisposable
{
    private readonly HttpClient _http;
    private readonly IWorkspaceContext _workspace;
    private readonly Lock _gate = new();

    private AssistantSettings _settings;
    private Kernel? _kernel;

    public AssistantService(AssistantSettings settings, IWorkspaceContext workspace, HttpClient? httpClient = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>Konfigurasi yang sedang dipakai.</summary>
    public AssistantSettings Settings => _settings;

    /// <summary>True bila asisten siap menerima pesan.</summary>
    public bool IsReady => _settings.IsConfigured;

    /// <summary>Alasan asisten belum siap, atau null bila sudah siap.</summary>
    public string? NotReadyReason => _settings.MissingConfiguration;

    /// <summary>
    /// Mengganti konfigurasi. Kernel dibangun ulang pada permintaan berikutnya, sehingga
    /// pergantian penyedia berlaku tanpa perlu me-restart aplikasi.
    /// </summary>
    public void UpdateSettings(AssistantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            _settings = settings;
            _kernel = null;
        }
    }

    /// <summary>
    /// Mengirim pesan pengguna dan mengalirkan balasan asisten potongan demi potongan.
    /// </summary>
    /// <remarks>
    /// Pesan pengguna dan pesan asisten yang masih kosong sudah ditambahkan ke
    /// <paramref name="session"/> sebelum metode ini dipanggil, supaya UI bisa langsung
    /// menampilkan keduanya. Metode ini mengisi teks pesan asisten seiring datangnya potongan.
    /// </remarks>
    public async IAsyncEnumerable<string> SendAsync(
        ChatSession session,
        ChatMessage assistantMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(assistantMessage);

        var kernel = GetOrCreateKernel();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        var history = BuildHistory(session);
        var executionSettings = KernelFactory.CreateExecutionSettings(_settings);

        var builder = new StringBuilder();

        IAsyncEnumerator<Microsoft.SemanticKernel.StreamingChatMessageContent>? enumerator = null;

        try
        {
            enumerator = chatService
                .GetStreamingChatMessageContentsAsync(history, executionSettings, kernel, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex)
        {
            assistantMessage.Error = Describe(ex);
            assistantMessage.IsStreaming = false;
            yield break;
        }

        try
        {
            while (true)
            {
                string? chunk;

                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    chunk = enumerator.Current.Content;
                }
                catch (OperationCanceledException)
                {
                    assistantMessage.Text = builder.ToString();
                    assistantMessage.Error = "Dibatalkan.";
                    break;
                }
                catch (Exception ex)
                {
                    assistantMessage.Error = Describe(ex);
                    break;
                }

                if (string.IsNullOrEmpty(chunk))
                {
                    continue;
                }

                builder.Append(chunk);
                assistantMessage.Text = builder.ToString();
                yield return chunk;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            assistantMessage.IsStreaming = false;
            assistantMessage.Text = builder.ToString();
            session.EnsureTitleFromFirstMessage();
            session.Model = _settings.Model;
        }
    }

    public void Dispose() => _http.Dispose();

    private Kernel GetOrCreateKernel()
    {
        lock (_gate)
        {
            return _kernel ??= KernelFactory.Create(_settings, _workspace, _http);
        }
    }

    /// <summary>
    /// Menyusun riwayat Semantic Kernel dari sesi.
    /// </summary>
    /// <remarks>
    /// Hanya <c>HistoryWindow</c> pesan terakhir yang dikirim: percakapan panjang akan
    /// melampaui jendela konteks model dan membuat tiap permintaan makin mahal.
    /// </remarks>
    private ChatHistory BuildHistory(ChatSession session)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(_settings.SystemPrompt);

        var recent = session.Messages
            .Where(m => m.Role != ChatRole.System && !string.IsNullOrWhiteSpace(m.Text))
            .TakeLast(_settings.HistoryWindow);

        foreach (var message in recent)
        {
            if (message.Role == ChatRole.Assistant)
            {
                history.AddAssistantMessage(message.Text);
                continue;
            }

            var images = message.Attachments
                .Where(a => a.Kind == AttachmentKind.Image && File.Exists(a.StoredPath))
                .ToList();

            if (images.Count == 0)
            {
                history.AddUserMessage(message.Text);
                continue;
            }

            var items = new ChatMessageContentItemCollection { new TextContent(message.Text) };

            foreach (var image in images)
            {
                // Gambar disematkan sebagai byte, bukan sebagai path: penyedia model
                // tidak bisa membaca berkas dari disk pengguna.
                items.Add(new ImageContent(File.ReadAllBytes(image.StoredPath), image.MimeType));
            }

            history.AddUserMessage(items);
        }

        return history;
    }

    /// <summary>Mengubah exception menjadi pesan yang bisa ditindaklanjuti pengguna.</summary>
    private string Describe(Exception exception) => exception switch
    {
        HttpRequestException http when http.Message.Contains("401", StringComparison.Ordinal)
            => $"Kunci API {_settings.Provider} ditolak. Periksa "
                + $"{AssistantSettings.EnvironmentVariableFor(_settings.Provider)} atau app.config.",

        HttpRequestException http when http.Message.Contains("429", StringComparison.Ordinal)
            => "Batas laju penyedia terlampaui. Tunggu sebentar lalu coba lagi.",

        HttpRequestException http when http.Message.Contains("404", StringComparison.Ordinal)
            => $"Model '{_settings.Model}' tidak ditemukan pada {_settings.Provider}. "
                + "Pilih model lain di Pengaturan.",

        HttpRequestException http
            => $"Tidak bisa menghubungi {_settings.Provider}: {http.Message}",

        TaskCanceledException => "Permintaan melewati batas waktu.",

        InvalidOperationException invalid => invalid.Message,

        _ => $"{exception.GetType().Name}: {exception.Message}",
    };
}

/// <summary>
/// Menyusun teks pesan pengguna berikut lampirannya.
/// </summary>
public static class MessageComposer
{
    /// <summary>
    /// Menggabungkan teks yang diketik dengan tautan dokumen.
    /// </summary>
    /// <remarks>
    /// Gambar tidak ikut ditulis di sini — gambar dikirim sebagai konten gambar sungguhan
    /// pada riwayat. Dokumen hanya bisa dirujuk lewat tautan, sesuai perilaku yang diminta.
    /// </remarks>
    public static string Compose(string text, IReadOnlyList<ChatAttachment> attachments)
    {
        var documents = attachments.Where(a => a.Kind == AttachmentKind.Document).ToList();
        if (documents.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text);
        builder.AppendLine().AppendLine();
        builder.AppendLine("Dokumen terlampir:");

        foreach (var document in documents)
        {
            builder.AppendLine($"- [{document.FileName}]({document.Url}) ({document.DisplaySize})");
        }

        return builder.ToString();
    }
}
