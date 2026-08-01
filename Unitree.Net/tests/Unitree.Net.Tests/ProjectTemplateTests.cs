using Shouldly;
using Unitree.Net.Wizard.Core.Projects;

namespace Unitree.Net.Tests;

/// <summary>
/// Tests for the wizard's template catalogue and project scaffolding.
/// </summary>
public sealed class ProjectTemplateTests : IDisposable
{
    private readonly string _workspace;
    private readonly ProjectService _projects;

    public ProjectTemplateTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "unitree-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_workspace);
        _projects = new ProjectService(RepositoryRoot());
    }

    /// <summary>Locates the repository root from the test assembly's location.</summary>
    private static string RepositoryRoot() =>
        ProjectService.TryLocateSdkRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Tests must run from inside the repository.");

    public static TheoryData<string> EveryTemplate()
    {
        var data = new TheoryData<string>();

        foreach (ProjectTemplate template in TemplateCatalog.All)
        {
            data.Add(template.Id);
        }

        return data;
    }

    [Fact]
    public void CatalogueIsNotEmptyAndIdentifiersAreUnique()
    {
        TemplateCatalog.All.ShouldNotBeEmpty();

        TemplateCatalog.All
            .Select(template => template.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ShouldBe(TemplateCatalog.All.Count);
    }

    [Theory]
    [MemberData(nameof(EveryTemplate))]
    public void TemplateIsWellFormed(string id)
    {
        ProjectTemplate template = TemplateCatalog.Find(id).ShouldNotBeNull();

        template.Name.ShouldNotBeNullOrWhiteSpace();
        template.Summary.ShouldNotBeNullOrWhiteSpace();
        template.Tags.ShouldNotBeEmpty();
        template.Files.ShouldContain(file => file.RelativePath == "Program.cs");

        foreach (TemplateFile file in template.Files)
        {
            file.Content.ShouldNotBeNullOrWhiteSpace();

            // Forward slashes only: the paths are rewritten for the platform on the way out, and a
            // backslash here would produce a file literally named "Components\App.razor" on Linux.
            file.RelativePath.ShouldNotContain('\\');
            Path.IsPathRooted(file.RelativePath).ShouldBeFalse();
        }
    }

    [Theory]
    [MemberData(nameof(EveryTemplate))]
    public void TemplateProjectReferencesResolveToRealFiles(string id)
    {
        ProjectTemplate template = TemplateCatalog.Find(id).ShouldNotBeNull();
        string root = RepositoryRoot();

        foreach (string reference in template.ProjectReferencePaths)
        {
            string full = Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar));

            // A reference that does not exist produces a restore failure whose message says nothing
            // about the template that caused it.
            File.Exists(full).ShouldBeTrue($"{id} references '{reference}', which does not exist");
        }
    }

    [Theory]
    [MemberData(nameof(EveryTemplate))]
    public async Task ScaffoldingProducesAProjectFileAndSources(string id)
    {
        ProjectTemplate template = TemplateCatalog.Find(id).ShouldNotBeNull();
        string name = "T" + id.Replace("-", string.Empty);

        WizardProject project = await _projects.CreateAsync(_workspace, name, template);

        File.Exists(project.ProjectFilePath).ShouldBeTrue();
        project.Kind.ShouldBe(template.Kind);
        project.TemplateId.ShouldBe(template.Id);

        IReadOnlyList<ProjectFile> files = _projects.EnumerateFiles(project);
        files.ShouldContain(file => file.RelativePath == "Program.cs");
        files.ShouldContain(file => file.RelativePath.EndsWith(".csproj", StringComparison.Ordinal));

        string projectText = await File.ReadAllTextAsync(project.ProjectFilePath);
        projectText.ShouldContain("net10.0");

        if (template.Kind == ProjectKind.Embedded)
        {
            // The robot's compute module is ARM64 Linux with no .NET installed, so an embedded
            // project that is not self-contained cannot run there at all.
            projectText.ShouldContain("linux-arm64");
            projectText.ShouldContain("<SelfContained>true</SelfContained>");
        }
    }

    [Fact]
    public async Task ScaffoldedProjectReferencesResolveFromTheProjectFolder()
    {
        WizardProject project = await _projects.CreateAsync(
            _workspace, "RelativeRefs", TemplateCatalog.Find("telemetry-monitor"));

        string text = await File.ReadAllTextAsync(project.ProjectFilePath);

        foreach (string line in text.Split('\n').Where(line => line.Contains("ProjectReference")))
        {
            string include = line.Split('"')[1];
            string resolved = Path.GetFullPath(Path.Combine(project.RootPath, include));

            // References are written relative to the project, so a project scaffolded anywhere on
            // disk still finds the SDK.
            File.Exists(resolved).ShouldBeTrue($"'{include}' does not resolve from {project.RootPath}");
        }
    }

    [Fact]
    public async Task BlankProjectStillBuildsAgainstTheSdk()
    {
        WizardProject project = await _projects.CreateAsync(_workspace, "BlankOne", template: null);

        project.TemplateId.ShouldBeNull();
        _projects.EnumerateFiles(project).ShouldContain(file => file.RelativePath == "Program.cs");

        string program = await File.ReadAllTextAsync(Path.Combine(project.RootPath, "Program.cs"));
        program.ShouldContain("AddUnitreeRobot");
    }

    [Fact]
    public async Task ExistingNonEmptyFolderIsRefused()
    {
        await _projects.CreateAsync(_workspace, "Duplicate", template: null);

        // Silently merging into a folder that already has a project in it is how someone loses work.
        await Should.ThrowAsync<IOException>(
            () => _projects.CreateAsync(_workspace, "Duplicate", template: null));
    }

    [Theory]
    [InlineData("has/slash")]
    [InlineData("has\\backslash")]
    [InlineData("9Lives")]
    public async Task InvalidProjectNamesAreRefused(string name)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _projects.CreateAsync(_workspace, name, template: null));
    }

    [Fact]
    public void SearchMatchesNameSummaryAndTags()
    {
        TemplateCatalog.Search("patrol").ShouldContain(template => template.Id == "patrol-route");
        TemplateCatalog.Search("ros2").ShouldContain(template => template.Id == "ros2-bridge-node");
        TemplateCatalog.Search(string.Empty).Count.ShouldBe(TemplateCatalog.All.Count);
        TemplateCatalog.Search("zzzz-nothing").ShouldBeEmpty();
    }

    [Fact]
    public void EveryProjectKindHasAtLeastOneTemplate()
    {
        foreach (ProjectKind kind in Enum.GetValues<ProjectKind>())
        {
            TemplateCatalog.ByKind(kind).ShouldNotBeEmpty($"nothing offers a {kind} project");
        }
    }

    [Fact]
    public void NamespacesAreValid()
    {
        ProjectService.ToNamespace("PatrolBot").ShouldBe("PatrolBot");
        ProjectService.ToNamespace("My Robot").ShouldBe("My_Robot");
        ProjectService.ToNamespace("robot-2").ShouldBe("robot_2");
        ProjectService.ToNamespace("2fast").ShouldBe("_2fast");
    }

    [Fact]
    public async Task BuildOutputIsExcludedFromTheFileTree()
    {
        WizardProject project = await _projects.CreateAsync(_workspace, "Ignoring", template: null);

        Directory.CreateDirectory(Path.Combine(project.RootPath, "obj"));
        File.WriteAllText(Path.Combine(project.RootPath, "obj", "noise.cs"), "// generated");

        // Build output in the explorer is noise, and on a restored project there is a great deal of it.
        _projects.EnumerateFiles(project).ShouldNotContain(file => file.RelativePath.StartsWith("obj/"));
    }

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
            // A file left locked by a failed test must not fail the run as well.
        }
    }
}
