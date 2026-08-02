using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DepthAI.Wizard.Projects;

namespace DepthAI.Wizard.App.Views;

/// <summary>Kartu template pada galeri.</summary>
public sealed class TemplateCard(ProjectTemplate template, bool isSelected)
{
    public ProjectTemplate Template { get; } = template;

    public string Id => Template.Id;

    public string Title => Template.Title;

    public string Description => Template.Description;

    public string Icon => Template.Icon;

    public string KindLabel => Template.Kind switch
    {
        ProjectKind.Console => "Konsol",
        ProjectKind.Desktop => "Desktop",
        _ => "Web",
    };

    public IBrush CardBackground => isSelected
        ? App.Resource("AccentNearSoft")
        : App.Resource("SurfaceDeep");

    public IBrush CardBorder => isSelected
        ? App.Resource("AccentNear")
        : App.Resource("HairLine");
}

/// <summary>
/// Dialog pembuatan proyek: pilih Kosong atau Dari Template, beri nama, tentukan lokasi.
/// </summary>
public partial class NewProjectDialog : Window
{
    private const string AllCategories = "Semua";

    private readonly ObservableCollection<TemplateCard> _cards = [];
    private readonly string? _sdkRoot = ProjectScaffolder.FindSdkRepositoryRoot();

    private ProjectTemplate _selected = TemplateCatalog.Get("object-detection-desktop");
    private string _category = AllCategories;

    public NewProjectDialog()
    {
        InitializeComponent();

        TemplateList.ItemsSource = _cards;

        CategoryList.ItemsSource = new[] { AllCategories }
            .Concat(Enum.GetNames<TemplateCategory>())
            .ToList();
        CategoryList.SelectedIndex = 0;

        LocationBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DepthAI Projects");

        RefreshCards();
        UpdateFooter();
    }

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        var blank = ReferenceEquals(sender, BlankToggle);

        BlankToggle.IsChecked = blank;
        TemplateToggle.IsChecked = !blank;

        // Mode kosong hanya menawarkan tiga kerangka, jadi kategorinya dikunci.
        CategoryList.IsEnabled = !blank;
        _category = blank ? nameof(TemplateCategory.Blank) : AllCategories;
        CategoryList.SelectedIndex = blank
            ? Enum.GetNames<TemplateCategory>().ToList().IndexOf(nameof(TemplateCategory.Blank)) + 1
            : 0;

        RefreshCards();
    }

    private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is string category)
        {
            _category = category;
            RefreshCards();
        }
    }

    private void RefreshCards()
    {
        var templates = TemplateCatalog.All.AsEnumerable();

        if (_category != AllCategories && Enum.TryParse<TemplateCategory>(_category, out var parsed))
        {
            templates = templates.Where(t => t.Category == parsed);
        }

        var list = templates.ToList();

        // Bila template terpilih tersaring keluar, pilihan dipindahkan ke kartu
        // pertama supaya tombol Buat tidak membuat sesuatu yang tak terlihat.
        if (list.Count > 0 && !list.Any(t => t.Id == _selected.Id))
        {
            _selected = list[0];
        }

        _cards.Clear();
        foreach (var template in list)
        {
            _cards.Add(new TemplateCard(template, template.Id == _selected.Id));
        }

        UpdateFooter();
    }

    private void OnTemplatePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: TemplateCard card })
        {
            _selected = card.Template;
            RefreshCards();
        }
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e) => UpdateFooter();

    private void UpdateFooter()
    {
        var name = NameBox?.Text ?? string.Empty;
        var location = LocationBox?.Text ?? string.Empty;

        if (FooterText is null || CreateButton is null)
        {
            return;
        }

        try
        {
            ProjectScaffolder.ValidateName(name);
        }
        catch (ArgumentException ex)
        {
            FooterText.Text = ex.Message;
            FooterText.Foreground = App.Resource("SignalError");
            CreateButton.IsEnabled = false;
            return;
        }

        CreateButton.IsEnabled = true;
        FooterText.Foreground = App.Resource("InkMuted");

        var target = Path.Combine(location, name);
        var requires = _selected.Requires.Count > 0
            ? $" · butuh: {string.Join(", ", _selected.Requires)}"
            : string.Empty;

        // Saat wizard berjalan dari repo SDK, proyek baru dirujuk lewat ProjectReference
        // supaya langsung bisa di-build tanpa paket yang dipublikasikan.
        var reference = _sdkRoot is null
            ? "merujuk paket NuGet DepthAI.Net"
            : "merujuk proyek SDK di repo ini";

        FooterText.Text = $"{_selected.Title} → {target}  ({reference}){requires}";
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pilih lokasi proyek",
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (folder is not null)
        {
            LocationBox.Text = folder;
            UpdateFooter();
        }
    }

    private async void OnCreate(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text ?? string.Empty;
        var location = LocationBox.Text ?? string.Empty;

        CreateButton.IsEnabled = false;

        try
        {
            Directory.CreateDirectory(location);

            var result = await ProjectScaffolder.CreateAsync(new ScaffoldOptions
            {
                ProjectName = name,
                ParentDirectory = location,
                Template = _selected,
                SdkReference = _sdkRoot is null ? SdkReferenceMode.Package : SdkReferenceMode.Project,
                SdkRepositoryRoot = _sdkRoot,
            });

            Close(result);
        }
        catch (Exception ex)
        {
            FooterText.Text = ex.Message;
            FooterText.Foreground = App.Resource("SignalError");
            CreateButton.IsEnabled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
