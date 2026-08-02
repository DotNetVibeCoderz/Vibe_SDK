using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DepthAI.Wizard.Ai;
using DepthAI.Wizard.Ai.Plugins;
using DepthAI.Wizard.Chat;
using DepthAI.Wizard.Prompts;

namespace DepthAI.Wizard.App.Views;

/// <summary>Lampiran yang sedang disiapkan, dengan ikon untuk chip di UI.</summary>
public sealed class PendingAttachment(ChatAttachment attachment)
{
    public ChatAttachment Attachment { get; } = attachment;

    public string FileName => Attachment.FileName;

    public string DisplaySize => Attachment.DisplaySize;

    public string Icon => Attachment.Kind == AttachmentKind.Image ? "🖼" : "📄";
}

/// <summary>
/// Panel chat Jack The Code Bender: multi-sesi, lampiran, dan balasan Markdown.
/// </summary>
/// <remarks>
/// Pembersihan dilakukan di <c>OnUnloaded</c>, bukan lewat <see cref="IDisposable"/>:
/// Avalonia tidak pernah memanggil Dispose pada kontrol, jadi mengimplementasikannya
/// justru menghasilkan sumber daya yang tidak pernah dilepas.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Kontrol Avalonia dilepas lewat OnUnloaded; Dispose tidak pernah dipanggil framework.")]
public partial class ChatPanel : UserControl
{
    private readonly ChatSessionStore _store = new();
    private readonly ObservableCollection<ChatSession> _sessions = [];
    private readonly ObservableCollection<PendingAttachment> _pending = [];

    private AssistantService? _assistant;
    private IWorkspaceContext? _workspace;
    private ChatSession _session = new();
    private CancellationTokenSource? _sendCts;

    public ChatPanel()
    {
        InitializeComponent();

        Settings = AssistantSettings.Load();

        SessionBox.ItemsSource = _sessions;
        AttachmentList.ItemsSource = _pending;

        MarkdownRenderer.CodeCopyRequested += OnCopyCode;
        MarkdownRenderer.LinkActivated += OnOpenLink;

        ShufflePrompts();
        UpdateProviderText();

        // Balasan yang sudah dirender memegang brush dari tema saat dibuat, jadi
        // seluruh alur pesan digambar ulang ketika tema berganti — tanpa ini, teks
        // chat lama tetap memakai warna tema sebelumnya dan jadi tidak terbaca.
        ActualThemeVariantChanged += OnThemeChanged;

        _ = LoadSessionsAsync();
    }

    /// <summary>Konfigurasi asisten saat ini.</summary>
    public AssistantSettings Settings { get; private set; }

    /// <summary>Menyambungkan panel ke proyek yang terbuka, agar asisten bisa menulis berkas.</summary>
    public void AttachWorkspace(IWorkspaceContext workspace)
    {
        _workspace = workspace;
        _assistant = new AssistantService(Settings, workspace);
        UpdateProviderText();
    }

    /// <summary>Menerapkan konfigurasi baru tanpa me-restart aplikasi.</summary>
    public void ApplySettings(AssistantSettings settings)
    {
        Settings = settings;
        _assistant?.UpdateSettings(settings);
        UpdateProviderText();
    }

    /// <summary>
    /// Melepaskan asisten beserta HttpClient-nya.
    /// </summary>
    /// <remarks>
    /// Panel ini hidup selama jendela utama hidup, jadi kebocorannya tidak terlihat saat
    /// dipakai — tapi tetap salah, dan langsung terlihat pada test yang membuat panel
    /// berulang kali.
    /// </remarks>
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        MarkdownRenderer.CodeCopyRequested -= OnCopyCode;
        MarkdownRenderer.LinkActivated -= OnOpenLink;

        _sendCts?.Cancel();
        _assistant?.Dispose();
        _assistant = null;

