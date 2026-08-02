using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;

namespace DepthAI.Wizard.App.Views;

/// <summary>
/// Menerapkan warna editor kode yang sesuai tema.
/// </summary>
/// <remarks>
/// AvaloniaEdit memuat definisi highlighting bawaan yang warnanya dirancang untuk latar
/// putih: keyword biru tua, string merah tua, komentar hijau tua. Di atas permukaan gelap
/// aplikasi ini, warna-warna itu nyaris tidak terbaca. Definisi bawaannya karena itu
/// diwarnai ulang, bukan dipakai apa adanya.
/// </remarks>
internal static class EditorTheme
{
    /// <summary>Definisi yang diwarnai ulang; sisanya dibiarkan memakai bawaan.</summary>
    private static readonly string[] ManagedDefinitions = ["C#", "XML", "JavaScript", "MarkDown", "HTML", "CSS"];

    /// <summary>
    /// Palet gelap: cerah dan jenuh, dipilih agar kontras di atas #111A1F.
    /// </summary>
    /// <remarks>
    /// Kuncinya adalah nama warna yang benar-benar ada pada definisi AvaloniaEdit —
    /// bukan tebakan. Nama yang meleset gagal tanpa suara: warnanya tetap bawaan, dan
    /// hasilnya persis yang sempat terjadi di sini, yaitu <c>await</c> dan <c>var</c>
    /// (ContextKeywords, Navy) serta isi string interpolasi (StringInterpolation, Black)
    /// yang tak terbaca di atas latar gelap.
    /// </remarks>
    private static readonly Dictionary<string, string> DarkPalette = new(StringComparer.OrdinalIgnoreCase)
    {
        // C#
        ["Comment"] = "#7A8F9B",
        ["String"] = "#8CD98C",
        ["StringInterpolation"] = "#E8F1F2",
        ["Char"] = "#8CD98C",
        ["Preprocessor"] = "#C792EA",
        ["Punctuation"] = "#AFC2CC",
        ["ValueTypeKeywords"] = "#4FD6BE",
        ["ReferenceTypeKeywords"] = "#4FD6BE",
        ["MethodCall"] = "#F5D67B",
        ["NumberLiteral"] = "#F2A03D",
        ["ThisOrBaseReference"] = "#C792EA",
        ["NullOrValueKeywords"] = "#C792EA",
        ["Keywords"] = "#6FB6F5",
        ["GotoKeywords"] = "#C792EA",
        ["ContextKeywords"] = "#6FB6F5",
        ["ExceptionKeywords"] = "#F08FA0",
        ["CheckedKeyword"] = "#6FB6F5",
        ["UnsafeKeywords"] = "#E3B341",
        ["OperatorKeywords"] = "#C792EA",
        ["ParameterModifiers"] = "#6FB6F5",
        ["Modifiers"] = "#6FB6F5",
        ["Visibility"] = "#6FB6F5",
        ["NamespaceKeywords"] = "#6FB6F5",
        ["GetSetAddRemove"] = "#6FB6F5",
        ["TrueFalse"] = "#C792EA",
        ["TypeKeywords"] = "#4FD6BE",
        ["SemanticKeywords"] = "#C792EA",

        // XML, AXAML, csproj
        ["XmlTag"] = "#6FB6F5",
        ["XmlDeclaration"] = "#C792EA",
        ["AttributeName"] = "#F5D67B",
        ["AttributeValue"] = "#8CD98C",
        ["Entity"] = "#F2A03D",
        ["BrokenEntity"] = "#F08FA0",
        ["CData"] = "#8CD98C",
        ["DocType"] = "#C792EA",

        // JavaScript — definisi ini juga yang dipakai berkas JSON, termasuk pipeline.
        ["Digits"] = "#F2A03D",
        ["Regex"] = "#8CD98C",
        ["Character"] = "#8CD98C",
        ["JavaScriptKeyWords"] = "#6FB6F5",
        ["JavaScriptIntrinsics"] = "#4FD6BE",
        ["JavaScriptLiterals"] = "#C792EA",
        ["JavaScriptGlobalFunctions"] = "#F5D67B",

        // Markdown
        ["Heading"] = "#6FB6F5",
        ["Emphasis"] = "#F5D67B",
        ["StrongEmphasis"] = "#F2A03D",
        ["Code"] = "#8CD98C",
        ["Link"] = "#6FB4E8",
        ["Image"] = "#6FB4E8",
        ["BlockQuote"] = "#7A8F9B",
        ["LineBreak"] = "#AFC2CC",
    };

