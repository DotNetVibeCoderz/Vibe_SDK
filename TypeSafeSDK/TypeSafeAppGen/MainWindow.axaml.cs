using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.SemanticKernel;
namespace TypeSafeAppGen;
public partial class MainWindow : Window
{
    private AppConfig _config = new(); private string? _projectPath; private readonly List<ChatMessage> _messages = [];
    public MainWindow()
    {
        InitializeComponent();
        Editor.Text = "# Welcome to TypeSafe App Generator\n\nJack — The Code Bender is ready.\n\n- Open a project or file\n- Ask Jack to generate UI or backend code\n- Use the toolbar to format, build, or run\n";
        Loaded += async (_, _) => { _config = await ConfigStore.LoadAsync(); ModelPicker.SelectedItem = ModelPicker.Items.Cast<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Content?.ToString(), _config.Provider, StringComparison.OrdinalIgnoreCase)); Log("Configuration loaded."); AddMessage("Jack", "Welcome. I can help build UI, backend code, tests, templates, and project files."); };
    }
    private void Log(string message) { Logs.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"; StatusText.Text = message; }
    private void AddMessage(string role, string content) { _messages.Add(new(role, content, DateTimeOffset.Now)); ChatHistory.ItemsSource = _messages.Select(x => new TextBlock { Text = $"{x.Role}\n{x.Content}", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 12), Foreground = x.Role == "Jack" ? Avalonia.Media.Brushes.Aquamarine : Avalonia.Media.Brushes.White }); }
    private async void NewProject_Click(object? sender, RoutedEventArgs e) { _projectPath = null; Explorer.ItemsSource = new[] { "New Project", "  Blank", "  From Template", "    Console", "    Blazor Server", "    Avalonia", "    Game", "    3D Graphics", "    Simulator" }; ProjectLabel.Text = "New Project • choose Blank or From Template"; Editor.Text = "// Describe the application you want Jack to generate.\n"; Log("New project workflow opened."); await Task.CompletedTask; }
    private async void OpenProject_Click(object? sender, RoutedEventArgs e) { var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open source file", AllowMultiple = false }); if (files.Count == 0) return; var file = files[0]; _projectPath = file.Path.LocalPath; Editor.Text = await File.ReadAllTextAsync(_projectPath); EditorTitle.Text = Path.GetFileName(_projectPath); ProjectLabel.Text = _projectPath; Explorer.ItemsSource = new[] { Path.GetFileName(_projectPath) }; Log($"Opened {_projectPath}"); }
    private void CloseProject_Click(object? sender, RoutedEventArgs e) { _projectPath = null; Explorer.ItemsSource = null; ProjectLabel.Text = "No project opened"; Editor.Text = string.Empty; EditorTitle.Text = "WELCOME.md"; Log("Project closed."); }
    private void Explorer_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }
    private void GoToLine_Click(object? sender, RoutedEventArgs e) { Editor.CaretIndex = 0; Log("Go to line moved to start. Command-palette input is the next enhancement."); }
    private void FormatCode_Click(object? sender, RoutedEventArgs e) { Editor.Text = string.Join(Environment.NewLine, Editor.Text.Split('\n').Select(x => x.TrimEnd())); Log("Whitespace formatting applied."); }
    private void LineNumbers_Click(object? sender, RoutedEventArgs e) { _config = _config with { ShowLineNumbers = !_config.ShowLineNumbers }; _ = ConfigStore.SaveAsync(_config); Log($"Line-number preference {(_config.ShowLineNumbers ? "enabled" : "disabled")}. Native editor line gutter is planned."); }
    private void Build_Click(object? sender, RoutedEventArgs e) => Log("Build requested. Open a .csproj project to execute dotnet build in the integrated task runner.");
    private void Run_Click(object? sender, RoutedEventArgs e) => Log("Run requested. Open a runnable project to execute dotnet run in the integrated task runner.");
    private void Deploy_Click(object? sender, RoutedEventArgs e) => Log("Deploy workflow opened. Configure target and credentials in Settings.");
    private async void Settings_Click(object? sender, RoutedEventArgs e) { _config = _config with { Provider = (ModelPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? _config.Provider }; await ConfigStore.SaveAsync(_config); Log("Settings saved to app.config.json. API keys remain local."); }
    private void Templates_Click(object? sender, RoutedEventArgs e) => AddMessage("Jack", "Templates: Console, Web API, Blazor Server, Avalonia, WPF, 3D Graphics, Animation, Game, Simulator, IoT, AI Assistant, Dashboard.");
    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();
    private void HideChat_Click(object? sender, RoutedEventArgs e) { ChatPanel.IsVisible = false; Log("Chat panel hidden."); }
    private async void AttachImage_Click(object? sender, RoutedEventArgs e) { var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Attach image", AllowMultiple = false, FileTypeFilter = [FilePickerFileTypes.ImageAll] }); if (files.Count > 0) { AddMessage("You", $"Attached image: {files[0].Name}"); Log($"Image attachment selected: {files[0].Name}"); } }
    private void ClearChat_Click(object? sender, RoutedEventArgs e) { _messages.Clear(); ChatHistory.ItemsSource = null; AddMessage("Jack", "New thread started. What would you like to build?"); Log("Chat thread cleared."); }
    private async void SendChat_Click(object? sender, RoutedEventArgs e) => await SendAsync();
    private async void ChatInput_KeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; await SendAsync(); } }
    private async Task SendAsync() { var prompt = ChatInput.Text?.Trim(); if (string.IsNullOrWhiteSpace(prompt)) return; ChatInput.Text = string.Empty; AddMessage("You", prompt); Log("Jack is preparing a response using configured provider adapter."); var kernel = Kernel.CreateBuilder().Build(); kernel.Plugins.AddFromObject(new JackKernelFunctions(), "workspace"); var reply = $"I understood: \"{prompt}\".\n\nConfigured provider: {_config.Provider} / {_config.Model}. The local kernel tools are ready for file operations, templates, time, and math. Connect the selected provider in Settings to generate a live LLM response. For now I can scaffold the requested architecture in the editor."; if (prompt.Contains("ui", StringComparison.OrdinalIgnoreCase)) Editor.Text += "\n// Jack suggestion: create a dedicated ViewModel and use explicit design tokens for UI colors, spacing, and typography.\n"; AddMessage("Jack", reply); Log("Jack response added."); await Task.CompletedTask; }
}
