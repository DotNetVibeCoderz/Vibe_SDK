using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeSafeAppGen.Workspace;

/// <summary>Satu template proyek dari <c>Templates/&lt;id&gt;/template.json</c>.</summary>
public sealed record ProjectTemplate
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string UseCase { get; init; } = "";
    public string Stack { get; init; } = "";
    /// <summary>Kerangka bersama di <c>Templates/_base/&lt;Base&gt;</c> yang disalin lebih dulu (mis. avalonia, console).</summary>
    public string? Base { get; init; }
    /// <summary>File yang dibuka di editor setelah proyek dibuat.</summary>
    public string MainFile { get; init; } = "Program.cs";

    internal string Folder { get; init; } = "";
}

/// <summary>Katalog template dan scaffolding proyek baru (Blank maupun From Template).</summary>
public static partial class ProjectTemplates
{
    public const string BlankId = "blank";
    public const string NamePlaceholder = "__ProjectName__";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static IReadOnlyList<ProjectTemplate>? _cache;

    public static string TemplatesRoot => Path.Combine(AppContext.BaseDirectory, "Templates");

    /// <summary>Urutan kategori di galeri; kategori yang tidak dikenal ditaruh di akhir.</summary>
    public static readonly string[] CategoryOrder = ["3D Graphics", "Animation", "Game", "Simulator", "Web", "AI", "Console"];

    public static IReadOnlyList<ProjectTemplate> All(string? root = null)
    {
        if (root is null && _cache is not null) return _cache;
        root ??= TemplatesRoot;
        var list = new List<ProjectTemplate>();
        if (Directory.Exists(root))
        {
            foreach (var manifest in Directory.EnumerateFiles(root, "template.json", SearchOption.AllDirectories))
            {
                var folder = Path.GetDirectoryName(manifest)!;
                if (Path.GetFileName(Path.GetDirectoryName(folder)!) == "_base") continue;
                var template = JsonSerializer.Deserialize<ProjectTemplate>(File.ReadAllText(manifest), JsonOptions);
                if (template is not null) list.Add(template with { Folder = folder });
            }
        }
        var ordered = list
            .OrderBy(t => Array.IndexOf(CategoryOrder, t.Category) is var i && i < 0 ? int.MaxValue : i)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (root == TemplatesRoot) _cache = ordered;
        return ordered;
    }

    public static ProjectTemplate? Find(string id, string? root = null) =>
        All(root).FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Nama proyek jadi identifier C# yang valid: huruf/angka, diawali huruf.</summary>
    public static string ToIdentifier(string name)
    {
        var builder = new StringBuilder();
        var upperNext = true;
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(upperNext ? char.ToUpperInvariant(c) : c);
                upperNext = false;
            }
            else upperNext = true;
        }
        if (builder.Length == 0) return "App";
        if (char.IsDigit(builder[0])) builder.Insert(0, "App");
        return builder.ToString();
    }

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]")]
    private static partial Regex InvalidFolderChars();

    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a project name.";
        if (InvalidFolderChars().IsMatch(name)) return "The name contains characters that are not allowed in a folder name.";
        if (name.Trim().Length > 64) return "Keep the name under 64 characters.";
        return null;
    }

    /// <summary>
    /// Membuat folder proyek baru dan menyalin file template dengan placeholder <see cref="NamePlaceholder"/>
    /// diganti identifier proyek. Mengembalikan path file utama yang dibuka di editor.
    /// </summary>
    public static string Scaffold(string templateId, string projectName, string parentFolder, string? root = null)
    {
        root ??= TemplatesRoot;
        if (ValidateName(projectName) is { } error) throw new ArgumentException(error, nameof(projectName));
        var target = Path.Combine(parentFolder, projectName.Trim());
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException($"The folder '{target}' already exists and is not empty.");

        var template = templateId == BlankId
            ? new ProjectTemplate { Id = BlankId, Name = "Blank", Base = "console", MainFile = "Program.cs", Folder = "" }
            : Find(templateId, root) ?? throw new ArgumentException($"Unknown template '{templateId}'.", nameof(templateId));

        var identifier = ToIdentifier(projectName);
        Directory.CreateDirectory(target);
        if (template.Base is { } baseName) CopyTree(Path.Combine(root, "_base", baseName), target, identifier);
        if (template.Folder.Length > 0) CopyTree(template.Folder, target, identifier);
        return Path.Combine(target, template.MainFile.Replace(NamePlaceholder, identifier));
    }

    private static void CopyTree(string source, string target, string identifier)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Template folder missing: {source}");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Equals("template.json", StringComparison.OrdinalIgnoreCase)) continue;
            // Template disimpan dengan akhiran .txt agar tidak ikut dikompilasi/di-restore oleh AppGen.
            if (relative.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) relative = relative[..^4];
            var destination = Path.Combine(target, relative.Replace(NamePlaceholder, identifier));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var content = File.ReadAllText(file).Replace(NamePlaceholder, identifier);
            File.WriteAllText(destination, content, new UTF8Encoding(false));
        }
    }
}
