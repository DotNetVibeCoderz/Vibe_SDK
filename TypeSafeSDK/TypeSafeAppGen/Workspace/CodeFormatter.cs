using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;

namespace TypeSafeAppGen.Workspace;

/// <summary>Hasil format: teks baru, atau teks asli dengan alasan kenapa tidak diformat.</summary>
public sealed record FormatResult(string Text, bool Changed, string Summary);

/// <summary>
/// Format Code per bahasa: C# lewat formatter Roslyn, JSON dan XML/XAML lewat parser bawaan .NET,
/// selebihnya hanya merapikan spasi di akhir baris. Line ending asli dipertahankan.
/// </summary>
public static class CodeFormatter
{
    private static readonly Lazy<AdhocWorkspace> RoslynWorkspace = new(() => new AdhocWorkspace());
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static FormatResult Format(string text, string extension)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        try
        {
            var (formatted, summary) = extension.ToLowerInvariant() switch
            {
                ".cs" => (FormatCSharp(text), "Formatted with the C# formatter."),
                ".json" => (FormatJson(text), "Formatted JSON."),
                ".csproj" or ".props" or ".targets" or ".xml" or ".axaml" or ".xaml" or ".slnx" or ".config" or ".resx" => (FormatXml(text), "Formatted XML."),
                _ => (TrimLines(text), "Trimmed trailing whitespace."),
            };
            formatted = NormalizeNewlines(formatted, newline);
            return new FormatResult(formatted, formatted != text, summary);
        }
        catch (Exception ex) when (ex is JsonException or XmlException)
        {
            return new FormatResult(text, false, $"Not formatted: the file has a syntax error ({ex.Message}).");
        }
    }

    private static string FormatCSharp(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var formatted = Formatter.Format(tree.GetRoot(), RoslynWorkspace.Value);
        return formatted.ToFullString();
    }

    private static string FormatJson(string text)
    {
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return node is null ? text : node.ToJsonString(IndentedJson) + "\n";
    }

    private static string FormatXml(string text)
    {
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        // Buang whitespace lama di antara elemen supaya indentasi baru konsisten, tapi biarkan teks isi.
        foreach (var node in document.DescendantNodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value) && t is not XCData).ToList())
            node.Remove();
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = document.Declaration is null, NewLineChars = "\n" };
        using var writer = new StringWriter();
        using (var xml = XmlWriter.Create(writer, settings)) document.Save(xml);
        return writer.ToString() + "\n";
    }

    private static string TrimLines(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd()));

    private static string NormalizeNewlines(string text, string newline) =>
        text.Replace("\r\n", "\n").Replace("\n", newline);
}
