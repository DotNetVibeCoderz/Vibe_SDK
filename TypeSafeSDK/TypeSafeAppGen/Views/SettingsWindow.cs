using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using TypeSafeAppGen.Ai;
using TypeSafeAppGen.Config;

namespace TypeSafeAppGen.Views;

/// <summary>
/// Semua pengaturan di <c>app.config.json</c> bisa diubah dari sini: model per provider, perilaku Jack,
/// editor, dan tools. Perubahan diedit pada salinan dan baru diterapkan saat Save.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppConfig _draft;
    private readonly Dictionary<LlmProvider, (TextBox Models, ComboBox Model, TextBox Key, TextBox Endpoint)> _providerFields = [];
    private AppConfig? _saved;

    private SettingsWindow(AppConfig config, string initialSection)
    {
        _draft = config.Clone();
        Title = "Settings";
        Width = 860;
        Height = 640;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Brush("PatinaPanel");

        var tabs = new TabControl { TabStripPlacement = Dock.Left, Padding = new Thickness(24, 4, 8, 4) };
        tabs.Items.Add(Section("Models", BuildModels()));
        tabs.Items.Add(Section("Jack", BuildJack()));
        tabs.Items.Add(Section("Editor", BuildEditor()));
        tabs.Items.Add(Section("Tools", BuildTools()));
        tabs.Items.Add(Section("About", BuildAbout()));
        tabs.SelectedIndex = Math.Max(0, tabs.Items.OfType<TabItem>().ToList().FindIndex(t => Equals(t.Header, initialSection)));

        var save = new Button { Content = "Save settings", IsDefault = true }.WithClass("primary");
        var cancel = new Button { Content = "Cancel" }.WithClass("ghost");
        save.Click += (_, _) =>
        {
            Collect();
            _saved = _draft;
            Close();
        };
        cancel.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 12) };
        actions.Children.Add(new TextBlock { Text = $"Saved to {ConfigStore.DefaultPath}", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("Faint"), FontSize = 11, Margin = new Thickness(0, 0, 16, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 });
        actions.Children.Add(cancel);
        actions.Children.Add(save);

        var root = new DockPanel();
        var footer = new Border { BorderBrush = Ui.Brush("Rule"), BorderThickness = new Thickness(0, 1, 0, 0), Child = actions };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(tabs);
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    public static async Task<AppConfig?> ShowAsync(Window owner, AppConfig config, string section = "Models")
    {
        var window = new SettingsWindow(config, section);
        await window.ShowDialog(owner);
        return window._saved;
    }

    private static TabItem Section(string title, Control content) => new()
    {
        Header = title,
        FontSize = 14,
        Content = new ScrollViewer { Content = content, Padding = new Thickness(0, 8, 16, 16) },
    };

    private static StackPanel Field(string label, Control input, string? hint = null)
    {
        var panel = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(Ui.Eyebrow(label));
        panel.Children.Add(input);
        if (hint is not null) panel.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Foreground = Ui.Brush("Muted"), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static TextBlock Heading(string text, string description)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        block.Inlines!.Add(new Avalonia.Controls.Documents.Run(text) { FontSize = 18, FontWeight = FontWeight.SemiBold });
        block.Inlines.Add(new Avalonia.Controls.Documents.LineBreak());
        block.Inlines.Add(new Avalonia.Controls.Documents.Run(description) { Foreground = Ui.Brush("Muted"), FontSize = 12.5 });
        return block;
    }

    private Control BuildModels()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Models", "Jack can use any of these providers. The model picker at the top of the chat panel switches between them."));

        var active = new ComboBox { ItemsSource = Enum.GetValues<LlmProvider>().Select(LlmFactory.DisplayName).ToList(), SelectedIndex = (int)_draft.ActiveProvider, MinWidth = 220 };
        active.SelectionChanged += (_, _) => _draft.ActiveProvider = (LlmProvider)Math.Max(0, active.SelectedIndex);
        panel.Children.Add(Field("Active provider", active));

        var providerTabs = new TabControl { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var provider in Enum.GetValues<LlmProvider>())
        {
            var profile = _draft.Profile(provider);
            var models = new TextBox { Text = string.Join(", ", profile.Models) }.WithClass("field");
            var model = new ComboBox { ItemsSource = profile.Models.ToList(), SelectedItem = profile.Model, MinWidth = 260 };
            models.LostFocus += (_, _) =>
            {
                var list = ParseModels(models.Text);
                var selected = model.SelectedItem as string;
                model.ItemsSource = list;
                model.SelectedItem = list.Contains(selected ?? "") ? selected : list.FirstOrDefault();
            };
            var key = new TextBox { Text = profile.ApiKey, PasswordChar = '•', Watermark = LlmFactory.RequiresApiKey(provider) ? "Paste your API key" : "Not needed for a local Ollama" }.WithClass("field");
            var reveal = new CheckBox { Content = "Show key", Margin = new Thickness(0, 4, 0, 0) };
            reveal.IsCheckedChanged += (_, _) => key.PasswordChar = reveal.IsChecked == true ? '\0' : '•';
            var keyPanel = new StackPanel();
            keyPanel.Children.Add(key);
            keyPanel.Children.Add(reveal);
            var endpoint = new TextBox { Text = profile.Endpoint, Watermark = EndpointHint(provider) }.WithClass("field");
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var test = new Button { Content = "Test connection" }.WithClass("ghost");
            test.Click += async (_, _) =>
            {
                var candidate = new ProviderProfile { Models = ParseModels(models.Text), Model = model.SelectedItem as string ?? "", ApiKey = key.Text ?? "", Endpoint = endpoint.Text ?? "" };
                test.IsEnabled = false;
                status.Foreground = Ui.Brush("Muted");
                status.Text = $"Sending a one-word request to {candidate.Model}…";
                status.Text = await TestAsync(provider, candidate);
                status.Foreground = status.Text.StartsWith("Connected", StringComparison.Ordinal) ? Ui.Brush("Verdigris") : Ui.Brush("Ember");
                test.IsEnabled = true;
            };
            _providerFields[provider] = (models, model, key, endpoint);

            var testRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            testRow.Children.Add(test);
            testRow.Children.Add(status);
            var form = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            form.Children.Add(Field("Models in the picker", models, "Comma-separated. For Azure OpenAI these are deployment names."));
            form.Children.Add(Field("Default model", model));
            form.Children.Add(Field("API key", keyPanel, "Stored locally in app.config.json. Never shared with Jack's tools."));
            form.Children.Add(Field("Endpoint", endpoint, EndpointHint(provider)));
            form.Children.Add(testRow);
            providerTabs.Items.Add(new TabItem { Header = LlmFactory.DisplayName(provider), FontSize = 13, Content = form });
        }
        providerTabs.SelectedIndex = (int)_draft.ActiveProvider;
        panel.Children.Add(providerTabs);
        return panel;
    }

    private static string EndpointHint(LlmProvider provider) => provider switch
    {
        LlmProvider.OpenAI => "https://api.openai.com/v1 — or any OpenAI-compatible URL (DeepSeek, Groq, LM Studio).",
        LlmProvider.AzureOpenAI => "https://<resource>.openai.azure.com/",
        LlmProvider.Claude => "https://api.anthropic.com (leave as is unless you use a gateway).",
        LlmProvider.Gemini => "Not used; Gemini is reached through Google AI Studio with your key.",
        LlmProvider.Ollama => "http://localhost:11434",
        _ => "",
    };

    private async Task<string> TestAsync(LlmProvider provider, ProviderProfile profile)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var kernel = LlmFactory.CreateBuilder(provider, profile, http).Build();
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory();
            history.AddUserMessage("Reply with the single word OK.");
            var settings = LlmFactory.CreateSettings(provider, profile.Model, _draft);
            settings.FunctionChoiceBehavior = null;
            var reply = await chat.GetChatMessageContentAsync(history, settings, kernel);
            return $"Connected — {profile.Model} replied “{(reply.Content ?? "").Trim()}”.";
        }
        catch (Exception ex)
        {
            var probe = _draft.Clone();
            probe.ActiveProvider = provider;
            probe.Providers[provider] = profile;
            return JackAgent.DescribeError(ex, probe);
        }
    }

    private Control BuildJack()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Jack — The Code Bender", "How Jack thinks and how far it may go on its own."));

        var temperatureValue = new TextBlock { Width = 40, VerticalAlignment = VerticalAlignment.Center };
        var temperature = new Slider { Minimum = 0, Maximum = 1.5, Value = _draft.Temperature, TickFrequency = 0.05, IsSnapToTickEnabled = true, Width = 280 };
        temperature.ValueChanged += (_, e) =>
        {
            _draft.Temperature = Math.Round(e.NewValue, 2);
            temperatureValue.Text = _draft.Temperature.ToString("0.00");
        };
        temperatureValue.Text = _draft.Temperature.ToString("0.00");
        var temperatureRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        temperatureRow.Children.Add(temperature);
        temperatureRow.Children.Add(temperatureValue);
        panel.Children.Add(Field("Temperature", temperatureRow, "Lower is more predictable code. Reasoning models (gpt-5, Claude Opus 5, Sonnet 5) ignore this setting."));

        var maxTokens = new NumericUpDown { Minimum = 1024, Maximum = 128000, Increment = 1024, Value = _draft.MaxOutputTokens, Width = 180, FormatString = "0" };
        maxTokens.ValueChanged += (_, e) => _draft.MaxOutputTokens = (int)(e.NewValue ?? 16000);
        panel.Children.Add(Field("Max output tokens", maxTokens, "Upper bound for one reply (Claude and Gemini)."));

        var rounds = new NumericUpDown { Minimum = 1, Maximum = 100, Value = _draft.MaxToolRounds, Width = 180, FormatString = "0" };
        rounds.ValueChanged += (_, e) => _draft.MaxToolRounds = (int)(e.NewValue ?? 24);
        panel.Children.Add(Field("Max tool rounds per message", rounds, "How many rounds of file edits, builds, and searches Jack may run before it must report back."));

        var prompt = new TextBox { Text = _draft.SystemPrompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 170 }.WithClass("field");
        prompt.TextChanged += (_, _) => _draft.SystemPrompt = prompt.Text ?? "";
        var reset = new Button { Content = "Restore default prompt", Margin = new Thickness(0, 6, 0, 0) }.WithClass("ghost");
        reset.Click += (_, _) => prompt.Text = AppConfig.DefaultSystemPrompt;
        var promptPanel = new StackPanel();
        promptPanel.Children.Add(prompt);
        promptPanel.Children.Add(reset);
        panel.Children.Add(Field("System prompt", promptPanel, "Jack's personality and rules. Tool instructions are added automatically."));
        return panel;
    }

    private Control BuildEditor()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Editor", "Typing and display preferences."));
        var fontSize = new NumericUpDown { Minimum = 9, Maximum = 32, Value = (decimal)_draft.EditorFontSize, Width = 140, FormatString = "0" };
        fontSize.ValueChanged += (_, e) => _draft.EditorFontSize = (double)(e.NewValue ?? 14);
        panel.Children.Add(Field("Font size", fontSize));
        panel.Children.Add(Check("Show line numbers", _draft.ShowLineNumbers, v => _draft.ShowLineNumbers = v));
        panel.Children.Add(Check("Wrap long lines", _draft.WordWrap, v => _draft.WordWrap = v));
        panel.Children.Add(Check("Save files automatically when switching tabs or building", _draft.AutoSave, v => _draft.AutoSave = v));
        return panel;
    }

    private Control BuildTools()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Tools", "Services Jack can call and where new projects go."));
        var tavily = new TextBox { Text = _draft.TavilyApiKey, PasswordChar = '•', Watermark = "tvly-…" }.WithClass("field");
        tavily.TextChanged += (_, _) => _draft.TavilyApiKey = tavily.Text ?? "";
        panel.Children.Add(Field("Tavily API key", tavily, "Enables Jack's internet search. Get a key at tavily.com. Page scraping works without it."));

        var folder = new TextBox { Text = _draft.ProjectsFolder }.WithClass("field");
        folder.TextChanged += (_, _) => _draft.ProjectsFolder = folder.Text ?? "";
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) }.WithClass("ghost");
        browse.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Default folder for new projects" });
            if (picked.Count > 0) folder.Text = picked[0].Path.LocalPath;
        };
        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(folder);
        panel.Children.Add(Field("Projects folder", row, "New projects, including ones Jack creates, are placed here."));
        return panel;
    }

    private static Control BuildAbout()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Heading("TypeSafe App Generator", "A code editor with Jack — The Code Bender, an assistant that writes, builds, and fixes .NET apps from a prompt."));
        panel.Children.Add(new TextBlock { Text = "Made by Gravicode Studios, led by Kang Fadhil.", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Dibuat oleh Gravicode Studios, dipimpin Kang Fadhil." }.WithClass("muted"));
        panel.Children.Add(new TextBlock { Text = $"Configuration file: {ConfigStore.DefaultPath}", TextWrapping = TextWrapping.Wrap, FontSize = 12 }.WithClass("mono"));
        panel.Children.Add(new TextBlock { Text = $"Version {typeof(SettingsWindow).Assembly.GetName().Version} · .NET {Environment.Version}", FontSize = 12 }.WithClass("muted"));
        return panel;
    }

    private static CheckBox Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 0, 0, 8) };
        box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);
        return box;
    }

    private static List<string> ParseModels(string? text) =>
        (text ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    private void Collect()
    {
        foreach (var (provider, fields) in _providerFields)
        {
            var profile = _draft.Profile(provider);
            var models = ParseModels(fields.Models.Text);
            var selected = fields.Model.SelectedItem as string;
            if (models.Count == 0 && !string.IsNullOrWhiteSpace(selected)) models.Add(selected);
            profile.Models = models;
            profile.Model = selected is not null && models.Contains(selected) ? selected : models.FirstOrDefault() ?? "";
            profile.ApiKey = fields.Key.Text?.Trim() ?? "";
            profile.Endpoint = fields.Endpoint.Text?.Trim() ?? "";
        }
    }
}
