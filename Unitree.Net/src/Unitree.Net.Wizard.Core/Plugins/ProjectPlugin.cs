using System.ComponentModel;
using System.Text;
using Microsoft.SemanticKernel;
using Unitree.Net.Wizard.Core.Projects;

namespace Unitree.Net.Wizard.Core.Plugins;

/// <summary>
/// Lets Jack read and write files in the project that is currently open.
/// </summary>
/// <remarks>
/// <para>
/// The project is reached through a callback rather than captured at construction, so opening a
/// different project does not leave the assistant editing the previous one.
/// </para>
/// <para>
/// Every path is resolved and checked to be inside the project root. Without that check a model that
/// has been talked into writing to <c>../../../etc</c> would be allowed to.
/// </para>
/// </remarks>
public sealed class ProjectPlugin
{
    private readonly Func<WizardProject?> _currentProject;
    private readonly ProjectService _projects;
    private readonly Action<string, string>? _fileChanged;

    /// <summary>Creates the plugin.</summary>
    /// <param name="currentProject">Returns the open project, or null when none is open.</param>
    /// <param name="projects">Used to enumerate the project's files.</param>
    /// <param name="fileChanged">
    /// Called with the relative path and new content whenever Jack writes a file, so the editor can
    /// refresh rather than showing stale text.
    /// </param>
    public ProjectPlugin(
        Func<WizardProject?> currentProject,
        ProjectService projects,
        Action<string, string>? fileChanged = null)
    {
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(projects);

        _currentProject = currentProject;
        _projects = projects;
        _fileChanged = fileChanged;
    }

    /// <summary>Describes the open project.</summary>
    [KernelFunction("get_project_info")]
    [Description(
        "Reports the open project's name, kind and file list. Call this before writing code so you " +
        "know what already exists.")]
    public string GetProjectInfo()
    {
        if (_currentProject() is not { } project)
        {
            return "No project is open. The operator needs to create or open one first.";
        }

        IReadOnlyList<ProjectFile> files = _projects.EnumerateFiles(project);

        var text = new StringBuilder();
        text.AppendLine($"Project: {project.Name}");
        text.AppendLine($"Kind: {project.Kind}");
        text.AppendLine($"Root: {project.RootPath}");

        if (project.TemplateId is { } template)
        {
            text.AppendLine($"Created from template: {template}");
        }

        text.AppendLine();
        text.AppendLine($"Files ({files.Count}):");

        foreach (ProjectFile file in files)
        {
            text.AppendLine($"  {file.RelativePath}  ({file.SizeBytes:N0} bytes)");
        }

        return text.ToString();
    }

    /// <summary>Reads a file from the open project.</summary>
    /// <param name="relativePath">Path relative to the project root.</param>
    [KernelFunction("read_project_file")]
    [Description("Reads a file from the open project. Read before you edit — do not guess at content.")]
    public string ReadProjectFile(
        [Description("Path relative to the project root, e.g. 'Program.cs'.")] string relativePath)
    {
        if (!TryResolve(relativePath, out string fullPath, out string error))
        {
            return error;
        }

        if (!File.Exists(fullPath))
        {
            return $"'{relativePath}' does not exist in this project.";
        }

        try
        {
            return $"--- {relativePath} ---\n{File.ReadAllText(fullPath)}";
        }
        catch (IOException exception)
        {
            return $"Could not read '{relativePath}': {exception.Message}";
        }
    }

    /// <summary>Writes a file into the open project, creating or replacing it.</summary>
    /// <param name="relativePath">Path relative to the project root.</param>
    /// <param name="content">The complete file content.</param>
    [KernelFunction("write_project_file")]
    [Description(
        "Creates or replaces a file in the open project. Supply the file's complete content, not a " +
        "fragment or a diff — this overwrites whatever is there.")]
    public string WriteProjectFile(
        [Description("Path relative to the project root, e.g. 'Behaviours/Patrol.cs'.")] string relativePath,
        [Description("The complete new content of the file.")] string content)
    {
        if (!TryResolve(relativePath, out string fullPath, out string error))
        {
            return error;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            bool existed = File.Exists(fullPath);
            File.WriteAllText(fullPath, content, Encoding.UTF8);

            _fileChanged?.Invoke(relativePath.Replace('\\', '/'), content);

            int lines = content.AsSpan().Count('\n') + 1;
            string wrote = $"{(existed ? "Replaced" : "Created")} '{relativePath}' ({lines} lines).";

            // A deterministic check, because the instruction to look the API up first does not hold:
            // the model is confident rather than uncertain, and confidence is what stops it reaching
            // for describe_sdk. A correction in the tool result cannot be skipped the way a prompt can.
            IReadOnlyList<string> problems = SdkLint.Check(relativePath, content);

            if (problems.Count == 0)
            {
                return $"{wrote} Tell the operator to press Build to check it compiles.";
            }

            return $"{wrote}\n\nBut this will not compile — {problems.Count} problem(s):\n"
                 + string.Join('\n', problems.Select(problem => $"  - {problem}"))
                 + "\n\nCall describe_sdk for the area, then write the file again with the corrections. "
                 + "Do not tell the operator it is ready until this is clean.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Could not write '{relativePath}': {exception.Message}";
        }
    }

    /// <summary>Searches the project's files for text.</summary>
    /// <param name="query">The text to look for.</param>
    [KernelFunction("search_project")]
    [Description("Searches every file in the open project for a string and returns matching lines.")]
    public string SearchProject(
        [Description("The text to look for. Case-insensitive.")] string query)
    {
        if (_currentProject() is not { } project)
        {
            return "No project is open.";
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return "Give me something to search for.";
        }

        var text = new StringBuilder();
        int matches = 0;

        foreach (ProjectFile file in _projects.EnumerateFiles(project))
        {
            string[] lines;

            try
            {
                lines = File.ReadAllLines(file.AbsolutePath);
            }
            catch (IOException)
            {
                continue;
            }

            for (int i = 0; i < lines.Length && matches < 100; i++)
            {
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    text.AppendLine($"{file.RelativePath}:{i + 1}: {lines[i].Trim()}");
                    matches++;
                }
            }
        }

        return matches == 0
            ? $"No matches for '{query}'."
            : $"{matches} match(es):\n{text}";
    }

    /// <summary>
    /// Resolves a project-relative path, refusing anything that escapes the project root.
    /// </summary>
    private bool TryResolve(string relativePath, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (_currentProject() is not { } project)
        {
            error = "No project is open.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "A file path is required.";
            return false;
        }

        string root = Path.GetFullPath(project.RootPath);
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));

        // Compared after full resolution, so "sub/../../outside" is caught the same way "../outside"
        // is. The trailing separator stops a sibling folder with a shared prefix from passing.
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{relativePath}' resolves outside the project. Only files inside the project can be touched.";
            return false;
        }

        fullPath = candidate;
        return true;
    }
}
