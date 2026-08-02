namespace DepthAI.Wizard.Projects;

/// <summary>Bentuk aplikasi yang dihasilkan template.</summary>
public enum ProjectKind
{
    /// <summary>Aplikasi konsol lintas platform.</summary>
    Console,

    /// <summary>Aplikasi desktop Avalonia.</summary>
    Desktop,

    /// <summary>Aplikasi web ASP.NET Core (Blazor atau Web API).</summary>
    Web,
}

/// <summary>Kategori untuk mengelompokkan template di galeri.</summary>
public enum TemplateCategory
{
    Blank,
    Detection,
    Depth,
    Analytics,
    Safety,
    Industri,
    Web,
}

/// <summary>Satu berkas yang dihasilkan template.</summary>
/// <param name="RelativePath">Path relatif terhadap akar proyek.</param>
/// <param name="Content">Isi berkas; token <c>{{ProjectName}}</c> diganti saat scaffolding.</param>
public sealed record TemplateFile(string RelativePath, string Content);

/// <summary>
/// Cetak biru proyek yang bisa dihasilkan wizard.
/// </summary>
public sealed record ProjectTemplate
{
    /// <summary>Pengenal stabil yang dipakai CLI dan API wizard.</summary>
    public required string Id { get; init; }

    /// <summary>Judul yang tampil di galeri.</summary>
    public required string Title { get; init; }

    /// <summary>Judul dalam Bahasa Inggris, untuk UI dwibahasa.</summary>
    public string TitleEnglish { get; init; } = string.Empty;

    /// <summary>Satu kalimat penjelas: apa yang dibangun template ini.</summary>
    public required string Description { get; init; }

    public string DescriptionEnglish { get; init; } = string.Empty;

    public required ProjectKind Kind { get; init; }

    public required TemplateCategory Category { get; init; }

    /// <summary>Emoji yang mewakili template di galeri.</summary>
    public string Icon { get; init; } = "📦";

    /// <summary>Kemampuan perangkat yang dibutuhkan, ditampilkan sebagai chip di galeri.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Berkas yang dihasilkan.</summary>
    public required IReadOnlyList<TemplateFile> Files { get; init; }

    /// <summary>Langkah lanjutan yang ditampilkan setelah proyek dibuat.</summary>
    public IReadOnlyList<string> NextSteps { get; init; } = [];
}
