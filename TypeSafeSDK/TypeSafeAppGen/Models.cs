using System.Text.Json;
namespace TypeSafeAppGen;
public sealed record AppConfig(string Provider="Ollama", string Model="llama3.2", string ApiKey="", string Endpoint="http://localhost:11434", double Temperature=0.2, string SystemPrompt="", bool ShowLineNumbers=true, double ChatPanelWidth=360);
public sealed record ChatMessage(string Role, string Content, DateTimeOffset Timestamp);
public sealed record TemplateDefinition(string Name, string Category, string Description);
public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "app.config.json");
    public static async Task<AppConfig> LoadAsync() => File.Exists(Path) ? await JsonSerializer.DeserializeAsync<AppConfig>(File.OpenRead(Path)) ?? new() : new();
    public static Task SaveAsync(AppConfig config) => File.WriteAllTextAsync(Path, JsonSerializer.Serialize(config, JsonOptions));
}
