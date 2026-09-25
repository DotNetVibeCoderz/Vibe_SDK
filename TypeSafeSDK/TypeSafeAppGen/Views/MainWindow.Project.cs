using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TypeSafeAppGen.Editor;
using TypeSafeAppGen.Workspace;

namespace TypeSafeAppGen.Views;

public partial class MainWindow
{
    private ProjectWorkspace? _workspace;
    private readonly List<EditorTab> _tabs = [];
    private EditorTab? _activeTab;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _refreshDebounce;
    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);

    // ================================================================ project

    private async void NewProject_Click(object? sender, RoutedEventArgs e) => await NewProjectAsync(startWithTemplates: false);
    private async void Templates_Click(object? sender, RoutedEventArgs e) => await NewProjectAsync(startWithTemplates: true);

    private async Task NewProjectAsync(bool startWithTemplates)
    {
        var request = await NewProjectWindow.ShowAsync(this, _config.ProjectsFolder, startWithTemplates);
        if (request is null) return;
        try
        {
            Directory.CreateDirectory(request.ParentFolder);
            var mainFile = ProjectTemplates.Scaffold(request.TemplateId, request.Name, request.ParentFolder);
            Log($"Created project '{request.Name}' from template '{request.TemplateId}'.");
            _config.ProjectsFolder = request.ParentFolder;
            await OpenProjectAsync(Path.Combine(request.ParentFolder, request.Name), mainFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log($"Could not create the project: {ex.Message}");
            SetStatus($"Could not create the project: {ex.Message}", StatusTone.Error);
        }
    }

    private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open project folder", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) await OpenProjectAsync(path);
    }

    private async void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open file", AllowMultiple = true });
        foreach (var file in files)
            if (file.TryGetLocalPath() is { } path) await OpenFileAsync(path);
    }

    public async Task OpenProjectAsync(string folder, string? fileToOpen = null)
    {
        if (!await CloseProjectAsync(showStart: false)) return;
        try
        {
            _workspace = new ProjectWorkspace(folder);
        }
        catch (DirectoryNotFoundException ex)
        {
            Log(ex.Message);
            ShowStartPage();
            return;
        }
        _workspace.FileChanged += path => Dispatcher.UIThread.Post(() => OnWorkspaceFileChanged(path));
        _config.RememberProject(_workspace.Root);
        await SaveConfigAsync();
        RefreshRecentMenu();
        StartWatcher();
        RefreshExplorer();
        UpdateTitle();
        Log($"Opened project {_workspace.Root}");

        var target = fileToOpen ?? DefaultFileToOpen(_workspace);
        if (target is not null) await OpenFileAsync(target);
        else ShowStartPage();
    }

    /// <summary>File yang paling mungkin ingin dilihat: MainWindow, Program, atau file .cs pertama.</summary>
    private static string? DefaultFileToOpen(ProjectWorkspace workspace)
    {
        var files = workspace.EnumerateFiles().Take(500).ToList();
        return files.FirstOrDefault(f => Path.GetFileName(f).Equals("MainWindow.cs", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => Path.GetFileName(f).Equals("Program.cs", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
    }

    private async void CloseProject_Click(object? sender, RoutedEventArgs e) => await CloseProjectAsync(showStart: true);

    private async Task<bool> CloseProjectAsync(bool showStart)
    {
        if (!await ConfirmDiscardAsync(_tabs)) return false;
        StopProcess();
        _watcher?.Dispose();
        _watcher = null;
        _tabs.Clear();
        _activeTab = null;
        var wasOpen = _workspace is not null;
        _workspace = null;
        _expandedFolders.Clear();
        Explorer.ItemsSource = null;
        RefreshExplorer();
        RenderTabs();
        ClearProblems();
        UpdateTitle();
        UpdateCaretStatus();
        if (wasOpen) Log("Project closed.");
        if (showStart) ShowStartPage();
        return true;
    }

    private void RefreshRecentMenu()
    {
        var items = _config.RecentProjects.Where(Directory.Exists).Select(path =>
        {
            var item = new MenuItem { Header = path };
            item.Click += async (_, _) => await OpenProjectAsync(path);
            return item;
        }).ToList();
        RecentMenu.ItemsSource = items.Count > 0 ? items : [new MenuItem { Header = "No recent projects", IsEnabled = false }];
    }

    // ================================================================ explorer

    private void WireEvents()
    {
        ExplorerActions.Children.Add(Ui.IconButton(Icons.NewFile, "New file", NewFile_Click, 14));
        ExplorerActions.Children.Add(Ui.IconButton(Icons.NewProject, "New folder", NewFolder_Click, 14));
        ExplorerActions.Children.Add(Ui.IconButton(Icons.Refresh, "Refresh", (_, _) => RefreshExplorer(), 14));
        ExplorerActions.Children.Add(Ui.IconButton(Icons.Collapse, "Collapse all", (_, _) => { _expandedFolders.Clear(); RefreshExplorer(); }, 14));

        Explorer.DoubleTapped += async (_, _) =>
        {
            if (Explorer.SelectedItem is TreeViewItem { Tag: FileNode { IsDirectory: false } node }) await OpenFileAsync(node.Path);
        };
        Explorer.KeyDown += async (_, e) =>
        {
            if (Explorer.SelectedItem is not TreeViewItem { Tag: FileNode node } item) return;
            if (e.Key == Key.Enter && !node.IsDirectory) await OpenFileAsync(node.Path);
            else if (e.Key == Key.Enter) item.IsExpanded = !item.IsExpanded;
            else if (e.Key == Key.Delete) await DeleteNodeAsync(node);
            else if (e.Key == Key.F2) await RenameNodeAsync(node);
            else return;
            e.Handled = true;
        };
        // Klik tunggal pada file langsung membuka tab, seperti preview di VS Code.
        Explorer.SelectionChanged += async (_, _) =>
        {
            if (Explorer.SelectedItem is TreeViewItem { Tag: FileNode { IsDirectory: false } node } && node.Path != _activeTab?.FilePath)
                await OpenFileAsync(node.Path);
        };

        GoToLineInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitGoToLine(); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideGoToLine(); e.Handled = true; }
        };
        GoToLineInput.LostFocus += (_, _) => HideGoToLine();

        ProblemStatus.PointerPressed += (_, _) => ShowPanelTab(PanelTab.Problems);
        ProblemsList.DoubleTapped += async (_, _) =>
        {
            if (ProblemsList.SelectedItem is ListBoxItem { Tag: BuildDiagnostic { FilePath: { } file } diagnostic } && File.Exists(file))
                await OpenFileAsync(file, diagnostic.Line, diagnostic.Column);
        };

        WireChatEvents();
    }

    private sealed record FileNode(string Path, bool IsDirectory);

    private void RefreshExplorer()
    {
        ExplorerEmpty.IsVisible = _workspace is null;
        Explorer.IsVisible = _workspace is not null;
        if (_workspace is null)
        {
            ExplorerTitle.Text = "EXPLORER";
            return;
        }
        ExplorerTitle.Text = _workspace.Name.ToUpperInvariant();
        ToolTip.SetTip(ExplorerTitle, _workspace.Root);
        var selected = (Explorer.SelectedItem as TreeViewItem)?.Tag as FileNode;
        Explorer.ItemsSource = BuildChildren(_workspace.Root);
        if (selected is not null) SelectInExplorer(selected.Path);
    }

    private List<TreeViewItem> BuildChildren(string folder)
    {
        var items = new List<TreeViewItem>();
        IEnumerable<string> dirs, files;
        try
        {
            dirs = Directory.GetDirectories(folder).Where(d => !ProjectWorkspace.IgnoredFolders.Contains(Path.GetFileName(d))).Order(StringComparer.OrdinalIgnoreCase);
            files = Directory.GetFiles(folder).Order(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return items;
        }
        foreach (var dir in dirs) items.Add(CreateNode(new FileNode(dir, true)));
        foreach (var file in files) items.Add(CreateNode(new FileNode(file, false)));
        return items;
    }

    private TreeViewItem CreateNode(FileNode node)
    {
        var name = Path.GetFileName(node.Path);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        header.Children.Add(Ui.Icon(node.IsDirectory ? Icons.Folder : Icons.File, 13, node.IsDirectory ? Ui.Brush("Brass") : FileTint(name), 1.6));
        header.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        var item = new TreeViewItem { Header = header, Tag = node, ContextMenu = BuildContextMenu(node) };
        if (!node.IsDirectory) return item;

        // Folder dimuat malas: isinya dibaca saat pertama kali dibuka, supaya proyek besar tetap cepat.
        if (_expandedFolders.Contains(node.Path))
        {
            item.ItemsSource = BuildChildren(node.Path);
            item.IsExpanded = true;
        }
        else if (Directory.EnumerateFileSystemEntries(node.Path).Any())
        {
            item.ItemsSource = new[] { new TreeViewItem { Header = "…" } };
        }
        item.PropertyChanged += (_, e) =>
        {
            if (e.Property != TreeViewItem.IsExpandedProperty) return;
            if (item.IsExpanded)
            {
                _expandedFolders.Add(node.Path);
                item.ItemsSource = BuildChildren(node.Path);
            }
            else _expandedFolders.Remove(node.Path);
        };
        return item;
    }

    private static IBrush FileTint(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".cs" => Ui.Brush("Verdigris"),
        ".axaml" or ".xaml" or ".razor" or ".html" or ".css" => Ui.Brush("Copper"),
        ".csproj" or ".slnx" or ".sln" or ".json" => Ui.Brush("Brass"),
        _ => Ui.Brush("Muted"),
    };

    private ContextMenu BuildContextMenu(FileNode node)
    {
        var folder = node.IsDirectory ? node.Path : Path.GetDirectoryName(node.Path)!;
        MenuItem Item(string header, Func<Task> action)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await action();
            return item;
        }
        var items = new List<Control>();
        if (!node.IsDirectory) items.Add(Item("Open", () => OpenFileAsync(node.Path)));
        items.Add(Item("New File…", () => CreateFileInAsync(folder)));
        items.Add(Item("New Folder…", () => CreateFolderInAsync(folder)));
        items.Add(new Separator());
        items.Add(Item("Rename…", () => RenameNodeAsync(node)));
        items.Add(Item("Delete", () => DeleteNodeAsync(node)));
        items.Add(new Separator());
        items.Add(Item("Copy Path", async () => { if (Clipboard is { } c) await c.SetTextAsync(node.Path); }));
        items.Add(Item("Reveal in File Explorer", () => { Reveal(node.Path); return Task.CompletedTask; }));
        if (!node.IsDirectory) items.Add(Item("Ask Jack About This File", () => AskJackAboutAsync(node.Path)));
        return new ContextMenu { ItemsSource = items };
    }

    private void SelectInExplorer(string path)
    {
        if (Explorer.ItemsSource is not IEnumerable<TreeViewItem> roots) return;
        var match = Find(roots);
        if (match is not null) Explorer.SelectedItem = match;

        TreeViewItem? Find(IEnumerable<TreeViewItem> items)
        {
            foreach (var item in items)
            {
                if (item.Tag is FileNode node && node.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return item;
                if (item.Tag is FileNode { IsDirectory: true } dir && item.IsExpanded && item.ItemsSource is IEnumerable<TreeViewItem> children
                    && path.StartsWith(dir.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Find(children) is { } found) return found;
            }
            return null;
        }
    }

    /// <summary>Buka semua folder induk dari sebuah file agar terlihat di explorer.</summary>
    private void RevealInExplorer(string path)
    {
        if (_workspace is null || !path.StartsWith(_workspace.Root, StringComparison.OrdinalIgnoreCase)) return;
        var dir = Path.GetDirectoryName(path);
        var changed = false;
        while (dir is not null && dir.Length > _workspace.Root.Length)
        {
            changed |= _expandedFolders.Add(dir);
            dir = Path.GetDirectoryName(dir);
        }
        if (changed) RefreshExplorer();
        SelectInExplorer(path);
    }

    private static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", path]);
            else Process.Start("xdg-open", Path.GetDirectoryName(path) ?? path);
        }
        catch (Exception) { }
    }

    private static void OpenFolderInShell(string folder)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"\"{folder}\"");
            else Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", folder);
        }
        catch (Exception) { }
    }

    private void StartWatcher()
    {
        if (_workspace is null) return;
        _watcher = new FileSystemWatcher(_workspace.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
        void Changed(object _, FileSystemEventArgs e)
        {
            if (ProjectWorkspace.IsIgnored(e.FullPath, _workspace.Root)) return;
            Dispatcher.UIThread.Post(ScheduleRefresh);
        }
        _watcher.Created += Changed;
        _watcher.Deleted += Changed;
        _watcher.Renamed += (s, e) => Changed(s!, e);
        _watcher.Changed += Changed;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Banyak event file berurutan (mis. saat Jack menulis 10 file) digabung jadi satu refresh.</summary>
    private void ScheduleRefresh()
    {
        _refreshDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, async (_, _) =>
        {
            _refreshDebounce!.Stop();
            RefreshExplorer();
            foreach (var tab in _tabs.ToList())
            {
                if (tab.FilePath is not null && !File.Exists(tab.FilePath)) continue;
                if (await tab.ReloadIfChangedAsync()) Log($"Reloaded {tab.Title} (changed on disk).");
            }
        });
        _refreshDebounce.Stop();
        _refreshDebounce.Start();
    }

    private void OnWorkspaceFileChanged(string path) => ScheduleRefresh();

    private async void NewFile_Click(object? sender, RoutedEventArgs e)
    {
        if (_workspace is null)
        {
            var tab = EditorTab.CreateUntitled();
            AddTab(tab);
            return;
        }
        var folder = (Explorer.SelectedItem as TreeViewItem)?.Tag is FileNode node
            ? node.IsDirectory ? node.Path : Path.GetDirectoryName(node.Path)!
            : _workspace.Root;
        await CreateFileInAsync(folder);
    }

    private async void NewFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_workspace is null) return;
        var folder = (Explorer.SelectedItem as TreeViewItem)?.Tag is FileNode { IsDirectory: true } node ? node.Path : _workspace.Root;
        await CreateFolderInAsync(folder);
    }

    private static string? ValidateFileName(string name) =>
        name.IndexOfAny(Path.GetInvalidFileNameChars().Where(c => c != '/' && c != '\\').ToArray()) >= 0 ? "That name contains characters that are not allowed." : null;

    private async Task CreateFileInAsync(string folder)
    {
        var name = await Dialogs.PromptAsync(this, "New file", "File name (you can include sub-folders)", "NewFile.cs", "Create", ValidateFileName);
        if (name is null || _workspace is null) return;
        try
        {
            var path = _workspace.Resolve(Path.Combine(folder, name));
            if (File.Exists(path)) { await OpenFileAsync(path); return; }
            await _workspace.WriteAsync(path, "");
            _expandedFolders.Add(folder);
            RefreshExplorer();
            await OpenFileAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Could not create the file: {ex.Message}");
        }
    }

    private async Task CreateFolderInAsync(string folder)
    {
        var name = await Dialogs.PromptAsync(this, "New folder", "Folder name", "NewFolder", "Create", ValidateFileName);
        if (name is null || _workspace is null) return;
        try
        {
            Directory.CreateDirectory(_workspace.Resolve(Path.Combine(folder, name)));
            _expandedFolders.Add(folder);
            RefreshExplorer();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Could not create the folder: {ex.Message}");
        }
    }

    private async Task RenameNodeAsync(FileNode node)
    {
        var oldName = Path.GetFileName(node.Path);
        var name = await Dialogs.PromptAsync(this, $"Rename {oldName}", "New name", oldName, "Rename", ValidateFileName);
        if (name is null || name == oldName) return;
        var target = Path.Combine(Path.GetDirectoryName(node.Path)!, name);
        try
        {
            if (node.IsDirectory) Directory.Move(node.Path, target);
            else File.Move(node.Path, target);
            foreach (var tab in _tabs.Where(t => t.FilePath is not null && (t.FilePath.Equals(node.Path, StringComparison.OrdinalIgnoreCase)
                || t.FilePath.StartsWith(node.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                tab.Rename(target + tab.FilePath![node.Path.Length..]);
            RenderTabs();
            RefreshExplorer();
            Log($"Renamed {oldName} to {name}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Could not rename: {ex.Message}");
        }
    }

    private async Task DeleteNodeAsync(FileNode node)
    {
        var name = Path.GetFileName(node.Path);
        var message = node.IsDirectory ? $"The folder '{name}' and everything in it will be deleted." : $"'{name}' will be deleted.";
        if (!await Dialogs.ConfirmAsync(this, $"Delete {name}?", message + " This cannot be undone.", "Delete")) return;
        try
        {
            if (node.IsDirectory) Directory.Delete(node.Path, recursive: true);
            else File.Delete(node.Path);
            foreach (var tab in _tabs.Where(t => t.FilePath is not null && t.FilePath.StartsWith(node.Path, StringComparison.OrdinalIgnoreCase)).ToList())
                RemoveTab(tab);
            RefreshExplorer();
            Log($"Deleted {name}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Could not delete: {ex.Message}");
        }
    }

    // ================================================================ tabs

    public async Task OpenFileAsync(string path, int? line = null, int column = 1)
    {
        path = Path.GetFullPath(path);
        var tab = _tabs.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (tab is null)
        {
            if (!File.Exists(path)) { Log($"File not found: {path}"); return; }
            if (new FileInfo(path).Length > 20_000_000) { Log($"{Path.GetFileName(path)} is larger than 20 MB and was not opened."); return; }
            try
            {
                tab = await EditorTab.OpenAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"Could not open {path}: {ex.Message}");
                return;
            }
            AddTab(tab);
        }
        else
        {
            await tab.ReloadIfChangedAsync();
            ActivateTab(tab);
        }
        RevealInExplorer(path);
        if (line is { } l) GoTo(l, column);
    }

    private void AddTab(EditorTab tab)
    {
        tab.DirtyChanged += _ => Dispatcher.UIThread.Post(() => { RenderTabs(); UpdateTitle(); });
        _tabs.Add(tab);
        ActivateTab(tab);
    }

    private void ActivateTab(EditorTab tab)
    {
        if (_activeTab is not null && _activeTab != tab)
        {
            _activeTab.CaretOffset = Editor.CaretOffset;
            _activeTab.VerticalOffset = Editor.VerticalOffset;
            if (_config.AutoSave && _activeTab.IsDirty && _activeTab.FilePath is not null) _ = _activeTab.SaveAsync();
        }
        _activeTab = tab;
        Editor.Document = tab.Document;
        var scope = _syntax.ScopeFor(tab.FilePath);
        if (scope is not null) _textMate.SetGrammar(scope);
        else _textMate.SetGrammar(null);
        Editor.CaretOffset = Math.Min(tab.CaretOffset, tab.Document.TextLength);
        Dispatcher.UIThread.Post(() => Editor.ScrollToVerticalOffset(tab.VerticalOffset), DispatcherPriority.Background);
        StartHost.IsVisible = false;
        Editor.IsVisible = true;
        RenderTabs();
        UpdateTitle();
        UpdateCaretStatus();
        Editor.Focus();
    }

    private void RenderTabs()
    {
        TabStrip.Children.Clear();
        foreach (var tab in _tabs)
        {
            var active = tab == _activeTab;
            var close = new Button { Content = Ui.Icon(tab.IsDirty ? "M12 12m-4 0a4 4 0 1 0 8 0a4 4 0 1 0 -8 0" : Icons.Close, 10), Padding = new Thickness(3), Margin = new Thickness(6, 0, 0, 0) }.WithClass("icon");
            ToolTip.SetTip(close, "Close (Ctrl+W)");
            close.Click += async (_, _) => await CloseTabAsync(tab);
            var label = new TextBlock { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Foreground = active ? Ui.Brush("Ink") : Ui.Brush("Muted") };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Ui.Icon(Icons.File, 12, FileTint(tab.Title), 1.6));
            content.Children.Add(new Border { Width = 7 });
            content.Children.Add(label);
            content.Children.Add(close);
            var chip = new Border
            {
                Child = content,
                Padding = new Thickness(12, 0, 6, 0),
                Height = 34,
                Background = active ? Ui.Brush("PatinaBase") : Brushes.Transparent,
                BorderBrush = active ? Ui.Brush("Verdigris") : Ui.Brush("Rule"),
                BorderThickness = active ? new Thickness(0, 2, 1, 0) : new Thickness(0, 0, 1, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(chip, tab.FilePath ?? "Unsaved file");
            chip.PointerPressed += async (_, e) =>
            {
                var point = e.GetCurrentPoint(chip);
                if (point.Properties.IsMiddleButtonPressed) await CloseTabAsync(tab);
                else if (tab != _activeTab) ActivateTab(tab);
            };
            TabStrip.Children.Add(chip);
        }
    }

    private void CycleTab(int direction)
    {
        if (_tabs.Count < 2 || _activeTab is null) return;
        var index = (_tabs.IndexOf(_activeTab) + direction + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[index]);
    }

    private async void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is not null) await CloseTabAsync(_activeTab);
    }

    private async Task CloseTabAsync(EditorTab tab)
    {
        if (!await ConfirmDiscardAsync([tab])) return;
        RemoveTab(tab);
    }

    private void RemoveTab(EditorTab tab)
    {
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        if (_activeTab == tab)
        {
            _activeTab = null;
            if (_tabs.Count > 0) ActivateTab(_tabs[Math.Clamp(index, 0, _tabs.Count - 1)]);
            else ShowStartPage();
        }
        RenderTabs();
        UpdateTitle();
    }

    /// <summary>Tanya simpan untuk tab yang berubah. False berarti pengguna membatalkan.</summary>
    private async Task<bool> ConfirmDiscardAsync(IReadOnlyCollection<EditorTab> tabs)
    {
        var dirty = tabs.Where(t => t.IsDirty).ToList();
        if (dirty.Count == 0) return true;
        var choice = await Dialogs.AskSaveAsync(this, string.Join(", ", dirty.Select(t => t.Title)));
        if (choice == SaveChoice.Cancel) return false;
        if (choice == SaveChoice.Save)
            foreach (var tab in dirty)
                if (!await SaveTabAsync(tab)) return false;
        return true;
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is not null) await SaveTabAsync(_activeTab);
    }

    private async void SaveAll_Click(object? sender, RoutedEventArgs e) => await SaveAllAsync();

    private async Task SaveAllAsync()
    {
        foreach (var tab in _tabs.Where(t => t.IsDirty).ToList()) await SaveTabAsync(tab);
    }

    private async Task<bool> SaveTabAsync(EditorTab tab)
    {
        try
        {
            if (tab.FilePath is null)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save file", SuggestedFileName = "NewFile.cs" });
                if (file?.TryGetLocalPath() is not { } path) return false;
                await tab.SaveAsync(path);
                _textMate.SetGrammar(_syntax.ScopeFor(path));
            }
            else await tab.SaveAsync();
            RenderTabs();
            UpdateTitle();
            SetStatus($"Saved {tab.Title}", StatusTone.Success);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Could not save {tab.Title}: {ex.Message}");
            SetStatus($"Could not save {tab.Title}", StatusTone.Error);
            return false;
        }
    }

    // ================================================================ editor commands

    private void Undo_Click(object? sender, RoutedEventArgs e) => Editor.Undo();
    private void Redo_Click(object? sender, RoutedEventArgs e) => Editor.Redo();

    private void Find_Click(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        Editor.Focus();
        Editor.SearchPanel?.Open();
    }

    private void GoToLine_Click(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is null)
        {
            SetStatus("Open a file first, then go to a line.");
            return;
        }
        GoToLineHint.Text = $"Current line {Editor.TextArea.Caret.Line} of {Editor.Document.LineCount}. Type a number and press Enter.";
        GoToLineInput.Text = "";
        GoToLinePanel.IsVisible = true;
        GoToLineInput.Focus();
    }

    private void CommitGoToLine()
    {
        var text = GoToLineInput.Text?.Trim() ?? "";
        var parts = text.Split(':', ',');
        if (!int.TryParse(parts[0], out var line))
        {
            GoToLineHint.Text = "Type a line number such as 42, or 42:8 for a column.";
            return;
        }
        var column = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 1;
        HideGoToLine();
        GoTo(line, column);
    }

    private void HideGoToLine()
    {
        if (!GoToLinePanel.IsVisible) return;
        GoToLinePanel.IsVisible = false;
        Editor.Focus();
    }

    private void GoTo(int line, int column = 1)
    {
        var document = Editor.Document;
        line = Math.Clamp(line, 1, document.LineCount);
        var docLine = document.GetLineByNumber(line);
        Editor.CaretOffset = docLine.Offset + Math.Clamp(column - 1, 0, docLine.Length);
        Editor.TextArea.Caret.BringCaretToView();
        Dispatcher.UIThread.Post(() => Editor.ScrollTo(line, column), DispatcherPriority.Background);
        Editor.Select(docLine.Offset, docLine.Length);
        Editor.Focus();
    }

    private void FormatCode_Click(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        var extension = Path.GetExtension(_activeTab.FilePath ?? ".txt");
        var result = CodeFormatter.Format(Editor.Document.Text, extension);
        if (result.Changed)
        {
            var caret = Editor.TextArea.Caret.Line;
            // Satu operasi Replace = satu langkah undo.
            Editor.Document.Replace(0, Editor.Document.TextLength, result.Text);
            Editor.TextArea.Caret.Line = Math.Min(caret, Editor.Document.LineCount);
        }
        SetStatus(result.Changed ? result.Summary : result.Summary.StartsWith("Not", StringComparison.Ordinal) ? result.Summary : "Already formatted.",
            result.Summary.StartsWith("Not", StringComparison.Ordinal) ? StatusTone.Error : StatusTone.Success);
    }
}
