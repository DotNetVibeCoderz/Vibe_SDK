using System.Text.RegularExpressions;

namespace DepthAI.Wizard.Projects;

/// <summary>Cara proyek hasil generate merujuk SDK DepthAI.Net.</summary>
public enum SdkReferenceMode
{
    /// <summary>PackageReference ke paket NuGet yang dipublikasikan.</summary>
    Package,

    /// <summary>
    /// ProjectReference ke proyek SDK di disk. Dipakai saat wizard berjalan dari dalam
    /// repo SDK, supaya proyek baru bisa langsung di-build tanpa paket yang dipublikasikan.
    /// </summary>
    Project,
}

/// <summary>Opsi pembuatan proyek.</summary>
public sealed record ScaffoldOptions
{
    /// <summary>Nama proyek; juga menjadi nama direktori dan assembly.</summary>
    public required string ProjectName { get; init; }

    /// <summary>Direktori induk tempat folder proyek dibuat.</summary>
    public required string ParentDirectory { get; init; }

    /// <summary>Template yang dipakai.</summary>
    public required ProjectTemplate Template { get; init; }

    public SdkReferenceMode SdkReference { get; init; } = SdkReferenceMode.Package;

    /// <summary>Akar repo SDK; wajib bila <see cref="SdkReference"/> adalah Project.</summary>
    public string? SdkRepositoryRoot { get; init; }

    /// <summary>Menimpa direktori yang sudah ada alih-alih menolak.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>Hasil pembuatan proyek.</summary>
public sealed record ScaffoldResult(string ProjectDirectory, string ProjectFile, IReadOnlyList<string> CreatedFiles);

/// <summary>
/// Menulis template ke disk, mengganti token, dan menyambungkan referensi SDK.
/// </summary>
public static partial class ProjectScaffolder
{
    /// <summary>
    /// Nama berkas solusi SDK yang menandai akar repo. Dua ekstensi diperiksa karena
    /// .NET 10 membuat solusi berformat XML <c>.slnx</c>, sementara repo lama memakai <c>.sln</c>.
    /// </summary>
    private static readonly string[] SolutionFileNames = ["DepthAI.Net.slnx", "DepthAI.Net.sln"];

    /// <summary>Proyek SDK yang dirujuk saat memakai <see cref="SdkReferenceMode.Project"/>.</summary>
    private static readonly (string PackageId, string RelativePath)[] SdkProjects =
    [
        ("DepthAI.Net", @"src\DepthAI.Net.Core\DepthAI.Net.Core.csproj"),
        ("DepthAI.Net.Imaging.ImageSharp", @"src\DepthAI.Net.Imaging.ImageSharp\DepthAI.Net.Imaging.ImageSharp.csproj"),
        ("DepthAI.Net.Imaging.SkiaSharp", @"src\DepthAI.Net.Imaging.SkiaSharp\DepthAI.Net.Imaging.SkiaSharp.csproj"),
    ];

    /// <summary>Membuat proyek dari template.</summary>
    public static async Task<ScaffoldResult> CreateAsync(
        ScaffoldOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateName(options.ProjectName);

        var directory = Path.Combine(options.ParentDirectory, options.ProjectName);

        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any() && !options.Overwrite)
        {
            throw new IOException(
                $"Direktori '{directory}' sudah ada dan tidak kosong. Pilih nama lain atau aktifkan penimpaan.");
        }

        Directory.CreateDirectory(directory);

        var projectNamespace = ToNamespace(options.ProjectName);
        var created = new List<string>();

