using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using TypeSafeAppGen.Workspace;

namespace TypeSafeAppGen.Ai.Plugins;

/// <summary>Operasi file di proyek yang sedang terbuka. Semua path dibatasi ke folder proyek.</summary>
public sealed class WorkspacePlugin(IJackHost host)
{
    private const int MaxReadChars = 80_000;
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp", ".dll", ".exe", ".pdb", ".zip", ".nupkg", ".ttf", ".otf", ".woff", ".woff2", ".mp3", ".mp4", ".wav",
    };

    private ProjectWorkspace Workspace => host.Workspace
        ?? throw new InvalidOperationException("No project is open. Ask the user to open or create a project first, or call create_project_from_template.");

    [KernelFunction("get_project_info"), Description("Get the open project's root folder, its build target, and the full file list. Call this first before editing.")]
    public string GetProjectInfo()
    {
        var workspace = Workspace;
        var target = workspace.FindBuildTarget();
        return $"Project: {workspace.Name}\nRoot: {workspace.Root}\nBuild target: {(target is null ? "(none — create a .csproj first)" : workspace.Relative(target))}\nFiles:\n{workspace.DescribeTree()}";
    }

    [KernelFunction("list_files"), Description("List files under a folder of the project (relative path, empty for the root). bin/obj/.git are skipped.")]
    public string ListFiles([Description("Folder relative to the project root; empty for the root.")] string folder = "")
    {
        var workspace = Workspace;
        var files = workspace.EnumerateFiles(string.IsNullOrWhiteSpace(folder) ? null : folder).Take(400).Select(workspace.Relative).ToList();
        return files.Count == 0 ? "(no files)" : string.Join('\n', files);
    }

    [KernelFunction("read_file"), Description("Read a text file from the project. Returns the content with 1-based line numbers so you can reference lines.")]
    public async Task<string> ReadFileAsync(
        [Description("File path relative to the project root.")] string path,
        [Description("First line to return, 1-based. 0 for the start.")] int startLine = 0,
        [Description("Last line to return, inclusive. 0 for the end.")] int endLine = 0)
    {
        var full = Workspace.Resolve(path);
        if (BinaryExtensions.Contains(Path.GetExtension(full))) return $"'{path}' is a binary file; it cannot be read as text.";
        if (!File.Exists(full)) return $"File not found: {path}";
        var lines = await File.ReadAllLinesAsync(full);
        var first = Math.Max(1, startLine);
        var last = endLine <= 0 ? lines.Length : Math.Min(endLine, lines.Length);
        var builder = new StringBuilder();
        for (var i = first; i <= last; i++)
        {
            builder.Append(i).Append(": ").AppendLine(lines[i - 1]);
            if (builder.Length > MaxReadChars)
            {
                builder.AppendLine($"… truncated at line {i} of {lines.Length}. Call read_file again with startLine={i + 1}.");
                break;
            }
        }
        return builder.Length == 0 ? "(empty file)" : builder.ToString();
    }

    [KernelFunction("write_file"), Description("Create or overwrite a file with the complete content. Parent folders are created. The file opens in the editor so the user can see it.")]
    public async Task<string> WriteFileAsync(
        [Description("File path relative to the project root.")] string path,
        [Description("The full file content. Never send partial content or placeholders like '...'.")] string content)
    {
        var existed = File.Exists(Workspace.Resolve(path));
        var full = await Workspace.WriteAsync(path, content);
        await host.OpenFileAsync(full);
        return $"{(existed ? "Updated" : "Created")} {Workspace.Relative(full)} ({content.Split('\n').Length} lines).";
    }

    [KernelFunction("edit_file"), Description("Replace one exact, unique snippet in a file. Cheaper than write_file for small changes. Include enough surrounding lines to make oldText unique.")]
    public async Task<string> EditFileAsync(
        [Description("File path relative to the project root.")] string path,
        [Description("Exact existing text to replace, including whitespace.")] string oldText,
        [Description("Replacement text.")] string newText)
    {
        var full = Workspace.Resolve(path);
        if (!File.Exists(full)) return $"File not found: {path}";
        var content = await File.ReadAllTextAsync(full);
        // Toleransi line ending: model sering mengirim \n meski file memakai \r\n.
        var normalizedOld = content.Contains("\r\n") ? oldText.Replace("\r\n", "\n").Replace("\n", "\r\n") : oldText.Replace("\r\n", "\n");
        var normalizedNew = content.Contains("\r\n") ? newText.Replace("\r\n", "\n").Replace("\n", "\r\n") : newText.Replace("\r\n", "\n");
        var first = content.IndexOf(normalizedOld, StringComparison.Ordinal);
        if (first < 0) return "oldText was not found. Read the file again and copy the snippet exactly.";
        if (content.IndexOf(normalizedOld, first + 1, StringComparison.Ordinal) >= 0) return "oldText appears more than once. Add surrounding lines so it is unique.";
        await Workspace.WriteAsync(path, string.Concat(content.AsSpan(0, first), normalizedNew, content.AsSpan(first + normalizedOld.Length)));
        var line = content[..first].Count(c => c == '\n') + 1;
        await host.OpenFileAsync(full, line);
        return $"Edited {Workspace.Relative(full)} at line {line}.";
    }

    [KernelFunction("create_folder"), Description("Create a folder inside the project.")]
    public string CreateFolder([Description("Folder path relative to the project root.")] string path)
    {
        var full = Workspace.Resolve(path);
        Directory.CreateDirectory(full);
        Workspace.NotifyChanged(full);
        return $"Folder ready: {Workspace.Relative(full)}";
    }

    [KernelFunction("delete_path"), Description("Delete a file or folder inside the project. Use only when the user's request requires removing it.")]
    public string DeletePath([Description("File or folder path relative to the project root.")] string path)
    {
        var full = Workspace.Resolve(path);
        if (!File.Exists(full) && !Directory.Exists(full)) return $"Nothing to delete at {path}.";
        Workspace.Delete(path);
        return $"Deleted {path}.";
    }

    [KernelFunction("search_in_files"), Description("Search project text files for a regular expression. Returns matching lines as path:line: text.")]
    public string SearchInFiles(
        [Description(".NET regular expression, case-insensitive.")] string pattern,
        [Description("Optional file name filter such as *.cs or *.razor.")] string filePattern = "*")
    {
        var workspace = Workspace;
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException ex) { return $"Invalid pattern: {ex.Message}"; }

        var filter = new Regex("^" + Regex.Escape(filePattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
        var results = new StringBuilder();
        var count = 0;
        foreach (var file in workspace.EnumerateFiles())
        {
            if (!filter.IsMatch(Path.GetFileName(file)) || BinaryExtensions.Contains(Path.GetExtension(file))) continue;
            if (new FileInfo(file).Length > 1_000_000) continue;
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (!regex.IsMatch(line)) continue;
                results.Append(workspace.Relative(file)).Append(':').Append(lineNumber).Append(": ").AppendLine(line.Trim());
                if (++count >= 120) return results.AppendLine("… more matches omitted; narrow the pattern.").ToString();
            }
        }
        return count == 0 ? "No matches." : results.ToString();
    }

    [KernelFunction("get_active_editor"), Description("Get the file the user is looking at in the editor: its path, caret line, selected text, and content.")]
    public async Task<string> GetActiveEditorAsync()
    {
        var snapshot = await host.GetActiveEditorAsync();
        if (snapshot is null) return "No file is open in the editor.";
        var text = snapshot.Text.Length > MaxReadChars ? snapshot.Text[..MaxReadChars] + "\n… truncated" : snapshot.Text;
        return $"Path: {snapshot.Path ?? "(unsaved)"}\nCaret line: {snapshot.CaretLine}\nSelection: {(string.IsNullOrEmpty(snapshot.Selection) ? "(none)" : snapshot.Selection)}\n---\n{text}";
    }

    [KernelFunction("open_in_editor"), Description("Open a project file in the editor, optionally scrolled to a line, to show the user something.")]
    public async Task<string> OpenInEditorAsync(
        [Description("File path relative to the project root.")] string path,
        [Description("1-based line to reveal; 0 for the top.")] int line = 0)
    {
        var full = Workspace.Resolve(path);
        if (!File.Exists(full)) return $"File not found: {path}";
        await host.OpenFileAsync(full, line > 0 ? line : null);
        return $"Opened {Workspace.Relative(full)}.";
    }
}
