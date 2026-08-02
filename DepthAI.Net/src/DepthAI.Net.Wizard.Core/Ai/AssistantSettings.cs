using System.Globalization;
using System.Xml.Linq;

namespace DepthAI.Wizard.Ai;

/// <summary>Penyedia model yang didukung asisten.</summary>
public enum AiProvider
{
    OpenAI,
    Anthropic,
    Gemini,
    Ollama,
}

/// <summary>
/// Konfigurasi asisten, dibaca dari <c>app.config</c>.
/// </summary>
/// <remarks>
/// Kunci API dibaca dari variabel lingkungan lebih dulu dan hanya jatuh ke app.config
/// bila variabelnya kosong. Berkas konfigurasi ikut terbawa saat proyek dibagikan atau
/// di-commit; variabel lingkungan tidak.
/// </remarks>
public sealed record AssistantSettings
{
    /// <summary>Nama berkas konfigurasi yang dicari di samping aplikasi.</summary>
    public const string ConfigFileName = "app.config";

    public AiProvider Provider { get; init; } = AiProvider.OpenAI;

    /// <summary>Id model, misalnya <c>gpt-4o</c> atau <c>claude-opus-5</c>.</summary>
    public string Model { get; init; } = "gpt-4o";

    /// <summary>Kunci API; kosong berarti belum dikonfigurasi.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Endpoint kustom; dipakai Ollama dan deployment yang self-hosted.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Persona asisten.</summary>
    public string SystemPrompt { get; init; } = DefaultSystemPrompt;

    /// <summary>0 = deterministik, 1 = kreatif.</summary>
    public double Temperature { get; init; } = 0.3;

    /// <summary>Batas token keluaran per balasan.</summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>Jumlah pesan terakhir yang dikirim ulang sebagai konteks percakapan.</summary>
    public int HistoryWindow { get; init; } = 20;

    /// <summary>Kunci API Tavily untuk fungsi pencarian internet.</summary>
    public string TavilyApiKey { get; init; } = string.Empty;

    /// <summary>Mengizinkan asisten memanggil kernel function secara otomatis.</summary>
    public bool EnableFunctionCalling { get; init; } = true;

    /// <summary>True bila penyedia yang dipilih sudah punya kredensial yang dibutuhkan.</summary>
    public bool IsConfigured => Provider == AiProvider.Ollama || !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Penjelasan singkat apa yang masih kurang; null bila sudah siap.</summary>
    public string? MissingConfiguration => IsConfigured
        ? null
        : $"Kunci API untuk {Provider} belum diisi. Set variabel lingkungan "
            + $"{EnvironmentVariableFor(Provider)} atau isi app.config.";

    public static string DefaultSystemPrompt =>
        """
        Kamu adalah Jack The Code Bender, asisten koding di dalam Computer Vision App Wizard
        milik DepthAI.Net — SDK .NET untuk kamera OAK dari Luxonis.

        Tugasmu: membantu developer membangun aplikasi computer vision dengan C# dan .NET 10.

        Pedoman:
        - Tulis kode C# modern (.NET 10): file-scoped namespace, nullable aktif, async/await.
        - Pakai API DepthAI.Net: Pipeline.CreateBuilder(), DepthAiDevice.OpenAsync(),
          device.GetStream<ImageFrame>(...), DetectionFrame, DepthFrame.
        - Frame itu dipooling. Kalau frame perlu hidup lebih lama dari callback, panggil Clone().
        - Kalau kamu butuh detail API, panggil fungsi yang tersedia — jangan menebak nama tipe.
        - Jawab dalam bahasa yang dipakai pengguna (Indonesia atau Inggris).
        - Jelaskan keputusan desain secara singkat, jangan bertele-tele.
        - Kalau permintaan ambigu, ajukan satu pertanyaan penajam, lalu kerjakan.
        """;

    /// <summary>Nama variabel lingkungan yang menyimpan kunci API tiap penyedia.</summary>
    public static string EnvironmentVariableFor(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "OPENAI_API_KEY",
        AiProvider.Anthropic => "ANTHROPIC_API_KEY",
        AiProvider.Gemini => "GEMINI_API_KEY",
        _ => "OLLAMA_API_KEY",
    };

    /// <summary>Model bawaan yang disarankan untuk tiap penyedia.</summary>
    public static IReadOnlyList<string> ModelsFor(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => ["gpt-4o", "gpt-4o-mini", "gpt-4.1", "o4-mini"],
        AiProvider.Anthropic => ["claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5-20251001"],
        AiProvider.Gemini => ["gemini-2.5-pro", "gemini-2.5-flash"],
        _ => ["llama3.2", "qwen2.5-coder", "deepseek-coder-v2", "phi4"],
    };

