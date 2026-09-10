using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using PollenRobotics.Net.Ai;
using PollenRobotics.Net.Ai.Chat;
using PollenRobotics.Net.Core.Diagnostics;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Wizard.Core.Templates;

namespace PollenRobotics.Net.Wizard.Views;

/// <summary>A starter prompt shown on an empty thread.</summary>
/// <param name="Title">Short label.</param>
/// <param name="Preview">The first line of the prompt.</param>
/// <param name="Use">Command that puts it in the composer.</param>
public sealed record PromptCard(string Title, string Preview, ICommand Use);

/// <summary>An attached file waiting to be sent.</summary>
/// <param name="Icon">Glyph for the chip.</param>
/// <param name="Label">File name.</param>
/// <param name="Remove">Command that detaches it.</param>
/// <param name="Attachment">The attachment itself.</param>
public sealed record AttachmentChip(string Icon, string Label, ICommand Remove, ChatAttachment Attachment);

/// <summary>
/// The chat panel: Jack, his sessions, and the composer.
/// </summary>
/// <remarks>
/// <para>
/// Sessions are persisted to disk so a conversation survives a restart, which matters more here
/// than in a web chat - the whole point of the panel is to work alongside a project over hours.
/// </para>
/// <para>
/// Replies stream. A code-generation answer takes tens of seconds to produce, and a panel that
/// shows nothing for that long reads as broken however fast it eventually finishes.
/// </para>
/// </remarks>
public partial class ChatPanel : UserControl
{
    private readonly ObservableCollection<AttachmentChip> _attachments = [];
    private readonly List<ChatAttachment> _pending = [];

    private ChatSessionStore _store = new();
    private JackTheCodeBender? _jack;
    private RobotLogSink? _log;
    private CancellationTokenSource? _generating;
    private bool _suppressSessionChange;

    /// <summary>Raised when the panel's close button is used.</summary>
    public event Action? HideRequested;

    /// <summary>Raised when the settings button is used.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised when a code block's insert button is used.</summary>
    public event Action<string>? CodeInsertRequested;

    /// <summary>The robot the starter prompts are drawn for.</summary>
    public RobotKind Robot { get; set; } = RobotKind.ReachyMini;

