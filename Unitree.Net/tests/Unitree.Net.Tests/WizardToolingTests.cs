using Shouldly;
using Unitree.Net.Ai;
using Unitree.Net.Wizard.Core;
using Unitree.Net.Wizard.Core.Chat;
using Unitree.Net.Wizard.Core.Plugins;
using Unitree.Net.Wizard.Core.Projects;

namespace Unitree.Net.Tests;

/// <summary>
/// Tests for the wizard's assistant plumbing: its tools, its chat sessions and its settings.
/// </summary>
public sealed class WizardToolingTests : IDisposable
{
    private readonly string _workspace;

    public WizardToolingTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "unitree-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_workspace);
    }

    // ------------------------------------------------------------------ maths

    [Theory]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("2 ^ 3 ^ 2", 512)]          // right-associative, as every calculator does it
    [InlineData("-4 + 10", 6)]
    [InlineData("sqrt(1764)", 42)]
    [InlineData("max(3, 7)", 7)]
    [InlineData("round(3.14159, 2)", 3.14)]
    [InlineData("1.5e-3 * 1000", 1.5)]
    [InlineData("10 % 3", 1)]
    public void CalculatorEvaluatesExpressions(string expression, double expected)
    {
        var plugin = new UtilityPlugin();

        // The result is prose so the model can quote it back, which means asserting on the text.
        plugin.Calculate(expression).ShouldContain(expected.ToString("0.##########",
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CalculatorKnowsPiAndConvertsRadians()
    {
        var plugin = new UtilityPlugin();

        plugin.Calculate("deg(pi)").ShouldContain("180");
        plugin.Calculate("cos(0)").ShouldContain("1");
    }

    [Theory]
    [InlineData("2 +")]
    [InlineData("sqrt(")]
    [InlineData("nonsense(2)")]
    [InlineData("2 $ 3")]
    public void CalculatorReportsBadInputWithoutThrowing(string expression)
    {
        // A tool that throws takes the whole turn down. Returning the problem as text lets the model
        // correct itself and try again.
        new UtilityPlugin().Calculate(expression).ShouldContain("Could not evaluate");
    }

    [Fact]
    public void UnitConversionCoversTheCommonRoboticsUnits()
    {
        var plugin = new UtilityPlugin();

        plugin.ConvertUnits(Math.PI, "rad", "deg").ShouldContain("180");
        plugin.ConvertUnits(1, "m/s", "km/h").ShouldContain("3.6");
        plugin.ConvertUnits(500, "hz", "ms").ShouldContain("2");
        plugin.ConvertUnits(1, "furlong", "m").ShouldContain("No conversion");
    }

    // ------------------------------------------------------------- project plugin

    [Fact]
    public async Task ProjectPluginReadsAndWritesInsideTheProject()
    {
        var projects = new ProjectService(RepositoryRoot());
        WizardProject project = await projects.CreateAsync(_workspace, "PluginTarget", template: null);

        var plugin = new ProjectPlugin(() => project, projects);

        plugin.GetProjectInfo().ShouldContain("PluginTarget");
        plugin.ReadProjectFile("Program.cs").ShouldContain("AddUnitreeRobot");

        plugin.WriteProjectFile("Behaviours/Patrol.cs", "// written by a test").ShouldContain("Created");
        File.Exists(Path.Combine(project.RootPath, "Behaviours", "Patrol.cs")).ShouldBeTrue();

        plugin.SearchProject("written by a test").ShouldContain("Behaviours/Patrol.cs");
    }

    [Theory]
    [InlineData("return battery.Value.StateOfCharge > 20;", "StateOfChargePercent")]
    [InlineData("var v = s.Battery.VoltageVolts;", "PackVoltage")]
    [InlineData("if (result != NavigationResult.Reached) { }", "Arrived")]
    [InlineData("var stats = controller.Statistics;", "LoopStatistics")]
    [InlineData("controller.SetJoint(0, 1f, 0f, 0f, 40f, 2f);", "SetJointPosition")]
    [InlineData("var c = new DualArmCoordinator(robot);", "ArmController")]
    [InlineData("var d = new GaitAnomalyDetector();", "GaitAnalyzer")]
    // The two an operator's own DanceBot actually hit, in that order.
    [InlineData("Host.CreateApplicationBuilder(args).ConfigureServices((c, s) => { });", "builder.Services")]
    [InlineData("using Unitree.Net.Control;\nvar v = new VelocityCommand(1f, 0f, 0f);", "Unitree.Net.Core")]
    public async Task WritingCodeWithAMemberThatDoesNotExistIsReported(string code, string expected)
    {
        var projects = new ProjectService(RepositoryRoot());
        WizardProject project = await projects.CreateAsync(_workspace, "Linted", template: null);

        var plugin = new ProjectPlugin(() => project, projects);
        string reply = plugin.WriteProjectFile("Behaviours/Guard.cs", code);

        // The file is still written — the operator may have asked for exactly that — but the reply
        // has to name the real member, because telling the model to look it up first does not work.
        File.Exists(Path.Combine(project.RootPath, "Behaviours", "Guard.cs")).ShouldBeTrue();
        reply.ShouldContain("will not compile");
        reply.ShouldContain(expected);
    }

    [Fact]
    public async Task CorrectCodeIsNotFlagged()
    {
        var projects = new ProjectService(RepositoryRoot());
        WizardProject project = await projects.CreateAsync(_workspace, "Clean", template: null);

        var plugin = new ProjectPlugin(() => project, projects);

        string reply = plugin.WriteProjectFile("Behaviours/Guard.cs", """
using Unitree.Net.Sensors;

public static class Guard
{
    public static bool IsSafe(TelemetryHub telemetry) =>
        telemetry.GetBattery() is { StateOfChargePercent: > 20 };
}
""");

        // A lint that cries wolf gets ignored, which costs more than it saves.
        reply.ShouldNotContain("will not compile");
        reply.ShouldContain("Created");
    }

    [Fact]
    public async Task MissingDependencyInjectionUsingIsReported()
    {
        var projects = new ProjectService(RepositoryRoot());
        WizardProject project = await projects.CreateAsync(_workspace, "MissingUsing", template: null);

        var plugin = new ProjectPlugin(() => project, projects);

        // The compiler blames IServiceCollection for this and never mentions the namespace. It caught
        // every one of the sixteen templates the first time they were built.
        string reply = plugin.WriteProjectFile(
            "Setup.cs", "builder.Services.AddUnitreeRobot(builder.Configuration);");

        reply.ShouldContain("Unitree.Net.Extensions.DependencyInjection");
    }

    [Theory]
    [InlineData("../escape.cs")]
    [InlineData("sub/../../escape.cs")]
    [InlineData("C:\\Windows\\System32\\escape.cs")]
    public async Task ProjectPluginRefusesPathsOutsideTheProject(string path)
    {
        var projects = new ProjectService(RepositoryRoot());
        WizardProject project = await projects.CreateAsync(_workspace, "Sandboxed", template: null);

        var plugin = new ProjectPlugin(() => project, projects);

        // A model can be talked into writing anywhere. The guard is what makes that harmless.
        plugin.WriteProjectFile(path, "// should not land").ShouldContain("outside the project");
        plugin.ReadProjectFile(path).ShouldContain("outside the project");
    }

    [Fact]
    public void ProjectPluginSaysSoWhenNothingIsOpen()
    {
        var plugin = new ProjectPlugin(() => null, new ProjectService(RepositoryRoot()));

        plugin.GetProjectInfo().ShouldContain("No project is open");
        plugin.ReadProjectFile("Program.cs").ShouldContain("No project is open");
    }

    // ----------------------------------------------------------------- sdk plugin

    [Fact]
    public void SdkPluginDescribesEveryArea()
    {
        var plugin = new SdkPlugin(() => null);

        foreach (string area in (string[])
                 ["overview", "connection", "locomotion", "telemetry", "lowlevel",
                  "navigation", "arms", "ai", "ros2", "config"])
        {
            plugin.DescribeSdk(area).Length.ShouldBeGreaterThan(200, $"'{area}' returned almost nothing");
        }

        // An unknown area falls back to the overview rather than to an error, so a model that guesses
        // still gets something useful.
        plugin.DescribeSdk("nonsense").ShouldBe(plugin.DescribeSdk("overview"));
    }

    [Fact]
    public void SdkReferenceNamesTheThreeThingsThatCatchPeopleOut()
    {
        string overview = new SdkPlugin(() => null).DescribeSdk("overview");

        // These are the failures that produce no error at all on a real robot, so the reference has to
        // carry them or Jack will generate code that silently does nothing.
        overview.ShouldContain("BalanceStandAsync");
        overview.ShouldContain("BeginLowLevelSessionAsync");
        // The third one: a velocity command expires on the robot, so continuous motion needs the
        // stream that resends it. Anchored on the API rather than on a word, because the wording of
        // this paragraph has already changed once.
        overview.ShouldContain("StartVelocityStream");
        overview.ShouldContain("expires");
        overview.ShouldContain("run against real hardware", Case.Insensitive);
    }

    [Fact]
    public void SdkPluginListsAndReturnsTemplates()
    {
        var plugin = new SdkPlugin(() => null);

        plugin.ListTemplates().ShouldContain("telemetry-monitor");
        plugin.ListTemplates("embedded").ShouldContain("embedded-inspection");
        plugin.GetTemplateCode("patrol-route").ShouldContain("WaypointNavigator");
        plugin.GetTemplateCode("no-such-template").ShouldContain("No template called");
    }

    // --------------------------------------------------------------------- chat

    [Fact]
    public void SessionTakesItsTitleFromTheFirstThingTyped()
    {
        var session = new ChatSession();
        session.Title.ShouldBe("New chat");

        session.Add(ChatMessage.Create(ChatRole.User, "Write me a patrol behaviour for a Go2"));
        session.Title.ShouldBe("Write me a patrol behaviour for a Go2");

        // Later messages must not rename it, or the list reshuffles under the operator mid-conversation.
        session.Add(ChatMessage.Create(ChatRole.Assistant, "Here you go"));
        session.Add(ChatMessage.Create(ChatRole.User, "Now add a battery check"));
        session.Title.ShouldBe("Write me a patrol behaviour for a Go2");
    }

    [Fact]
    public void LongFirstMessageIsTruncatedForTheTitle()
    {
        var session = new ChatSession();
        session.Add(ChatMessage.Create(ChatRole.User, new string('x', 200)));

        session.Title.Length.ShouldBeLessThanOrEqualTo(42);
        session.Title.ShouldEndWith("…");
    }

    [Fact]
    public async Task SnapshotSurvivesConcurrentStreamingUpdates()
    {
        var session = new ChatSession();
        session.Add(ChatMessage.Create(ChatRole.User, "go"));
        session.Add(ChatMessage.Create(ChatRole.Assistant, string.Empty));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Mirrors what streaming does: rewrite the last message from one thread while another
        // enumerates the conversation to render it. Against the raw list this throws
        // "collection was modified" within a few iterations.
        Task writer = Task.Run(() =>
        {
            for (int i = 0; !cancellation.IsCancellationRequested && i < 20_000; i++)
            {
                session.ReplaceLast(ChatMessage.Create(ChatRole.Assistant, new string('x', i % 64)));
            }
        }, CancellationToken.None);

        Task reader = Task.Run(() =>
        {
            while (!writer.IsCompleted)
            {
                foreach (ChatMessage message in session.Snapshot())
                {
                    _ = message.Text.Length;
                }
            }
        }, CancellationToken.None);

        await Should.NotThrowAsync(() => Task.WhenAll(writer, reader));
    }

    [Fact]
    public void ResetClearsMessagesButKeepsTheSession()
    {
        var session = new ChatSession();
        string id = session.Id;

        session.Add(ChatMessage.Create(ChatRole.User, "hello"));
        session.Reset();

        session.Id.ShouldBe(id);
        session.IsEmpty.ShouldBeTrue();
        session.Title.ShouldBe("New chat");
    }

    [Fact]
    public async Task SessionsRoundTripThroughTheStore()
    {
        var store = new ChatSessionStore(_workspace);
        var session = new ChatSession();

        session.Add(ChatMessage.Create(ChatRole.User, "remember this"));
        session.Add(ChatMessage.Create(ChatRole.Assistant, "# heading\n\n```csharp\nvar x = 1;\n```"));
        store.Save(session);

        ChatSession restored = new ChatSessionStore(_workspace).LoadAll()
            .ShouldHaveSingleItem();

        restored.Id.ShouldBe(session.Id);
        restored.Messages.Count.ShouldBe(2);
        restored.Messages[1].Text.ShouldContain("```csharp");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task TextAttachmentsAreReadAndBinaryOnesAreNot()
    {
        var store = new ChatSessionStore(_workspace);

        using var text = new MemoryStream("public class Robot { }"u8.ToArray());
        ChatAttachment document = await store.StoreAttachmentAsync("Robot.cs", text);

        document.Kind.ShouldBe(AttachmentKind.Document);
        document.ExtractedText.ShouldNotBeNull().ShouldContain("public class Robot");

        using var image = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0]);
        ChatAttachment picture = await store.StoreAttachmentAsync("shot.png", image);

        // Images go to the model as image content, so no text is extracted for them.
        picture.Kind.ShouldBe(AttachmentKind.Image);
        picture.ContentType.ShouldBe("image/png");
        picture.ExtractedText.ShouldBeNull();
    }

    [Fact]
    public async Task DeletingASessionRemovesItsAttachments()
    {
        var store = new ChatSessionStore(_workspace);

        using var content = new MemoryStream("notes"u8.ToArray());
        ChatAttachment attachment = await store.StoreAttachmentAsync("notes.txt", content);

        var session = new ChatSession();
        session.Add(new ChatMessage(
            "m1", ChatRole.User, "see attached", DateTimeOffset.Now, [attachment], []));

        store.Save(session);
        store.Delete(session);

        File.Exists(attachment.StoredPath).ShouldBeFalse();
        store.LoadAll().ShouldBeEmpty();
    }

    // ----------------------------------------------------------------- prompts

    [Fact]
    public void PromptGalleryCoversEveryCategoryWithUniqueIds()
    {
        PromptGallery.All.Select(example => example.Id).Distinct().Count()
            .ShouldBe(PromptGallery.All.Count);

        foreach (string category in PromptGallery.Categories)
        {
            PromptGallery.InCategory(category).ShouldNotBeEmpty($"'{category}' has no examples");
        }

        // Featured takes one per category, so the empty state shows breadth rather than four
        // variations on the same idea.
        PromptGallery.Featured(9).Select(example => example.Category).Distinct().Count()
            .ShouldBe(PromptGallery.Featured(9).Count);
    }

    [Fact]
    public void PromptsAreCompleteRequestsRatherThanHints()
    {
        foreach (PromptExample example in PromptGallery.All)
        {
            example.Title.ShouldNotBeNullOrWhiteSpace();

            // A one-word hint teaches nothing about how much to ask for, which is the whole point of
            // showing examples.
            example.Prompt.Length.ShouldBeGreaterThan(40, $"'{example.Id}' is too short to be useful");
        }
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public void SettingsProduceUsableAiOptions()
    {
        var settings = new WizardSettings { Provider = LlmProvider.Ollama };
        AiOptions options = settings.ToAiOptions();

        options.Provider.ShouldBe(LlmProvider.Ollama);
        options.GetEffectiveModelId().ShouldNotBeNullOrWhiteSpace();
        options.GetEffectiveEndpoint().ShouldNotBeNull();

        // Ollama needs no key, which is why it is the default.
        Should.NotThrow(options.Validate);
    }

    [Fact]
    public void AnUneditedPersonaTracksTheCodeRatherThanTheConfigFile()
    {
        // Saving used to write the resolved prompt back unconditionally, so the file froze whatever
        // the built-in persona said the day it was first written — and every later improvement to it,
        // including the SDK-lookup rule added after Jack shipped broken code, silently never arrived.
        var settings = new WizardSettings();
        var stale = "You are Jack The Code Bender. (an older build's wording)";

        settings.SystemPrompt.ShouldBe(WizardSettings.DefaultSystemPrompt);

        // A file holding a built-in prompt is ignored: the flag says it was never customised.
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Ai.SystemPrompt"] = stale,
            ["Ai.SystemPromptIsCustom"] = "False",
        };

        ReadPromptFrom(values).ShouldBe(WizardSettings.DefaultSystemPrompt);

        // A file with no flag at all — every file written before this existed — behaves the same way.
        ReadPromptFrom(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Ai.SystemPrompt"] = stale,
        }).ShouldBe(WizardSettings.DefaultSystemPrompt);

        // But a prompt the operator actually wrote is kept.
        ReadPromptFrom(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Ai.SystemPrompt"] = "You are a laconic assistant.",
            ["Ai.SystemPromptIsCustom"] = "True",
        }).ShouldBe("You are a laconic assistant.");
    }

    /// <summary>Round-trips one set of app-settings values through the store.</summary>
    private static string ReadPromptFrom(Dictionary<string, string> values)
    {
        string path = Path.Combine(Path.GetTempPath(), $"unitree-cfg-{Guid.NewGuid():n}.config");

        var document = new System.Xml.Linq.XDocument(
            new System.Xml.Linq.XElement("configuration",
                new System.Xml.Linq.XElement("appSettings",
                    values.Select(pair => new System.Xml.Linq.XElement("add",
                        new System.Xml.Linq.XAttribute("key", pair.Key),
                        new System.Xml.Linq.XAttribute("value", pair.Value))))));

        document.Save(path);

        try
        {
            return WizardSettingsStore.LoadFrom(path).SystemPrompt;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DefaultPersonaCarriesTheSafetyInstructions()
    {
        string persona = WizardSettings.DefaultSystemPrompt;

        // These are the three failures that produce no error at all on a robot, so the persona has to
        // carry them or Jack will confidently write code that silently does nothing.
        persona.ShouldContain("BalanceStandAsync");
        persona.ShouldContain("release the motors");
        persona.ShouldContain("Never claim something has been tested on real hardware");

        // And this one, because the first file Jack ever wrote used BatteryStatus.StateOfCharge — a
        // member that does not exist — without calling describe_sdk. Confidence, not uncertainty, is
        // what makes a model skip the lookup, so the instruction has to be unconditional.
        persona.ShouldContain("describe_sdk");
        persona.ShouldContain("StateOfChargePercent");
    }

    [Fact]
    public void SdkLookupIsDescribedAsUnconditional()
    {
        string description = typeof(SdkPlugin)
            .GetMethod(nameof(SdkPlugin.DescribeSdk))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;

        // The description is the entire specification the model sees. "Call this if unsure" is read
        // as permission to skip; "always" is not.
        description.ShouldContain("ALWAYS");
        description.ShouldContain("not in any training data", Case.Insensitive);
    }

    private static string RepositoryRoot() =>
        ProjectService.TryLocateSdkRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Tests must run from inside the repository.");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked file must not fail the run.
        }
    }
}
