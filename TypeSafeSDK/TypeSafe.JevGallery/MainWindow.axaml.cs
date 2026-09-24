using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using TypeSafeSdk;

namespace TypeSafe.JevGallery;

public partial class MainWindow : Window
{
    private readonly GalleryConfig _config;
    private readonly TypeSafeClient _client;
    private readonly Dictionary<SpecimenDomain, Button> _domainButtons = [];
    private readonly Dictionary<string, Border> _specimenRows = [];
    private SpecimenDomain? _filter;
    private Specimen _current = SpecimenCatalog.All[0];
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _config = GalleryConfig.Load();
        _client = new TypeSafeClient(new TypeSafeOptions
        {
            ApiKey = _config.ResolveApiKey(),
            Endpoint = _config.Endpoint,
            DefaultModel = _config.DefaultModel,
            Simulator = _config.UseSimulator
        });

        MotionToggle.IsChecked = _config.Motion;
        ModeText.Text = _config.UseSimulator ? "SIMULATOR" : "LIVE API";
        ModeDot.Background = Brush("Moss");
        if (!_config.UseSimulator) ModeDot.Background = Brush("Brass");

        RunButton.Click += async (_, _) => await RunAsync();
        ResetButton.Click += (_, _) => { InputBox.Text = _current.SampleInput; Status("Input restored to the catalogue sample."); };
        CopyButton.Click += async (_, _) => await CopyCodeAsync();

        ModeNote.Text = _config.UseSimulator
            ? "Simulator mode answers locally by matching criteria text — it weights rare words, honours negation, and abstains with a flat distribution when it finds no signal. The same code against the live API reasons semantically instead."
            : $"Live mode calls {_config.Endpoint} with the key from app.config.json, TYPESAFE_API_KEY, or TYPESAFE_API_KEY_FILE. Every answer below is a real API response.";

