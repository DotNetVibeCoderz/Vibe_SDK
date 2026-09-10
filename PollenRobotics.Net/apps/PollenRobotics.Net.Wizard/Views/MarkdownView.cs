using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PollenRobotics.Net.Wizard.Views;

/// <summary>
/// Renders Markdown into Avalonia controls.
/// </summary>
/// <remarks>
/// <para>
/// Jack answers in Markdown and the answers are mostly code, so the code block is the element that
/// had to be right: monospaced, horizontally scrollable rather than wrapped, with a copy button and
/// the language shown. Everything else - headings, lists, tables, quotes, images - is here because
/// a model that has been told it may use Markdown will use all of it, and an unrendered table is
/// worse than no table.
/// </para>
/// <para>
/// Deliberately not a WebView. The chat panel sits beside a code editor in a desktop tool, and
/// putting a browser in it would mean a second theme to keep in step, a second font stack, and a
/// process boundary between the assistant and the editor it is meant to be driving.
/// </para>
/// </remarks>
public sealed class MarkdownView : ContentControl
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    /// <summary>The Markdown to render.</summary>
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    /// <summary>
    /// Renders using <see cref="ContentControl"/>'s template rather than looking for one of its own.
    /// </summary>
    /// <remarks>
    /// Avalonia matches control themes on the exact runtime type, so a class derived from
    /// ContentControl gets no template at all and draws nothing - no error, no warning, just an
    /// empty rectangle where the content should be. This is what made the whole chat thread appear
    /// blank while the conversation was sitting in memory perfectly intact.
    /// </remarks>
    protected override Type StyleKeyOverride => typeof(ContentControl);

    /// <summary>Raised when a code block's copy button is used.</summary>
    public event Action<string>? CodeCopyRequested;

    /// <summary>Raised when a code block's insert button is used.</summary>
    public event Action<string>? CodeInsertRequested;

    /// <inheritdoc cref="MarkdownProperty" />
    public string? Markdown { get => GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MarkdownProperty)
        {
            Content = Render(change.GetNewValue<string?>());
        }
    }

    /// <summary>
    /// Re-renders once the control is in the tree.
    /// </summary>
    /// <remarks>
    /// Resource lookup walks the logical tree, so a control built with an object initializer -
    /// <c>new MarkdownView { Markdown = ... }</c> - renders while it still has no parent and
    /// resolves every theme brush to nothing. The first pass produced correctly structured content
    /// with no colours, which drew as a blank panel with working buttons in it. Rendering again on
    /// attach is what makes the brushes resolve.
    /// </remarks>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Content = Render(Markdown);
    }

    private Control Render(string? markdown)
    {
        var stack = new StackPanel { Spacing = 8 };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return stack;
        }

        MarkdownDocument document = Markdig.Markdown.Parse(markdown, Pipeline);

        foreach (Block block in document)
        {
            if (RenderBlock(block) is { } control)
            {
                stack.Children.Add(control);
            }
        }

        return stack;
    }

    private Control? RenderBlock(Block block) => block switch
    {
        HeadingBlock heading => RenderHeading(heading),
        ParagraphBlock paragraph => RenderParagraph(paragraph),
        FencedCodeBlock fenced => RenderCode(fenced.Lines.ToString(), fenced.Info),
        CodeBlock code => RenderCode(code.Lines.ToString(), null),
        ListBlock list => RenderList(list),
        QuoteBlock quote => RenderQuote(quote),
        Table table => RenderTable(table),
        ThematicBreakBlock => new Border
        {
            Height = 1,
            Margin = new Thickness(0, 6),
            Background = Brush("LineBrush"),
        },
        _ => null,
    };

    private Control RenderHeading(HeadingBlock heading)
    {
        var text = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, heading.Level == 1 ? 4 : 6, 0, 0),
            FontSize = heading.Level switch { 1 => 19, 2 => 16, 3 => 14, _ => 13 },
        };

        AppendInlines(text.Inlines!, heading.Inline);
        return text;
    }

    private Control RenderParagraph(ParagraphBlock paragraph)
    {
        // An image on its own line is a figure, not a run of text, so it is lifted out of the
        // paragraph rather than being squeezed into an inline.
        if (paragraph.Inline is { } inline &&
            inline.FirstChild is LinkInline { IsImage: true } image &&
            inline.FirstChild == inline.LastChild)
        {
            return RenderImage(image);
        }

        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextBrush"),
            FontSize = 13,
            LineHeight = 20,
        };

        AppendInlines(text.Inlines!, paragraph.Inline);
        return text;
    }

    /// <summary>
    /// A fenced code block: monospaced, scrollable, with copy and insert.
    /// </summary>
    /// <remarks>
    /// Code does not wrap. Wrapping a generated C# file at the panel width makes every line after
    /// the first look like a continuation, and a reader cannot tell a wrapped line from a real one.
    /// It scrolls instead.
    /// </remarks>
    private Control RenderCode(string code, string? language)
    {
        code = code.TrimEnd();

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(10, 6, 6, 0),
        };

        var languageLabel = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "code" : language.Trim(),
            FontFamily = Font("MonoFont"),
            FontSize = 10,
            Foreground = Brush("TextFaintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(languageLabel, 0);
        header.Children.Add(languageLabel);

        var copy = SmallButton("Copy");
        copy.Click += (_, _) => CodeCopyRequested?.Invoke(code);
        Grid.SetColumn(copy, 2);
        header.Children.Add(copy);

        var insert = SmallButton("Insert");
        insert.Click += (_, _) => CodeInsertRequested?.Invoke(code);
        Grid.SetColumn(insert, 3);
        header.Children.Add(insert);

        var body = new SelectableTextBlock
        {
            Text = code,
            FontFamily = Font("MonoFont"),
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = Brush("TextBrush"),
            Margin = new Thickness(12, 6, 12, 10),
        };

        var scroller = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            MaxHeight = 420,
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(scroller);

        return new Border
        {
            Child = stack,
            Background = Brush("PanelRaisedBrush"),
            BorderBrush = Brush("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2),
        };
    }

    private Control RenderList(ListBlock list)
    {
        var stack = new StackPanel { Spacing = 3, Margin = new Thickness(4, 2, 0, 2) };
        int index = list.IsOrdered ? int.TryParse(list.OrderedStart, out int start) ? start : 1 : 0;

        foreach (Block item in list)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*") };

            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{index++}." : "•",
                FontFamily = list.IsOrdered ? Font("MonoFont") : FontFamily.Default,
                FontSize = list.IsOrdered ? 11 : 13,
                Foreground = Brush("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 0),
            };

            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            var content = new StackPanel { Spacing = 4 };

            if (item is ContainerBlock container)
            {
                foreach (Block child in container)
                {
                    if (RenderBlock(child) is { } rendered)
                    {
                        content.Children.Add(rendered);
                    }
                }
            }

            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            stack.Children.Add(row);
        }

        return stack;
    }

    private Control RenderQuote(QuoteBlock quote)
    {
        var content = new StackPanel { Spacing = 6, Margin = new Thickness(12, 4, 0, 4) };

        foreach (Block child in quote)
        {
            if (RenderBlock(child) is { } rendered)
            {
                content.Children.Add(rendered);
            }
        }

        return new Border
        {
            Child = content,
            BorderBrush = Brush("AccentBrush"),
            BorderThickness = new Thickness(2, 0, 0, 0),
        };
    }

    private Control RenderTable(Table table)
    {
        var grid = new Grid();

        int columnCount = table.OfType<TableRow>().Max(r => r.Count);

        for (int i = 0; i < columnCount; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        int rowIndex = 0;

        foreach (TableRow row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            for (int column = 0; column < row.Count; column++)
            {
                var cellContent = new StackPanel { Spacing = 2 };

                if (row[column] is TableCell cell)
                {
                    foreach (Block child in cell)
                    {
                        if (RenderBlock(child) is { } rendered)
                        {
                            cellContent.Children.Add(rendered);
                        }
                    }
                }

                var border = new Border
                {
                    Child = cellContent,
                    Padding = new Thickness(9, 5),
                    BorderBrush = Brush("LineBrush"),
                    // Only bottom and right, so adjacent cells share one hairline rather than
                    // doubling it into a two-pixel rule.
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = row.IsHeader ? Brush("PanelRaisedBrush") : null,
                };

                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, column);
                grid.Children.Add(border);
            }

            rowIndex++;
        }

        return new Border
        {
            Child = new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            },
            BorderBrush = Brush("LineBrush"),
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 4),
            ClipToBounds = true,
        };
    }

    /// <summary>
    /// Renders an image reference.
    /// </summary>
    /// <remarks>
    /// Only local files are loaded. Fetching a remote image would mean the chat panel makes network
    /// requests to whatever URL a model produced, which is not a decision a Markdown renderer should
    /// be making on the user's behalf. Remote images show their address instead.
    /// </remarks>
    private Control RenderImage(LinkInline image)
    {
        string? url = image.Url;

        if (!string.IsNullOrWhiteSpace(url) && File.Exists(url))
        {
            try
            {
                return new Border
                {
                    Child = new Image
                    {
                        Source = new Bitmap(url),
                        Stretch = Stretch.Uniform,
                        MaxHeight = 320,
                        HorizontalAlignment = HorizontalAlignment.Left,
                    },
                    CornerRadius = new CornerRadius(6),
                    ClipToBounds = true,
                    Margin = new Thickness(0, 4),
                };
            }
            catch (Exception)
            {
                // Not a decodable image. Fall through to the text form.
            }
        }

        return new TextBlock
        {
            Text = $"[image] {url}",
            FontFamily = Font("MonoFont"),
            FontSize = 11,
            Foreground = Brush("TextFaintBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private void AppendInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null)
        {
            return;
        }

        foreach (Markdig.Syntax.Inlines.Inline inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case EmphasisInline emphasis:
                    var span = new Span();

                    // DelimiterCount of two is bold, one is italic - that is the whole distinction.
                    if (emphasis.DelimiterCount >= 2)
                    {
                        span.FontWeight = FontWeight.SemiBold;
                    }
                    else
                    {
                        span.FontStyle = FontStyle.Italic;
                    }

                    AppendInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;

                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = Font("MonoFont"),
                        FontSize = 12,
                        Foreground = Brush("AccentBrush"),
                    });
                    break;

                case LinkInline { IsImage: false } link:
                    var linkSpan = new Span { Foreground = Brush("AccentBrush") };
                    AppendInlines(linkSpan.Inlines, link);

                    if (linkSpan.Inlines.Count == 0 && link.Url is { Length: > 0 })
                    {
                        linkSpan.Inlines.Add(new Run(link.Url));
                    }

                    target.Add(linkSpan);
                    break;

                case LinkInline { IsImage: true } image:
                    target.Add(new Run($"[image] {image.Url}")
                    {
                        FontFamily = Font("MonoFont"),
                        FontSize = 11,
                        Foreground = Brush("TextFaintBrush"),
                    });
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case ContainerInline nested:
                    AppendInlines(target, nested);
                    break;

                default:
                    target.Add(new Run(inline.ToString() ?? string.Empty));
                    break;
            }
        }
    }

    private static Button SmallButton(string text) => new()
    {
        Content = text,
        FontSize = 10,
        Padding = new Thickness(8, 2),
        Margin = new Thickness(4, 0, 0, 0),
        Classes = { "ghost" },
    };

    /// <summary>
    /// Resolves a theme brush, falling back rather than returning null.
    /// </summary>
    /// <remarks>
    /// Assigning null to Foreground does not fall back to the inherited value - it paints nothing.
    /// A missing resource has to resolve to a visible colour or the text silently disappears.
    /// </remarks>
    private IBrush Brush(string key, IBrush? fallback = null) =>
        this.TryFindResource(key, out object? value) && value is IBrush brush
            ? brush
            : fallback ?? Foreground ?? Brushes.Gray;

    private FontFamily Font(string key) =>
        this.TryFindResource(key, out object? value) && value is FontFamily family ? family : new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");
}
