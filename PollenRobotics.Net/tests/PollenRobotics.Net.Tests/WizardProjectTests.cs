using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Wizard.Core.Projects;
using PollenRobotics.Net.Wizard.Core.Templates;
using Shouldly;
using Xunit;

namespace PollenRobotics.Net.Tests;

/// <summary>Checks on what the wizard writes to disk.</summary>
public class WizardProjectTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"pollen-wizard-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// A generated folder must contain exactly one file MSBuild will treat as a project.
    /// </summary>
    /// <remarks>
    /// Regression test. The wizard's metadata file used to be named <c>.pollenproj</c>, and MSBuild
    /// resolves a bare <c>dotnet build</c> or <c>dotnet run</c> by globbing <c>*.*proj</c> — so it
    /// found two candidates and failed with MSB1011. The wizard itself never noticed, because it
    /// always passes the .csproj path explicitly; the failure only hit someone following the README
    /// the wizard had just written for them, on the very first command it tells them to run.
    /// </remarks>
    [Fact]
    public async Task AGeneratedProjectHasOnlyOneFileMsbuildWillTreatAsAProject()
    {
        var project = new WizardProject
        {
            Name = "DeskPet",
            Directory = _directory,
            Robot = RobotKind.ReachyMini,
            Kind = ProjectKind.Console,
        };

        Directory.CreateDirectory(_directory);
        await project.SaveAsync(TestContext.Current.CancellationToken);
        File.WriteAllText(project.ProjectFilePath, "<Project />");

        // MSBuild's own rule: anything matching *.*proj in the folder is a candidate project.
        string[] candidates = [.. Directory.EnumerateFiles(_directory, "*.*proj")
            .Select(Path.GetFileName)
            .OfType<string>()];

        candidates.ShouldBe(["DeskPet.csproj"]);
    }

    /// <summary>Metadata written by an older build still opens.</summary>
    /// <remarks>
    /// The rename is only safe if projects created before it keep working, so the loader still
    /// falls back to the old name. Without this, an existing project would silently open with
    /// default robot and kind, and Run would start the wrong simulator.
    /// </remarks>
    [Fact]
    public async Task AProjectSavedUnderTheOldExtensionStillOpens()
    {
        var project = new WizardProject
        {
            Name = "OldPet",
            Directory = _directory,
            Robot = RobotKind.MicroDuck,
            Kind = ProjectKind.Console,
            TemplateId = "duck.patrol",
        };

        Directory.CreateDirectory(_directory);
        await project.SaveAsync(TestContext.Current.CancellationToken);
        File.Move(project.MetadataPath, Path.Combine(_directory, "OldPet.pollenproj"));

        WizardProject? reopened = await WizardProject.OpenAsync(_directory, TestContext.Current.CancellationToken);

        reopened.ShouldNotBeNull();
        reopened.Robot.ShouldBe(RobotKind.MicroDuck);
        reopened.TemplateId.ShouldBe("duck.patrol");
    }

    /// <summary>Every template scaffolds a folder MSBuild can build without being told which project.</summary>
    [Fact]
    public void NoTemplateWritesASecondProjectFile()
    {
        foreach (RobotTemplate template in TemplateCatalog.All)
        {
            string[] projectFiles = [.. template.Generate("Probe")
                .Select(f => f.RelativePath)
                .Where(p => p.EndsWith("proj", StringComparison.OrdinalIgnoreCase))];

            projectFiles.ShouldBe(["Probe.csproj"], $"template {template.Id}");
        }
    }
}