        BuildDomainRail();
        BuildSpecimenList();
        Select(SpecimenCatalog.All[0]);
        Status($"{SpecimenCatalog.All.Count} specimens loaded. Pick one and run it.");
    }

    // ── Rail ───────────────────────────────────────────────────────────────────

    private void BuildDomainRail()
    {
        DomainList.Children.Add(DomainButton(null, "All specimens", SpecimenCatalog.All.Count));
        foreach (var domain in Enum.GetValues<SpecimenDomain>())
            DomainList.Children.Add(DomainButton(domain, domain.ToString(), SpecimenCatalog.Counts.GetValueOrDefault(domain)));
    }

    private Button DomainButton(SpecimenDomain? domain, string label, int count)
    {
        var marker = new Border { Width = 2, Background = Brushes.Transparent, Margin = new Thickness(0, 2, 10, 2) };
        var text = new TextBlock { Text = label, FontFamily = Font("Body"), FontSize = 13.5, Foreground = Brush("Ash"), VerticalAlignment = VerticalAlignment.Center };
        var badge = new TextBlock { Text = count.ToString("00"), FontFamily = Font("Mono"), FontSize = 11, Foreground = Brush("Ash"), Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), HorizontalAlignment = HorizontalAlignment.Stretch };
        layout.Children.Add(marker);
        Grid.SetColumn(text, 1); layout.Children.Add(text);
        Grid.SetColumn(badge, 2); layout.Children.Add(badge);

        var button = new Button
        {
            Content = layout, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 7), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = new(Avalonia.Input.StandardCursorType.Hand)
        };
        button.Click += (_, _) => { _filter = domain; BuildSpecimenList(); PaintDomainRail(); };
        button.Tag = (marker, text);
        if (domain is not null) _domainButtons[domain.Value] = button; else _domainButtons[(SpecimenDomain)(-1)] = button;
        return button;
    }

    private void PaintDomainRail()
    {
        foreach (var (domain, button) in _domainButtons)
        {
            var active = _filter is null ? (int)domain == -1 : domain == _filter;
            var (marker, text) = ((Border, TextBlock))button.Tag!;
            marker.Background = active ? Brush("Brass") : Brushes.Transparent;
            text.Foreground = active ? Brush("Bone") : Brush("Ash");
        }
    }

    // ── Catalogue list ─────────────────────────────────────────────────────────

    private void BuildSpecimenList()
    {
        SpecimenList.Children.Clear();
        _specimenRows.Clear();
        var visible = SpecimenCatalog.All.Where(s => _filter is null || s.Domain == _filter).ToList();
        SpecimenCount.Text = visible.Count.ToString("00");

        foreach (var specimen in visible)
        {
            var accession = new TextBlock { Text = specimen.Accession, FontFamily = Font("Mono"), FontSize = 10.5, Foreground = Brush("Rule"), LetterSpacing = 1 };
            var title = new TextBlock { Text = specimen.Title, FontFamily = Font("Body"), FontSize = 14, Foreground = Brush("Ash"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };

            var row = new Border
            {
                Padding = new Thickness(14, 12, 12, 13),
                CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(2, 0, 0, 0),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Cursor = new(Avalonia.Input.StandardCursorType.Hand),
                Child = new StackPanel { Children = { accession, title } }
            };
            row.Tag = (specimen, accession, title);
            row.PointerPressed += (_, _) => Select(specimen);
            _specimenRows[specimen.Accession] = row;
            SpecimenList.Children.Add(row);
        }
        PaintSpecimenList();
        PaintDomainRail();
    }

    private void PaintSpecimenList()
    {
        foreach (var (accession, row) in _specimenRows)
        {
            var active = accession == _current.Accession;
            var (_, number, title) = ((Specimen, TextBlock, TextBlock))row.Tag!;
            row.Background = active ? Brush("Casing") : Brushes.Transparent;
            row.BorderBrush = active ? Brush("Brass") : Brushes.Transparent;
            number.Foreground = active ? Brush("Brass") : Brush("Rule");
            title.Foreground = active ? Brush("Bone") : Brush("Ash");
        }
    }

    // ── Detail ─────────────────────────────────────────────────────────────────

    private void Select(Specimen specimen)
    {
        _current = specimen;
        DetailAccession.Text = specimen.Accession;
        DetailDomain.Text = specimen.Domain.ToString().ToUpperInvariant();
        DetailTitle.Text = specimen.Title;
        DetailBlurb.Text = specimen.Blurb;
        InputBox.Text = specimen.SampleInput;
        CodeBlock.Text = specimen.Code;
        RunNote.Text = $"{specimen.Questions.Count} QUESTION{(specimen.Questions.Count == 1 ? "" : "S")} · {_config.DefaultModel.ToUpperInvariant()}";
        ResultPanel.Children.Clear();
        ResultPanel.Children.Add(Placeholder("Run the specimen to see the answer and its probability distribution."));
        PaintSpecimenList();
    }

    private async Task RunAsync()
    {
        if (_busy) return;
        _busy = true;
        RunButton.IsEnabled = false;
        RunButton.Content = "RUNNING…";
        Status($"Calling System One for {_current.Accession}…");
        try
        {
            var state = _current.State(InputBox.Text ?? "");
            var response = await _client.SystemOneAsync(state, new Dictionary<string, object>(_current.Questions));
            Render(response);
            Status($"{_current.Accession} answered {response.Answers.Count} question(s) · model {response.Model}"
                   + (response.Usage is { } usage ? $" · {usage.TotalTokens} tokens" : ""));
        }
        catch (TypeSafeApiException ex)
        {
            ShowError($"TypeSafe API error {ex.StatusCode}", ex.Message + (ex.RequestId is null ? "" : $"\nrequest_id: {ex.RequestId}"));
            Status($"API error {ex.StatusCode} on {_current.Accession}.");
        }
        catch (TypeSafeException ex)
        {
            ShowError("TypeSafe SDK error", ex.Message);
            Status($"SDK error on {_current.Accession}.");
        }
        finally
        {
            _busy = false;
            RunButton.IsEnabled = true;
            RunButton.Content = "RUN SPECIMEN";
        }
    }

    private void Render(SystemOneResponse response)
    {
        ResultPanel.Children.Clear();
        if (response.Answers.Count == 0) { ResultPanel.Children.Add(Placeholder("The API returned no answers for these questions.")); return; }

        foreach (var (name, answer) in response.Answers)
        {
            switch (answer)
            {
                case ChoiceAnswer choice:
                    ResultPanel.Children.Add(Readout(name, choice.Choice, $"CONFIDENCE {choice.Confidence:0.00}",
                        Segments(choice.Probabilities, choice.Choice)));
                    break;
                case NoulAnswer noul:
                    ResultPanel.Children.Add(Readout(name, noul.Noul.ToString("0.00", CultureInfo.InvariantCulture),
                        noul.IsTrue ? "READS TRUE AT 0.50" : "READS FALSE AT 0.50",
                        Segments(new Dictionary<string, double> { ["true"] = noul.Noul, ["false"] = 1 - noul.Noul }, noul.IsTrue ? "true" : "false")));
                    break;
                case ScoreAnswer score:
                    var labelled = score.Probabilities.ToDictionary(
                        pair => score.Legend.TryGetValue(pair.Key, out var legend) ? legend : pair.Key, pair => pair.Value);
                    ResultPanel.Children.Add(Readout(name, $"{score.Score:0.00}",
                        score.NearestLabel is null ? $"CONFIDENCE {score.Confidence:0.00}" : $"{score.NearestLabel.ToUpperInvariant()} · CONFIDENCE {score.Confidence:0.00}",
                        Segments(labelled, score.NearestLabel)));
                    break;
            }
        }
    }

    /// <summary>Satu readout: nama pertanyaan, jawaban besar, catatan, dan distribution strip.</summary>
    private Control Readout(string question, string answer, string note, Control strip)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = question.ToUpperInvariant(), FontFamily = Font("Mono"), FontSize = 10.5, LetterSpacing = 1.8, Foreground = Brush("Ash") });
        left.Children.Add(new TextBlock { Text = answer, FontFamily = Font("Display"), FontSize = 34, FontWeight = FontWeight.SemiBold, Foreground = Brush("Bone"), Margin = new Thickness(0, 2, 0, 0) });
        header.Children.Add(left);

        var noteText = new TextBlock { Text = note, FontFamily = Font("Mono"), FontSize = 10.5, LetterSpacing = 1.4, Foreground = Brush("Brass"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
        Grid.SetColumn(noteText, 1);
        header.Children.Add(noteText);

        return new StackPanel { Spacing = 12, Children = { header, strip } };
    }

    /// <summary>
    /// Signature galeri: satu pita horizontal yang lebar tiap segmennya adalah massa probabilitas.
    /// Segmen terpilih memakai moss, sisanya rust redup, sehingga kasus ambigu terlihat langsung.
    /// </summary>
    private Control Segments(IReadOnlyDictionary<string, double> probabilities, string? winner)
    {
        var ordered = probabilities.Where(pair => pair.Value > 0.0005).OrderByDescending(pair => pair.Value).Take(8).ToList();
        if (ordered.Count == 0) return new TextBlock { Text = "no distribution reported", FontFamily = Font("Mono"), FontSize = 11, Foreground = Brush("Rule") };

        var strip = new Grid { Height = 34, ColumnDefinitions = [] };
        var legend = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var animate = MotionToggle.IsChecked == true;
        var bars = new List<Border>();

        for (var index = 0; index < ordered.Count; index++)
        {
            var (label, probability) = (ordered[index].Key, ordered[index].Value);
            var isWinner = string.Equals(label, winner, StringComparison.OrdinalIgnoreCase);
            strip.ColumnDefinitions.Add(new ColumnDefinition(probability, GridUnitType.Star));

            var bar = new Border
            {
                Background = isWinner ? Brush("Moss") : Brush("Rust"),
                Opacity = isWinner ? 1 : 0.34,
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(index == 0 ? 0 : 2, 0, 0, 0),
                RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative)
            };
            if (animate)
            {
                bar.RenderTransform = TransformOperations.Parse("scaleX(0.001)");
                bar.Transitions = [new TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(420),
                    Delay = TimeSpan.FromMilliseconds(index * 55),
                    Easing = new CubicEaseOut()
                }];
                bars.Add(bar);
            }
            Grid.SetColumn(bar, index);
            strip.Children.Add(bar);

            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 0, 20, 6) };
            chip.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Background = isWinner ? Brush("Moss") : Brush("Rust"), Opacity = isWinner ? 1 : 0.34 });
            chip.Children.Add(new TextBlock { Text = label, FontFamily = Font("Body"), FontSize = 12.5, Foreground = isWinner ? Brush("Bone") : Brush("Ash"), VerticalAlignment = VerticalAlignment.Center });
            chip.Children.Add(new TextBlock { Text = probability.ToString("0.00", CultureInfo.InvariantCulture), FontFamily = Font("Mono"), FontSize = 11.5, Foreground = Brush("Rule"), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(chip);
        }

        if (animate)
            Dispatcher.UIThread.Post(() => { foreach (var bar in bars) bar.RenderTransform = TransformOperations.Parse("scaleX(1)"); }, DispatcherPriority.Background);

        return new StackPanel { Children = { strip, legend } };
    }

    private void ShowError(string title, string detail)
    {
        ResultPanel.Children.Clear();
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontFamily = Font("Mono"), FontSize = 11, LetterSpacing = 1.6, Foreground = Brush("Rust") });
        panel.Children.Add(new TextBlock { Text = detail, FontFamily = Font("Body"), FontSize = 13, Foreground = Brush("Ash"), TextWrapping = TextWrapping.Wrap });
        ResultPanel.Children.Add(new Border
        {
            Background = Brush("Recess"), BorderBrush = Brush("Rust"), BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(18, 16), Child = panel
        });
    }

    private Control Placeholder(string text) => new TextBlock
    {
        Text = text, FontFamily = Font("Body"), FontSize = 13, Foreground = Brush("Rule"), TextWrapping = TextWrapping.Wrap, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left
    };

    private async Task CopyCodeAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) { Status("Clipboard is unavailable on this platform."); return; }
        await clipboard.SetTextAsync(_current.Code);
        Status($"Sample code for {_current.Accession} copied.");
    }

    private void Status(string message) => StatusText.Text = message.ToUpperInvariant();

    private IBrush Brush(string key) => (IBrush)Application.Current!.Resources[key]!;
    private FontFamily Font(string key) => (FontFamily)Application.Current!.Resources[key]!;
}