    /// <summary>Creates the panel.</summary>
    public ChatPanel()
    {
        InitializeComponent();
        this.FindControl<ItemsControl>("Attachments")!.ItemsSource = _attachments;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Wires the panel to an assistant and a log.</summary>
    public async Task InitialiseAsync(JackTheCodeBender jack, ChatSessionStore store, RobotLogSink log)
    {
        _jack = jack;
        _store = store;
        _log = log;

        await _store.LoadAsync();

        if (_store.Sessions().Count == 0)
        {
            _store.Create();
        }

        RefreshSessions();
        RefreshModelLabel();
        RefreshPrompts();
        RenderThread();

        jack.OptionsChanged += _ => Dispatcher.UIThread.Post(RefreshModelLabel);
    }

    private void RefreshModelLabel()
    {
        TextBlock label = this.FindControl<TextBlock>("ModelLabel")!;

        if (_jack is not { IsReady: true })
        {
            label.Text = "not configured";
            label.Foreground = Resource<IBrush>("SignalBrush");
            return;
        }

        AiOptions options = _jack.Options;
        label.Text = $"{options.Provider.ToString().ToLowerInvariant()} · {options.ResolvedModel}";
        label.Foreground = Resource<IBrush>("TextFaintBrush");
    }

    private void RefreshSessions()
    {
        ComboBox picker = this.FindControl<ComboBox>("SessionPicker")!;
        IReadOnlyList<ChatSession> sessions = _store.Sessions();

        _suppressSessionChange = true;
        picker.ItemsSource = sessions.Select(s => s.Title).ToList();

        int index = _store.Active is { } active ? sessions.ToList().FindIndex(s => s.Id == active.Id) : 0;
        picker.SelectedIndex = Math.Max(0, index);
        _suppressSessionChange = false;
    }

    private void RefreshPrompts()
    {
        var cards = PromptLibrary.Sample(4, Robot)
            .Select(example => new PromptCard(
                example.Title,
                Shorten(example.Prompt),
                new RelayCommand(() =>
                {
                    TextBox composer = this.FindControl<TextBox>("Composer")!;
                    composer.Text = example.Prompt;
                    composer.Focus();
                    composer.CaretIndex = example.Prompt.Length;
                })))
            .ToList();

        this.FindControl<ItemsControl>("PromptCards")!.ItemsSource = cards;
    }

    /// <summary>
    /// Rebuilds the thread from the active session.
    /// </summary>
    /// <remarks>
    /// The whole thread is rebuilt rather than diffed. A conversation is a few dozen messages and
    /// rebuilding is instant; diffing an append-mostly list is the kind of optimisation that buys
    /// nothing and costs a class of bugs where the view and the model drift apart.
    /// </remarks>
    private void RenderThread()
    {
        StackPanel thread = this.FindControl<StackPanel>("Thread")!;
        StackPanel starters = this.FindControl<StackPanel>("StarterPrompts")!;

        // Keep the starter card block; drop everything after it.
        while (thread.Children.Count > 1)
        {
            thread.Children.RemoveAt(1);
        }

        IReadOnlyList<ChatMessage> messages = _store.Active?.Snapshot() ?? [];
        starters.IsVisible = messages.Count == 0;

        foreach (ChatMessage message in messages)
        {
            try
            {
                thread.Children.Add(RenderMessage(message));
            }
            catch (Exception ex)
            {
                // A message that fails to render must not blank the whole conversation. Show what
                // went wrong in its place and carry on with the rest.
                _log?.Error("jack", $"Could not render a {message.Role} message: {ex}");

                thread.Children.Add(new TextBlock
                {
                    Text = $"[this message could not be rendered: {ex.Message}]",
                    Foreground = Resource<IBrush>("SignalBrush"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        Dispatcher.UIThread.Post(
            () => this.FindControl<ScrollViewer>("ThreadScroller")!.ScrollToEnd(),
            DispatcherPriority.Background);
    }

    private Control RenderMessage(ChatMessage message)
    {
        if (message.Role == ChatRole.Note)
        {
            return new Border
            {
                Background = Resource<IBrush>("SignalSoftBrush"),
                BorderBrush = Resource<IBrush>("SignalBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(11, 8),
                Child = new TextBlock
                {
                    Text = message.Content,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Resource<IBrush>("SignalBrush"),
                },
            };
        }

        bool isUser = message.Role == ChatRole.User;

        var header = new TextBlock
        {
            Text = isUser ? "You" : JackTheCodeBender.DisplayName,
            Classes = { "eyebrow" },
            Foreground = isUser ? Resource<IBrush>("TextFaintBrush") : Resource<IBrush>("AccentBrush"),
            Margin = new Thickness(0, 0, 0, 5),
        };

        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(header);

        if (isUser)
        {
            content.Children.Add(new SelectableTextBlock
            {
                Text = message.Content,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                LineHeight = 20,
            });
        }
        else
        {
            var markdown = new MarkdownView { Markdown = message.Content };
            markdown.CodeCopyRequested += CopyToClipboard;
            markdown.CodeInsertRequested += code => CodeInsertRequested?.Invoke(code);
            content.Children.Add(markdown);

            if (message.IsStreaming)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "…",
                    Foreground = Resource<IBrush>("TextFaintBrush"),
                    FontSize = 16,
                });
            }
        }

        foreach (ChatAttachment attachment in message.Attachments)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"{(attachment.Kind == AttachmentKind.Image ? "🖼" : "📎")} {attachment.Describe()}",
                Classes = { "mono", "faint" },
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        return new Border
        {
            Child = content,
            Background = isUser ? Resource<IBrush>("PanelRaisedBrush") : null,
            BorderBrush = isUser ? Resource<IBrush>("LineBrush") : null,
            BorderThickness = new Thickness(isUser ? 1 : 0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(isUser ? 12 : 2, isUser ? 10 : 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private async void OnSendClicked(object? sender, RoutedEventArgs e) => await SendAsync();

    private async void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter sends, Shift+Enter breaks the line. The opposite convention makes a multi-line
        // prompt - which is most of them here - unreasonably awkward to type.
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async Task SendAsync()
    {
        if (_generating is not null)
        {
            _generating.Cancel();
            return;
        }

        TextBox composer = this.FindControl<TextBox>("Composer")!;
        string prompt = composer.Text?.Trim() ?? string.Empty;

        if (prompt.Length == 0)
        {
            return;
        }

        if (_jack is not { IsReady: true } jack)
        {
            _store.EnsureActive().AddNote(
                "Jack is not configured yet. Open the settings and set a provider and API key, "
                + "or put them in appsettings.json.");

            RenderThread();
            return;
        }

        composer.Text = string.Empty;

        ChatAttachment[] attachments = [.. _pending];
        _pending.Clear();
        _attachments.Clear();

        ChatSession session = _store.EnsureActive();

        _generating = new CancellationTokenSource();
        Button send = this.FindControl<Button>("SendButton")!;
        send.Content = "Stop";
        send.Classes.Set("danger", true);
        send.Classes.Set("primary", false);

        void OnChanged(ChatSession _) => Dispatcher.UIThread.Post(RenderThread, DispatcherPriority.Background);
        session.Changed += OnChanged;

        try
        {
            await foreach (string _ in jack.AskAsync(session, prompt, attachments, _generating.Token))
            {
                // The session raises Changed for every chunk; the loop just has to drain.
            }

            await _store.SaveAsync();
        }
        catch (Exception ex)
        {
            _log?.Error("jack", ex.Message);
            session.AddNote($"Jack could not answer: {ex.Message}");
        }
        finally
        {
            session.Changed -= OnChanged;
            _generating?.Dispose();
            _generating = null;

            send.Content = "Send";
            send.Classes.Set("danger", false);
            send.Classes.Set("primary", true);

            RefreshSessions();
            RenderThread();
        }
    }

    private void OnNewSessionClicked(object? sender, RoutedEventArgs e)
    {
        _store.Create();
        RefreshSessions();
        RefreshPrompts();
        RenderThread();
    }

    private async void OnDeleteSessionClicked(object? sender, RoutedEventArgs e)
    {
        if (_store.Active is { } active)
        {
            _store.Delete(active.Id);

            if (_store.Sessions().Count == 0)
            {
                _store.Create();
            }

            await _store.SaveAsync();
            RefreshSessions();
            RenderThread();
        }
    }

    private async void OnResetSessionClicked(object? sender, RoutedEventArgs e)
    {
        _store.Active?.Reset();
        await _store.SaveAsync();
        RefreshSessions();
        RefreshPrompts();
        RenderThread();
    }

    private void OnSessionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSessionChange || sender is not ComboBox { SelectedIndex: >= 0 } picker)
        {
            return;
        }

        IReadOnlyList<ChatSession> sessions = _store.Sessions();

        if (picker.SelectedIndex < sessions.Count)
        {
            _store.Select(sessions[picker.SelectedIndex].Id);
            RenderThread();
        }
    }

    /// <summary>
    /// Attaches an image.
    /// </summary>
    /// <remarks>
    /// Images go to the model as image content, which means the model needs to be able to fetch
    /// them. A local path cannot be fetched, so the chip records the file and the assistant skips
    /// any attachment that is not an http(s) URL rather than pretending it looked at it. Point the
    /// upload settings at a host to make this work end to end.
    /// </remarks>
    private async void OnAttachImageClicked(object? sender, RoutedEventArgs e) =>
        await AttachAsync(AttachmentKind.Image, "Attach an image",
            [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"] }]);

    private async void OnAttachDocumentClicked(object? sender, RoutedEventArgs e) =>
        await AttachAsync(AttachmentKind.Document, "Attach a document",
            [new FilePickerFileType("Documents") { Patterns = ["*.md", "*.txt", "*.cs", "*.json", "*.csv", "*.pdf", "*.log"] }]);

    private async Task AttachAsync(AttachmentKind kind, string title, IReadOnlyList<FilePickerFileType> filters)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [.. filters],
        });

        foreach (IStorageFile file in files)
        {
            string path = file.TryGetLocalPath() ?? file.Path.ToString();
            long? size = null;

            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                // Not a local file. The size is decoration.
            }

            var attachment = new ChatAttachment
            {
                FileName = file.Name,
                Kind = kind,
                Uri = path,
                SizeBytes = size,
            };

            _pending.Add(attachment);

            AttachmentChip chip = null!;
            chip = new AttachmentChip(
                kind == AttachmentKind.Image ? "🖼" : "📎",
                file.Name,
                new RelayCommand(() =>
                {
                    _pending.Remove(attachment);
                    _attachments.Remove(chip);
                }),
                attachment);

            _attachments.Add(chip);
        }
    }

    private void OnHideClicked(object? sender, RoutedEventArgs e) => HideRequested?.Invoke();

    private void OnSettingsClicked(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private async void CopyToClipboard(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        var item = new DataTransferItem();
        item.SetText(text);

        var payload = new DataTransfer();
        payload.Add(item);

        await clipboard.SetDataAsync(payload);
        _log?.Info("jack", "Code copied to the clipboard.");
    }

    private static string Shorten(string prompt)
    {
        string flattened = prompt.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= 96 ? flattened : flattened[..96].TrimEnd() + "…";
    }

    private T? Resource<T>(string key) where T : class =>
        this.TryFindResource(key, out object? value) ? value as T : null;
}
