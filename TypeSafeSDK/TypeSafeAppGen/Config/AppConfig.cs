using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TypeSafeAppGen.Config;

/// <summary>Penyedia LLM yang didukung Jack.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LlmProvider>))]
public enum LlmProvider
{
    OpenAI,
    AzureOpenAI,
    Claude,
    Gemini,
    Ollama,
}

/// <summary>Pengaturan satu penyedia LLM: model aktif, daftar model pilihan, kunci, dan endpoint.</summary>
public sealed class ProviderProfile
{
    public string Model { get; set; } = "";
    public List<string> Models { get; set; } = [];
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "";

    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public ProviderProfile Clone() => new() { Model = Model, Models = [.. Models], ApiKey = ApiKey, Endpoint = Endpoint };
}

/// <summary>Seluruh konfigurasi TypeSafe App Generator, disimpan di <c>app.config.json</c>.</summary>
public sealed class AppConfig
{
    public const string DefaultSystemPrompt =
        "You are Jack — The Code Bender, the coding assistant inside TypeSafe App Generator, created by Gravicode Studios led by Kang Fadhil. " +
        "You turn a prompt into a complete, runnable .NET 10 application: UI and backend. " +
        "Work inside the open project using your workspace tools: inspect before you edit, write whole files, then build and fix every error until the build succeeds. " +
        "Desktop apps use Avalonia UI, web apps use Blazor Server. Follow standard C# naming, nullable enabled, sealed records by default. " +
        "Keep answers short: say what you changed and why, and put code the user should read in fenced code blocks with a language tag.";

    public LlmProvider ActiveProvider { get; set; } = LlmProvider.Ollama;
    public Dictionary<LlmProvider, ProviderProfile> Providers { get; set; } = CreateDefaultProviders();
    public double Temperature { get; set; } = 0.2;
    public int MaxOutputTokens { get; set; } = 16000;
    public int MaxToolRounds { get; set; } = 24;
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    public string TavilyApiKey { get; set; } = "";

    public bool ShowLineNumbers { get; set; } = true;
    public bool WordWrap { get; set; }
    public double EditorFontSize { get; set; } = 14;
    public bool AutoSave { get; set; }

    public double ExplorerWidth { get; set; } = 250;
    public double ChatPanelWidth { get; set; } = 400;
    public bool ChatPanelVisible { get; set; } = true;
    public bool ExplorerVisible { get; set; } = true;
    public double LogsPanelHeight { get; set; } = 200;
    public bool LogsPanelVisible { get; set; } = true;

    public string ProjectsFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TypeSafeProjects");
    public List<string> RecentProjects { get; set; } = [];

    [JsonIgnore]
    public ProviderProfile ActiveProfile => Profile(ActiveProvider);

    public ProviderProfile Profile(LlmProvider provider)
    {
        if (!Providers.TryGetValue(provider, out var profile))
        {
            profile = CreateDefaultProviders()[provider];
            Providers[provider] = profile;
        }
        return profile;
    }

    public void RememberProject(string path)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 8) RecentProjects.RemoveRange(8, RecentProjects.Count - 8);
    }

    public AppConfig Clone()
    {
        var json = JsonSerializer.Serialize(this, ConfigStore.JsonOptions);
        return JsonSerializer.Deserialize<AppConfig>(json, ConfigStore.JsonOptions)!;
    }

    public static Dictionary<LlmProvider, ProviderProfile> CreateDefaultProviders() => new()
    {
        [LlmProvider.OpenAI] = new() { Model = "gpt-5-mini", Models = ["gpt-5-mini", "gpt-5", "gpt-4.1"], Endpoint = "https://api.openai.com/v1" },
        [LlmProvider.AzureOpenAI] = new() { Model = "gpt-5-mini", Models = ["gpt-5-mini"], Endpoint = "https://<resource>.openai.azure.com/" },
        [LlmProvider.Claude] = new() { Model = "claude-opus-5", Models = ["claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5", "claude-fable-5-1"], Endpoint = "https://api.anthropic.com" },
        [LlmProvider.Gemini] = new() { Model = "gemini-2.5-flash", Models = ["gemini-2.5-flash", "gemini-2.5-pro"], Endpoint = "" },
        [LlmProvider.Ollama] = new() { Model = "llama3.2", Models = ["llama3.2", "qwen2.5-coder", "gpt-oss"], Endpoint = "http://localhost:11434" },
    };
}

