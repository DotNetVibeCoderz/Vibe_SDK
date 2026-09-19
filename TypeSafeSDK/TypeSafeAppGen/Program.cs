using System.Text.Json;

var configPath = Path.Combine(AppContext.BaseDirectory, "app.config.json");
var config = File.Exists(configPath) ? await JsonSerializer.DeserializeAsync<GeneratorConfig>(File.OpenRead(configPath)) : new GeneratorConfig();
Console.WriteLine("TypeSafe App Generator — Jack, The Code Bender");
Console.WriteLine($"Provider: {config?.Provider ?? "Ollama"} | Model: {config?.Model ?? "local"}");
Console.WriteLine("Scaffold command: dotnet typesafe new --template console|blazor|avalonia|api");
record GeneratorConfig(string Provider = "Ollama", string Model = "local", string Endpoint = "http://localhost:11434", double Temperature = 0.2, string SystemPrompt = "You are Jack, The Code Bender.");
