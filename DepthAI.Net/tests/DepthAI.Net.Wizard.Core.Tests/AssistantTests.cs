using DepthAI.Wizard.Ai;
using DepthAI.Wizard.Ai.Plugins;
using DepthAI.Wizard.Chat;
using DepthAI.Wizard.Prompts;

namespace DepthAI.Wizard.Tests;

public class AssistantSettingsTests : IDisposable
{
    private readonly string _configPath = Path.Combine(
        Path.GetTempPath(), $"depthai-config-{Guid.NewGuid():N}.config");

    public void Dispose()
    {
        if (File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_FallsBackToDefaultsWhenFileIsMissing()
    {
        var settings = AssistantSettings.Load(Path.Combine(Path.GetTempPath(), "tidak-ada.config"));

        Assert.Equal(AiProvider.OpenAI, settings.Provider);
        Assert.False(string.IsNullOrWhiteSpace(settings.SystemPrompt));
    }

    [Fact]
    public void Load_SurvivesCorruptConfigFile()
    {
        File.WriteAllText(_configPath, "<configuration><appSettings>rusak");

        // Konfigurasi rusak tidak boleh membuat wizard gagal dibuka — pengguna
        // masih perlu bisa membuka dialog pengaturan untuk memperbaikinya.
        var settings = AssistantSettings.Load(_configPath);

        Assert.Equal(AiProvider.OpenAI, settings.Provider);
    }

    [Fact]
    public async Task SaveThenLoad_PreservesSettings()
    {
        var original = new AssistantSettings
        {
            Provider = AiProvider.Gemini,
            Model = "gemini-2.5-pro",
            Temperature = 0.85,
            MaxTokens = 1234,
            HistoryWindow = 7,
            EnableFunctionCalling = false,
            SystemPrompt = "Jadilah ringkas.",
            ApiKey = "kunci-uji",
        };

        await original.SaveAsync(_configPath);
        var restored = AssistantSettings.Load(_configPath);

        Assert.Equal(AiProvider.Gemini, restored.Provider);
        Assert.Equal("gemini-2.5-pro", restored.Model);
        Assert.Equal(0.85, restored.Temperature, 3);
        Assert.Equal(1234, restored.MaxTokens);
        Assert.Equal(7, restored.HistoryWindow);
        Assert.False(restored.EnableFunctionCalling);
        Assert.Equal("Jadilah ringkas.", restored.SystemPrompt);
    }

    [Fact]
    public async Task SaveAsync_DoesNotPersistKeysThatCameFromTheEnvironment()
    {
        const string Variable = "GEMINI_API_KEY";
        var previous = Environment.GetEnvironmentVariable(Variable);

        try
        {
            Environment.SetEnvironmentVariable(Variable, "rahasia-lingkungan");

            var settings = AssistantSettings.Load(_configPath) with
            {
                Provider = AiProvider.Gemini,
                ApiKey = "rahasia-lingkungan",
            };

            await settings.SaveAsync(_configPath);

            // Menulisnya akan memindahkan rahasia dari lingkungan ke berkas di disk,
            // yang kemudian ikut ter-commit.
            var contents = await File.ReadAllTextAsync(_configPath);
            Assert.DoesNotContain("rahasia-lingkungan", contents, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }

    [Fact]
    public void IsConfigured_RequiresKeyForCloudProvidersButNotOllama()
    {
        var openAi = new AssistantSettings { Provider = AiProvider.OpenAI, ApiKey = string.Empty };
        var ollama = new AssistantSettings { Provider = AiProvider.Ollama, ApiKey = string.Empty };

        Assert.False(openAi.IsConfigured);
        Assert.NotNull(openAi.MissingConfiguration);
        Assert.True(ollama.IsConfigured);
    }

    [Fact]
    public void ModelsFor_ReturnsSuggestionsForEveryProvider()
        => Assert.All(Enum.GetValues<AiProvider>(),
            provider => Assert.NotEmpty(AssistantSettings.ModelsFor(provider)));
}

public class ExpressionEvaluatorTests
{
    private readonly MathPlugin _plugin = new();

    [Theory]
    [InlineData("2+3*4", "14")]
    [InlineData("(2+3)*4", "20")]
    [InlineData("10/4", "2.5")]
    [InlineData("2^3^2", "512")]
    [InlineData("-5+3", "-2")]
    [InlineData("sqrt(16)", "4")]
    [InlineData("max(3, 7)", "7")]
    [InlineData("round(2.6)", "3")]
    [InlineData("(1920*1080*3)/1024/1024", "5.9326171875")]
    public void Calculate_EvaluatesExpressions(string expression, string expected)
        => Assert.Equal(expected, _plugin.Calculate(expression));

    [Fact]
    public void Calculate_ReportsDivisionByZeroWithoutThrowing()
        => Assert.Contains("nol", _plugin.Calculate("1/0"), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Calculate_ReportsMalformedInput()
        => Assert.Contains("Tidak bisa menghitung", _plugin.Calculate("2 +"), StringComparison.Ordinal);

    [Fact]
    public void Calculate_UsesInvariantDecimalSeparator()
    {
        // Kalau evaluator memakai budaya lokal, mesin berlokal Indonesia akan
        // membaca "1.5" sebagai 15.
        Assert.Equal("1.5", _plugin.Calculate("3/2"));
    }

    [Fact]
    public void FrameBandwidth_ComputesRealisticNumbers()
    {
        var summary = _plugin.FrameBandwidth(1920, 1080, 30);

        Assert.Contains("MB/s", summary, StringComparison.Ordinal);
        Assert.Contains("Mbps", summary, StringComparison.Ordinal);
    }
}

public class ChatSessionTests
{
    [Fact]
    public void EnsureTitleFromFirstMessage_UsesFirstUserMessage()
    {
        var session = new ChatSession();
        session.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = "Buatkan penghitung orang" });

        session.EnsureTitleFromFirstMessage();

        Assert.Equal("Buatkan penghitung orang", session.Title);
    }

    [Fact]
    public void EnsureTitleFromFirstMessage_DoesNotOverwriteUserChosenTitle()
    {
        var session = new ChatSession { Title = "Judul saya" };
        session.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = "halo" });

        session.EnsureTitleFromFirstMessage();

        Assert.Equal("Judul saya", session.Title);
    }

    [Fact]
    public void Preview_TruncatesLongMessages()
    {
        var session = new ChatSession();
        session.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = new string('x', 200) });