/// <summary>Konfigurasi galeri, dibaca dari <c>app.config.json</c> di samping executable.</summary>
public sealed record GalleryConfig(
    string ApiKey = "",
    string Endpoint = TypeSafeConstants.DefaultBaseUrl,
    string DefaultModel = TypeSafeConstants.DefaultModel,
    bool UseSimulator = true,
    bool Motion = true)
{
    /// <summary>
    /// Mencari API key tanpa pernah menyimpannya di repo: konfigurasi lebih dulu, lalu
    /// <c>TYPESAFE_API_KEY</c>, lalu file yang ditunjuk <c>TYPESAFE_API_KEY_FILE</c>.
    /// </summary>
    public string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey)) return ApiKey;
        var fromEnvironment = Environment.GetEnvironmentVariable(TypeSafeConstants.ApiKeyEnv);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;
        var file = Environment.GetEnvironmentVariable(TypeSafeConstants.ApiKeyFileEnv);
        if (string.IsNullOrWhiteSpace(file)) return "";
        try { return TypeSafeApiKeyLoader.LoadFromFile(file); }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { return ""; }
    }

    /// <summary>Memuat konfigurasi; jatuh ke nilai default bila file tidak ada atau rusak.</summary>
    public static GalleryConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "app.config.json");
        if (!File.Exists(path)) return new GalleryConfig();
        try { return JsonSerializer.Deserialize<GalleryConfig>(File.ReadAllText(path)) ?? new GalleryConfig(); }
        catch (JsonException) { return new GalleryConfig(); }
    }
}
