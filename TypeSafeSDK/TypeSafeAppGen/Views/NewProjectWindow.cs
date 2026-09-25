using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TypeSafeAppGen.Workspace;

namespace TypeSafeAppGen.Views;

public sealed record NewProjectRequest(string TemplateId, string Name, string ParentFolder);

/// <summary>
/// Dialog New Project: dua jalur (Blank atau From Template), galeri template per kategori,
/// lalu nama dan lokasi. Tombol Create hanya aktif bila semua isian valid.
/// </summary>
public sealed class NewProjectWindow : Window
{
    private readonly IReadOnlyList<ProjectTemplate> _templates = ProjectTemplates.All();
    private readonly WrapPanel _gallery = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _categories = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBox _name = new TextBox { Watermark = "MyApp" }.WithClass("field");
    private readonly TextBox _location = new TextBox().WithClass("field");
    private readonly TextBlock _error = new() { Foreground = Ui.Brush("Ember"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _pathPreview = new() { Foreground = Ui.Brush("Muted"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _create = new Button { Content = "Create project", IsDefault = true }.WithClass("primary");
    private readonly ToggleButton _blankMode;
    private readonly ToggleButton _templateMode;
    private readonly ScrollViewer _galleryScroll;
    private readonly ScrollViewer _categoryRow;
    private readonly StackPanel _blankInfo = BlankInfo();
    private string _category = "All";
    private string _selectedId = ProjectTemplates.BlankId;
    private NewProjectRequest? _result;

    public NewProjectWindow(string defaultFolder, bool startWithTemplates)
    {
        Title = "New project";
        Width = 940;
        Height = 660;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Brush("PatinaPanel");
        _location.Text = defaultFolder;

        _blankMode = ModeCard("Blank", "An empty .NET 10 console project. Describe the app to Jack and let it grow from there.");
        _templateMode = ModeCard("From template", "Start from a working app: 3D graphics, animation, games, simulators, web, and AI.");
        _blankMode.Click += (_, _) => SetMode(template: false);
        _templateMode.Click += (_, _) => SetMode(template: true);

        var modes = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 18) };
        modes.Children.Add(_blankMode);
        modes.Children.Add(_templateMode);

        _galleryScroll = new ScrollViewer { Content = _gallery, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var galleryArea = new DockPanel();
        _categoryRow = new ScrollViewer { Content = _categories, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(_categoryRow, Dock.Top);
        galleryArea.Children.Add(_categoryRow);
        DockPanel.SetDock(_blankInfo, Dock.Top);
        galleryArea.Children.Add(_blankInfo);
        galleryArea.Children.Add(_galleryScroll);

        var browse = new Button { Content = "Browse…" }.WithClass("ghost");
        browse.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose where to create the project", AllowMultiple = false });
            if (folders.Count > 0) _location.Text = folders[0].Path.LocalPath;
        };

        var nameColumn = new StackPanel { Spacing = 6 };
        nameColumn.Children.Add(Ui.Eyebrow("Project name"));
        nameColumn.Children.Add(_name);
        var locationRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(8, 0, 0, 0);
        locationRow.Children.Add(browse);
        locationRow.Children.Add(_location);
        var locationColumn = new StackPanel { Spacing = 6 };
        locationColumn.Children.Add(Ui.Eyebrow("Location"));
        locationColumn.Children.Add(locationRow);
        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,2*") };
        fields.Children.Add(nameColumn);
        Grid.SetColumn(locationColumn, 2);
        fields.Children.Add(locationColumn);

        var cancel = new Button { Content = "Cancel", IsCancel = true }.WithClass("ghost");
        cancel.Click += (_, _) => Close();
        _create.Click += (_, _) => Submit();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(cancel);
        actions.Children.Add(_create);

        var footer = new StackPanel { Spacing = 10, Margin = new Thickness(0, 16, 0, 0) };
        footer.Children.Add(fields);
        footer.Children.Add(_pathPreview);
        footer.Children.Add(_error);
        footer.Children.Add(actions);

        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(new TextBlock { Text = "New project", FontSize = 22 }.WithClass("display"));
        header.Children.Add(new TextBlock { Text = "Pick a starting point. Every template builds and runs as-is." }.WithClass("muted"));

        var root = new DockPanel { Margin = new Thickness(28, 24) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(modes, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(modes);
        root.Children.Add(footer);
        root.Children.Add(galleryArea);
        Content = root;

        _name.TextChanged += (_, _) => Validate();
        _location.TextChanged += (_, _) => Validate();
        // Enter selalu berarti Create, meski fokus sedang di kartu atau chip (yang sendirinya menangani Enter).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Submit();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => _name.Focus();

        BuildCategories();
        SetMode(startWithTemplates);
    }

    public static async Task<NewProjectRequest?> ShowAsync(Window owner, string defaultFolder, bool startWithTemplates = false)
    {
        var window = new NewProjectWindow(defaultFolder, startWithTemplates);
        await window.ShowDialog(owner);
        return window._result;
    }

    private static ToggleButton ModeCard(string title, string description)
    {
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        text.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Muted"), FontSize = 12 });
        return new ToggleButton
        {
            Classes = { "card" },
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 12),
            Margin = new Thickness(0, 0, 8, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>Isi mode Blank: apa yang dibuat, dan langkah berikutnya bersama Jack.</summary>
    private static StackPanel BlankInfo()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(4, 8, 0, 0), MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Ui.Eyebrow("What you get"));
        foreach (var line in new[] { "<Name>.csproj — a .NET 10 console project, nullable enabled", "Program.cs — one line that prints a greeting" })
            panel.Children.Add(new TextBlock { Text = line, FontSize = 12.5 }.WithClass("mono"));
        panel.Children.Add(new Border { Height = 10 });
        panel.Children.Add(Ui.Eyebrow("Then"));
        panel.Children.Add(new TextBlock
        {
            Text = "Describe the app in the Jack panel — for example “turn this into an Avalonia pomodoro timer with a progress ring”. Jack rewrites the project, adds packages, and builds it until it compiles.",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Foreground = Ui.Brush("Muted"),
        });
        return panel;
    }

    private void SetMode(bool template)
    {
        _blankMode.IsChecked = !template;
        _templateMode.IsChecked = template;
        _blankInfo.IsVisible = !template;
        _categoryRow.IsVisible = template;
        _galleryScroll.IsVisible = template;
        _selectedId = template ? (_templates.FirstOrDefault(t => t.Id == _selectedId)?.Id ?? _templates.FirstOrDefault()?.Id ?? ProjectTemplates.BlankId) : ProjectTemplates.BlankId;
        if (template) BuildGallery();
        SuggestName();
        Validate();
    }

    private void BuildCategories()
    {
        var names = new[] { "All" }.Concat(_templates.Select(t => t.Category).Distinct()).ToList();
        foreach (var name in names)
        {
            var chip = new ToggleButton { Content = name, IsChecked = name == _category, Padding = new Thickness(12, 4), CornerRadius = new CornerRadius(12), FontSize = 12 };
            chip.Click += (_, _) =>
            {
                _category = name;
                foreach (var other in _categories.Children.OfType<ToggleButton>()) other.IsChecked = Equals(other.Content, name);
                BuildGallery();
            };
            _categories.Children.Add(chip);
        }
    }

    private void BuildGallery()
    {
        _gallery.Children.Clear();
        foreach (var template in _templates.Where(t => _category == "All" || t.Category == _category))
            _gallery.Children.Add(TemplateCard(template));
    }

    private Control TemplateCard(ProjectTemplate template)
    {
        var selected = template.Id == _selectedId;
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = template.Category.ToUpperInvariant(), Foreground = selected ? Ui.Brush("Verdigris") : Ui.Brush("Muted") }.WithClass("eyebrow"));
        body.Children.Add(new TextBlock { Text = template.Name, FontSize = 15, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock { Text = template.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Brush("Ink"), LineHeight = 17 });
        body.Children.Add(new TextBlock { Text = template.UseCase, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = Ui.Brush("Muted"), LineHeight = 16, FontStyle = FontStyle.Italic });
        body.Children.Add(new TextBlock { Text = template.Stack, FontSize = 11, Foreground = Ui.Brush("Faint") }.WithClass("mono"));

        var card = new Button
        {
            Classes = { "card" },
            Content = body,
            Width = 270,
            MinHeight = 170,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14, 12),
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(8),
            Background = selected ? Ui.Brush("VerdigrisWash") : Ui.Brush("PatinaRaised"),
            BorderBrush = selected ? Ui.Brush("Verdigris") : Ui.Brush("Rule"),
            BorderThickness = new Thickness(1),
        };
        card.Click += (_, _) =>
        {
            _selectedId = template.Id;
            BuildGallery();
            SuggestName();
            Validate();
        };
        card.DoubleTapped += (_, _) => Submit();
        return card;
    }

    /// <summary>Isi nama otomatis dari template yang dipilih, selama pengguna belum mengetik nama sendiri.</summary>
    private void SuggestName()
    {
        var current = _name.Text ?? "";
        var suggestions = _templates.Select(t => ProjectTemplates.ToIdentifier(t.Name)).Append("MyApp");
        if (current.Length > 0 && !suggestions.Contains(current)) return;
        var template = _templates.FirstOrDefault(t => t.Id == _selectedId);
        _name.Text = template is null ? "MyApp" : ProjectTemplates.ToIdentifier(template.Name);
    }

    private string? Problem()
    {
        var name = _name.Text?.Trim() ?? "";
        if (ProjectTemplates.ValidateName(name) is { } nameError) return nameError;
        var folder = _location.Text?.Trim() ?? "";
        if (folder.Length == 0 || !Path.IsPathRooted(folder)) return "Choose a full folder path for the location.";
        var target = Path.Combine(folder, name);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) return $"'{target}' already exists. Pick another name.";
        return null;
    }

    private void Validate()
    {
        var problem = Problem();
        _error.Text = problem ?? "";
        _error.IsVisible = problem is not null && (_name.Text?.Length ?? 0) > 0;
        _create.IsEnabled = problem is null;
        _pathPreview.Text = problem is null ? $"Creates {Path.Combine(_location.Text!.Trim(), _name.Text!.Trim())}" : "";
    }

    private void Submit()
    {
        if (Problem() is not null) return;
        _result = new NewProjectRequest(_selectedId, _name.Text!.Trim(), _location.Text!.Trim());
        Close();
    }
}
