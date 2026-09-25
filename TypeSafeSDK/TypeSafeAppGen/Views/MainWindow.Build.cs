using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TypeSafeAppGen.Workspace;

namespace TypeSafeAppGen.Views;

public partial class MainWindow
{
    private enum PanelTab { Output, Problems, Logs }

    private const int MaxOutputChars = 600_000;
    private readonly List<BuildDiagnostic> _problems = [];
    private readonly Dictionary<PanelTab, ToggleButton> _panelTabButtons = [];
    private CancellationTokenSource? _processCts;
    private Task? _runningProcess;
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private string _lastPublishFolder = "";

    private void BuildPanelChrome()
    {
        foreach (var tab in Enum.GetValues<PanelTab>())
        {
            var button = new ToggleButton { Content = tab.ToString().ToUpperInvariant(), FontSize = 11, Padding = new Thickness(10, 4), FontFamily = Ui.Font("Mono") }.WithClass("tool");
            button.Click += (_, _) => ShowPanelTab(tab);
            _panelTabButtons[tab] = button;
            PanelTabs.Children.Add(button);
        }
        PanelActions.Children.Add(Ui.IconButton(Icons.Trash, "Clear", (_, _) => ClearActivePanel(), 14));
        PanelActions.Children.Add(Ui.IconButton(Icons.Close, "Hide panel (Ctrl+J)", (_, _) => SetPanelVisible(false), 14));
        ShowPanelTab(PanelTab.Output);
        UpdateProblemStatus();
    }

    private PanelTab _panelTab;

    private void ShowPanelTab(PanelTab tab)
    {
        _panelTab = tab;
        foreach (var (key, button) in _panelTabButtons) button.IsChecked = key == tab;
        OutputView.IsVisible = tab == PanelTab.Output;
        ProblemsList.IsVisible = tab == PanelTab.Problems;
        LogsView.IsVisible = tab == PanelTab.Logs;
        if (!BottomPanel.IsVisible) SetPanelVisible(true);
    }

    private void ClearActivePanel()
    {
        switch (_panelTab)
        {
            case PanelTab.Output: OutputView.Document.Text = ""; break;
            case PanelTab.Problems: ClearProblems(); break;
            case PanelTab.Logs: LogsView.Document.Text = ""; break;
        }
    }

    // ================================================================ output

    private void AppendOutput(string text)
    {
        var document = OutputView.Document;
        if (document.TextLength > MaxOutputChars) document.Remove(0, document.TextLength / 2);
        document.Insert(document.TextLength, text);
        OutputView.ScrollToLine(OutputView.Document.LineCount);
    }

    /// <summary>
    /// Baris output dikumpulkan lalu dikirim ke UI per batch, supaya build ribuan baris tidak
    /// membanjiri UI thread dengan satu dispatch per baris.
    /// </summary>
    private sealed class OutputPump(Action<string> flush)
    {
        private readonly System.Text.StringBuilder _pending = new();
        private readonly object _gate = new();
        private bool _scheduled;

        public void Add(string line)
        {
            lock (_gate)
            {
                _pending.Append(line).Append('\n');
                if (_scheduled) return;
                _scheduled = true;
            }
            DispatcherTimer.RunOnce(Drain, TimeSpan.FromMilliseconds(50), DispatcherPriority.Background);
        }

        public void Drain()
        {
            string text;
            lock (_gate)
            {
                text = _pending.ToString();
                _pending.Clear();
                _scheduled = false;
            }
            if (text.Length > 0) flush(text);
        }
    }

    // ================================================================ problems

    private void ClearProblems()
    {
        _problems.Clear();
        ProblemsList.ItemsSource = null;
        UpdateProblemStatus();
    }

    private void AddProblem(BuildDiagnostic diagnostic)
    {
        if (_problems.Contains(diagnostic)) return;
        _problems.Add(diagnostic);
        RenderProblems();
    }

    private void RenderProblems()
    {
        ProblemsList.ItemsSource = _problems.OrderBy(p => p.Severity).Select(p =>
        {
            var row = new DockPanel { Margin = new Thickness(2, 1) };
            var icon = Ui.Icon(p.Severity == DiagnosticSeverity.Error ? Icons.Alert : Icons.Alert, 13, p.Severity == DiagnosticSeverity.Error ? Ui.Brush("Ember") : Ui.Brush("Brass"), 1.7);
            icon.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var location = new TextBlock { Text = $"{p.Location}  {p.Code}", Foreground = Ui.Brush("Muted"), FontSize = 11.5, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }.WithClass("mono");
            DockPanel.SetDock(location, Dock.Right);
            row.Children.Add(location);
            row.Children.Add(new TextBlock { Text = p.Message, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 });
            var item = new ListBoxItem { Content = row, Tag = p, Padding = new Thickness(8, 3) };
            ToolTip.SetTip(item, $"{p.FilePath}\n{p.Message}\nDouble-click to open.");
            return item;
        }).ToList();
        UpdateProblemStatus();
    }