        base.OnUnloaded(e);
    }

    // ------------------------------------------------------------- Sesi

    private async Task LoadSessionsAsync()
    {
        var sessions = await _store.LoadAllAsync();

        _sessions.Clear();
        foreach (var session in sessions)
        {
            _sessions.Add(session);
        }

        if (_sessions.Count == 0)
        {
            _sessions.Add(_session);
        }

        SessionBox.SelectedIndex = 0;
    }

    private void OnSessionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SessionBox.SelectedItem is ChatSession session)
        {
            _session = session;
            RenderThread();
        }
    }

    private async void OnNewSession(object? sender, RoutedEventArgs e)
    {
        var session = new ChatSession();
        _sessions.Insert(0, session);
        SessionBox.SelectedIndex = 0;

        await _store.SaveAsync(session);
    }

    private async void OnResetSession(object? sender, RoutedEventArgs e)
    {
        // Mengosongkan, bukan menghapus: pengguna sering ingin memulai ulang
        // percakapan tapi tetap menyimpan lampiran yang sudah diunggah.
        _session.Messages.Clear();
        _session.Title = "Sesi baru";

        RenderThread();
        await _store.SaveAsync(_session);
        SetStatus("Sesi dikosongkan");
    }

    private async void OnDeleteSession(object? sender, RoutedEventArgs e)
    {
        var target = _session;
        _store.Delete(target.Id);
        _sessions.Remove(target);

        if (_sessions.Count == 0)
        {
            var replacement = new ChatSession();
            _sessions.Add(replacement);
            await _store.SaveAsync(replacement);
        }

        SessionBox.SelectedIndex = 0;
        SetStatus("Sesi dihapus");
    }

    // --------------------------------------------------------- Lampiran

    private async void OnAttachImage(object? sender, RoutedEventArgs e)
        => await AttachAsync("Pilih gambar", FilePickerFileTypes.ImageAll);

    private async void OnAttachDocument(object? sender, RoutedEventArgs e)
        => await AttachAsync("Pilih dokumen", new FilePickerFileType("Dokumen")
        {
            Patterns = ["*.md", "*.txt", "*.pdf", "*.json", "*.csv", "*.cs", "*.log", "*.xml"],
        });

    private async Task AttachAsync(string title, FilePickerFileType fileType)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [fileType],
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is null)
            {
                continue;
            }

            try
            {
                var attachment = await _store.AddAttachmentAsync(_session.Id, path);
                _pending.Add(new PendingAttachment(attachment));
            }
            catch (Exception ex)
            {
                SetStatus($"Gagal melampirkan {Path.GetFileName(path)}: {ex.Message}", isError: true);
            }
        }
    }

    private void OnRemoveAttachment(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PendingAttachment attachment })
        {
            _pending.Remove(attachment);
        }
    }

    // ------------------------------------------------------------ Kirim

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter mengirim; Shift+Enter menyisipkan baris baru — konvensi yang
        // sudah dikenal dari aplikasi chat lain.
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void OnSend(object? sender, RoutedEventArgs e)
    {
        if (_sendCts is not null)
        {
            _sendCts.Cancel();
            return;
        }

        _ = SendAsync();
    }

    private async Task SendAsync()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (_workspace is null)
        {
            SetStatus("Panel chat belum tersambung ke workspace.", isError: true);
            return;
        }

        _assistant ??= new AssistantService(Settings, _workspace);

        if (!_assistant.IsReady)
        {
            SetStatus(_assistant.NotReadyReason ?? "Asisten belum dikonfigurasi.", isError: true);
            return;
        }

        var attachments = _pending.Select(p => p.Attachment).ToList();

        var userMessage = new ChatMessage
        {
            Role = ChatRole.User,
            Text = MessageComposer.Compose(text, attachments),
            Attachments = attachments,
        };

        var assistantMessage = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Text = string.Empty,
            IsStreaming = true,
        };

        _session.Messages.Add(userMessage);
        _session.Messages.Add(assistantMessage);

        InputBox.Text = string.Empty;
        _pending.Clear();
        RenderThread();

        using var cts = new CancellationTokenSource();
        _sendCts = cts;
        SendButton.Content = "Hentikan";
        SetStatus("Jack sedang berpikir…");

        try
        {
            var lastRender = DateTime.UtcNow;

            await foreach (var _ in _assistant.SendAsync(_session, assistantMessage, cts.Token))
            {
                // Alur teks datang sangat rapat; merender ulang tiap potongan membuat
                // UI tersendat. 60 ms sudah terasa mulus tanpa membebani layout.
                if (DateTime.UtcNow - lastRender > TimeSpan.FromMilliseconds(60))
                {
                    lastRender = DateTime.UtcNow;
                    RenderThread();
                }
            }
        }
        catch (Exception ex)
        {
            assistantMessage.Error = ex.Message;
        }
        finally
        {
            _sendCts = null;
            SendButton.Content = "Kirim";
            assistantMessage.IsStreaming = false;

            RenderThread();
            await _store.SaveAsync(_session);

            SetStatus(assistantMessage.Error is null ? string.Empty : assistantMessage.Error,
                isError: assistantMessage.Error is not null);

            RefreshSessionList();
        }
    }

    // ---------------------------------------------------------- Render

    private void RenderThread()
    {
        var messages = _session.Messages.Where(m => m.Role != ChatRole.System).ToList();

        EmptyState.IsVisible = messages.Count == 0;

        var panel = new StackPanel { Spacing = 14 };

        foreach (var message in messages)
        {
            panel.Children.Add(RenderMessage(message));
        }

        MessageList.Content = panel;

        // Digulir setelah layout selesai, jika tidak posisi akhirnya dihitung dari
        // tinggi konten yang lama.
        Dispatcher.UIThread.Post(() => ThreadScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    private Control RenderMessage(ChatMessage message)
    {
        var isUser = message.Role == ChatRole.User;

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 6),
            Children =
            {
                new TextBlock
                {
                    Text = isUser ? "ANDA" : "JACK",
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    LetterSpacing = 1.4,
                    Foreground = isUser ? App.Resource("InkFaint") : App.Resource("AccentNear"),
                },
                new TextBlock
                {
                    Text = message.Timestamp.ToString("HH:mm"),
                    FontSize = 10,
                    Foreground = App.Resource("InkFaint"),
                },
            },
        };

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(header);

        if (message.IsStreaming && string.IsNullOrEmpty(message.Text))
        {
            body.Children.Add(new TextBlock
            {
                Text = "▍",
                Foreground = App.Resource("AccentNear"),
                FontSize = 13,
            });
        }
        else
        {
            body.Children.Add(MarkdownRenderer.Render(message.Text));
        }

        // Lampiran gambar ditampilkan sebagai pratinjau agar pengguna melihat
        // persis apa yang dikirim ke model.
        foreach (var attachment in message.Attachments.Where(a => a.Kind == AttachmentKind.Image))
        {
            body.Children.Add(MarkdownRenderer.Render($"![{attachment.FileName}]({attachment.Url})"));
        }

        if (message.Error is not null)
        {
            body.Children.Add(new Border
            {
                Background = App.Resource("SurfaceMid"),
                BorderBrush = App.Resource("SignalError"),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(10, 8),
                CornerRadius = new CornerRadius(4),
                Child = new SelectableTextBlock
                {
                    Text = message.Error,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = App.Resource("SignalError"),
                },
            });
        }

        return new Border
        {
            Background = isUser ? App.Resource("SurfaceMid") : Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(isUser ? 12 : 0, isUser ? 10 : 0, isUser ? 12 : 0, isUser ? 10 : 0),
            Child = body,
        };
    }

    private void RefreshSessionList()
    {
        var snapshot = _sessions.ToList();
        var selected = SessionBox.SelectedIndex;

        _sessions.Clear();
        foreach (var session in snapshot)
        {
            _sessions.Add(session);
        }

        SessionBox.SelectedIndex = Math.Max(0, selected);
    }

    // ------------------------------------------------------------- Prompt

    private void ShufflePrompts() => PromptList.ItemsSource = PromptGallery.Sample(6);

    private void OnShufflePrompts(object? sender, RoutedEventArgs e) => ShufflePrompts();

    private void OnPromptChosen(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PromptTemplate template })
        {
            InputBox.Text = template.Prompt;
            InputBox.Focus();
            InputBox.CaretIndex = template.Prompt.Length;
        }
    }

    // ------------------------------------------------------------- Lain-lain

    private void OnHide(object? sender, RoutedEventArgs e) => IsVisible = false;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        RenderThread();
        UpdateProviderText();
        SetStatus(StatusText.Text ?? string.Empty);
    }

    private async void OnCopyCode(string code)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(code);
        SetStatus("Kode disalin ke papan klip");
    }

    private void OnOpenLink(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Tidak bisa membuka {url}: {ex.Message}", isError: true);
        }
    }

    private void UpdateProviderText()
    {
        if (ProviderText is null)
        {
            return;
        }

        ProviderText.Text = Settings.IsConfigured
            ? $"{Settings.Provider} · {Settings.Model}"
            : $"{Settings.Provider} — kunci API belum diisi";

        ProviderText.Foreground = Settings.IsConfigured
            ? App.Resource("InkMuted")
            : App.Resource("SignalWarning");
    }

    private void SetStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? App.Resource("SignalError") : App.Resource("InkMuted");
    }
}