    /// <summary>Palet terang: nada gelap yang tetap kontras di atas putih.</summary>
    private static readonly Dictionary<string, string> LightPalette = new(StringComparer.OrdinalIgnoreCase)
    {
        // C#
        ["Comment"] = "#4A5B66",
        ["String"] = "#0A6E3D",
        ["StringInterpolation"] = "#10191E",
        ["Char"] = "#0A6E3D",
        ["Preprocessor"] = "#6B2FA8",
        ["Punctuation"] = "#3A4A54",
        ["ValueTypeKeywords"] = "#0A6157",
        ["ReferenceTypeKeywords"] = "#0A6157",
        ["MethodCall"] = "#7A5A00",
        ["NumberLiteral"] = "#A0530A",
        ["ThisOrBaseReference"] = "#6B2FA8",
        ["NullOrValueKeywords"] = "#6B2FA8",
        ["Keywords"] = "#0A4F9E",
        ["GotoKeywords"] = "#6B2FA8",
        ["ContextKeywords"] = "#0A4F9E",
        ["ExceptionKeywords"] = "#B02742",
        ["CheckedKeyword"] = "#0A4F9E",
        ["UnsafeKeywords"] = "#875F06",
        ["OperatorKeywords"] = "#6B2FA8",
        ["ParameterModifiers"] = "#0A4F9E",
        ["Modifiers"] = "#0A4F9E",
        ["Visibility"] = "#0A4F9E",
        ["NamespaceKeywords"] = "#0A4F9E",
        ["GetSetAddRemove"] = "#0A4F9E",
        ["TrueFalse"] = "#6B2FA8",
        ["TypeKeywords"] = "#0A6157",
        ["SemanticKeywords"] = "#6B2FA8",

        // XML, AXAML, csproj
        ["XmlTag"] = "#0A4F9E",
        ["XmlDeclaration"] = "#6B2FA8",
        ["AttributeName"] = "#7A5A00",
        ["AttributeValue"] = "#0A6E3D",
        ["Entity"] = "#A0530A",
        ["BrokenEntity"] = "#B02742",
        ["CData"] = "#0A6E3D",
        ["DocType"] = "#6B2FA8",

        // JavaScript dan JSON
        ["Digits"] = "#A0530A",
        ["Regex"] = "#0A6E3D",
        ["Character"] = "#0A6E3D",
        ["JavaScriptKeyWords"] = "#0A4F9E",
        ["JavaScriptIntrinsics"] = "#0A6157",
        ["JavaScriptLiterals"] = "#6B2FA8",
        ["JavaScriptGlobalFunctions"] = "#7A5A00",

        // Markdown
        ["Heading"] = "#0A4F9E",
        ["Emphasis"] = "#7A5A00",
        ["StrongEmphasis"] = "#A0530A",
        ["Code"] = "#0A6E3D",
        ["Link"] = "#1F5E96",
        ["Image"] = "#1F5E96",
        ["BlockQuote"] = "#4A5B66",
        ["LineBreak"] = "#3A4A54",
    };

    /// <summary>
    /// Menerapkan warna permukaan dan highlighting sesuai tema yang aktif.
    /// Aman dipanggil berulang kali; dipanggil lagi setiap tema berganti.
    /// </summary>
    public static void Apply(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

        editor.Background = App.Resource("EditorBackground");
        editor.Foreground = App.Resource("InkBright");

        var textArea = editor.TextArea;
        textArea.Background = App.Resource("EditorBackground");

        // Kursor harus terlihat di atas kedua permukaan; warna teks utama selalu cukup.
        textArea.Caret.CaretBrush = App.Resource("AccentNear");

        textArea.SelectionBrush = new SolidColorBrush(
            isDark ? Color.FromArgb(90, 79, 155, 217) : Color.FromArgb(70, 42, 109, 168));
        textArea.SelectionForeground = App.Resource("InkBright");

        // Nomor baris dijaga tetap redup tapi terbaca: terlalu redup membuat navigasi
        // ke baris tertentu jadi menyiksa.
        editor.LineNumbersForeground = App.Resource("InkMuted");

        ApplyHighlightingPalette(isDark ? DarkPalette : LightPalette);

        // Definisi yang sudah terpasang perlu ditetapkan ulang agar editor
        // menggambar ulang dengan warna baru.
        var current = editor.SyntaxHighlighting;
        editor.SyntaxHighlighting = null;
        editor.SyntaxHighlighting = current;
    }

    /// <summary>
    /// Mewarnai ulang definisi highlighting bawaan.
    /// </summary>
    /// <remarks>
    /// <see cref="HighlightingManager.Instance"/> adalah singleton, jadi perubahan ini
    /// berlaku untuk semua editor di aplikasi — yang memang diinginkan, karena temanya
    /// juga berlaku untuk seluruh aplikasi.
    /// </remarks>
    private static void ApplyHighlightingPalette(Dictionary<string, string> palette)
    {
        foreach (var name in ManagedDefinitions)
        {
            IHighlightingDefinition? definition;

            try
            {
                definition = HighlightingManager.Instance.GetDefinition(name);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                continue;
            }

            if (definition is null)
            {
                continue;
            }

            foreach (var color in definition.NamedHighlightingColors)
            {
                if (color.Name is not null
                    && palette.TryGetValue(color.Name, out var hex)
                    && Color.TryParse(hex, out var parsed))
                {
                    color.Foreground = new SimpleHighlightingBrush(parsed);
                }
            }
        }
    }
}
