using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

// Avalonia dan Markdig sama-sama punya tipe bernama Inline; alias ini menegaskan
// bahwa yang dimaksud di sini adalah simpul pohon sintaks Markdig.
using MarkdownInline = Markdig.Syntax.Inlines.Inline;

namespace DepthAI.Wizard.App.Views;

/// <summary>
/// Merender Markdown menjadi kontrol Avalonia.
/// </summary>
/// <remarks>
/// Markdig dipakai untuk mem-parsing, lalu pohon sintaksnya dipetakan ke kontrol —
/// bukan ke HTML dalam WebView. Balasan asisten adalah bagian dari UI aplikasi, jadi
/// harus mengikuti tema, ukuran font, dan perilaku seleksi yang sama dengan panel lain.
/// </remarks>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmphasisExtras()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    /// <summary>Dipicu saat pengguna menekan tombol salin pada blok kode.</summary>
    public static event Action<string>? CodeCopyRequested;

    /// <summary>Dipicu saat tautan diklik.</summary>
    public static event Action<string>? LinkActivated;

    /// <summary>Merender Markdown menjadi panel berisi kontrol.</summary>
    public static Control Render(string markdown)
    {
        var panel = new StackPanel { Spacing = 10 };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return panel;
        }

        var document = Markdown.Parse(markdown, Pipeline);

        foreach (var block in document)
        {
            var control = RenderBlock(block);
            if (control is not null)
            {
                panel.Children.Add(control);
            }
        }

        return panel;
    }

    private static Control? RenderBlock(Block block) => block switch
    {
        HeadingBlock heading => RenderHeading(heading),
        ParagraphBlock paragraph => RenderParagraph(paragraph),
        FencedCodeBlock code => RenderCode(code.Lines.ToString(), code.Info),
        CodeBlock code => RenderCode(code.Lines.ToString(), null),
        QuoteBlock quote => RenderQuote(quote),
        ListBlock list => RenderList(list),
        Table table => RenderTable(table),
        ThematicBreakBlock => new Border
        {
            Height = 1,
            Margin = new Thickness(0, 6),
            Background = App.Resource("HairLine"),
        },
        _ => null,
    };

    private static Control RenderHeading(HeadingBlock heading)
    {
        var text = new SelectableTextBlock
        {
            FontWeight = FontWeight.SemiBold,
            Foreground = App.Resource("InkBright"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = heading.Level switch
            {
                1 => 18,
                2 => 16,
                3 => 14,
                _ => 13,
            },
            Margin = new Thickness(0, heading.Level <= 2 ? 6 : 2, 0, 0),
        };

        AppendInlines(text.Inlines!, heading.Inline);
        return text;
    }

    private static Control RenderParagraph(ParagraphBlock paragraph)
    {
        // Paragraf yang isinya hanya satu gambar dirender sebagai gambar, bukan
        // sebagai baris teks berisi tautan.
        if (paragraph.Inline?.FirstChild is LinkInline { IsImage: true } image
            && image.NextSibling is null)
        {
            return RenderImage(image);
        }

        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20,
            Foreground = App.Resource("InkBright"),
        };

        AppendInlines(text.Inlines!, paragraph.Inline);
        return text;
    }

    private static Control RenderQuote(QuoteBlock quote)
    {
        var inner = new StackPanel { Spacing = 6 };

        foreach (var child in quote)
        {
            var control = RenderBlock(child);
            if (control is not null)
            {
                inner.Children.Add(control);
            }
        }

        return new Border
        {
            BorderBrush = App.Resource("AccentFar"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 4, 0, 4),
            Child = inner,
        };
    }

    private static Control RenderList(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 4 };
        var index = 1;

        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem)
            {
                continue;
            }

            var content = new StackPanel { Spacing = 4 };
            foreach (var child in listItem)
            {
                var control = RenderBlock(child);
                if (control is not null)
                {
                    content.Children.Add(control);
                }
            }

            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{index++}." : "•",
                Foreground = App.Resource("AccentNear"),
                FontSize = 13,
                MinWidth = 20,
                VerticalAlignment = VerticalAlignment.Top,
            };

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Margin = new Thickness(6, 0, 0, 0),
            };

            Grid.SetColumn(marker, 0);
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);

            panel.Children.Add(row);
        }

        return panel;
    }

    private static Control RenderCode(string code, string? language)
    {
        code = code.TrimEnd();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var languageLabel = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "kode" : language,
            FontSize = 10,
            LetterSpacing = 1.2,
            Foreground = App.Resource("InkFaint"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var copyButton = new Button
        {
            Content = "Salin",
            FontSize = 10,
            Padding = new Thickness(8, 2),
            Background = Brushes.Transparent,
            Foreground = App.Resource("InkMuted"),
            BorderThickness = new Thickness(0),
        };

        copyButton.Click += (_, _) =>
        {
            CodeCopyRequested?.Invoke(code);
            copyButton.Content = "Tersalin";
        };

        Grid.SetColumn(languageLabel, 0);
        Grid.SetColumn(copyButton, 1);
        header.Children.Add(languageLabel);
        header.Children.Add(copyButton);

        var body = new SelectableTextBlock
        {
            Text = code,
            FontFamily = MonoFont,
            FontSize = 12,
            Foreground = App.Resource("InkBright"),
            TextWrapping = TextWrapping.NoWrap,
        };

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = body,
            Margin = new Thickness(0, 6, 0, 0),
        };

        return new Border
        {
            Background = App.Resource("SurfaceAbyss"),
            BorderBrush = App.Resource("HairLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Child = new StackPanel { Children = { header, scroller } },
        };
    }

    private static Control RenderTable(Table table)
    {
        var grid = new Grid();
        var columnCount = table.OfType<TableRow>().Max(r => r.Count);

        for (var i = 0; i < columnCount; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var rowIndex = 0;

        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            for (var column = 0; column < row.Count; column++)
            {
                if (row[column] is not TableCell cell)
                {
                    continue;
                }

                var content = new StackPanel();
                foreach (var block in cell)
                {
                    var control = RenderBlock(block);
                    if (control is not null)
                    {
                        content.Children.Add(control);
                    }
                }

                var container = new Border
                {
                    Padding = new Thickness(10, 6),
                    // Baris kepala diberi latar berbeda; sisanya dipisah garis rambut saja.
                    Background = row.IsHeader ? App.Resource("SurfaceNear") : Brushes.Transparent,
                    BorderBrush = App.Resource("HairLine"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = content,
                };

                Grid.SetRow(container, rowIndex);
                Grid.SetColumn(container, column);
                grid.Children.Add(container);
            }

            rowIndex++;
        }

        return new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = App.Resource("SurfaceMid"),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = grid,
            },
        };
    }

    /// <summary>
    /// Merender gambar. Berkas lokal dan data URI ditampilkan langsung; gambar jarak
    /// jauh ditampilkan sebagai tautan agar merender balasan tidak memicu permintaan
    /// jaringan yang tidak diminta pengguna.
    /// </summary>
    private static Control RenderImage(LinkInline image)
    {
        var url = image.Url ?? string.Empty;

        try
        {
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var path = new Uri(url).LocalPath;
                if (File.Exists(path))
                {
                    return new Image
                    {
                        Source = new Bitmap(path),
                        Stretch = Stretch.Uniform,
                        MaxHeight = 320,
                        HorizontalAlignment = HorizontalAlignment.Left,
                    };
                }
            }
            else if (File.Exists(url))
            {
                return new Image
                {
                    Source = new Bitmap(url),
                    Stretch = Stretch.Uniform,
                    MaxHeight = 320,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException)
        {
            // Gambar yang gagal dimuat jatuh ke tampilan tautan di bawah.
        }

        return MediaChip("🖼️", image.Title ?? Path.GetFileName(url), url);
    }

    /// <summary>
    /// Chip untuk media yang tidak bisa dirender inline. Avalonia tidak punya kontrol
    /// pemutar audio atau video bawaan, jadi tautannya dibuka di aplikasi sistem.
    /// </summary>
    private static Control MediaChip(string icon, string label, string url)
    {
        var button = new Button
        {
            Background = App.Resource("SurfaceNear"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = icon, FontSize = 14 },
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(label) ? url : label,
                        FontSize = 12,
                        Foreground = App.Resource("InkBright"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = "buka ↗",
                        FontSize = 11,
                        Foreground = App.Resource("AccentFar"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };

        button.Click += (_, _) => LinkActivated?.Invoke(url);
        return button;
    }

    private static void AppendInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            AppendInline(target, inline);
        }
    }

    private static void AppendInline(InlineCollection target, MarkdownInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;

            case EmphasisInline emphasis:
            {
                var span = new Span();

                // Markdig memakai jumlah delimiter untuk membedakan miring dari tebal.
                if (emphasis.DelimiterChar is '*' or '_')
                {
                    if (emphasis.DelimiterCount >= 2)
                    {
                        span.FontWeight = FontWeight.SemiBold;
                    }
                    else
                    {
                        span.FontStyle = FontStyle.Italic;
                    }
                }
                else if (emphasis.DelimiterChar == '~')
                {
                    span.TextDecorations = TextDecorations.Strikethrough;
                }

                foreach (var child in emphasis)
                {
                    AppendInline(span.Inlines, child);
                }

                target.Add(span);
                break;
            }

            case CodeInline code:
                target.Add(new Run(code.Content)
                {
                    FontFamily = MonoFont,
                    Foreground = App.Resource("AccentNear"),
                });
                break;

            case LinkInline { IsImage: false } link:
            {
                var run = new Run(link.FirstChild is LiteralInline literal
                    ? literal.Content.ToString()
                    : link.Url ?? "tautan")
                {
                    Foreground = App.Resource("AccentFar"),
                    TextDecorations = TextDecorations.Underline,
                };

                target.Add(run);
                break;
            }

            case LineBreakInline:
                target.Add(new LineBreak());
                break;

            case ContainerInline nested:
                foreach (var child in nested)
                {
                    AppendInline(target, child);
                }

                break;

            default:
                target.Add(new Run(inline.ToString() ?? string.Empty));
                break;
        }
    }

    private static FontFamily MonoFont => Application.Current?.TryGetResource(
        "MonoFont", Application.Current.ActualThemeVariant, out var value) == true && value is FontFamily family
            ? family
            : FontFamily.Default;
}
