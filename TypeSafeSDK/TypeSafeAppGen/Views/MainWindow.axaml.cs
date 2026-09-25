using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Search;
using AvaloniaEdit.TextMate;
using TypeSafeAppGen.Ai;
using TypeSafeAppGen.Config;
using TypeSafeAppGen.Editor;

namespace TypeSafeAppGen.Views;

/// <summary>
/// Jendela utama: menu, toolbar, explorer, editor bertab, panel output/problems/logs, panel Jack, dan status bar.
/// Logika dibagi ke beberapa file partial: Project (explorer, tab, editor), Build (proses dotnet),
/// Jack (chat dan <see cref="IJackHost"/>), dan Start (halaman awal).
/// </summary>
public partial class MainWindow : Window
{
    private AppConfig _config = new();
    private readonly PatinaRegistryOptions _syntax = new();
    private readonly TextMate.Installation _textMate;
    private GridLength _explorerWidth = new(250);
    private GridLength _chatWidth = new(400);
    private GridLength _panelHeight = new(200);
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        _textMate = Editor.InstallTextMate(_syntax);
        SearchPanel.Install(Editor);
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.EnableRectangularSelection = true;
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.Parse("#552A8C78"));
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaretStatus();
        Editor.TextArea.SelectionChanged += (_, _) => UpdateCaretStatus();

        BuildToolbar();
        BuildPanelChrome();
        BuildChatChrome();
        WireEvents();

        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        Opened += async (_, _) => await InitializeAsync();
        Closing += OnClosing;
    }

    private async Task InitializeAsync()
    {

        _config = await ConfigStore.LoadAsync();
        ApplyConfig();
        RefreshModelPicker();
        RefreshRecentMenu();
        ShowChatWelcome();
        Log($"Configuration loaded from {ConfigStore.DefaultPath}");

        var last = _config.RecentProjects.FirstOrDefault(Directory.Exists);
        if (last is not null) await OpenProjectAsync(last);
        else ShowStartPage();
        ChatInput.Focus();
    }

    // ---------------------------------------------------------------- layout & config

    private void ApplyConfig()
    {
        _explorerWidth = new GridLength(_config.ExplorerWidth);
        _chatWidth = new GridLength(_config.ChatPanelWidth);
        _panelHeight = new GridLength(_config.LogsPanelHeight);
        SetExplorerVisible(_config.ExplorerVisible);
        SetChatVisible(_config.ChatPanelVisible);
        SetPanelVisible(_config.LogsPanelVisible);
        ApplyEditorPreferences();
    }

    private void ApplyEditorPreferences()
    {
        Editor.ShowLineNumbers = _config.ShowLineNumbers;
        Editor.WordWrap = _config.WordWrap;
        Editor.FontSize = _config.EditorFontSize;
        LineNumbersMenu.IsChecked = _config.ShowLineNumbers;
        WordWrapMenu.IsChecked = _config.WordWrap;
    }

    /// <summary>Menyimpan lebar/tinggi panel terakhir ke konfigurasi.</summary>
    private void CaptureLayout()
    {
        var columns = WorkspaceGrid.ColumnDefinitions;
        if (ExplorerPanel.IsVisible) _config.ExplorerWidth = Math.Round(columns[0].ActualWidth);
        if (ChatPanel.IsVisible) _config.ChatPanelWidth = Math.Round(columns[4].ActualWidth);
        if (BottomPanel.IsVisible) _config.LogsPanelHeight = Math.Round(CenterGrid.RowDefinitions[3].ActualHeight);
    }

    private async Task SaveConfigAsync()
    {
        CaptureLayout();
        try { await ConfigStore.SaveAsync(_config); }
        catch (IOException ex) { Log($"Could not save settings: {ex.Message}"); }
    }

    private void SetExplorerVisible(bool visible)
    {
        var column = WorkspaceGrid.ColumnDefinitions[0];
        if (!visible && ExplorerPanel.IsVisible && column.ActualWidth > 0) _explorerWidth = new GridLength(column.ActualWidth);
        column.Width = visible ? _explorerWidth : new GridLength(0);
        column.MinWidth = visible ? 160 : 0;
        ExplorerPanel.IsVisible = visible;
        ExplorerSplitter.IsVisible = visible;
        ExplorerMenu.IsChecked = visible;
        _explorerToggle.IsChecked = visible;
        _config.ExplorerVisible = visible;
    }

    private void SetChatVisible(bool visible)
    {
        var column = WorkspaceGrid.ColumnDefinitions[4];
        if (!visible && ChatPanel.IsVisible && column.ActualWidth > 0) _chatWidth = new GridLength(column.ActualWidth);
        column.Width = visible ? _chatWidth : new GridLength(0);
        column.MinWidth = visible ? 300 : 0;
        ChatPanel.IsVisible = visible;
        ChatSplitter.IsVisible = visible;
        ChatMenu.IsChecked = visible;
        _chatToggle.IsChecked = visible;
        _config.ChatPanelVisible = visible;
        if (visible) Dispatcher.UIThread.Post(() => ChatInput.Focus());
    }

    private void SetPanelVisible(bool visible)
    {
        var row = CenterGrid.RowDefinitions[3];
        if (!visible && BottomPanel.IsVisible && row.ActualHeight > 0) _panelHeight = new GridLength(row.ActualHeight);
        row.Height = visible ? _panelHeight : new GridLength(0);
        row.MinHeight = visible ? 90 : 0;
        BottomPanel.IsVisible = visible;
        PanelSplitter.IsVisible = visible;
        PanelMenu.IsChecked = visible;
        _panelToggle.IsChecked = visible;
        _config.LogsPanelVisible = visible;
    }

    // ---------------------------------------------------------------- toolbar

    private Avalonia.Controls.Primitives.ToggleButton _explorerToggle = null!;
    private Avalonia.Controls.Primitives.ToggleButton _panelToggle = null!;
    private Avalonia.Controls.Primitives.ToggleButton _chatToggle = null!;
    private Button _runButton = null!;
    private Button _stopButton = null!;
    private Button _buildButton = null!;
    private Button _deployButton = null!;
    private TextBlock _projectChip = null!;

    private void BuildToolbar()
    {
        Separator Gap() => new() { Width = 1, Height = 18, Margin = new Thickness(6, 0), Background = Ui.Brush("Rule") };
        ToolBar.Children.Add(Ui.ToolButton(Icons.NewProject, "New", "New project (Ctrl+Shift+N)", NewProject_Click));
        ToolBar.Children.Add(Ui.ToolButton(Icons.Folder, "Open", "Open folder (Ctrl+K) — use File › Open File for a single file", OpenFolder_Click));
        ToolBar.Children.Add(Ui.ToolButton(Icons.Save, "Save", "Save (Ctrl+S)", Save_Click));
        ToolBar.Children.Add(Gap());
        ToolBar.Children.Add(Ui.ToolButton(Icons.Format, "Format", "Format code (Shift+Alt+F)", FormatCode_Click));
        ToolBar.Children.Add(Ui.ToolButton(Icons.GoToLine, "Go to line", "Go to line (Ctrl+G)", GoToLine_Click));
        ToolBar.Children.Add(Gap());
        _buildButton = Ui.ToolButton(Icons.Build, "Build", "Build (Ctrl+Shift+B)", Build_Click);
        _runButton = Ui.ToolButton(Icons.Run, "Run", "Run (F5)", Run_Click, "run");
        _stopButton = Ui.ToolButton(Icons.Stop, "Stop", "Stop the running process (Shift+F5)", Stop_Click, "stop");
        _deployButton = Ui.ToolButton(Icons.Deploy, "Deploy", "Publish a release build", Deploy_Click);
        _stopButton.IsVisible = false;
        ToolBar.Children.Add(_buildButton);
        ToolBar.Children.Add(_runButton);
        ToolBar.Children.Add(_stopButton);
        ToolBar.Children.Add(_deployButton);
        ToolBar.Children.Add(Gap());
        _projectChip = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(6, 0), FontSize = 12, Foreground = Ui.Brush("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 };
        ToolBar.Children.Add(_projectChip);

        Avalonia.Controls.Primitives.ToggleButton Toggle(string icon, string tip, Action<bool> set)
        {
            var toggle = new Avalonia.Controls.Primitives.ToggleButton { Content = Ui.Icon(icon, 16) }.WithClass("tool");
            ToolTip.SetTip(toggle, tip);
            toggle.Click += (_, _) => set(toggle.IsChecked == true);
            ToggleBar.Children.Add(toggle);
            return toggle;
        }
        _explorerToggle = Toggle(Icons.SidebarLeft, "Explorer (Ctrl+B)", SetExplorerVisible);
        _panelToggle = Toggle(Icons.PanelBottom, "Output panel (Ctrl+J)", SetPanelVisible);
        _chatToggle = Toggle(Icons.SidebarRight, "Jack panel (Ctrl+Alt+B)", SetChatVisible);
        ToggleBar.Children.Add(Ui.IconButton(Icons.Settings, "Settings (Ctrl+,)", Settings_Click, 16));
    }

    // ---------------------------------------------------------------- keyboard

    /// <summary>Shortcut global. Ditangani di fase tunnel supaya tetap jalan saat fokus ada di editor.</summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        Action? action = (e.Key, ctrl, shift, alt) switch
        {
            (Key.N, true, true, false) => () => NewProject_Click(null, e),
            (Key.N, true, false, false) => () => NewFile_Click(null, e),
            (Key.O, true, false, false) => () => OpenFile_Click(null, e),
            (Key.K, true, false, false) => () => OpenFolder_Click(null, e),
            (Key.S, true, true, false) => () => SaveAll_Click(null, e),
            (Key.S, true, false, false) => () => Save_Click(null, e),
            (Key.W, true, false, false) => () => CloseTab_Click(null, e),
            (Key.G, true, false, false) => () => GoToLine_Click(null, e),
            (Key.F, false, true, true) => () => FormatCode_Click(null, e),
            (Key.B, true, true, false) => () => Build_Click(null, e),
            (Key.B, true, false, true) => () => SetChatVisible(!ChatPanel.IsVisible),
            (Key.B, true, false, false) => () => SetExplorerVisible(!ExplorerPanel.IsVisible),
            (Key.J, true, false, false) => () => SetPanelVisible(!BottomPanel.IsVisible),
            (Key.I, true, false, false) => () => AskJackFile_Click(null, e),
            (Key.L, true, false, false) => () => SetChatVisible(true),
            (Key.Z, false, false, true) => () => WordWrap_Click(null, e),
            (Key.F5, false, true, false) => () => Stop_Click(null, e),
            (Key.F5, false, false, false) => () => Run_Click(null, e),
            (Key.OemComma, true, false, false) => () => Settings_Click(null, e),
            (Key.OemPlus or Key.Add, true, false, false) => () => ZoomIn_Click(null, e),
            (Key.OemMinus or Key.Subtract, true, false, false) => () => ZoomOut_Click(null, e),
            (Key.Tab, true, false, false) => () => CycleTab(1),
            (Key.Tab, true, true, false) => () => CycleTab(-1),
            (Key.Escape, false, false, false) when GoToLinePanel.IsVisible => HideGoToLine,
            _ => null,
        };
        if (action is null) return;
        e.Handled = true;
        action();
    }

    // ---------------------------------------------------------------- status & logs

    private const int MaxLogChars = 400_000;

    public void Log(string message)
    {
        void Write()
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            var document = LogsView.Document;
            if (document.TextLength > MaxLogChars) document.Remove(0, document.TextLength / 2);
            document.Insert(document.TextLength, line);
            LogsView.ScrollToLine(LogsView.Document.LineCount);
            SetStatus(message);
        }
        if (Dispatcher.UIThread.CheckAccess()) Write();
        else Dispatcher.UIThread.Post(Write);
    }

    private enum StatusTone { Idle, Busy, Jack, Success, Error }

    private void SetStatus(string message, StatusTone? tone = null)
    {
        StatusText.Text = message.Split('\n')[0];
        if (tone is not { } t) return;
        StatusDot.Fill = t switch
        {
            StatusTone.Busy => Ui.Brush("Brass"),
            StatusTone.Jack => Ui.Brush("Copper"),
            StatusTone.Error => Ui.Brush("Ember"),
            _ => Ui.Brush("Verdigris"),
        };
    }

    private void UpdateCaretStatus()
    {
        if (_activeTab is null)
        {
            CaretStatus.Text = "";
            LanguageStatus.Text = "";
            return;
        }
        var caret = Editor.TextArea.Caret;
        var selection = Editor.SelectionLength;
        CaretStatus.Text = selection > 0 ? $"Ln {caret.Line}, Col {caret.Column} ({selection} selected)" : $"Ln {caret.Line}, Col {caret.Column}";
        LanguageStatus.Text = Languages.DisplayName(_activeTab.FilePath);
    }

    private void UpdateTitle()
    {
        var parts = new List<string>();
        if (_activeTab is not null) parts.Add((_activeTab.IsDirty ? "● " : "") + _activeTab.Title);
        if (_workspace is not null) parts.Add(_workspace.Name);
        parts.Add("TypeSafe App Generator");
        Title = string.Join(" — ", parts);
        _projectChip.Text = _workspace is null ? "No project" : _workspace.Root;
        ToolTip.SetTip(_projectChip, _workspace?.Root);
    }

    // ---------------------------------------------------------------- window lifetime

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed) return;
        e.Cancel = true;
        if (!await ConfirmDiscardAsync(_tabs)) return;
        _jackCts?.Cancel();
        _processCts?.Cancel();
        _watcher?.Dispose();
        await SaveConfigAsync();
        _closingConfirmed = true;
        Close();
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Settings_Click(object? sender, RoutedEventArgs e) => await OpenSettingsAsync("Models");

    private async Task OpenSettingsAsync(string section)
    {
        CaptureLayout();
        var updated = await SettingsWindow.ShowAsync(this, _config, section);
        if (updated is null) return;
        _config = updated;
        await SaveConfigAsync();
        ApplyEditorPreferences();
        RefreshModelPicker();
        Log("Settings saved.");
    }

    private void Shortcuts_Click(object? sender, RoutedEventArgs e)
    {
        ShowStartPage(forceVisible: true);
        Log("Keyboard shortcuts are listed on the start page.");
    }

    private async void About_Click(object? sender, RoutedEventArgs e) => await OpenSettingsAsync("About");

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => Zoom(1);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => Zoom(-1);

    private void Zoom(int delta)
    {
        _config.EditorFontSize = Math.Clamp(_config.EditorFontSize + delta, 9, 32);
        Editor.FontSize = _config.EditorFontSize;
        SetStatus($"Editor font size {_config.EditorFontSize}");
    }

    private void ToggleExplorer_Click(object? sender, RoutedEventArgs e) => SetExplorerVisible(!ExplorerPanel.IsVisible);
    private void TogglePanel_Click(object? sender, RoutedEventArgs e) => SetPanelVisible(!BottomPanel.IsVisible);
    private void ToggleChat_Click(object? sender, RoutedEventArgs e) => SetChatVisible(!ChatPanel.IsVisible);

    private void LineNumbers_Click(object? sender, RoutedEventArgs e)
    {
        _config.ShowLineNumbers = !_config.ShowLineNumbers;
        ApplyEditorPreferences();
        SetStatus(_config.ShowLineNumbers ? "Line numbers shown" : "Line numbers hidden");
    }

    private void WordWrap_Click(object? sender, RoutedEventArgs e)
    {
        _config.WordWrap = !_config.WordWrap;
        ApplyEditorPreferences();
        SetStatus(_config.WordWrap ? "Word wrap on" : "Word wrap off");
    }
}
