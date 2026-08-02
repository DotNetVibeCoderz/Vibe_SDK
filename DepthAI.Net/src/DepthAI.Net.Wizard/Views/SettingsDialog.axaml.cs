using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DepthAI.Wizard.Ai;

namespace DepthAI.Wizard.App.Views;

/// <summary>Dialog pengaturan asisten; menulis ke app.config saat disimpan.</summary>
public partial class SettingsDialog : Window
{
    public SettingsDialog() : this(AssistantSettings.Load()) { }

    public SettingsDialog(AssistantSettings settings)
    {
        InitializeComponent();

        ProviderBox.ItemsSource = Enum.GetValues<AiProvider>();
        ProviderBox.SelectedItem = settings.Provider;

        ModelBox.ItemsSource = AssistantSettings.ModelsFor(settings.Provider);
        ModelBox.SelectedItem = settings.Model;

        EndpointBox.Text = settings.Endpoint;
        ApiKeyBox.Text = settings.ApiKey;
        TavilyBox.Text = settings.TavilyApiKey;
        TemperatureSlider.Value = settings.Temperature;
        MaxTokensBox.Text = settings.MaxTokens.ToString(CultureInfo.InvariantCulture);
        HistoryBox.Text = settings.HistoryWindow.ToString(CultureInfo.InvariantCulture);
        FunctionsBox.IsChecked = settings.EnableFunctionCalling;
        SystemPromptBox.Text = settings.SystemPrompt;

        UpdateTemperatureText();
        UpdateKeySource();
    }

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedItem is not AiProvider provider || ModelBox is null)
        {
            return;
        }

        var models = AssistantSettings.ModelsFor(provider);
        ModelBox.ItemsSource = models;
        ModelBox.SelectedIndex = 0;

        UpdateKeySource();
    }

    private void OnTemperatureChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty)
        {
            UpdateTemperatureText();
        }
    }

    private void UpdateTemperatureText()
    {
        if (TemperatureText is not null)
        {
            TemperatureText.Text = TemperatureSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Memberi tahu apakah kunci berasal dari variabel lingkungan, sehingga pengguna
    /// tahu kenapa kolomnya sudah terisi dan kenapa nilainya tidak ditulis ke app.config.
    /// </summary>
    private void UpdateKeySource()
    {
        if (ProviderBox.SelectedItem is not AiProvider provider || KeySourceText is null)
        {
            return;
        }

        var variable = AssistantSettings.EnvironmentVariableFor(provider);
        var fromEnvironment = Environment.GetEnvironmentVariable(variable);

        KeySourceText.Text = string.IsNullOrWhiteSpace(fromEnvironment)
            ? $"belum ada {variable}"
            : $"diambil dari {variable}";

        KeySourceText.Foreground = string.IsNullOrWhiteSpace(fromEnvironment)
            ? App.Resource("InkFaint")
            : App.Resource("AccentMid");
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        var provider = ProviderBox.SelectedItem as AiProvider? ?? AiProvider.OpenAI;
        var model = (ModelBox.SelectedItem as string) ?? ModelBox.Text ?? AssistantSettings.ModelsFor(provider)[0];

        var settings = new AssistantSettings
        {
            Provider = provider,
            Model = model,
            ApiKey = ApiKeyBox.Text ?? string.Empty,
            Endpoint = string.IsNullOrWhiteSpace(EndpointBox.Text) ? null : EndpointBox.Text,
            TavilyApiKey = TavilyBox.Text ?? string.Empty,
            Temperature = TemperatureSlider.Value,
            MaxTokens = ParseOr(MaxTokensBox.Text, 4096),
            HistoryWindow = ParseOr(HistoryBox.Text, 20),
            EnableFunctionCalling = FunctionsBox.IsChecked ?? true,
            SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text)
                ? AssistantSettings.DefaultSystemPrompt
                : SystemPromptBox.Text,
        };

        if (!settings.IsConfigured)
        {
            StatusText.Text = settings.MissingConfiguration;
            StatusText.Foreground = App.Resource("SignalWarning");
            return;
        }

        try
        {
            await settings.SaveAsync();
            Close(settings);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Tidak bisa menyimpan app.config: {ex.Message}";
            StatusText.Foreground = App.Resource("SignalError");
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private static int ParseOr(string? text, int fallback)
        => int.TryParse(text, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;
}