/// <summary>Membaca dan menulis <c>app.config.json</c> di folder aplikasi.</summary>
public static class ConfigStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "app.config.json");

    public static async Task<AppConfig> LoadAsync(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AppConfig();
        try
        {
            var text = await File.ReadAllTextAsync(path);
            return Parse(text);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Berkas rusak tidak boleh membuat aplikasi gagal start; simpan cadangan lalu pakai default.
            try { File.Copy(path, path + ".bak", overwrite: true); } catch (IOException) { }
            return new AppConfig();
        }
    }

    public static async Task SaveAsync(AppConfig config, string? path = null)
    {
        path ??= DefaultPath;
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Menerima bentuk baru maupun bentuk lama (Provider/Model/ApiKey/Endpoint di level atas).</summary>
    internal static AppConfig Parse(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("app.config.json must contain a JSON object.");
        if (node.ContainsKey(nameof(AppConfig.Providers)))
            return Normalize(node.Deserialize<AppConfig>(JsonOptions) ?? new AppConfig());

        var config = new AppConfig();
        if (Enum.TryParse<LlmProvider>(node["Provider"]?.GetValue<string>(), ignoreCase: true, out var provider))
        {
            config.ActiveProvider = provider;
            var profile = config.Profile(provider);
            if (node["Model"]?.GetValue<string>() is { Length: > 0 } model)
            {
                profile.Model = model;
                if (!profile.Models.Contains(model)) profile.Models.Insert(0, model);
            }
            if (node["ApiKey"]?.GetValue<string>() is { Length: > 0 } key) profile.ApiKey = key;
            if (node["Endpoint"]?.GetValue<string>() is { Length: > 0 } endpoint) profile.Endpoint = endpoint;
        }
        if (node["Temperature"]?.GetValue<double>() is { } temperature) config.Temperature = temperature;
        if (node["SystemPrompt"]?.GetValue<string>() is { Length: > 0 } prompt) config.SystemPrompt = prompt;
        if (node["ShowLineNumbers"]?.GetValue<bool>() is { } lines) config.ShowLineNumbers = lines;
        if (node["ChatPanelWidth"]?.GetValue<double>() is { } width) config.ChatPanelWidth = width;
        return Normalize(config);
    }

    private static AppConfig Normalize(AppConfig config)
    {
        foreach (var (provider, defaults) in AppConfig.CreateDefaultProviders())
        {
            var profile = config.Profile(provider);
            if (profile.Models.Count == 0) profile.Models = defaults.Models;
            if (string.IsNullOrWhiteSpace(profile.Model)) profile.Model = profile.Models[0];
            if (!profile.Models.Contains(profile.Model)) profile.Models.Insert(0, profile.Model);
        }
        config.Temperature = Math.Clamp(config.Temperature, 0, 2);
        config.EditorFontSize = Math.Clamp(config.EditorFontSize, 9, 32);
        config.ChatPanelWidth = Math.Clamp(config.ChatPanelWidth, 300, 900);
        config.ExplorerWidth = Math.Clamp(config.ExplorerWidth, 160, 600);
        config.LogsPanelHeight = Math.Clamp(config.LogsPanelHeight, 90, 600);
        if (string.IsNullOrWhiteSpace(config.SystemPrompt)) config.SystemPrompt = AppConfig.DefaultSystemPrompt;
        return config;
    }
}
