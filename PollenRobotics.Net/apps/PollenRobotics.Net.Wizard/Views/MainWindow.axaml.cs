using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using Microsoft.Extensions.Configuration;
using PollenRobotics.Net.Ai;
using PollenRobotics.Net.Ai.Chat;
using PollenRobotics.Net.Core.Diagnostics;
using PollenRobotics.Net.Ui.Infrastructure;
using PollenRobotics.Net.Wizard.Core.Build;
using PollenRobotics.Net.Wizard.Core.Projects;
using PollenRobotics.Net.Wizard.Dialogs;

namespace PollenRobotics.Net.Wizard.Views;

/// <summary>One file in the project tree.</summary>
/// <param name="Name">Display name, relative to the project root.</param>
/// <param name="Path">Absolute path.</param>
/// <param name="Icon">A short glyph for the kind of file.</param>
public sealed record FileEntry(string Name, string Path, string Icon);

/// <summary>
/// The Robot Wizard: a code editor for robot applications with Jack alongside it.
/// </summary>
/// <remarks>
/// The editor, the build pipeline and the assistant are three separate things sharing one log. That
/// sharing is the point: a build error, a robot connection failure and Jack's own reasoning all
/// land in the same panel in the order they happened, which is the only way to see that the failed
/// build is the one Jack just caused.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly RobotLogSink _log = new();
    private readonly ObservableCollection<FileEntry> _files = [];
    private readonly ProjectRunner _runner;
    private readonly JackTheCodeBender _jack;
    private readonly ChatSessionStore _sessions = new();

    private WizardProject? _project;
    private string? _openFilePath;
    private bool _dirty;
    private CancellationTokenSource? _running;

    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        InitializeComponent();

        _runner = new ProjectRunner(_log);
        _runner.OutputReceived += line => _log.Info("run", line);
        _runner.Exited += _ => Dispatcher.UIThread.Post(() => SetRunning(false));

        _jack = new JackTheCodeBender(LoadAiOptions());

        this.FindControl<ListBox>("FileList")!.ItemsSource = _files;
        this.FindControl<Ui.Controls.LogPanel>("Log")!.Sink = _log;

        TextEditor editor = this.FindControl<TextEditor>("Editor")!;
        editor.TextChanged += (_, _) => SetDirty(true);
        editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaret();

        ChatPanel chat = this.FindControl<ChatPanel>("Chat")!;
        chat.HideRequested += () => SetChatVisible(false);
        chat.SettingsRequested += async () => await ShowJackSettingsAsync();
        chat.CodeInsertRequested += InsertCode;

        UpdateThemeGlyph();

        Opened += (_, _) => FitToScreen();
        Opened += async (_, _) => await InitialiseAsync();
        Closing += (_, _) => _running?.Cancel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async Task InitialiseAsync()
    {
        _log.Info("wizard", $"PollenRobotics Robot Wizard. {Core.Templates.TemplateCatalog.Count} project templates available.");

        AiOptions options = _jack.Options;

        if (options.IsConfigured)
        {
            try
            {
                _jack.Configure(options);
                _log.Info("jack", $"Jack is using {options.Provider}/{options.ResolvedModel}.");
            }
            catch (Exception ex)
            {
                _log.Error("jack", ex.Message);
            }
        }
        else
        {
            _log.Warn("jack", options.ConfigurationProblem ?? "Jack is not configured.");
        }

        await this.FindControl<ChatPanel>("Chat")!.InitialiseAsync(_jack, _sessions, _log);

        if (Program.StartupProject is { Length: > 0 } path)
        {
            if (await WizardProject.OpenAsync(path) is { } project)
            {
                OpenProject(project);
            }
            else
            {
                _log.Error("wizard", $"No project found at {path}.");
            }
        }
    }

    /// <summary>
    /// Reads Jack's settings from appsettings.json beside the executable.
    /// </summary>
    /// <remarks>
    /// The spec asks for the persona, temperature and model to live in configuration rather than in
    /// code, so an operator can retune the assistant without a rebuild.
    /// </remarks>
    private static AiOptions LoadAiOptions()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        return AiOptions.FromConfiguration(configuration);
    }

    // ---------------------------------------------------------------- project

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectDialog();
        WizardProject? created = await dialog.ShowDialog<WizardProject?>(this);

        if (created is not null)
        {
            OpenProject(created);
            _log.Info("wizard", $"Created {created.Name} from {created.TemplateId ?? "a blank project"}.");
        }
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a project folder",
            AllowMultiple = false,
        });

        if (folders.Count == 0)
        {
            return;
        }

        string path = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
        WizardProject? project = await WizardProject.OpenAsync(path);

        if (project is null)
        {
            _log.Error("wizard", $"No .csproj found in {path}.");
            return;
        }

        OpenProject(project);
    }

    private void OpenProject(WizardProject project)
    {
        _project = project;
        RefreshFiles();

        this.FindControl<TextBlock>("ProjectLabel")!.Text = project.DisplayName;
        this.FindControl<TextBlock>("TargetLabel")!.Text =
            $"{project.Robot} · {project.DefaultRunTarget.ToString().ToLowerInvariant()}";

        this.FindControl<ChatPanel>("Chat")!.Robot = project.Robot;
        Title = $"{project.Name} — PollenRobotics Robot Wizard";

        // Open the entry point rather than whatever sorts first: it is what anyone wants to see.
        FileEntry? program = _files.FirstOrDefault(f => f.Name.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase));

        if (program is not null)
        {
            this.FindControl<ListBox>("FileList")!.SelectedItem = program;
        }

        SetStatus($"Opened {project.Name}");
    }

    private void OnCloseProject(object? sender, RoutedEventArgs e)
    {
        _project = null;
        _openFilePath = null;
        _files.Clear();

        this.FindControl<TextEditor>("Editor")!.Text = string.Empty;
        this.FindControl<TextBlock>("ProjectLabel")!.Text = "no project open";
        this.FindControl<TextBlock>("OpenFileLabel")!.Text = "no file open";
        this.FindControl<TextBlock>("TargetLabel")!.Text = string.Empty;

        Title = "PollenRobotics Robot Wizard";
        SetDirty(false);
        SetStatus("Project closed");
    }

    private void RefreshFiles()
    {
        _files.Clear();

        if (_project is not { } project)
        {
            return;
        }

        foreach (string path in project.SourceFiles())
        {
            string relative = Path.GetRelativePath(project.Directory, path).Replace('\\', '/');
            _files.Add(new FileEntry(relative, path, IconFor(path)));
        }
    }

    private static string IconFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "cs",
        ".csproj" => "proj",
        ".razor" => "raz",
        ".axaml" => "xml",
        ".json" => "{}",
        ".md" => "md",
        _ => "·",
    };

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: FileEntry entry })
        {
            return;
        }

        try
        {
            this.FindControl<TextEditor>("Editor")!.Text = File.ReadAllText(entry.Path);
            _openFilePath = entry.Path;
            this.FindControl<TextBlock>("OpenFileLabel")!.Text = entry.Name;
            SetDirty(false);
        }
        catch (IOException ex)
        {
            _log.Error("wizard", $"Could not open {entry.Name}: {ex.Message}");
        }
    }

    private void OnSaveFile(object? sender, RoutedEventArgs e) => SaveOpenFile();

    private bool SaveOpenFile()
    {
        if (_openFilePath is not { } path)
        {
            return false;
        }

        try
        {
            File.WriteAllText(path, this.FindControl<TextEditor>("Editor")!.Text);
            SetDirty(false);
            SetStatus($"Saved {Path.GetFileName(path)}");
            return true;
        }
        catch (IOException ex)
        {
            _log.Error("wizard", $"Could not save {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    // ---------------------------------------------------------------- editing

    private void OnCut(object? sender, RoutedEventArgs e) => this.FindControl<TextEditor>("Editor")!.Cut();

    private void OnCopy(object? sender, RoutedEventArgs e) => this.FindControl<TextEditor>("Editor")!.Copy();

    private void OnPaste(object? sender, RoutedEventArgs e) => this.FindControl<TextEditor>("Editor")!.Paste();

    private void OnUndo(object? sender, RoutedEventArgs e) => this.FindControl<TextEditor>("Editor")!.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => this.FindControl<TextEditor>("Editor")!.Redo();

    private void OnFind(object? sender, RoutedEventArgs e) =>
        AvaloniaEdit.Search.SearchPanel.Install(this.FindControl<TextEditor>("Editor")!).Open();

    private void OnReplace(object? sender, RoutedEventArgs e)
    {
        // AvaloniaEdit's search panel does replace as well; opening it in replace mode is the same
        // control with one more row.
        AvaloniaEdit.Search.SearchPanel panel =
            AvaloniaEdit.Search.SearchPanel.Install(this.FindControl<TextEditor>("Editor")!);

        panel.IsReplaceMode = true;
        panel.Open();
    }

    private async void OnGoToLine(object? sender, RoutedEventArgs e)
    {
        TextEditor editor = this.FindControl<TextEditor>("Editor")!;

        var dialog = new GoToLineDialog(editor.Document.LineCount);
        int? line = await dialog.ShowDialog<int?>(this);

        if (line is { } target)
        {
            GoToLine(target);
        }
    }

    private void GoToLine(int line)
    {
        TextEditor editor = this.FindControl<TextEditor>("Editor")!;
        int clamped = Math.Clamp(line, 1, editor.Document.LineCount);

        editor.TextArea.Caret.Line = clamped;
        editor.TextArea.Caret.Column = 1;
        editor.ScrollToLine(clamped);
        editor.Focus();
    }

    private void OnToggleLineNumbers(object? sender, RoutedEventArgs e)
    {
        TextEditor editor = this.FindControl<TextEditor>("Editor")!;
        editor.ShowLineNumbers = !editor.ShowLineNumbers;
        SetStatus(editor.ShowLineNumbers ? "Line numbers on" : "Line numbers off");
    }

    private void OnToggleWordWrap(object? sender, RoutedEventArgs e)
    {
        TextEditor editor = this.FindControl<TextEditor>("Editor")!;
        editor.WordWrap = !editor.WordWrap;
        SetStatus(editor.WordWrap ? "Word wrap on" : "Word wrap off");
    }

    /// <summary>Inserts a code block from the chat at the caret.</summary>
    private void InsertCode(string code)
    {
        TextEditor editor = this.FindControl<TextEditor>("Editor")!;
        editor.Document.Insert(editor.CaretOffset, code);
        editor.Focus();
        SetStatus("Inserted code from Jack");
    }

    // ---------------------------------------------------------------- build and run

    private async void OnBuild(object? sender, RoutedEventArgs e) => await BuildAsync();

    private async Task<bool> BuildAsync()
    {
        if (_project is not { } project)
        {
            _log.Warn("build", "Open a project first.");
            return false;
        }

        if (_dirty)
        {
            SaveOpenFile();
        }

        SetStatus("Building…");
        BuildResult result = await _runner.BuildAsync(project);
        SetStatus(result.Summary());

        // Jump to the first error. Reading a build failure means finding the line, and the wizard
        // already knows where it is.
        BuildDiagnostic? first = result.Errors.FirstOrDefault(d => d.File is not null);

        if (first is { File: { } file, Line: > 0 } diagnostic)
        {
            OpenFileAt(file, diagnostic.Line);
        }

        return result.Succeeded;
    }

    private void OpenFileAt(string path, int line)
    {
        FileEntry? entry = _files.FirstOrDefault(f =>
            string.Equals(Path.GetFullPath(f.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

        if (entry is not null)
        {
            this.FindControl<ListBox>("FileList")!.SelectedItem = entry;
        }

        Dispatcher.UIThread.Post(() => GoToLine(line), DispatcherPriority.Background);
    }

    private async void OnRunSimulator(object? sender, RoutedEventArgs e) => await RunAsync(RunTarget.Simulator);

    private async void OnRunRobot(object? sender, RoutedEventArgs e) => await RunAsync(RunTarget.Robot);

    private async Task RunAsync(RunTarget target)
    {
        if (_project is not { } project)
        {
            _log.Warn("run", "Open a project first.");
            return;
        }

        if (!await BuildAsync())
        {
            _log.Error("run", "Not running: the build failed.");
            return;
        }

        SetRunning(true);
        _running = new CancellationTokenSource();

        try
        {
            await _runner.RunAsync(project, target, cancellationToken: _running.Token);
        }
        catch (OperationCanceledException)
        {
            _log.Info("run", "Stopped.");
        }
        finally
        {
            _running?.Dispose();
            _running = null;
            SetRunning(false);
        }
    }

    private void OnStopRun(object? sender, RoutedEventArgs e)
    {
        _runner.Stop();
        _running?.Cancel();
        SetRunning(false);
    }

    private void SetRunning(bool running)
    {
        this.FindControl<Button>("StopButton")!.IsEnabled = running;
        SetStatus(running ? "Running" : "Ready");
    }

    private async void OnDeploy(object? sender, RoutedEventArgs e)
    {
        if (_project is not { } project)
        {
            _log.Warn("deploy", "Open a project first.");
            return;
        }

        var dialog = new DeployDialog(project.Name);
        (string Host, string Directory, string Rid)? target = await dialog.ShowDialog<(string, string, string)?>(this);

        if (target is not { } destination)
        {
            return;
        }

        SetStatus("Deploying…");
        bool ok = await _runner.DeployAsync(project, destination.Host, destination.Directory, destination.Rid);
        SetStatus(ok ? "Deployed" : "Deploy failed - see the log");
    }

    // ---------------------------------------------------------------- panels

    private void OnToggleChat(object? sender, RoutedEventArgs e) =>
        SetChatVisible(!this.FindControl<ChatPanel>("Chat")!.IsVisible);

    private void SetChatVisible(bool visible)
    {
        this.FindControl<ChatPanel>("Chat")!.IsVisible = visible;
        this.FindControl<Border>("ChatSplitter")!.IsVisible = visible;
    }

    private void OnToggleLog(object? sender, RoutedEventArgs e)
    {
        Ui.Controls.LogPanel log = this.FindControl<Ui.Controls.LogPanel>("Log")!;
        log.IsVisible = !log.IsVisible;
    }

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeGlyph();
    }

    private void UpdateThemeGlyph()
    {
        Button button = this.FindControl<Button>("ThemeButton")!;
        button.Content = ThemeManager.Glyph(ThemeManager.Current);
        ToolTip.SetTip(button, ThemeManager.Describe(ThemeManager.Current));
    }

    private async void OnJackSettings(object? sender, RoutedEventArgs e) => await ShowJackSettingsAsync();

    private async Task ShowJackSettingsAsync()
    {
        var dialog = new JackSettingsDialog(_jack.Options);
        AiOptions? updated = await dialog.ShowDialog<AiOptions?>(this);

        if (updated is null)
        {
            return;
        }

        try
        {
            _jack.Configure(updated);
            _log.Info("jack", $"Jack is now using {updated.Provider}/{updated.ResolvedModel}.");
        }
        catch (Exception ex)
        {
            _log.Error("jack", ex.Message);
        }
    }

    private async void OnAbout(object? sender, RoutedEventArgs e) => await new AboutDialog().ShowDialog(this);

    // ---------------------------------------------------------------- status

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        this.FindControl<TextBlock>("DirtyLabel")!.Text = dirty ? "unsaved" : string.Empty;
    }

    private void UpdateCaret()
    {
        TextEditor editor = this.FindControl<TextEditor>("Editor")!;
        this.FindControl<TextBlock>("CaretLabel")!.Text =
            $"Ln {editor.TextArea.Caret.Line}, Col {editor.TextArea.Caret.Column}";
    }

    private void SetStatus(string text) =>
        Dispatcher.UIThread.Post(() => this.FindControl<TextBlock>("StatusText")!.Text = text);

    /// <summary>
    /// Shrinks the window to fit the screen it opens on.
    /// </summary>
    /// <remarks>
    /// The designed size assumes a desktop monitor. On a laptop the bottom of the window - the
    /// status bar, and in the wizard the entire chat composer - falls off the screen, and a window
    /// that large cannot be dragged back into view.
    /// </remarks>
    private void FitToScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        double scale = screen.Scaling;
        double availableWidth = screen.WorkingArea.Width / scale;
        double availableHeight = screen.WorkingArea.Height / scale;

        Width = Math.Min(Width, availableWidth - 40);
        Height = Math.Min(Height, availableHeight - 40);
    }

}
