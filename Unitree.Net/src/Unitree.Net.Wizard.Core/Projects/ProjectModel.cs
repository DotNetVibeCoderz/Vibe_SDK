namespace Unitree.Net.Wizard.Core.Projects;

/// <summary>
/// What kind of application a generated project is.
/// </summary>
/// <remarks>
/// The kind decides the SDK, the output type, and — most importantly — how the project is run and
/// deployed. An embedded project is cross-published for the robot's ARM64 compute module; a console
/// project runs on the developer's machine and talks to the robot over the network.
/// </remarks>
public enum ProjectKind
{
    /// <summary>A console application, run on the operator's machine.</summary>
    Console,

    /// <summary>A desktop application with a windowed UI.</summary>
    Desktop,

    /// <summary>An ASP.NET Core web application.</summary>
    Web,

    /// <summary>A service published for the robot's own compute module and deployed over SSH.</summary>
    Embedded,
}

/// <summary>
/// Where a project runs when the operator presses Run.
/// </summary>
public enum RunTarget
{
    /// <summary>Against the virtual robot on the local multicast group.</summary>
    Simulator,

    /// <summary>Against a real robot over the configured network interface.</summary>
    Robot,
}

/// <summary>
/// One file a template contributes to a new project.
/// </summary>
/// <param name="RelativePath">Path relative to the project root, using forward slashes.</param>
/// <param name="Content">The file's text.</param>
public readonly record struct TemplateFile(string RelativePath, string Content);

/// <summary>
/// A starting point for a new robot application.
/// </summary>
/// <param name="Id">Stable identifier, used in the UI and in tests.</param>
/// <param name="Name">Display name.</param>
/// <param name="Summary">One line describing what it does.</param>
/// <param name="Kind">What kind of application it produces.</param>
/// <param name="Tags">Search keywords.</param>
/// <param name="Files">The files it writes, excluding the project file.</param>
/// <param name="PackageReferences">NuGet packages beyond the SDK projects it needs.</param>
/// <param name="ProjectReferencePaths">
/// SDK projects the template references, as paths relative to the repository root.
/// </param>
public sealed record ProjectTemplate(
    string Id,
    string Name,
    string Summary,
    ProjectKind Kind,
    IReadOnlyList<string> Tags,
    IReadOnlyList<TemplateFile> Files,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> ProjectReferencePaths);

/// <summary>
/// A project open in the editor.
/// </summary>
/// <param name="Name">Project name, which is also the folder and assembly name.</param>
/// <param name="RootPath">Absolute path to the project folder.</param>
/// <param name="ProjectFilePath">Absolute path to the <c>.csproj</c>.</param>
/// <param name="Kind">What kind of application it is.</param>
/// <param name="TemplateId">Which template created it, or <see langword="null"/> for a blank project.</param>
public sealed record WizardProject(
    string Name,
    string RootPath,
    string ProjectFilePath,
    ProjectKind Kind,
    string? TemplateId);

/// <summary>
/// A file in the open project's tree.
/// </summary>
/// <param name="RelativePath">Path relative to the project root, using forward slashes.</param>
/// <param name="AbsolutePath">Full path on disk.</param>
/// <param name="SizeBytes">Size in bytes.</param>
public readonly record struct ProjectFile(string RelativePath, string AbsolutePath, long SizeBytes)
{
    /// <summary>The file name without its directory.</summary>
    public string FileName => Path.GetFileName(RelativePath);

    /// <summary>
    /// The Monaco language identifier for this file, or <c>plaintext</c>.
    /// </summary>
    public string Language => Path.GetExtension(RelativePath).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".csproj" or ".props" or ".targets" or ".xml" or ".xaml" => "xml",
        ".json" => "json",
        ".razor" or ".html" or ".cshtml" => "html",
        ".css" => "css",
        ".js" => "javascript",
        ".ts" => "typescript",
        ".md" => "markdown",
        ".yml" or ".yaml" => "yaml",
        ".sh" => "shell",
        ".py" => "python",
        _ => "plaintext",
    };
}
