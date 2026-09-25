using System.Text.RegularExpressions;

namespace TypeSafeAppGen.Workspace;

public enum DiagnosticSeverity { Error, Warning }

/// <summary>Satu pesan compiler/MSBuild yang bisa diklik untuk membuka file di baris terkait.</summary>
public sealed record BuildDiagnostic(DiagnosticSeverity Severity, string Code, string Message, string? FilePath, int Line, int Column)
{
    public string Location => FilePath is null ? "" : $"{Path.GetFileName(FilePath)}({Line},{Column})";
}

/// <summary>Mengurai output <c>dotnet build</c> menjadi daftar diagnostic unik.</summary>
public static partial class BuildDiagnostics
{
    // Contoh: C:\app\Program.cs(12,5): error CS1002: ; expected [C:\app\App.csproj]
    [GeneratedRegex(@"^\s*(?<file>[^\s(][^(]*?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FileDiagnostic();

    // Contoh: MSBUILD : error MSB1009: Project file does not exist.
    [GeneratedRegex(@"^\s*(?<origin>[^:]+?)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ToolDiagnostic();

    public static BuildDiagnostic? ParseLine(string line)
    {
        var match = FileDiagnostic().Match(line);
        if (match.Success)
        {
            return new BuildDiagnostic(
                Severity(match.Groups["sev"].Value),
                match.Groups["code"].Value.ToUpperInvariant(),
                match.Groups["msg"].Value,
                match.Groups["file"].Value.Trim(),
                int.Parse(match.Groups["line"].Value),
                int.Parse(match.Groups["col"].Value));
        }
        match = ToolDiagnostic().Match(line);
        if (!match.Success) return null;
        return new BuildDiagnostic(Severity(match.Groups["sev"].Value), match.Groups["code"].Value.ToUpperInvariant(), match.Groups["msg"].Value, null, 0, 0);
    }

    /// <summary>dotnet build mencetak tiap diagnostic dua kali (saat kompilasi dan di ringkasan); duplikat dibuang.</summary>
    public static IReadOnlyList<BuildDiagnostic> Parse(string output)
    {
        var seen = new HashSet<BuildDiagnostic>();
        var list = new List<BuildDiagnostic>();
        foreach (var line in output.Split('\n'))
            if (ParseLine(line.TrimEnd('\r')) is { } diagnostic && seen.Add(diagnostic))
                list.Add(diagnostic);
        return list;
    }

    private static DiagnosticSeverity Severity(string value) =>
        value.Equals("error", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
}
