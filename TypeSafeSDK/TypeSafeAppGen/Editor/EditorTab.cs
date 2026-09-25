using System.Text;
using AvaloniaEdit.Document;

namespace TypeSafeAppGen.Editor;

/// <summary>
/// Satu file terbuka. <see cref="TextDocument"/> dipertahankan per tab sehingga undo stack, seleksi,
/// dan posisi scroll tidak hilang saat berpindah tab.
/// </summary>
public sealed class EditorTab
{
    private string _savedText;

    private EditorTab(string? path, string text)
    {
        FilePath = path;
        _savedText = text;
        Document = new TextDocument(text) { FileName = path };
        Document.TextChanged += (_, _) => DirtyChanged?.Invoke(this);
    }

    public string? FilePath { get; private set; }
    public TextDocument Document { get; }
    public string Title => FilePath is null ? "Untitled" : Path.GetFileName(FilePath);
    public bool IsDirty => Document.TextLength != _savedText.Length || Document.Text != _savedText;
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
    public DateTime LastWriteUtc { get; private set; }

    public event Action<EditorTab>? DirtyChanged;

    public static async Task<EditorTab> OpenAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path);
        return new EditorTab(path, text) { LastWriteUtc = File.GetLastWriteTimeUtc(path) };
    }

    public static EditorTab CreateUntitled(string text = "") => new(null, text);

    public async Task SaveAsync(string? newPath = null)
    {
        if (newPath is not null)
        {
            FilePath = newPath;
            Document.FileName = newPath;
        }
        if (FilePath is null) throw new InvalidOperationException("Choose a file name first.");
        var text = Document.Text;
        await File.WriteAllTextAsync(FilePath, text, new UTF8Encoding(false));
        _savedText = text;
        LastWriteUtc = File.GetLastWriteTimeUtc(FilePath);
        DirtyChanged?.Invoke(this);
    }

    /// <summary>Memuat ulang dari disk bila file diubah di luar editor (mis. oleh Jack) dan tab ini belum diedit.</summary>
    public async Task<bool> ReloadIfChangedAsync()
    {
        if (FilePath is null || !File.Exists(FilePath) || IsDirty) return false;
        var stamp = File.GetLastWriteTimeUtc(FilePath);
        if (stamp == LastWriteUtc) return false;
        var text = await File.ReadAllTextAsync(FilePath);
        LastWriteUtc = stamp;
        if (text == _savedText) return false;
        _savedText = text;
        // Replace dalam satu update agar undo tetap bisa mengembalikan versi sebelumnya.
        Document.Replace(0, Document.TextLength, text);
        DirtyChanged?.Invoke(this);
        return true;
    }

    public void Rename(string newPath)
    {
        FilePath = newPath;
        Document.FileName = newPath;
    }
}
