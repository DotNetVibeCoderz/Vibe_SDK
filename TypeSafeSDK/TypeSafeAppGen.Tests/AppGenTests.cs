using Microsoft.SemanticKernel;
using TypeSafeAppGen.Ai;
using TypeSafeAppGen.Ai.Plugins;
using TypeSafeAppGen.Config;
using TypeSafeAppGen.Workspace;

namespace TypeSafeAppGen.Tests;

public sealed class ProjectTemplateTests
{
    [Fact]
    public void Catalog_CoversEveryRequiredCategory()
    {
        var templates = ProjectTemplates.All();

        Assert.True(templates.Count >= 12);
        Assert.Equal(templates.Count, templates.Select(t => t.Id).Distinct().Count());
        foreach (var category in new[] { "3D Graphics", "Animation", "Game", "Simulator", "Web", "AI" })
            Assert.Contains(templates, t => t.Category == category);
        Assert.All(templates, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.False(string.IsNullOrWhiteSpace(t.UseCase));
        });
    }

    [Fact]
    public void Scaffold_EveryTemplateProducesACompleteProject()
    {
        using var temp = new TempFolder();
        foreach (var template in ProjectTemplates.All().Append(new ProjectTemplate { Id = ProjectTemplates.BlankId }))
        {
            var name = "My " + template.Id;
            var main = ProjectTemplates.Scaffold(template.Id, name, temp.Path);
            var folder = Path.Combine(temp.Path, name);
            var identifier = ProjectTemplates.ToIdentifier(name);

            Assert.True(File.Exists(main), $"{template.Id}: main file {main} missing");
            Assert.True(File.Exists(Path.Combine(folder, identifier + ".csproj")), $"{template.Id}: csproj missing");
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
            Assert.DoesNotContain(files, f => f.EndsWith(".txt") || f.EndsWith("template.json"));
            Assert.All(files, f => Assert.DoesNotContain(ProjectTemplates.NamePlaceholder, File.ReadAllText(f)));
        }
    }

    [Fact]
    public void Scaffold_RefusesToOverwriteANonEmptyFolder()
    {
        using var temp = new TempFolder();
        temp.Write("Existing/keep.txt", "mine");

        Assert.Throws<IOException>(() => ProjectTemplates.Scaffold("snake", "Existing", temp.Path));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(temp.Path, "Existing", "keep.txt")));
    }

    [Theory]
    [InlineData("my cool app", "MyCoolApp")]
    [InlineData("3d-viewer", "App3dViewer")]
    [InlineData("!!!", "App")]
    [InlineData("TerrainFlyover", "TerrainFlyover")]
    public void ToIdentifier_ProducesValidCSharpNames(string name, string expected) =>
        Assert.Equal(expected, ProjectTemplates.ToIdentifier(name));

    [Theory]
    [InlineData("")]
    [InlineData("bad/name")]
    [InlineData("what?")]
    public void ValidateName_RejectsUnusableFolderNames(string name) => Assert.NotNull(ProjectTemplates.ValidateName(name));
}

public sealed class ConfigStoreTests
{
    [Fact]
    public void Parse_MigratesTheLegacyFlatShape()
    {
        var config = ConfigStore.Parse("""
            { "Provider": "Claude", "Model": "claude-sonnet-5", "ApiKey": "k", "Endpoint": "https://gw.example", "Temperature": 0.4, "ShowLineNumbers": false }
            """);

        Assert.Equal(LlmProvider.Claude, config.ActiveProvider);
        Assert.Equal("claude-sonnet-5", config.ActiveProfile.Model);
        Assert.Equal("k", config.ActiveProfile.ApiKey);
        Assert.Equal("https://gw.example", config.ActiveProfile.Endpoint);
        Assert.Equal(0.4, config.Temperature);
        Assert.False(config.ShowLineNumbers);
        Assert.Contains("claude-opus-5", config.ActiveProfile.Models);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsEverySetting()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "app.config.json");
        var config = new AppConfig { ActiveProvider = LlmProvider.Gemini, Temperature = 0.7, TavilyApiKey = "t", ChatPanelWidth = 520 };
        config.Profile(LlmProvider.Gemini).Model = "gemini-2.5-pro";
        config.RememberProject(@"C:\p1");

        await ConfigStore.SaveAsync(config, path);
        var loaded = await ConfigStore.LoadAsync(path);

