using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TypeSafeAppGen.Ai;
using TypeSafeAppGen.Config;
using TypeSafeAppGen.Workspace;

namespace TypeSafeAppGen.Views;

public partial class MainWindow : IJackHost
{
    private const long MaxImageBytes = 5 * 1024 * 1024;
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    private sealed record ModelChoice(LlmProvider Provider, string Model)
    {
        public override string ToString() => $"{LlmFactory.DisplayName(Provider)} · {Model}";
    }

    private JackAgent? _jack;
    private CancellationTokenSource? _jackCts;
    private readonly List<ImageAttachment> _attachments = [];
    private List<ModelChoice> _modelChoices = [];
    private bool _updatingPicker;
    private bool _stickToBottom = true;

    private JackAgent Jack => _jack ??= new JackAgent(this, () => _config);
    private bool JackBusy => _jackCts is not null;
    private CodeActions CodeActions => new(async code => { if (Clipboard is { } c) await c.SetTextAsync(code); }, InsertIntoEditor);

    // ================================================================ chrome

    private void BuildChatChrome()
    {
        ChatActions.Children.Add(Ui.IconButton(Icons.Trash, "Clear chat thread", ClearChat_Click, 15));
        ChatActions.Children.Add(Ui.IconButton(Icons.Close, "Hide Jack (Ctrl+Alt+B)", (_, _) => SetChatVisible(false), 15));
        ComposerTools.Children.Add(Ui.IconButton(Icons.Attach, "Attach images (or drop them on this panel)", AttachImage_Click, 15));
        var hint = new TextBlock { Text = "Ctrl+Enter", FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }.WithClass("muted");
        ComposerTools.Children.Add(hint);
        SetSendButton(busy: false);
    }