        foreach (var file in options.Template.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = ApplyTokens(file.RelativePath, options.ProjectName, projectNamespace);
            var fullPath = Path.Combine(directory, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            var content = ApplyTokens(file.Content, options.ProjectName, projectNamespace);
            content = ApplySdkReference(content, options);

            await File.WriteAllTextAsync(fullPath, content, cancellationToken);
            created.Add(relativePath);
        }

        var projectFile = created.FirstOrDefault(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Template '{options.Template.Id}' tidak menghasilkan berkas .csproj.");

        return new ScaffoldResult(directory, Path.Combine(directory, projectFile), created);
    }

    /// <summary>
    /// Mencari akar repo SDK dengan menelusuri direktori induk. Mengembalikan null bila
    /// wizard tidak dijalankan dari dalam repo — kondisi normal untuk instalasi biasa.
    /// </summary>
    public static string? FindSdkRepositoryRoot(string? startDirectory = null)
    {
        var directory = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);

        while (directory is not null)
        {
            foreach (var solution in SolutionFileNames)
            {
                if (File.Exists(Path.Combine(directory.FullName, solution)))
                {
                    return directory.FullName;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Memeriksa nama proyek sebelum berkas apa pun ditulis.</summary>
    public static void ValidateName(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        if (projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "Nama proyek mengandung karakter yang tidak boleh dipakai pada nama berkas.", nameof(projectName));
        }

        if (!ValidNamePattern().IsMatch(projectName))
        {
            throw new ArgumentException(
                "Nama proyek harus diawali huruf dan hanya boleh berisi huruf, angka, titik, garis bawah, dan tanda hubung.",
                nameof(projectName));
        }
    }

    /// <summary>Mengubah nama proyek menjadi namespace C# yang valid.</summary>
    public static string ToNamespace(string projectName)
    {
        var segments = projectName
            .Split(['-', ' ', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static s => char.ToUpperInvariant(s[0]) + s[1..]);

        var candidate = string.Concat(segments);

        // Namespace tidak boleh diawali angka; awalan huruf menjaga hasil tetap bisa dikompilasi.
        return char.IsLetter(candidate[0]) ? candidate : "App" + candidate;
    }

    private static string ApplyTokens(string content, string projectName, string projectNamespace)
        => content
            .Replace("{{ProjectName}}", projectName, StringComparison.Ordinal)
            .Replace("{{ProjectNamespace}}", projectNamespace, StringComparison.Ordinal);

    /// <summary>
    /// Mengganti placeholder referensi SDK dengan PackageReference atau ProjectReference.
    /// </summary>
    private static string ApplySdkReference(string content, ScaffoldOptions options)
    {
        if (!content.Contains(TemplateFragments.SdkReferenceToken, StringComparison.Ordinal)
            && !content.Contains("DepthAI.Net.Imaging", StringComparison.Ordinal))
        {
            return content;
        }

        if (options.SdkReference == SdkReferenceMode.Package)
        {
            return content.Replace(
                TemplateFragments.SdkReferenceToken,
                $"""<PackageReference Include="DepthAI.Net" Version="{TemplateFragments.SdkVersion}" />""",
                StringComparison.Ordinal);
        }

        var root = options.SdkRepositoryRoot
            ?? throw new InvalidOperationException(
                "SdkReferenceMode.Project dipilih tapi SdkRepositoryRoot tidak diisi.");

        var projectDirectory = Path.Combine(options.ParentDirectory, options.ProjectName);

        var coreReference = ProjectReferenceFor("DepthAI.Net", root, projectDirectory);
        content = content.Replace(TemplateFragments.SdkReferenceToken, coreReference, StringComparison.Ordinal);

        // Paket adapter imaging dirujuk langsung oleh sebagian template, jadi
        // PackageReference-nya juga ditukar menjadi ProjectReference.
        foreach (var (packageId, _) in SdkProjects.Where(p => p.PackageId != "DepthAI.Net"))
        {
            var packageReference =
                $"""<PackageReference Include="{packageId}" Version="{TemplateFragments.SdkVersion}" />""";

            content = content.Replace(
                packageReference,
                ProjectReferenceFor(packageId, root, projectDirectory),
                StringComparison.Ordinal);
        }

        return content;
    }

    private static string ProjectReferenceFor(string packageId, string repositoryRoot, string projectDirectory)
    {
        var relative = SdkProjects.First(p => p.PackageId == packageId).RelativePath;
        var absolute = Path.Combine(repositoryRoot, relative);

        // Path relatif menjaga proyek tetap bisa dipindah bersama repo.
        var reference = Path.GetRelativePath(projectDirectory, absolute);
        return $"""<ProjectReference Include="{reference}" />""";
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._-]*$")]
    private static partial Regex ValidNamePattern();
}