    /// <summary>
    /// Memuat konfigurasi dari <c>app.config</c> di direktori aplikasi.
    /// Berkas yang tidak ada bukan error — nilai bawaan dipakai.
    /// </summary>
    public static AssistantSettings Load(string? configPath = null)
    {
        var path = configPath ?? Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(path))
        {
            try
            {
                var document = XDocument.Load(path);
                foreach (var entry in document.Descendants("appSettings").Elements("add"))
                {
                    var key = entry.Attribute("key")?.Value;
                    var value = entry.Attribute("value")?.Value;

                    if (!string.IsNullOrWhiteSpace(key) && value is not null)
                    {
                        settings[key] = value;
                    }
                }
            }
            catch (System.Xml.XmlException)
            {
                // Konfigurasi rusak diperlakukan seperti konfigurasi kosong: asisten
                // tetap bisa dibuka dan pengguna bisa memperbaikinya lewat dialog Settings.
            }
        }

        var provider = ParseEnum(settings.GetValueOrDefault("Ai:Provider"), AiProvider.OpenAI);

        return new AssistantSettings
        {
            Provider = provider,
            Model = settings.GetValueOrDefault("Ai:Model") is { Length: > 0 } model
                ? model
                : ModelsFor(provider)[0],
            ApiKey = ResolveSecret(EnvironmentVariableFor(provider), settings.GetValueOrDefault("Ai:ApiKey")),
            Endpoint = settings.GetValueOrDefault("Ai:Endpoint") is { Length: > 0 } endpoint ? endpoint : null,
            SystemPrompt = settings.GetValueOrDefault("Ai:SystemPrompt") is { Length: > 0 } prompt
                ? prompt
                : DefaultSystemPrompt,
            Temperature = ParseDouble(settings.GetValueOrDefault("Ai:Temperature"), 0.3),
            MaxTokens = ParseInt(settings.GetValueOrDefault("Ai:MaxTokens"), 4096),
            HistoryWindow = ParseInt(settings.GetValueOrDefault("Ai:HistoryWindow"), 20),
            TavilyApiKey = ResolveSecret("TAVILY_API_KEY", settings.GetValueOrDefault("Tools:TavilyApiKey")),
            EnableFunctionCalling = ParseBool(settings.GetValueOrDefault("Ai:EnableFunctionCalling"), true),
        };
    }

    /// <summary>
    /// Menyimpan konfigurasi ke <c>app.config</c>.
    /// </summary>
    /// <remarks>
    /// Kunci API sengaja tidak ditulis bila nilainya berasal dari variabel lingkungan:
    /// menyimpannya akan memindahkan rahasia dari lingkungan ke berkas di disk.
    /// </remarks>
    public async Task SaveAsync(string? configPath = null, CancellationToken cancellationToken = default)
    {
        var path = configPath ?? Path.Combine(AppContext.BaseDirectory, ConfigFileName);

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableFor(Provider));
        var persistApiKey = !string.IsNullOrWhiteSpace(ApiKey) && ApiKey != fromEnvironment;

        var tavilyFromEnvironment = Environment.GetEnvironmentVariable("TAVILY_API_KEY");
        var persistTavily = !string.IsNullOrWhiteSpace(TavilyApiKey) && TavilyApiKey != tavilyFromEnvironment;

        var appSettings = new XElement("appSettings",
            Setting("Ai:Provider", Provider.ToString()),
            Setting("Ai:Model", Model),
            persistApiKey ? Setting("Ai:ApiKey", ApiKey) : null,
            Endpoint is null ? null : Setting("Ai:Endpoint", Endpoint),
            Setting("Ai:SystemPrompt", SystemPrompt),
            Setting("Ai:Temperature", Temperature.ToString("0.##", CultureInfo.InvariantCulture)),
            Setting("Ai:MaxTokens", MaxTokens.ToString(CultureInfo.InvariantCulture)),
            Setting("Ai:HistoryWindow", HistoryWindow.ToString(CultureInfo.InvariantCulture)),
            Setting("Ai:EnableFunctionCalling", EnableFunctionCalling ? "true" : "false"),
            persistTavily ? Setting("Tools:TavilyApiKey", TavilyApiKey) : null);

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("configuration", appSettings));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, document.ToString(), cancellationToken);

        static XElement Setting(string key, string value)
            => new("add", new XAttribute("key", key), new XAttribute("value", value));
    }

    /// <summary>Variabel lingkungan menang atas berkas konfigurasi.</summary>
    private static string ResolveSecret(string environmentVariable, string? fromConfig)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(value) ? fromConfig ?? string.Empty : value;
    }

    private static TEnum ParseEnum<TEnum>(string? text, TEnum fallback)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(text, ignoreCase: true, out var value) ? value : fallback;

    private static double ParseDouble(string? text, double fallback)
        => double.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static int ParseInt(string? text, int fallback)
        => int.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool ParseBool(string? text, bool fallback)
        => bool.TryParse(text, out var value) ? value : fallback;
}
