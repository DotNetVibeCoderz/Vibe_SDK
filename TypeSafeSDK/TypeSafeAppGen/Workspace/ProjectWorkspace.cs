using System.Text;

namespace TypeSafeAppGen.Workspace;

/// <summary>
/// Proyek yang sedang terbuka. Semua akses file dari Jack melewati <see cref="Resolve"/> sehingga
/// path relatif maupun absolut tidak bisa keluar dari folder proyek.
/// </summary>
public sealed class ProjectWorkspace
{
    /// <summary>Folder yang tidak ditampilkan di explorer dan tidak dibaca Jack.</summary>
    public static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".vscode", ".idea", "node_modules", "publish", "TestResults", "artifacts",
    };

    public ProjectWorkspace(string root)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException($"Project folder not found: {Root}");
    }

    public string Root { get; }
    public string Name => Path.GetFileName(Root);

    /// <summary>Raised setelah Jack atau aplikasi menulis file, agar explorer dan tab editor ikut diperbarui.</summary>
    public event Action<string>? FileChanged;

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is empty.", nameof(path));
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
        var inside = full.Equals(Root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!inside) throw new UnauthorizedAccessException($"'{path}' is outside the project folder.");
        return full;
    }

    public string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    public static bool IsIgnored(string fullPath, string root)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (IgnoredFolders.Contains(part)) return true;
        return false;
    }

    public async Task<string> ReadAsync(string path, CancellationToken ct = default) =>
        await File.ReadAllTextAsync(Resolve(path), ct);

    public async Task<string> WriteAsync(string path, string content, CancellationToken ct = default)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, new UTF8Encoding(false), ct);
        FileChanged?.Invoke(full);
        return full;
    }

    public void Delete(string path)
    {
        var full = Resolve(path);
        if (full.Equals(Root, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("The project root cannot be deleted.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        else File.Delete(full);
        FileChanged?.Invoke(full);
    }

    public void NotifyChanged(string fullPath) => FileChanged?.Invoke(fullPath);

    public IEnumerable<string> EnumerateFiles(string? folder = null)
    {
        var start = folder is null ? Root : Resolve(folder);
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase)) yield return file;
            foreach (var sub in dirs.OrderDescending(StringComparer.OrdinalIgnoreCase))
                if (!IgnoredFolders.Contains(Path.GetFileName(sub))) pending.Push(sub);
        }
    }

    /// <summary>Pohon file ringkas untuk konteks Jack, dibatasi agar prompt tetap kecil.</summary>
    public string DescribeTree(int maxEntries = 200)
    {
        var builder = new StringBuilder();
        var count = 0;
        foreach (var file in EnumerateFiles())
        {
            if (++count > maxEntries)
            {
                builder.AppendLine($"… ({count - 1}+ files shown, list truncated)");
                break;
            }
            builder.AppendLine(Relative(file));
        }
        return builder.Length == 0 ? "(empty project)" : builder.ToString();
    }

    /// <summary>File build terbaik di proyek: .slnx/.sln di root, atau .csproj terdangkal.</summary>
    public string? FindBuildTarget()
    {
        var solution = Directory.EnumerateFiles(Root, "*.slnx").Concat(Directory.EnumerateFiles(Root, "*.sln")).FirstOrDefault();
        if (solution is not null) return solution;
        return FindProjects().FirstOrDefault();
    }

    /// <summary>Proyek yang bisa dijalankan: OutputType Exe/WinExe atau proyek Web, terdangkal lebih dulu.</summary>
    public string? FindRunnableProject()
    {
        foreach (var project in FindProjects())
        {
            var text = File.ReadAllText(project);
            if (text.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<OutputType>Exe", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<OutputType>WinExe", StringComparison.OrdinalIgnoreCase))
                return project;
        }
        return FindProjects().FirstOrDefault();
    }

    public IEnumerable<string> FindProjects() =>
        EnumerateFiles()
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);
}
