using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace TypeSafeAppGen.Editor;

/// <summary>
/// Opsi registry TextMate: grammar bawaan TextMateSharp, tetapi tema default diganti tema "Patina"
/// agar warna sintaks satu keluarga dengan warna aplikasi.
/// </summary>
public sealed class PatinaRegistryOptions : IRegistryOptions
{
    private readonly RegistryOptions _inner = new(ThemeName.DarkPlus);
    private readonly Lazy<IRawTheme> _theme = new(LoadTheme);

    public IRawTheme GetTheme(string scopeName) => _inner.GetTheme(scopeName);
    public IRawGrammar GetGrammar(string scopeName) => _inner.GetGrammar(scopeName);
    public ICollection<string> GetInjections(string scopeName) => _inner.GetInjections(scopeName);
    public IRawTheme GetDefaultTheme() => _theme.Value;

    /// <summary>Scope grammar untuk sebuah file, atau null bila tidak ada highlighter yang cocok.</summary>
    public string? ScopeFor(string? path)
    {
        var extension = Path.GetExtension(path ?? "").ToLowerInvariant();
        extension = extension switch
        {
            ".axaml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".slnx" or ".resx" or ".config" or ".manifest" => ".xml",
            ".razor" => ".cshtml",
            ".http" => ".txt",
            _ => extension,
        };
        if (extension.Length == 0) return null;
        try { return _inner.GetScopeByExtension(extension); }
        catch (Exception) { return null; }
    }

    private static IRawTheme LoadTheme()
    {
        using var stream = typeof(PatinaRegistryOptions).Assembly.GetManifestResourceStream("TypeSafeAppGen.Editor.patina-theme.json")
            ?? throw new InvalidOperationException("Embedded theme patina-theme.json is missing.");
        using var reader = new StreamReader(stream);
        return ThemeReader.ReadThemeSync(reader);
    }
}

/// <summary>Nama bahasa untuk status bar.</summary>
public static class Languages
{
    public static string DisplayName(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() switch
    {
        ".cs" => "C#",
        ".axaml" => "Avalonia XAML",
        ".xaml" => "XAML",
        ".razor" => "Razor",
        ".cshtml" => "Razor Page",
        ".csproj" or ".props" or ".targets" => "MSBuild",
        ".slnx" => "Solution",
        ".json" => "JSON",
        ".xml" or ".resx" or ".config" => "XML",
        ".md" => "Markdown",
        ".css" => "CSS",
        ".js" => "JavaScript",
        ".ts" => "TypeScript",
        ".html" or ".htm" => "HTML",
        ".py" => "Python",
        ".sql" => "SQL",
        ".yml" or ".yaml" => "YAML",
        ".http" => "HTTP",
        ".ps1" => "PowerShell",
        ".sh" => "Shell",
        _ => "Plain text",
    };
}