        Assert.True(session.Preview.Length <= 61);
        Assert.EndsWith("…", session.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageComposer_AppendsDocumentLinksButNotImages()
    {
        var attachments = new[]
        {
            new ChatAttachment
            {
                FileName = "spesifikasi.pdf",
                Kind = AttachmentKind.Document,
                StoredPath = "/tmp/spesifikasi.pdf",
                Url = "file:///tmp/spesifikasi.pdf",
                SizeBytes = 2048,
            },
            new ChatAttachment
            {
                FileName = "layar.png",
                Kind = AttachmentKind.Image,
                StoredPath = "/tmp/layar.png",
                Url = "file:///tmp/layar.png",
                SizeBytes = 1024,
            },
        };

        var composed = MessageComposer.Compose("Tolong lihat ini", attachments);

        // Gambar dikirim sebagai konten gambar sungguhan, jadi tidak perlu ditautkan.
        Assert.Contains("spesifikasi.pdf", composed, StringComparison.Ordinal);
        Assert.DoesNotContain("layar.png", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageComposer_LeavesTextAloneWithoutDocuments()
        => Assert.Equal("halo", MessageComposer.Compose("halo", []));
}

public class ChatSessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "depthai-sessions-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly ChatSessionStore _store;

    public ChatSessionStoreTests() => _store = new ChatSessionStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsMessages()
    {
        var session = new ChatSession { Title = "Uji" };
        session.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = "halo" });
        session.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = "hai" });

        await _store.SaveAsync(session);
        var loaded = await _store.LoadAllAsync();

        var restored = Assert.Single(loaded);
        Assert.Equal("Uji", restored.Title);
        Assert.Equal(2, restored.Messages.Count);
    }

    [Fact]
    public async Task Delete_RemovesSessionAndItsAttachments()
    {
        var session = new ChatSession();
        await _store.SaveAsync(session);

        var source = Path.Combine(_root, "sumber.txt");
        await File.WriteAllTextAsync(source, "isi");
        await _store.AddAttachmentAsync(session.Id, source);

        _store.Delete(session.Id);

        Assert.Empty(await _store.LoadAllAsync());
    }

    [Fact]
    public async Task AddAttachmentAsync_CopiesFileSoItSurvivesSourceDeletion()
    {
        var session = new ChatSession();
        await _store.SaveAsync(session);

        var source = Path.Combine(_root, "gambar.png");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);

        var attachment = await _store.AddAttachmentAsync(session.Id, source);
        File.Delete(source);

        Assert.True(File.Exists(attachment.StoredPath));
        Assert.Equal(AttachmentKind.Image, attachment.Kind);
        Assert.Equal("image/png", attachment.MimeType);
    }

    [Fact]
    public async Task AddAttachmentAsync_DoesNotOverwriteOnNameCollision()
    {
        var session = new ChatSession();
        await _store.SaveAsync(session);

        var source = Path.Combine(_root, "catatan.txt");
        await File.WriteAllTextAsync(source, "pertama");
        var first = await _store.AddAttachmentAsync(session.Id, source);

        await File.WriteAllTextAsync(source, "kedua");
        var second = await _store.AddAttachmentAsync(session.Id, source);

        Assert.NotEqual(first.StoredPath, second.StoredPath);
        Assert.Equal("pertama", await File.ReadAllTextAsync(first.StoredPath));
    }

    [Fact]
    public async Task LoadAllAsync_SkipsCorruptSessionFiles()
    {
        var good = new ChatSession { Title = "Baik" };
        await _store.SaveAsync(good);

        var brokenDirectory = Path.Combine(_root, "rusak");
        Directory.CreateDirectory(brokenDirectory);
        await File.WriteAllTextAsync(Path.Combine(brokenDirectory, "session.json"), "{ bukan json");

        // Satu berkas rusak tidak boleh menghilangkan seluruh riwayat pengguna.
        var loaded = await _store.LoadAllAsync();

        Assert.Single(loaded);
        Assert.Equal("Baik", loaded[0].Title);
    }
}

public class PromptGalleryTests
{
    [Fact]
    public void All_CoversEveryCategory()
        => Assert.All(Enum.GetValues<PromptCategory>(),
            category => Assert.NotEmpty(PromptGallery.ByCategory(category)));

    [Fact]
    public void All_HaveSubstantialPrompts()
        => Assert.All(PromptGallery.All, prompt =>
        {
            Assert.False(string.IsNullOrWhiteSpace(prompt.Title));
            // Prompt yang samar membuat asisten menebak; galeri harus memberi contoh
            // yang cukup spesifik untuk langsung dikerjakan.
            Assert.True(prompt.Prompt.Length > 60, $"prompt '{prompt.Title}' terlalu pendek");
        });

    [Fact]
    public void Sample_IsDeterministicForTheSameSeed()
        => Assert.Equal(
            PromptGallery.Sample(5, seed: 99).Select(p => p.Title),
            PromptGallery.Sample(5, seed: 99).Select(p => p.Title));
}