    private void WireChatEvents()
    {
        ChatInput.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                await SendAsync();
            }
        }, RoutingStrategies.Tunnel);
        ChatInput.GotFocus += (_, _) => ComposerFrame.BorderBrush = Ui.Brush("Copper");
        ChatInput.LostFocus += (_, _) => ComposerFrame.BorderBrush = Ui.Brush("Rule");

        ModelPicker.SelectionChanged += async (_, _) => await OnModelPickedAsync();

        ChatScroll.ScrollChanged += (_, _) =>
        {
            var distance = ChatScroll.Extent.Height - ChatScroll.Viewport.Height - ChatScroll.Offset.Y;
            _stickToBottom = distance < 60;
        };

        ChatPanel.AddHandler(DragDrop.DragEnterEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(DataFormat.File)) DropHint.IsVisible = true;
        });
        ChatPanel.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropHint.IsVisible = false);
        ChatPanel.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            DropHint.IsVisible = false;
            if (e.DataTransfer.TryGetFiles() is { } files)
                foreach (var file in files)
                    if (file.TryGetLocalPath() is { } path) await AddAttachmentAsync(path);
        });
    }

    private void SetSendButton(bool busy)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(Ui.Icon(busy ? Icons.Stop : Icons.Send, 13));
        content.Children.Add(new TextBlock { Text = busy ? "Stop" : "Send", VerticalAlignment = VerticalAlignment.Center });
        SendButton.Content = content;
        SendButton.Classes.Set("busy", busy);
        ToolTip.SetTip(SendButton, busy ? "Stop Jack" : "Send (Ctrl+Enter)");
    }

    // ================================================================ model picker

    private void RefreshModelPicker()
    {
        _updatingPicker = true;
        _modelChoices = Enum.GetValues<LlmProvider>()
            .SelectMany(p => _config.Profile(p).Models.Select(m => new ModelChoice(p, m)))
            .ToList();
        var items = _modelChoices.Select(c =>
        {
            var profile = _config.Profile(c.Provider);
            var ready = LlmFactory.Validate(c.Provider, new ProviderProfile { Model = c.Model, ApiKey = profile.ApiKey, Endpoint = profile.Endpoint }) is null;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, Fill = ready ? Ui.Brush("Verdigris") : Ui.Brush("Faint"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = LlmFactory.DisplayName(c.Provider), Foreground = Ui.Brush("Muted") });
            row.Children.Add(new TextBlock { Text = c.Model, Foreground = Ui.Brush("Ink") });
            if (!ready) row.Children.Add(new TextBlock { Text = "needs setup", Foreground = Ui.Brush("Faint"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            return (object)new ComboBoxItem { Content = row };
        }).ToList();
        items.Add(new ComboBoxItem { Content = new TextBlock { Text = "Configure models…", Foreground = Ui.Brush("Verdigris") } });
        ModelPicker.ItemsSource = items;
        ModelPicker.SelectedIndex = Math.Max(0, _modelChoices.FindIndex(c => c.Provider == _config.ActiveProvider && c.Model == _config.ActiveProfile.Model));
        ModelStatus.Text = $"{LlmFactory.DisplayName(_config.ActiveProvider)} · {_config.ActiveProfile.Model}";
        _updatingPicker = false;
    }

    private async Task OnModelPickedAsync()
    {
        if (_updatingPicker) return;
        var index = ModelPicker.SelectedIndex;
        if (index == _modelChoices.Count)
        {
            RefreshModelPicker();
            await OpenSettingsAsync("Models");
            return;
        }
        if (index < 0 || index >= _modelChoices.Count) return;
        var choice = _modelChoices[index];
        _config.ActiveProvider = choice.Provider;
        _config.ActiveProfile.Model = choice.Model;
        ModelStatus.Text = choice.ToString();
        await SaveConfigAsync();
        Log($"Jack now uses {choice}.");
    }

    // ================================================================ thread

    private void ShowChatWelcome()
    {
        ChatThread.Children.Clear();
        var intro = new StackPanel { Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        intro.Children.Add(new TextBlock
        {
            Text = "Tell me what to build. I write the files, run the build, and fix what breaks — you watch it happen in the editor.",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });
        intro.Children.Add(new TextBlock { Text = "Attach a screenshot or sketch and I will build the UI from it.", TextWrapping = TextWrapping.Wrap }.WithClass("muted"));
        intro.Children.Add(new Border { Height = 6 });
        intro.Children.Add(Ui.Eyebrow("Try"));
        foreach (var suggestion in new[]
        {
            "Build a pomodoro timer desktop app with a circular progress ring and session history",
            "Create a Blazor Server expense tracker with categories and a monthly chart",
            "Explain the file I have open and suggest improvements",
            "Build the project and fix every error",
        })
        {
            var button = new Button { Content = new TextBlock { Text = suggestion, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 } }.WithClass("row");
            button.BorderBrush = Ui.Brush("Rule");
            button.BorderThickness = new Thickness(1);
            button.Click += (_, _) =>
            {
                ChatInput.Text = suggestion;
                ChatInput.CaretIndex = suggestion.Length;
                ChatInput.Focus();
            };
            intro.Children.Add(button);
        }
        ChatThread.Children.Add(intro);
    }

    private async void ClearChat_Click(object? sender, RoutedEventArgs e)
    {
        if (JackBusy) _jackCts?.Cancel();
        if (Jack.MessageCount > 0 && !await Dialogs.ConfirmAsync(this, "Clear the chat thread?", "Jack forgets this conversation. Files it wrote stay as they are.", "Clear thread"))
            return;
        Jack.Clear();
        _attachments.Clear();
        RenderAttachments();
        ShowChatWelcome();
        Log("Chat thread cleared.");
    }

    private void ScrollChatToEnd(bool force = false)
    {
        if (force || _stickToBottom) Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private async void Send_Click(object? sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (JackBusy)
        {
            _jackCts?.Cancel();
            return;
        }
        var prompt = ChatInput.Text?.Trim() ?? "";
        if (prompt.Length == 0 && _attachments.Count == 0) return;
        if (prompt.Length == 0) prompt = "Build what you see in the attached image.";

        if (Jack.MessageCount == 0) ChatThread.Children.Clear();
        var images = _attachments.ToList();
        ChatThread.Children.Add(new UserMessageView(prompt, images));
        ChatInput.Text = "";
        _attachments.Clear();
        RenderAttachments();

        var reply = new JackMessageView(CodeActions);
        reply.ContentGrew += () => ScrollChatToEnd();
        ChatThread.Children.Add(reply);
        ScrollChatToEnd(force: true);

        if (LlmFactory.Validate(_config.ActiveProvider, _config.ActiveProfile) is { } problem)
        {
            reply.ShowError(problem, () => _ = OpenSettingsAsync("Models"));
            return;
        }

        _jackCts = new CancellationTokenSource();
        SetSendButton(busy: true);
        reply.ShowThinking(true);
        SetStatus($"Jack is working with {ModelStatus.Text}…", StatusTone.Jack);

        var callbacks = new JackTurnCallbacks
        {
            OnText = delta => Dispatcher.UIThread.Post(() => reply.AppendText(delta)),
            OnToolStarted = (name, detail) =>
            {
                var done = new TaskCompletionSource<Action<bool, string>>();
                Dispatcher.UIThread.Post(() =>
                {
                    done.SetResult(reply.AddTool(name, detail));
                    SetStatus($"Jack: {name} {detail}", StatusTone.Jack);
                });
                _pendingTools.Enqueue(done.Task);
            },
            OnToolFinished = (name, ok, summary) =>
            {
                if (!_pendingTools.TryDequeue(out var pending)) return;
                pending.ContinueWith(t => Dispatcher.UIThread.Post(() => t.Result(ok, summary)), TaskScheduler.Default);
                Log($"Jack · {name}: {(ok ? "ok" : "failed")} {summary}");
            },
        };

        try
        {
            await Jack.SendAsync(prompt, images, callbacks, _jackCts.Token);
            SetStatus("Jack finished.", StatusTone.Success);
        }
        catch (OperationCanceledException)
        {
            reply.AppendText("\n\n*Stopped.*");
            SetStatus("Jack stopped.", StatusTone.Idle);
        }
        catch (Exception ex)
        {
            var message = JackAgent.DescribeError(ex, _config);
            var fixable = message.Contains("Settings", StringComparison.Ordinal);
            reply.ShowError(message, fixable ? () => _ = OpenSettingsAsync("Models") : null);
            Log($"Jack error: {ex.GetType().Name}: {ex.Message}");
            SetStatus("Jack could not finish — see the chat panel.", StatusTone.Error);
        }
        finally
        {
            _pendingTools.Clear();
            reply.Flush();
            reply.ShowThinking(false);
            _jackCts.Dispose();
            _jackCts = null;
            SetSendButton(busy: false);
            ScrollChatToEnd();
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<Task<Action<bool, string>>> _pendingTools = new();

    private void FocusJack_Click(object? sender, RoutedEventArgs e) => SetChatVisible(true);

    private async void AskJackFile_Click(object? sender, RoutedEventArgs e)
    {
        if (_activeTab?.FilePath is { } path) await AskJackAboutAsync(path);
        else SetChatVisible(true);
    }

    private async Task AskJackAboutAsync(string path)
    {
        SetChatVisible(true);
        var relative = _workspace is null ? path : _workspace.Relative(path);
        ChatInput.Text = $"Explain what {relative} does, then point out bugs or improvements worth making.";
        await SendAsync();
    }

    // ================================================================ attachments

    private async void AttachImage_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Attach images", AllowMultiple = true, FileTypeFilter = [FilePickerFileTypes.ImageAll] });
        foreach (var file in files)
            if (file.TryGetLocalPath() is { } path) await AddAttachmentAsync(path);
    }

    private async Task AddAttachmentAsync(string path)
    {
        if (!ImageTypes.TryGetValue(Path.GetExtension(path), out var mime))
        {
            SetStatus($"{Path.GetFileName(path)} is not an image (PNG, JPG, GIF, or WebP).", StatusTone.Error);
            return;
        }
        var info = new FileInfo(path);
        if (info.Length > MaxImageBytes)
        {
            SetStatus($"{info.Name} is larger than 5 MB. Attach a smaller image.", StatusTone.Error);
            return;
        }
        _attachments.Add(new ImageAttachment(info.Name, await File.ReadAllBytesAsync(path), mime));
        RenderAttachments();
        ChatInput.Focus();
    }

    private void RenderAttachments()
    {
        AttachmentStrip.Children.Clear();
        AttachmentStrip.IsVisible = _attachments.Count > 0;
        foreach (var attachment in _attachments)
        {
            Bitmap? thumbnail = null;
            try
            {
                using var stream = new MemoryStream(attachment.Data);
                thumbnail = Bitmap.DecodeToWidth(stream, 96);
            }
            catch (Exception) { }
            var remove = Ui.IconButton(Icons.Close, "Remove", (_, _) =>
            {
                _attachments.Remove(attachment);
                RenderAttachments();
            }, 10);
            remove.Padding = new Thickness(3);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (thumbnail is not null) row.Children.Add(new Border { CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = new Image { Source = thumbnail, Width = 28, Height = 28, Stretch = Stretch.UniformToFill } });
            row.Children.Add(new TextBlock { Text = attachment.Name, FontSize = 11.5, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(remove);
            AttachmentStrip.Children.Add(new Border
            {
                Background = Ui.Brush("PatinaRaised"),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(4, 3, 2, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = row,
            });
        }
    }

    private void InsertIntoEditor(string code)
    {
        if (_activeTab is null)
        {
            var tab = TypeSafeAppGen.Editor.EditorTab.CreateUntitled(code);
            AddTab(tab);
            SetStatus("Code opened in a new tab.");
            return;
        }
        Editor.Document.Replace(Editor.SelectionStart, Editor.SelectionLength, code);
        Editor.Focus();
        SetStatus($"Inserted {code.Split('\n').Length} line(s) at the cursor.");
    }

    // ================================================================ IJackHost

    ProjectWorkspace? IJackHost.Workspace => _workspace;

    string IJackHost.ProjectsFolder => _config.ProjectsFolder;

    Task<ActiveEditorSnapshot?> IJackHost.GetActiveEditorAsync() => Dispatcher.UIThread.InvokeAsync<ActiveEditorSnapshot?>(() =>
    {
        if (_activeTab is null) return null;
        var selection = Editor.SelectionLength > 0 ? Editor.SelectedText : null;
        return new ActiveEditorSnapshot(_activeTab.FilePath, _activeTab.Document.Text, Editor.TextArea.Caret.Line, selection);
    }).GetTask();

    Task IJackHost.OpenFileAsync(string fullPath, int? line) => Dispatcher.UIThread.InvokeAsync(() => OpenFileAsync(fullPath, line));

    Task IJackHost.OpenProjectAsync(string folder, string? fileToOpen) => Dispatcher.UIThread.InvokeAsync(() => OpenProjectAsync(folder, fileToOpen));
}