        Assert.Equal(LlmProvider.Gemini, loaded.ActiveProvider);
        Assert.Equal("gemini-2.5-pro", loaded.ActiveProfile.Model);
        Assert.Equal(0.7, loaded.Temperature);
        Assert.Equal("t", loaded.TavilyApiKey);
        Assert.Equal(520, loaded.ChatPanelWidth);
        Assert.Equal([@"C:\p1"], loaded.RecentProjects);
        Assert.Contains("\"ActiveProvider\": \"Gemini\"", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Load_FallsBackToDefaultsWhenTheFileIsCorrupt()
    {
        using var temp = new TempFolder();
        var path = temp.Write("app.config.json", "{ not json");

        var config = await ConfigStore.LoadAsync(path);

        Assert.Equal(LlmProvider.Ollama, config.ActiveProvider);
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void RememberProject_KeepsEightMostRecentWithoutDuplicates()
    {
        var config = new AppConfig();
        for (var i = 0; i < 10; i++) config.RememberProject($@"C:\p{i}");
        config.RememberProject(@"C:\P3");

        Assert.Equal(8, config.RecentProjects.Count);
        Assert.Equal(@"C:\P3", config.RecentProjects[0]);
        Assert.Single(config.RecentProjects, p => p.Equals(@"C:\p3", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class LlmFactoryTests
{
    [Theory]
    [InlineData(LlmProvider.OpenAI, "gpt-5-mini", false)]
    [InlineData(LlmProvider.AzureOpenAI, "o4-mini", false)]
    [InlineData(LlmProvider.OpenAI, "gpt-4.1", true)]
    [InlineData(LlmProvider.Claude, "claude-opus-5", false)]
    [InlineData(LlmProvider.Claude, "claude-haiku-4-5", true)]
    [InlineData(LlmProvider.Ollama, "llama3.2", true)]
    public void SupportsTemperature_MatchesModelFamilies(LlmProvider provider, string model, bool expected) =>
        Assert.Equal(expected, LlmFactory.SupportsTemperature(provider, model));

    [Fact]
    public void Validate_ExplainsWhatIsMissing()
    {
        Assert.Contains("API key", LlmFactory.Validate(LlmProvider.Claude, new ProviderProfile { Model = "claude-opus-5" }));
        Assert.Contains("endpoint", LlmFactory.Validate(LlmProvider.AzureOpenAI, new ProviderProfile { Model = "m", ApiKey = "k", Endpoint = "https://<resource>.openai.azure.com/" }));
        Assert.Null(LlmFactory.Validate(LlmProvider.Ollama, new ProviderProfile { Model = "llama3.2", Endpoint = "http://localhost:11434" }));
    }

    [Fact]
    public void CreateBuilder_RefusesAnIncompleteProfile()
    {
        using var http = new HttpClient();
        Assert.Throws<LlmConfigurationException>(() => LlmFactory.CreateBuilder(LlmProvider.OpenAI, new ProviderProfile { Model = "gpt-5" }, http));
    }

    [Theory]
    [InlineData(LlmProvider.OpenAI)]
    [InlineData(LlmProvider.AzureOpenAI)]
    [InlineData(LlmProvider.Claude)]
    [InlineData(LlmProvider.Gemini)]
    [InlineData(LlmProvider.Ollama)]
    public void CreateBuilder_RegistersAChatServiceForEveryProvider(LlmProvider provider)
    {
        using var http = new HttpClient();
        var profile = new ProviderProfile { Model = "m", ApiKey = "k", Endpoint = provider is LlmProvider.AzureOpenAI ? "https://r.openai.azure.com/" : provider is LlmProvider.Ollama ? "http://localhost:11434" : "" };

        var kernel = LlmFactory.CreateBuilder(provider, profile, http).Build();

        Assert.NotNull(kernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>());
    }
}

/// <summary>Host palsu: kernel functions diuji tanpa jendela Avalonia.</summary>
internal sealed class FakeHost(ProjectWorkspace? workspace) : IJackHost
{
    public List<(string Path, int? Line)> Opened { get; } = [];
    public ProjectWorkspace? Workspace => workspace;
    public string ProjectsFolder => Path.GetTempPath();
    public Task<ActiveEditorSnapshot?> GetActiveEditorAsync() => Task.FromResult<ActiveEditorSnapshot?>(null);
    public Task OpenFileAsync(string fullPath, int? line = null) { Opened.Add((fullPath, line)); return Task.CompletedTask; }
    public Task OpenProjectAsync(string folder, string? fileToOpen = null) => Task.CompletedTask;
    public Task<ProcessResult> RunDotnetAsync(string title, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct) =>
        Task.FromResult(new ProcessResult(0, "", TimeSpan.Zero, false));
}

public sealed class KernelFunctionTests
{
    [Fact]
    public async Task WriteFile_CreatesTheFileAndOpensIt()
    {
        using var temp = new TempFolder();
        var host = new FakeHost(new ProjectWorkspace(temp.Path));

        var result = await new WorkspacePlugin(host).WriteFileAsync("Views/Home.cs", "class Home {}\n");

        Assert.StartsWith("Created", result);
        Assert.Single(host.Opened);
        Assert.Equal("class Home {}\n", File.ReadAllText(Path.Combine(temp.Path, "Views", "Home.cs")));
    }

    [Fact]
    public async Task EditFile_ToleratesLineEndingsAndReportsTheLine()
    {
        using var temp = new TempFolder();
        temp.Write("A.cs", "line1\r\nvar x = 1;\r\nline3\r\n");
        var host = new FakeHost(new ProjectWorkspace(temp.Path));

        var result = await new WorkspacePlugin(host).EditFileAsync("A.cs", "var x = 1;\nline3", "var x = 2;\nline3");

        Assert.Equal("Edited A.cs at line 2.", result);
        Assert.Equal("line1\r\nvar x = 2;\r\nline3\r\n", File.ReadAllText(Path.Combine(temp.Path, "A.cs")));
        Assert.Equal(2, host.Opened[0].Line);
    }

    [Fact]
    public async Task EditFile_RefusesAmbiguousSnippets()
    {
        using var temp = new TempFolder();
        temp.Write("A.cs", "x = 1;\nx = 1;\n");

        var result = await new WorkspacePlugin(new FakeHost(new ProjectWorkspace(temp.Path))).EditFileAsync("A.cs", "x = 1;", "x = 2;");

        Assert.Contains("more than once", result);
    }

    [Fact]
    public async Task ReadFile_NumbersLinesAndHonoursRange()
    {
        using var temp = new TempFolder();
        temp.Write("A.cs", "a\nb\nc\nd\n");

        var result = await new WorkspacePlugin(new FakeHost(new ProjectWorkspace(temp.Path))).ReadFileAsync("A.cs", 2, 3);

        Assert.Equal("2: b\n3: c\n", result.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task WorkspaceFunctions_CannotEscapeTheProject()
    {
        using var temp = new TempFolder();
        var plugin = new WorkspacePlugin(new FakeHost(new ProjectWorkspace(temp.Path)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => plugin.WriteFileAsync("../evil.cs", "x"));
    }

    [Fact]
    public void WorkspaceFunctions_ExplainWhenNoProjectIsOpen()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new WorkspacePlugin(new FakeHost(null)).GetProjectInfo());
        Assert.Contains("create_project_from_template", error.Message);
    }

    [Fact]
    public void SearchInFiles_ReturnsPathAndLine()
    {
        using var temp = new TempFolder();
        temp.Write("src/A.cs", "class A\n{\n    void Render() {}\n}\n");
        temp.Write("bin/B.cs", "void Render() {}");

        var result = new WorkspacePlugin(new FakeHost(new ProjectWorkspace(temp.Path))).SearchInFiles("render", "*.cs");

        Assert.Equal("src/A.cs:3: void Render() {}", result.Trim());
    }

    [Fact]
    public void Calculate_ReturnsExactValuesOrAnError()
    {
        var math = new MathPlugin();
        Assert.Equal("1280", math.Calculate("1920/1080*720"));
        Assert.StartsWith("Error", math.Calculate("1/0"));
        Assert.Contains("median=2.5", new MathPlugin().Statistics("1, 2, 3, 4"));
    }

    [Fact]
    public void DateFunctions_DoCalendarArithmetic()
    {
        var time = new DateTimePlugin();
        Assert.Equal("2026-03-01 (Sunday)", time.AddToDate("2026-01-31", days: 29));
        Assert.Contains("5 business days", time.DateDifference("2026-09-21", "2026-09-28"));
    }

    [Fact]
    public void HtmlToText_DropsScriptsAndMarkup()
    {
        var text = WebPlugin.HtmlToText("<html><head><title>T</title></head><body><script>alert(1)</script><h1>Hello</h1><p>World &amp; more</p></body></html>");

        Assert.Equal("Hello\nWorld & more", text);
    }

    [Fact]
    public async Task TypeSafeClassify_UsesTheOfflineSimulator()
    {
        var result = await new TypeSafeSdkPlugin().ClassifyAsync("I was charged twice for my invoice", "billing, technical, other");

        Assert.StartsWith("billing", result);
    }

    [Fact]
    public void ListTemplates_IncludesBlankAndTheGallery()
    {
        var list = new ProjectPlugin(new FakeHost(null)).ListTemplates();

        Assert.Contains("blank | Console", list);
        Assert.Contains("snake | Game", list);
    }
}
