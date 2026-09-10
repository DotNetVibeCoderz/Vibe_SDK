using System.Text.Json;
using System.Text.Json.Serialization;
using PollenRobotics.Net.Core.Robots;

namespace PollenRobotics.Net.Wizard.Core.Projects;

/// <summary>Where a generated application is meant to run.</summary>
public enum ProjectKind
{
    /// <summary>A command-line program.</summary>
    Console,

    /// <summary>An Avalonia desktop application.</summary>
    Desktop,

    /// <summary>An ASP.NET Core app with a Blazor page.</summary>
    Web,

    /// <summary>A worker service published self-contained and run on the robot.</summary>
    Embedded,
}

/// <summary>Where a project runs when the wizard's Run command is used.</summary>
public enum RunTarget
{
    /// <summary>Against the built-in simulator.</summary>
    Simulator,

    /// <summary>Against real hardware.</summary>
    Robot,
}

/// <summary>One file in a generated project.</summary>
/// <param name="RelativePath">Path relative to the project root, using forward slashes.</param>
/// <param name="Content">The file's text.</param>
public readonly record struct ProjectFile(string RelativePath, string Content);

/// <summary>
/// A project the wizard has open.
/// </summary>
/// <remarks>
/// The wizard writes a small <c>.pollen.json</c> beside the <c>.csproj</c> to remember which robot
/// the project targets and which template it came from. That is not derivable from the .NET project
/// file, and without it Run has no idea whether to start the simulator with a duck or a Reachy.
///
/// The extension must not end in <c>proj</c>. MSBuild treats every <c>*.*proj</c> in a folder as a
/// project file, so a sibling <c>.pollenproj</c> made bare <c>dotnet build</c> and <c>dotnet run</c>
/// fail with MSB1011 - which is the first command every generated README tells the user to run.
/// </remarks>
public sealed record WizardProject
{
    /// <summary>Project name, which is also the assembly name.</summary>
    public required string Name { get; init; }

    /// <summary>Absolute path to the project directory.</summary>
    public required string Directory { get; init; }

    /// <summary>Which robot this project drives.</summary>
    public RobotKind Robot { get; init; } = RobotKind.ReachyMini;

    /// <summary>What kind of application it is.</summary>
    public ProjectKind Kind { get; init; } = ProjectKind.Console;

    /// <summary>Id of the template it was created from, or null for a blank project.</summary>
    public string? TemplateId { get; init; }

    /// <summary>Where Run points by default.</summary>
    public RunTarget DefaultRunTarget { get; init; } = RunTarget.Simulator;

    /// <summary>When the project was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>The .csproj path.</summary>
    [JsonIgnore]
    public string ProjectFilePath => Path.Combine(Directory, $"{Name}.csproj");

    /// <summary>The wizard metadata path.</summary>
    [JsonIgnore]
    public string MetadataPath => Path.Combine(Directory, $"{Name}.pollen.json");

    /// <summary>The file name shown in a title bar.</summary>
    [JsonIgnore]
    public string DisplayName => $"{Name} ({Robot}, {Kind})";

    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes the wizard metadata file.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        await using FileStream stream = File.Create(MetadataPath);
        await JsonSerializer.SerializeAsync(stream, this, FileOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a project from a directory or from a <c>.pollen.json</c> path.
    /// </summary>
    /// <remarks>
    /// A directory containing a <c>.csproj</c> but no <c>.pollen.json</c> still opens - the wizard
    /// can edit any .NET project, it just falls back to defaults for the robot and kind. Refusing
    /// would make the editor useless for anything it did not itself create.
    /// </remarks>
    public static async Task<WizardProject?> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        string directory = System.IO.Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;

        // *.pollenproj is the pre-0.1.1 name, still read so projects created by an earlier build
        // keep opening. They are rewritten under the new name on the next save.
        string? metadata = System.IO.Directory.EnumerateFiles(directory, "*.pollen.json").FirstOrDefault()
            ?? System.IO.Directory.EnumerateFiles(directory, "*.pollenproj").FirstOrDefault();

        if (metadata is not null)
        {
            try
            {
                await using FileStream stream = File.OpenRead(metadata);
                WizardProject? project = await JsonSerializer
                    .DeserializeAsync<WizardProject>(stream, FileOptions, cancellationToken).ConfigureAwait(false);

                if (project is not null)
                {
                    // The directory in the file is where the project was created, which is wrong the
                    // moment anyone moves or shares the folder. Trust the path we were opened from.
                    return project with { Directory = directory };
                }
            }
            catch (JsonException)
            {
                // Fall through to the csproj-only path rather than refusing to open the project.
            }
        }

        string? csproj = System.IO.Directory.EnumerateFiles(directory, "*.csproj").FirstOrDefault();
        if (csproj is null)
        {
            return null;
        }

        return new WizardProject
        {
            Name = Path.GetFileNameWithoutExtension(csproj),
            Directory = directory,
        };
    }

    /// <summary>The source files in the project, for the editor's file tree.</summary>
    public IReadOnlyList<string> SourceFiles()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        string[] extensions = [".cs", ".csproj", ".razor", ".axaml", ".json", ".config", ".md"];

        return
        [
            .. System.IO.Directory
                .EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
                .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                // bin and obj are build output. Showing them buries the two files anyone wants
                // under a few hundred they do not.
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        ];
    }
}
