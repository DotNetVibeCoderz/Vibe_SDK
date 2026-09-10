using System.Collections.Frozen;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Wizard.Core.Projects;

namespace PollenRobotics.Net.Wizard.Core.Templates;

/// <summary>
/// Every template the New Project dialog offers.
/// </summary>
/// <remarks>
/// The catalogue is built once and frozen. Templates hold a generator delegate rather than
/// pre-rendered text, so a template can name the project it is generating - which matters, because
/// the namespace and assembly name come from it.
/// </remarks>
public static class TemplateCatalog
{
    private static readonly Lazy<FrozenDictionary<string, RobotTemplate>> Index = new(() =>
        BuildAll().ToFrozenDictionary(t => t.Id, StringComparer.Ordinal));

    /// <summary>Every template.</summary>
    public static IReadOnlyList<RobotTemplate> All => [.. Index.Value.Values.OrderBy(t => t.Robot).ThenBy(t => t.Category).ThenBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>Number of templates.</summary>
    public static int Count => Index.Value.Count;

    /// <summary>Looks a template up by id.</summary>
    /// <exception cref="KeyNotFoundException">No template with that id.</exception>
    public static RobotTemplate ById(string id) => Index.Value.TryGetValue(id, out RobotTemplate? template)
        ? template
        : throw new KeyNotFoundException($"No template with id '{id}'. Known ids: {string.Join(", ", Index.Value.Keys)}.");

    /// <summary>Looks a template up without throwing.</summary>
    public static RobotTemplate? Find(string id) => Index.Value.GetValueOrDefault(id);

    /// <summary>Templates for one robot.</summary>
    public static IReadOnlyList<RobotTemplate> For(RobotKind robot) => [.. All.Where(t => t.Robot == robot)];

    /// <summary>Templates in one category.</summary>
    public static IReadOnlyList<RobotTemplate> InCategory(TemplateCategory category) => [.. All.Where(t => t.Category == category)];

    /// <summary>Templates matching a search term, optionally narrowed to one robot.</summary>
    public static IReadOnlyList<RobotTemplate> Search(string term, RobotKind? robot = null) =>
    [
        .. All.Where(t => (robot is null || t.Robot == robot) && t.Matches(term))
    ];

    /// <summary>
    /// Writes a template into a directory and returns the project.
    /// </summary>
    /// <param name="template">The template to scaffold.</param>
    /// <param name="projectName">Project name, which becomes the assembly and namespace.</param>
    /// <param name="parentDirectory">Where to create the project folder.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="IOException">The target directory already contains a project.</exception>
    public static async Task<WizardProject> ScaffoldAsync(
        RobotTemplate template,
        string projectName,
        string parentDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);

        string directory = Path.Combine(parentDirectory, projectName);

        if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.csproj").Any())
        {
            throw new IOException($"{directory} already contains a project. Pick another name or another folder.");
        }

        Directory.CreateDirectory(directory);

        foreach (ProjectFile file in template.Generate(projectName))
        {
            string path = Path.Combine(directory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file.Content, cancellationToken).ConfigureAwait(false);
        }

        var project = new WizardProject
        {
            Name = projectName,
            Directory = directory,
            Robot = template.Robot,
            Kind = template.Kind,
            TemplateId = template.Id,
            // Reachy 2 has no in-process simulator in this SDK, so its templates default to a robot.
            DefaultRunTarget = template.Robot == RobotKind.Reachy2 ? RunTarget.Robot : RunTarget.Simulator,
        };

        await project.SaveAsync(cancellationToken).ConfigureAwait(false);
        return project;
    }

    /// <summary>
    /// Scaffolds an empty project with nothing but bring-up code.
    /// </summary>
    /// <remarks>This is what New Project's Blank option produces.</remarks>
    public static Task<WizardProject> ScaffoldBlankAsync(
        string projectName,
        string parentDirectory,
        RobotKind robot,
        ProjectKind kind = ProjectKind.Console,
        CancellationToken cancellationToken = default)
    {
        RobotTemplate blank = BlankTemplate(robot, kind);
        return ScaffoldAsync(blank, projectName, parentDirectory, cancellationToken);
    }

    private static RobotTemplate BlankTemplate(RobotKind robot, ProjectKind kind) => new()
    {
        Id = $"blank.{robot}".ToLowerInvariant(),
        Name = "Blank project",
        Description = $"An empty {RobotCatalog.For(robot).DisplayName} project with connection and shutdown already wired up.",
        Robot = robot,
        Kind = kind,
        Category = TemplateCategory.GettingStarted,
        Tags = ["blank", "empty"],
        Generate = name =>
        [
            ProjectScaffold.CsProj(name, robot, kind),
            new ProjectFile("Program.cs", new Ai.Plugins.CodeGenerationPlugin()
                .GenerateProgramSkeleton(robot.ToString(), kind.ToString(), "An empty project. Write the behaviour where it says to.")),
        ],
    };

    private static IEnumerable<RobotTemplate> BuildAll()
    {
        foreach (RobotTemplate template in ReachyMiniTemplates.All())
        {
            yield return template;
        }

        foreach (RobotTemplate template in MicroDuckTemplates.All())
        {
            yield return template;
        }

        foreach (RobotTemplate template in Reachy2Templates.All())
        {
            yield return template;
        }
    }
}
