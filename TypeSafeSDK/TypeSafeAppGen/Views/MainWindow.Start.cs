using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace TypeSafeAppGen.Views;

public partial class MainWindow
{
    private static readonly (string Keys, string Action)[] Shortcuts =
    [
        ("Ctrl+L", "Talk to Jack"),
        ("Ctrl+Enter", "Send to Jack"),
        ("Ctrl+I", "Ask Jack about this file"),
        ("Ctrl+Shift+N", "New project"),
        ("Ctrl+K / Ctrl+O", "Open folder / file"),
        ("Ctrl+S", "Save"),
        ("Ctrl+G", "Go to line"),
        ("Ctrl+F", "Find and replace"),
        ("Shift+Alt+F", "Format code"),
        ("Ctrl+Shift+B", "Build"),
        ("F5 / Shift+F5", "Run / stop"),
        ("Ctrl+B", "Toggle explorer"),
        ("Ctrl+J", "Toggle output panel"),
        ("Ctrl+Alt+B", "Toggle Jack"),
        ("Ctrl+Tab", "Next tab"),
    ];

    /// <summary>Halaman awal di area editor saat tidak ada tab terbuka.</summary>
    private void ShowStartPage(bool forceVisible = false)
    {
        if (_tabs.Count > 0 && !forceVisible) return;
        if (forceVisible && _activeTab is not null)
        {
            _activeTab.CaretOffset = Editor.CaretOffset;
            _activeTab = null;
            RenderTabs();
        }
        Editor.IsVisible = false;
        StartHost.IsVisible = true;
        UpdateTitle();
        UpdateCaretStatus();

        var headline = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 36) };
        var mark = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        mark.Children.Add(Ui.Icon(Icons.Bend, 18, Ui.Brush("Copper"), 2.6));
        mark.Children.Add(new TextBlock { Text = "TYPESAFE APP GENERATOR", VerticalAlignment = VerticalAlignment.Center }.WithClass("eyebrow"));
        headline.Children.Add(mark);
        headline.Children.Add(new TextBlock { Text = "Start a project, or ask Jack to build one.", FontSize = 26, TextWrapping = TextWrapping.Wrap }.WithClass("display"));
        headline.Children.Add(new TextBlock
        {
            Text = "Jack writes the files, builds them, and fixes the errors. Everything it touches opens here in the editor.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Left,
        }.WithClass("muted"));

        var start = Column("Start");
        start.Children.Add(Action(Icons.NewProject, "New project…", "Ctrl+Shift+N", NewProject_Click));
        start.Children.Add(Action(Icons.Spark, "Start from a template…", "", Templates_Click));
        start.Children.Add(Action(Icons.Folder, "Open folder…", "Ctrl+K", OpenFolder_Click));
        start.Children.Add(Action(Icons.File, "Open file…", "Ctrl+O", OpenFile_Click));

        var recent = Column("Recent");
        var recentProjects = _config.RecentProjects.Where(Directory.Exists).Take(6).ToList();
        if (recentProjects.Count == 0)
            recent.Children.Add(new TextBlock { Text = "Projects you open appear here.", Margin = new Thickness(10, 4) }.WithClass("muted"));
        foreach (var path in recentProjects)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontWeight = FontWeight.SemiBold });
            text.Children.Add(new TextBlock { Text = path, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis }.WithClass("muted"));
            var button = new Button { Content = text }.WithClass("row");
            button.Click += async (_, _) => await OpenProjectAsync(path);
            recent.Children.Add(button);
        }
        start.Children.Add(new Border { Height = 18 });
        foreach (var child in recent.Children.ToList())
        {
            recent.Children.Remove(child);
            start.Children.Add(child);
        }

        var keys = Column("Keyboard");
        var table = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        for (var i = 0; i < Shortcuts.Length; i++)
        {
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var key = new TextBlock { Text = Shortcuts[i].Keys, FontSize = 12, Margin = new Thickness(10, 4, 18, 4), Foreground = Ui.Brush("Verdigris") }.WithClass("mono");
            var label = new TextBlock { Text = Shortcuts[i].Action, FontSize = 12.5, Margin = new Thickness(0, 4), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(key, i);
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 1);
            table.Children.Add(key);
            table.Children.Add(label);
        }
        keys.Children.Add(table);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,48,*") };
        columns.Children.Add(start);
        Grid.SetColumn(keys, 2);
        columns.Children.Add(keys);

        var footer = new TextBlock { Text = "Made by Gravicode Studios, led by Kang Fadhil.", Margin = new Thickness(0, 40, 0, 0), FontSize = 12, Foreground = Ui.Brush("Faint") };

        var page = new StackPanel { MaxWidth = 900, Margin = new Thickness(48, 56, 48, 40), HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(headline);
        page.Children.Add(columns);
        page.Children.Add(footer);
        StartHost.Child = new ScrollViewer { Content = page };
    }

    private static StackPanel Column(string title)
    {
        var column = new StackPanel { Spacing = 2 };
        var eyebrow = Ui.Eyebrow(title);
        eyebrow.Margin = new Thickness(10, 0, 0, 8);
        column.Children.Add(eyebrow);
        return column;
    }

    private static Button Action(string icon, string label, string hint, EventHandler<RoutedEventArgs> onClick)
    {
        var row = new DockPanel();
        var hintText = new TextBlock { Text = hint, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) }.WithClass("muted");
        DockPanel.SetDock(hintText, Dock.Right);
        row.Children.Add(hintText);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        left.Children.Add(Ui.Icon(icon, 16, Ui.Brush("Verdigris")));
        left.Children.Add(new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(left);
        var button = new Button { Content = row }.WithClass("row");
        button.Click += onClick;
        return button;
    }
}