    private void UpdateProblemStatus()
    {
        var errors = _problems.Count(p => p.Severity == DiagnosticSeverity.Error);
        var warnings = _problems.Count - errors;
        ProblemStatus.Text = _problems.Count == 0 ? "" : $"⊗ {errors}  ⚠ {warnings}";
        ProblemStatus.Foreground = errors > 0 ? Ui.Brush("Ember") : Ui.Brush("Brass");
        if (_panelTabButtons.TryGetValue(PanelTab.Problems, out var button))
            button.Content = _problems.Count == 0 ? "PROBLEMS" : $"PROBLEMS {_problems.Count}";
    }

    // ================================================================ process runner

    /// <summary>
    /// Menjalankan <c>dotnet</c> untuk UI maupun Jack. Hanya satu proses aktif; aplikasi yang sedang
    /// di-Run dihentikan lebih dulu karena exe yang terkunci membuat build gagal.
    /// </summary>
    public async Task<ProcessResult> RunDotnetAsync(string title, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct)
    {
        var isRun = arguments.Count > 0 && arguments[0] == "run";
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            // Run sendiri tercatat di _runningProcess; hanya perintah lain yang perlu menghentikannya dulu.
            if (_runningProcess is not null && !isRun)
            {
                Log("Stopping the running app first.");
                StopProcess();
                try { await _runningProcess; } catch (Exception) { }
            }
            await SaveAllAsync();
        });

        await _processGate.WaitAsync(ct);
        var pump = new OutputPump(AppendOutput);
        try
        {
            var isBuild = arguments.Count > 0 && arguments[0] is "build" or "publish" or "test" or "run";
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (isBuild) ClearProblems();
                AppendOutput($"\n▶ {title}: dotnet {string.Join(' ', arguments.Select(Quote))}\n");
                ShowPanelTab(PanelTab.Output);
                SetStatus($"{title}…", StatusTone.Busy);
                SetProcessRunning(true);
            });
            _processCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var result = await ProcessRunner.DotnetAsync(arguments, workingDirectory, line =>
            {
                pump.Add(line);
                if (BuildDiagnostics.ParseLine(line) is { } diagnostic) Dispatcher.UIThread.Post(() => AddProblem(diagnostic));
            }, _processCts.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                pump.Drain();
                var verdict = result.Cancelled ? "stopped" : result.Succeeded ? "succeeded" : $"failed (exit code {result.ExitCode})";
                AppendOutput($"■ {title} {verdict} in {result.Duration.TotalSeconds:0.0}s\n");
                var errors = _problems.Count(p => p.Severity == DiagnosticSeverity.Error);
                SetStatus(result.Succeeded ? $"{title} succeeded" : result.Cancelled ? $"{title} stopped" : $"{title} failed — {errors} error(s)",
                    result.Succeeded || result.Cancelled ? StatusTone.Success : StatusTone.Error);
                if (!result.Succeeded && errors > 0) ShowPanelTab(PanelTab.Problems);
                Log($"{title} {verdict}.");
            });
            return result;
        }
        finally
        {
            _processCts?.Dispose();
            _processCts = null;
            _processGate.Release();
            await Dispatcher.UIThread.InvokeAsync(() => SetProcessRunning(false));
        }
    }

    private static string Quote(string argument) => argument.Contains(' ') ? $"\"{argument}\"" : argument;

    private void SetProcessRunning(bool running)
    {
        _stopButton.IsVisible = running;
        _runButton.IsVisible = !running;
        _buildButton.IsEnabled = !running;
        _deployButton.IsEnabled = !running;
    }

    private void StopProcess() => _processCts?.Cancel();

    private bool RequireProject(out ProjectWorkspace workspace)
    {
        workspace = _workspace!;
        if (_workspace is not null) return true;
        SetStatus("Open or create a project first.", StatusTone.Error);
        return false;
    }

    private async void Build_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireProject(out var workspace)) return;
        var target = workspace.FindBuildTarget();
        if (target is null) { SetStatus("No .csproj or solution in this folder to build.", StatusTone.Error); return; }
        await RunDotnetAsync("Build", ["build", target, "-nologo"], workspace.Root, CancellationToken.None);
    }

    private async void Run_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireProject(out var workspace) || _runningProcess is not null) return;
        var project = workspace.FindRunnableProject();
        if (project is null) { SetStatus("No runnable project (.csproj with Exe, WinExe, or Web SDK) found.", StatusTone.Error); return; }
        var run = RunDotnetAsync("Run", ["run", "--project", project], Path.GetDirectoryName(project)!, CancellationToken.None);
        _runningProcess = run;
        try { await run; }
        finally { _runningProcess = null; }
    }

    private void Stop_Click(object? sender, RoutedEventArgs e)
    {
        if (_processCts is null) return;
        StopProcess();
        SetStatus("Stopping…", StatusTone.Busy);
    }

    private async void Deploy_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireProject(out var workspace)) return;
        var project = workspace.FindRunnableProject();
        if (project is null) { SetStatus("No runnable project to publish.", StatusTone.Error); return; }
        var options = await DeployWindow.ShowAsync(this, Path.Combine(workspace.Root, "publish"));
        if (options is null) return;

        List<string> args = ["publish", project, "-c", "Release", "-o", options.OutputFolder, "-nologo"];
        if (options.Runtime is { } rid) args.AddRange(["-r", rid, "--self-contained", options.SelfContained ? "true" : "false"]);
        if (options.SingleFile) args.Add("-p:PublishSingleFile=true");
        var result = await RunDotnetAsync("Deploy", args, workspace.Root, CancellationToken.None);
        if (!result.Succeeded) return;
        _lastPublishFolder = options.OutputFolder;
        AppendOutput($"Published to {options.OutputFolder}\n");
        if (options.OpenFolder) OpenFolderInShell(options.OutputFolder);
    }
}

