using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Wizard.Core.Projects;

namespace PollenRobotics.Net.Wizard.Core.Templates;

/// <summary>Groups templates in the New Project dialog.</summary>
public enum TemplateCategory
{
    /// <summary>The smallest thing that connects and moves.</summary>
    GettingStarted,

    /// <summary>Expressive behaviour: emotions, idle life, reactions.</summary>
    Behaviour,

    /// <summary>Camera, microphone, depth sensor.</summary>
    Perception,

    /// <summary>Getting from one place to another.</summary>
    Navigation,

    /// <summary>Picking things up and putting them down.</summary>
    Manipulation,

    /// <summary>Language models driving the robot.</summary>
    Ai,

    /// <summary>Anything with a user interface.</summary>
    Interface,

    /// <summary>Diagnostics, calibration and test rigs.</summary>
    Tooling,
}

/// <summary>
/// One project template.
/// </summary>
/// <remarks>
/// A template produces a complete, compiling project rather than a snippet - that is the whole
/// point of picking one over a blank project. <c>tools/PollenRobotics.Net.TemplateCheck</c>
/// scaffolds and compiles every template in this catalogue, and it should be run after any change
/// to the SDK's public surface: templates are written against that surface by hand and nothing
/// recompiles them by accident.
/// </remarks>
public sealed record RobotTemplate
{
    /// <summary>Stable identifier, used in project metadata.</summary>
    public required string Id { get; init; }

    /// <summary>Name shown in the dialog.</summary>
    public required string Name { get; init; }

    /// <summary>One line describing what it does.</summary>
    public required string Description { get; init; }

    /// <summary>Which robot it drives.</summary>
    public required RobotKind Robot { get; init; }

    /// <summary>What kind of application it produces.</summary>
    public ProjectKind Kind { get; init; } = ProjectKind.Console;

    /// <summary>Which group it appears under.</summary>
    public required TemplateCategory Category { get; init; }

    /// <summary>Search keywords.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Rough difficulty, 1 to 3, shown as dots in the dialog.</summary>
    public int Difficulty { get; init; } = 1;

    /// <summary>Produces the project's files given a project name.</summary>
    public required Func<string, IReadOnlyList<ProjectFile>> Generate { get; init; }

    /// <summary>True when the template matches a search term.</summary>
    public bool Matches(string term) =>
        string.IsNullOrWhiteSpace(term) ||
        Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase));
}
