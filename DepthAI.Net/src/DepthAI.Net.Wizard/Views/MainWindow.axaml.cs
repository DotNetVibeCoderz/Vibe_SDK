using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Highlighting;
using DepthAI.Wizard.Ai.Plugins;
using DepthAI.Wizard.Build;
using DepthAI.Wizard.Projects;

namespace DepthAI.Wizard.App.Views;

/// <summary>
/// Jendela utama wizard: penjelajah berkas, editor bertab, panel logs, status bar,
/// dan panel chat.
/// </summary>
public partial class MainWindow : Window, IWorkspaceContext
{
    private readonly ObservableCollection<OpenFile> _openFiles = [];
    private readonly ObservableCollection<LogEntry> _logs = [];
    private readonly DotnetRunner _runner = new();

    private OpenFile? _activeFile;
    private string? _projectDirectory;
    private string? _projectFile;
    private CancellationTokenSource? _operationCts;
    private bool _suppressEditorEvents;
    private bool _logsCollapsed;

    /// <summary>Konstruktor tanpa argumen dibutuhkan pemuat XAML pada masa desain.</summary>
    public MainWindow() : this(null) { }

    /// <param name="startupProjectDirectory">
    /// Proyek yang langsung dibuka saat jendela muncul, atau null untuk mulai kosong.
    /// </param>
    public MainWindow(string? startupProjectDirectory)
    {
        InitializeComponent();

        TabStrip.ItemsSource = _openFiles;
        LogList.ItemsSource = _logs;

        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.Caret.PositionChanged += OnCaretMoved;

        _runner.LogEmitted += (_, line) => Dispatcher.UIThread.Post(() => AppendLog(line));

        Chat.AttachWorkspace(this);

        EditorTheme.Apply(Editor);
        ActualThemeVariantChanged += OnThemeChanged;

        KeyDown += OnWindowKeyDown;
        Closing += OnWindowClosing;

        _ = ProbeDeviceAsync();
        UpdateProjectChrome();

        if (startupProjectDirectory is not null)
        {
            LoadProject(startupProjectDirectory);

            // Berkas masuk dibuka supaya editor tidak menyambut pengguna dengan
            // panel kosong padahal proyeknya sudah termuat.
            var entry = Directory
                .EnumerateFiles(startupProjectDirectory, "Program.cs", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (entry is not null)
            {
                OpenFileInEditor(entry);
            }
        }
    }

    // ------------------------------------------------------ IWorkspaceContext

    /// <inheritdoc />
    public string? ProjectDirectory => _projectDirectory;

    /// <inheritdoc />
    public string? ProjectName => _projectDirectory is null ? null : Path.GetFileName(_projectDirectory);

    /// <inheritdoc />
    public async Task WriteFileAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        if (_projectDirectory is null)
        {
            throw new InvalidOperationException("Tidak ada proyek yang terbuka.");
        }

        var fullPath = Path.Combine(_projectDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);

        // Asisten menulis dari thread latar; sentuhan UI harus kembali ke UI thread.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RefreshFileTree();
            OpenFileInEditor(fullPath);
            SetStatus($"Jack menulis {relativePath}");
        });
    }

    // ------------------------------------------------------------ Proyek

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectDialog();
        var result = await dialog.ShowDialog<ScaffoldResult?>(this);

        if (result is null)
        {
            return;
        }

        LoadProject(result.ProjectDirectory);
        OpenFileInEditor(result.ProjectFile);

        var entry = result.CreatedFiles.FirstOrDefault(f =>
            f.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith("MainWindow.axaml.cs", StringComparison.OrdinalIgnoreCase));

        if (entry is not null)
        {
            OpenFileInEditor(Path.Combine(result.ProjectDirectory, entry));
        }

        AppendLog(new LogLine(DateTimeOffset.Now, LogLevel.Success,
            $"Proyek dibuat: {result.ProjectDirectory} ({result.CreatedFiles.Count} berkas)", "project"));
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Buka folder proyek",
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (folder is not null)
        {
            LoadProject(folder);
        }
    }

    private void OnCloseProject(object? sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        _projectDirectory = null;
        _projectFile = null;
        _openFiles.Clear();
        _activeFile = null;

        FileTree.ItemsSource = null;
        SetEditorText(string.Empty, null);
        UpdateProjectChrome();
        SetStatus("Proyek ditutup");
    }

    private void LoadProject(string directory)
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        _projectDirectory = directory;
        _projectFile = Directory
            .EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        _openFiles.Clear();
        _activeFile = null;
        SetEditorText(string.Empty, null);

        RefreshFileTree();
        UpdateProjectChrome();

        SetStatus(_projectFile is null
            ? "Folder dibuka — tidak ada .csproj, Build dan Run dinonaktifkan"
            : $"Proyek dibuka: {Path.GetFileNameWithoutExtension(_projectFile)}");
    }

    private void RefreshFileTree()
    {
        if (_projectDirectory is null)
        {
            FileTree.ItemsSource = null;
            return;
        }

        var root = FileNode.Build(_projectDirectory);
        FileTree.ItemsSource = new[] { root };
    }

    private void UpdateProjectChrome()
    {
        var hasProject = _projectDirectory is not null;

        NoProjectPanel.IsVisible = !hasProject;
        ProjectNameText.IsVisible = hasProject;
        ProjectNameText.Text = ProjectName;
        ProjectStatus.Text = hasProject ? $"📁 {ProjectName}" : string.Empty;

        EmptyEditorState.IsVisible = _activeFile is null;
        Editor.IsVisible = _activeFile is not null;
    }

    // ------------------------------------------------------------ Editor

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (FileTree.SelectedItem is FileNode { IsDirectory: false } node)
        {
            OpenFileInEditor(node.FullPath);
        }
    }

    private void OpenFileInEditor(string path)
    {
        var existing = _openFiles.FirstOrDefault(f =>
            string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            ActivateFile(existing);
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            AppendLog(new LogLine(DateTimeOffset.Now, LogLevel.Error,
                $"Tidak bisa membuka {Path.GetFileName(path)}: {ex.Message}", "editor"));
            return;
        }

        var file = new OpenFile { FullPath = path, Text = text };
        _openFiles.Add(file);
        ActivateFile(file);
    }

    private void ActivateFile(OpenFile file)
    {
        // Isi editor disimpan kembali ke tab yang ditinggalkan, supaya suntingan
        // yang belum disimpan tidak hilang saat berpindah berkas.
        if (_activeFile is not null)
        {
            _activeFile.Text = Editor.Text;
            _activeFile.IsActive = false;
        }

        file.IsActive = true;
        _activeFile = file;

        SetEditorText(file.Text, file.SyntaxName);
        RefreshTabStrip();
        UpdateProjectChrome();
    }

    private void SetEditorText(string text, string? syntaxName)
    {
        _suppressEditorEvents = true;

        Editor.SyntaxHighlighting = syntaxName is null
            ? null
            : HighlightingManager.Instance.GetDefinition(syntaxName);

        Editor.Text = text;
        _suppressEditorEvents = false;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorEvents || _activeFile is null)
        {
            return;
        }

        _activeFile.Text = Editor.Text;

        if (!_activeFile.IsDirty)
        {
            _activeFile.IsDirty = true;
            RefreshTabStrip();
        }
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        var caret = Editor.TextArea.Caret;
        CaretStatus.Text = $"Brs {caret.Line}, Kol {caret.Column}";
    }

    /// <summary>
    /// Membangun ulang tab strip. ItemsControl tidak mengamati properti tiap item,
    /// jadi koleksinya diganti isi agar templatnya dievaluasi ulang.
    /// </summary>
    private void RefreshTabStrip()
    {
        var snapshot = _openFiles.ToList();
        _openFiles.Clear();

        foreach (var file in snapshot)
        {
            _openFiles.Add(file);
        }
    }

    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: OpenFile file })
        {
            ActivateFile(file);
        }
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OpenFile file })
        {
            return;
        }

        // Menutup tab bukan menutup jendela; suntingan yang belum disimpan cukup
        // diingatkan sekali di sini.
        if (file.IsDirty)
        {
            SetStatus($"{Path.GetFileName(file.FullPath)} punya perubahan yang belum disimpan");
        }

        _openFiles.Remove(file);

        if (ReferenceEquals(file, _activeFile))
        {
            _activeFile = null;
            var next = _openFiles.LastOrDefault();

            if (next is not null)
            {
                ActivateFile(next);
            }
            else
            {
                SetEditorText(string.Empty, null);
                UpdateProjectChrome();
            }
        }

        e.Handled = true;
    }

    private void OnSave(object? sender, RoutedEventArgs e) => SaveActiveFile();

    private void OnSaveAll(object? sender, RoutedEventArgs e)
    {
        if (_activeFile is not null)
        {
            _activeFile.Text = Editor.Text;
        }

        var saved = 0;
        foreach (var file in _openFiles.Where(f => f.IsDirty))
        {
            if (TrySave(file))
            {
                saved++;
            }
        }

        RefreshTabStrip();
        SetStatus(saved == 0 ? "Tidak ada perubahan untuk disimpan" : $"{saved} berkas disimpan");
    }

    private void SaveActiveFile()
    {
        if (_activeFile is null)
        {
            return;
        }

        _activeFile.Text = Editor.Text;

        if (TrySave(_activeFile))
        {
            RefreshTabStrip();
            SetStatus($"{Path.GetFileName(_activeFile.FullPath)} disimpan");
        }
    }

    private bool TrySave(OpenFile file)
    {
        try
        {
            File.WriteAllText(file.FullPath, file.Text);
            file.IsDirty = false;
            return true;
        }
        catch (Exception ex)
        {
            AppendLog(new LogLine(DateTimeOffset.Now, LogLevel.Error,
                $"Tidak bisa menyimpan {Path.GetFileName(file.FullPath)}: {ex.Message}", "editor"));
            return false;
        }
    }

    // ----------------------------------------------------- Perintah edit

    private void OnUndo(object? sender, RoutedEventArgs e) => Editor.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => Editor.Redo();

    private void OnCut(object? sender, RoutedEventArgs e) => Editor.Cut();

    private void OnCopy(object? sender, RoutedEventArgs e) => Editor.Copy();

    private void OnPaste(object? sender, RoutedEventArgs e) => Editor.Paste();

    private void OnToggleLineNumbers(object? sender, RoutedEventArgs e)
    {
        Editor.ShowLineNumbers = !Editor.ShowLineNumbers;
        SetStatus(Editor.ShowLineNumbers ? "Nomor baris ditampilkan" : "Nomor baris disembunyikan");
    }

    private void OnFind(object? sender, RoutedEventArgs e) => ShowFindBar(replace: false);

    private void OnReplace(object? sender, RoutedEventArgs e) => ShowFindBar(replace: true);

    private void ShowFindBar(bool replace)
    {
        FindBar.IsVisible = true;
        ReplaceLabel.IsVisible = replace;
        ReplaceBox.IsVisible = replace;
        ReplaceOneButton.IsVisible = replace;
        ReplaceAllButton.IsVisible = replace;

        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void OnCloseFind(object? sender, RoutedEventArgs e)
    {
        FindBar.IsVisible = false;
        Editor.Focus();
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnCloseFind(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnFindNext(object? sender, RoutedEventArgs e) => FindNext();

    /// <summary>
    /// Mencari kemunculan berikutnya, melanjutkan dari posisi kursor dan membungkus
    /// ke awal berkas bila sudah sampai akhir.
    /// </summary>
    private void FindNext()
    {
        var needle = FindBox.Text;
        if (string.IsNullOrEmpty(needle))
        {
            return;
        }

        var start = Editor.SelectionStart + Math.Max(1, Editor.SelectionLength);
        var index = Editor.Text.IndexOf(needle, Math.Min(start, Editor.Text.Length), StringComparison.OrdinalIgnoreCase);

        if (index < 0)
        {
            index = Editor.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

            if (index < 0)
            {
                SetStatus($"'{needle}' tidak ditemukan");
                return;
            }

            SetStatus("Pencarian dilanjutkan dari awal berkas");
        }

        Editor.Select(index, needle.Length);
        Editor.ScrollTo(Editor.Document.GetLineByOffset(index).LineNumber, 0);
    }

    private void OnReplaceOne(object? sender, RoutedEventArgs e)
    {
        var needle = FindBox.Text;
        if (string.IsNullOrEmpty(needle))
        {
            return;
        }

        if (Editor.SelectedText.Equals(needle, StringComparison.OrdinalIgnoreCase))
        {
            Editor.Document.Replace(Editor.SelectionStart, Editor.SelectionLength, ReplaceBox.Text ?? string.Empty);
        }

        FindNext();
    }

    private void OnReplaceAll(object? sender, RoutedEventArgs e)
    {
        var needle = FindBox.Text;
        if (string.IsNullOrEmpty(needle))
        {
            return;
        }

        var replacement = ReplaceBox.Text ?? string.Empty;
        var original = Editor.Text;
        var updated = original.Replace(needle, replacement, StringComparison.OrdinalIgnoreCase);

        if (original == updated)
        {
            SetStatus($"'{needle}' tidak ditemukan");
            return;
        }

        var count = (original.Length - updated.Length) / Math.Max(1, needle.Length - replacement.Length);
        Editor.Document.Text = updated;
        SetStatus($"{Math.Abs(count)} penggantian dilakukan");
    }

    private async void OnGoToLine(object? sender, RoutedEventArgs e)
    {
        var dialog = new GoToLineDialog(Editor.Document.LineCount);
        var line = await dialog.ShowDialog<int?>(this);

        if (line is null)
        {
            return;
        }

        var target = Math.Clamp(line.Value, 1, Editor.Document.LineCount);
        var documentLine = Editor.Document.GetLineByNumber(target);

        Editor.CaretOffset = documentLine.Offset;
        Editor.ScrollTo(target, 0);
        Editor.Focus();
    }

    // ------------------------------------------------------ Build / Run

    private async void OnBuild(object? sender, RoutedEventArgs e)
    {
        if (!EnsureProjectReady())
        {
            return;
        }

        OnSaveAll(sender, e);
        await RunOperationAsync("Build", token => _runner.BuildAsync(_projectFile!, cancellationToken: token));
    }

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        if (!EnsureProjectReady())
        {
            return;
        }

        OnSaveAll(sender, e);
        await RunOperationAsync("Run", token => _runner.RunAsync(_projectFile!, cancellationToken: token));
    }

    private async void OnDeploy(object? sender, RoutedEventArgs e)
    {
        if (!EnsureProjectReady())
        {
            return;
        }

        var dialog = new DeployDialog(Path.Combine(_projectDirectory!, "publish"));
        var options = await dialog.ShowDialog<DeployOptions?>(this);

        if (options is null)
        {
            return;
        }

        OnSaveAll(sender, e);

        await RunOperationAsync("Deploy", token => _runner.DeployAsync(
            _projectFile!, options.OutputDirectory, options.RuntimeIdentifier, options.SelfContained, token));
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        _runner.Stop();
    }

    private bool EnsureProjectReady()
    {
        if (_projectFile is not null)
        {
            return true;
        }

        SetStatus("Tidak ada berkas .csproj di proyek ini");
        AppendLog(new LogLine(DateTimeOffset.Now, LogLevel.Error,
            "Build butuh berkas .csproj. Buka folder proyek .NET, atau buat proyek baru dari template.",
            "build"));

        return false;
    }

    private async Task RunOperationAsync(string title, Func<CancellationToken, Task<RunResult>> operation)
    {
        if (_runner.IsRunning)
        {
            SetStatus("Operasi lain masih berjalan");
            return;
        }

        _logsCollapsed = false;
        LogsPanel.Height = 200;

        using var cts = new CancellationTokenSource();
        _operationCts = cts;

        StopButton.IsEnabled = true;
        SetStatus($"{title} berjalan…");
        StartRibbon();

        try
        {
            var result = await operation(cts.Token);

            SetStatus(result.Succeeded
                ? $"{title} berhasil dalam {result.Duration.TotalSeconds:F1}s"
                : $"{title} gagal — {result.ErrorCount} error");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{title} dibatalkan");
        }
        finally
        {
            _operationCts = null;
            StopButton.IsEnabled = false;
            StopRibbon();
        }
    }

    // --------------------------------------------------------- Depth ribbon

    /// <summary>
    /// Menjalankan animasi depth ribbon. Ini elemen penanda aplikasi: strip gradien
    /// Turbo — peta warna yang sama dengan yang dipakai SDK untuk mewarnai kedalaman —
    /// menyapu status bar selama pekerjaan berjalan.
    /// </summary>
    private void StartRibbon()
    {
        DepthRibbonBar.Width = 0;

        // Lebarnya digerakkan lewat timer, bukan storyboard, karena progres MSBuild
        // tidak bisa diketahui; yang ditampilkan adalah "sedang bekerja", bukan persentase.
        _ribbonTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        var direction = 1.0;

        _ribbonTimer.Tick += (_, _) =>
        {
            var maximum = Math.Max(1, Bounds.Width);
            var next = DepthRibbonBar.Width + (direction * maximum / 45);

            if (next >= maximum)
            {
                next = maximum;
                direction = -1;
            }
            else if (next <= 0)
            {
                next = 0;
                direction = 1;
            }

            DepthRibbonBar.Width = next;
        };

        _ribbonTimer.Start();
    }

    private void StopRibbon()
    {
        _ribbonTimer?.Stop();
        _ribbonTimer = null;
        DepthRibbonBar.Width = 0;
    }

    private DispatcherTimer? _ribbonTimer;

    // ---------------------------------------------------------------- Logs

    private void AppendLog(LogLine line)
    {
        _logs.Add(LogEntry.From(line));

        // Riwayat log dibatasi: build panjang bisa menghasilkan puluhan ribu baris
        // dan membuat panelnya berat.
        while (_logs.Count > 2000)
        {
            _logs.RemoveAt(0);
        }

        var errors = _logs.Count(l => l.Level == LogLevel.Error);
        var warnings = _logs.Count(l => l.Level == LogLevel.Warning);
        LogSummary.Text = $"{_logs.Count} baris · {errors} error · {warnings} peringatan";

        LogScroller.ScrollToEnd();
    }

    private void OnClearLogs(object? sender, RoutedEventArgs e)
    {
        _logs.Clear();
        LogSummary.Text = string.Empty;
    }

    private void OnToggleLogs(object? sender, RoutedEventArgs e)
    {
        _logsCollapsed = !_logsCollapsed;
        LogsPanel.Height = _logsCollapsed ? 44 : 200;
    }

    // ------------------------------------------------------------- Lain-lain

    private void OnToggleChat(object? sender, RoutedEventArgs e)
    {
        Chat.IsVisible = !Chat.IsVisible;
        ChatSplitter.IsVisible = Chat.IsVisible;
    }

    private void OnToggleTheme(object? sender, RoutedEventArgs e) => App.ToggleTheme();

    /// <summary>
    /// Menyegarkan bagian UI yang warnanya diselesaikan dalam kode.
    /// </summary>
    /// <remarks>
    /// Warna yang dipasang lewat DynamicResource di XAML mengikuti tema dengan sendirinya.
    /// Tab, baris log, dan editor mengambil brush-nya di dalam kode, jadi nilainya beku
    /// pada tema saat elemen itu dibuat — dan tetap begitu sampai dibangun ulang.
    /// </remarks>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        EditorTheme.Apply(Editor);
        RefreshTabStrip();

        var entries = _logs.ToList();
        _logs.Clear();
        foreach (var entry in entries)
        {
            _logs.Add(entry);
        }
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(Chat.Settings);
        var settings = await dialog.ShowDialog<Wizard.Ai.AssistantSettings?>(this);

        if (settings is not null)
        {
            await settings.SaveAsync();
            Chat.ApplySettings(settings);
            SetStatus($"Asisten memakai {settings.Provider} · {settings.Model}");
        }
    }

    private async void OnAbout(object? sender, RoutedEventArgs e)
        => await new AboutDialog().ShowDialog(this);

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Memeriksa perangkat di latar supaya status bar menampilkan keadaan sesungguhnya.</summary>
    private async Task ProbeDeviceAsync()
    {
        var summary = await Task.Run(() =>
        {
            try
            {
                var devices = DepthAi.ListDevices();

                if (devices.Count == 0)
                {
                    return "⚠ tidak ada perangkat";
                }

                return DepthAi.IsNativeAvailable
                    ? $"◉ {devices[0].Name}"
                    : $"◎ {devices[0].Name} (simulasi)";
            }
            catch (Exception ex)
            {
                return $"⚠ {ex.GetType().Name}";
            }
        });

        DeviceStatus.Text = summary;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.S when ctrl && shift:
                OnSaveAll(sender, new RoutedEventArgs());
                break;
            case Key.S when ctrl:
                SaveActiveFile();
                break;
            case Key.F when ctrl:
                ShowFindBar(replace: false);
                break;
            case Key.H when ctrl:
                ShowFindBar(replace: true);
                break;
            case Key.G when ctrl:
                OnGoToLine(sender, new RoutedEventArgs());
                break;
            case Key.L when ctrl:
                OnToggleLineNumbers(sender, new RoutedEventArgs());
                break;
            case Key.J when ctrl:
                OnToggleChat(sender, new RoutedEventArgs());
                break;
            case Key.K when ctrl:
                OnToggleLogs(sender, new RoutedEventArgs());
                break;
            case Key.N when ctrl && shift:
                OnNewProject(sender, new RoutedEventArgs());
                break;
            case Key.O when ctrl:
                OnOpenProject(sender, new RoutedEventArgs());
                break;
            case Key.F5 when shift:
                OnStop(sender, new RoutedEventArgs());
                break;
            case Key.F5:
                OnRun(sender, new RoutedEventArgs());
                break;
            case Key.F6:
                OnBuild(sender, new RoutedEventArgs());
                break;
            case Key.Escape when FindBar.IsVisible:
                OnCloseFind(sender, new RoutedEventArgs());
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Menghentikan proses yang berjalan supaya tidak tertinggal setelah jendela ditutup.</summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _operationCts?.Cancel();
        _runner.Stop();
        StopRibbon();
    }

    /// <summary>
    /// Saat ini perubahan yang belum disimpan hanya dilaporkan di status bar, bukan
    /// diblokir lewat dialog — belum ada alur konfirmasi modal untuk itu.
    /// </summary>
    private bool ConfirmDiscardChanges()
    {
        var dirty = _openFiles.Count(f => f.IsDirty);

        if (dirty > 0)
        {
            SetStatus($"{dirty} berkas punya perubahan yang belum disimpan");
        }

        return true;
    }
}