public sealed record DeployOptions(string OutputFolder, string? Runtime, bool SelfContained, bool SingleFile, bool OpenFolder);

/// <summary>Dialog Deploy: target runtime, self-contained, single file, dan folder output untuk <c>dotnet publish</c>.</summary>
public sealed class DeployWindow : Window
{
    private static readonly (string Label, string? Rid)[] Targets =
    [
        ("Framework-dependent (any OS with .NET 10)", null),
        ("Windows x64", "win-x64"),
        ("Windows ARM64", "win-arm64"),
        ("Linux x64", "linux-x64"),
        ("macOS Apple silicon", "osx-arm64"),
        ("macOS Intel", "osx-x64"),
    ];

    private DeployOptions? _result;

    private DeployWindow(string defaultOutput)
    {
        Title = "Deploy";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Brush("PatinaPanel");

        var target = new ComboBox { ItemsSource = Targets.Select(t => t.Label).ToList(), SelectedIndex = OperatingSystem.IsWindows() ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var selfContained = new CheckBox { Content = "Self-contained (bundle the .NET runtime — no install needed on the target)", IsChecked = true };
        var singleFile = new CheckBox { Content = "Single executable file", IsChecked = false };
        var openFolder = new CheckBox { Content = "Open the output folder when done", IsChecked = true };
        var output = new TextBox { Text = defaultOutput }.WithClass("field");
        void Sync()
        {
            var framework = target.SelectedIndex == 0;
            selfContained.IsEnabled = !framework;
            singleFile.IsEnabled = !framework;
        }
        target.SelectionChanged += (_, _) => Sync();
        Sync();

        var publish = new Button { Content = "Publish release", IsDefault = true }.WithClass("primary");
        var cancel = new Button { Content = "Cancel" }.WithClass("ghost");
        publish.Click += (_, _) =>
        {
            var rid = Targets[Math.Max(0, target.SelectedIndex)].Rid;
            _result = new DeployOptions(output.Text?.Trim() is { Length: > 0 } o ? o : defaultOutput, rid, rid is not null && selfContained.IsChecked == true, rid is not null && singleFile.IsChecked == true, openFolder.IsChecked == true);
            Close();
        };
        cancel.Click += (_, _) => Close();

        var body = new StackPanel { Margin = new Thickness(24, 22), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "Deploy a release build", FontSize = 16, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock { Text = "Runs dotnet publish in Release mode and writes a ready-to-ship app to the output folder.", TextWrapping = TextWrapping.Wrap }.WithClass("muted"));
        body.Children.Add(Ui.Eyebrow("Target"));
        body.Children.Add(target);
        body.Children.Add(selfContained);
        body.Children.Add(singleFile);
        body.Children.Add(Ui.Eyebrow("Output folder"));
        body.Children.Add(output);
        body.Children.Add(openFolder);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(cancel);
        actions.Children.Add(publish);
        body.Children.Add(actions);
        Content = body;
    }

    public static async Task<DeployOptions?> ShowAsync(Window owner, string defaultOutput)
    {
        var window = new DeployWindow(defaultOutput);
        await window.ShowDialog(owner);
        return window._result;
    }
}
