using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TypeSafeAppGen.Ai;

namespace TypeSafeAppGen.Views;

/// <summary>Aksi yang tersedia di blok kode balasan Jack.</summary>
public sealed record CodeActions(Action<string> Copy, Action<string> InsertIntoEditor);

/// <summary>Markdown ringan untuk balasan chat: heading, bullet, **tebal**, `kode inline`, dan blok kode berpagar.</summary>
public static partial class ChatMarkdown
{
    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`)")]
    private static partial Regex InlineToken();

    public static IEnumerable<Control> Render(string markdown, CodeActions actions)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        var index = 0;
        while (index < lines.Length)
        {
            var line = lines[index];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (paragraph.Count > 0) { yield return Paragraph(paragraph); paragraph.Clear(); }
                var language = line.Trim()[3..].Trim();
                var code = new StringBuilder();
                index++;
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.AppendLine(lines[index]);
                    index++;
                }
                index++; // lewati pagar penutup (atau akhir teks saat masih streaming)
                yield return CodeBlock(language, code.ToString().TrimEnd('\n', '\r'), actions);
                continue;
            }
            if (line.Trim().Length == 0)
            {
                if (paragraph.Count > 0) { yield return Paragraph(paragraph); paragraph.Clear(); }
            }
            else paragraph.Add(line);
            index++;
        }
        if (paragraph.Count > 0) yield return Paragraph(paragraph);
    }

    private static Control Paragraph(List<string> lines)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 0, 8) };
        var inlines = block.Inlines!;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (i > 0) inlines.Add(new LineBreak());
            if (trimmed.StartsWith('#'))
            {
                var text = trimmed.TrimStart('#').Trim();
                inlines.Add(new Run(text) { FontWeight = FontWeight.SemiBold, FontSize = 14.5 });
                continue;
            }
            var bullet = trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal);
            if (bullet)
            {
                var depth = (line.Length - trimmed.Length) / 2;
                inlines.Add(new Run(new string(' ', depth * 3) + "•  ") { Foreground = Ui.Brush("Copper") });
                trimmed = trimmed[2..];
            }
            AddInline(inlines, bullet ? trimmed : line);
        }
        return block;
    }

    private static void AddInline(InlineCollection inlines, string text)
    {
        var position = 0;
        foreach (Match match in InlineToken().Matches(text))
        {
            if (match.Index > position) inlines.Add(new Run(text[position..match.Index]));
            var token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal))
                inlines.Add(new Run(token[2..^2]) { FontWeight = FontWeight.SemiBold });
            else
                inlines.Add(new Run(token[1..^1]) { FontFamily = Ui.Font("Mono"), FontSize = 12.5, Foreground = Ui.Brush("Verdigris") });
            position = match.Index + match.Length;
        }
        if (position < text.Length) inlines.Add(new Run(text[position..]));
    }

    private static Control CodeBlock(string language, string code, CodeActions actions)
    {
        var copy = new Button { Content = "Copy", FontSize = 11, Padding = new Thickness(8, 2) }.WithClass("tool");
        copy.Click += (_, _) =>
        {
            actions.Copy(code);
            copy.Content = "Copied";
            DispatcherTimer.RunOnce(() => copy.Content = "Copy", TimeSpan.FromSeconds(1.5));
        };
        var insert = new Button { Content = "Insert at cursor", FontSize = 11, Padding = new Thickness(8, 2) }.WithClass("tool");
        insert.Click += (_, _) => actions.InsertIntoEditor(code);

        var header = new DockPanel { Margin = new Thickness(10, 4, 4, 2) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        buttons.Children.Add(copy);
        buttons.Children.Add(insert);
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(new TextBlock { Text = (language.Length == 0 ? "code" : language).ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center }.WithClass("eyebrow"));

        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new SelectableTextBlock { Text = code, FontFamily = Ui.Font("Mono"), FontSize = 12.5, Margin = new Thickness(12, 4, 12, 10), Foreground = Ui.Brush("Ink") },
        };
        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(body);
        return new Border
        {
            Background = Ui.Brush("PatinaDeep"),
            BorderBrush = Ui.Brush("Rule"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2, 0, 10),
            Child = panel,
        };
    }
}

/// <summary>Pesan pengguna: teks polos dan thumbnail gambar terlampir.</summary>
public sealed class UserMessageView : Border
{
    public UserMessageView(string text, IReadOnlyList<ImageAttachment> images)
    {
        Background = Ui.Brush("PatinaRaised");
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12, 9);
        Margin = new Thickness(28, 0, 0, 16);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        if (images.Count > 0)
        {
            var strip = new WrapPanel();
            foreach (var image in images)
            {
                using var stream = new MemoryStream(image.Data);
                strip.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    ClipToBounds = true,
                    Margin = new Thickness(0, 0, 6, 6),
                    Child = new Image { Source = Bitmap.DecodeToWidth(stream, 160), Height = 64, Stretch = Stretch.UniformToFill },
                });
            }
            stack.Children.Add(strip);
        }
        Child = stack;
    }
}

/// <summary>
/// Balasan Jack. Tanda pengenalnya adalah "bend rail": batang tembaga yang turun di kiri lalu menekuk
/// ke kanan di bawah nama Jack. Isi dirender bertahap saat streaming, dengan chip untuk setiap tool call.
/// </summary>
public sealed class JackMessageView : Grid
{
    private readonly StackPanel _content = new();
    private readonly CodeActions _actions;
    private readonly TextBlock _stamp;
    private readonly DispatcherTimer _renderTimer;
    private StringBuilder? _currentText;
    private StackPanel? _currentHost;
    private bool _dirty;

    public JackMessageView(CodeActions actions)
    {
        _actions = actions;
        Margin = new Thickness(0, 0, 0, 18);
        ColumnDefinitions = new ColumnDefinitions("14,*");
        RowDefinitions = new RowDefinitions("Auto,*");

        var rail = new Border
        {
            BorderBrush = Ui.Brush("Copper"),
            BorderThickness = new Thickness(2, 2, 0, 0),
            CornerRadius = new CornerRadius(9, 0, 0, 0),
            Margin = new Thickness(3, 9, 0, 2),
        };
        Grid.SetRowSpan(rail, 2);
        Grid.SetColumnSpan(rail, 1);
        Children.Add(rail);

        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6, 0, 0, 6) };
        name.Children.Add(new TextBlock { Text = "Jack", Foreground = Ui.Brush("Copper"), FontSize = 13 }.WithClass("display"));
        _stamp = new TextBlock { Text = DateTime.Now.ToString("HH:mm"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center }.WithClass("muted");
        name.Children.Add(_stamp);
        Grid.SetColumn(name, 1);
        Children.Add(name);

        _content.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(_content, 1);
        Grid.SetRow(_content, 1);
        Children.Add(_content);

        // Render ulang markdown paling sering tiap 60 ms: streaming tetap halus tanpa membangun ulang UI per token.
        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => Flush());
    }

    public event Action? ContentGrew;

    public static JackMessageView FromText(string text, CodeActions actions)
    {
        var view = new JackMessageView(actions);
        view.AppendText(text);
        view.Flush();
        return view;
    }

    public void AppendText(string delta)
    {
        if (_currentText is null)
        {
            _currentText = new StringBuilder();
            _currentHost = new StackPanel();
            _content.Children.Add(_currentHost);
        }
        _currentText.Append(delta);
        _dirty = true;
        if (!_renderTimer.IsEnabled) _renderTimer.Start();
    }

    public void Flush()
    {
        _renderTimer.Stop();
        if (!_dirty || _currentText is null || _currentHost is null) return;
        _dirty = false;
        _currentHost.Children.Clear();
        foreach (var control in ChatMarkdown.Render(_currentText.ToString(), _actions)) _currentHost.Children.Add(control);
        ContentGrew?.Invoke();
    }

    /// <summary>Menambah chip tool call; mengembalikan fungsi untuk menandai selesai (sukses/gagal + ringkasan).</summary>
    public Action<bool, string> AddTool(string name, string detail)
    {
        Flush();
        _currentText = null; // teks sesudah tool masuk ke segmen baru, di bawah chip
        var icon = Ui.Icon(Icons.Spark, 12, Ui.Brush("Copper"), 1.6);
        var label = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.Inlines!.Add(new Run(name) { Foreground = Ui.Brush("Ink") });
        if (detail.Length > 0) label.Inlines.Add(new Run("  " + detail) { Foreground = Ui.Brush("Muted") });
        label.FontFamily = Ui.Font("Mono");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(icon);
        row.Children.Add(label);
        var chip = new Border
        {
            Background = Ui.Brush("CopperWash"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = row,
        };
        _content.Children.Add(chip);
        ContentGrew?.Invoke();

        return (ok, summary) =>
        {
            row.Children[0] = Ui.Icon(ok ? Icons.Check : Icons.Alert, 12, ok ? Ui.Brush("Verdigris") : Ui.Brush("Ember"), 1.8);
            chip.Background = Ui.Brush("PatinaRaised");
            if (!ok && summary.Length > 0) ToolTip.SetTip(chip, summary);
        };
    }

    public void ShowThinking(bool thinking)
    {
        _stamp.Text = thinking ? "working…" : DateTime.Now.ToString("HH:mm");
        _stamp.Foreground = thinking ? Ui.Brush("Copper") : Ui.Brush("Muted");
    }

    public void ShowError(string message, Action? openSettings)
    {
        Flush();
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("Ink"), LineHeight = 19 });
        if (openSettings is not null)
        {
            var button = new Button { Content = "Open settings", HorizontalAlignment = HorizontalAlignment.Left }.WithClass("ghost");
            button.Click += (_, _) => openSettings();
            panel.Children.Add(button);
        }
        _content.Children.Add(new Border
        {
            BorderBrush = Ui.Brush("Ember"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 6),
            Background = Ui.Brush("PatinaRaised"),
            CornerRadius = new CornerRadius(0, 6, 6, 0),
            Child = panel,
        });
        ContentGrew?.Invoke();
    }
}
