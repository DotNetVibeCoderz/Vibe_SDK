using System.Collections.ObjectModel;
using Avalonia.Media;
using DepthAI.Wizard.Build;

namespace DepthAI.Wizard.App.Views;

/// <summary>Simpul pada pohon berkas proyek.</summary>
public sealed class FileNode
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required bool IsDirectory { get; init; }

    public ObservableCollection<FileNode> Children { get; } = [];

    /// <summary>Ikon yang menandai jenis berkas sekilas.</summary>
    public string Icon => IsDirectory
        ? "📁"
        : Path.GetExtension(Name).ToLowerInvariant() switch
        {
            ".cs" => "📘",
            ".csproj" or ".sln" => "🧩",
            ".json" => "🧾",
            ".md" => "📖",
            ".axaml" or ".xaml" => "🎨",
            ".razor" => "⚡",
            ".config" or ".xml" => "⚙️",
            ".png" or ".jpg" or ".jpeg" => "🖼️",
            _ => "📄",
        };

    /// <summary>Direktori yang tidak berguna ditampilkan di penjelajah proyek.</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", ".git", ".idea", "node_modules",
    };

    /// <summary>Membangun pohon berkas dari sebuah direktori.</summary>
    public static FileNode Build(string directory)
    {
        var node = new FileNode
        {
            Name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)),
            FullPath = directory,
            IsDirectory = true,
        };

        try
        {
            foreach (var child in Directory.EnumerateDirectories(directory).OrderBy(d => d))
            {
                if (!Hidden.Contains(Path.GetFileName(child)))
                {
                    node.Children.Add(Build(child));
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f))
            {
                node.Children.Add(new FileNode
                {
                    Name = Path.GetFileName(file),
                    FullPath = file,
                    IsDirectory = false,
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Folder yang tidak bisa dibaca dilewati, bukan menjatuhkan seluruh pohon.
        }

        return node;
    }
}

/// <summary>Berkas yang sedang terbuka di editor.</summary>
public sealed class OpenFile
{
    public required string FullPath { get; init; }

    public required string Text { get; set; }

    /// <summary>True bila isi di editor berbeda dari isi di disk.</summary>
    public bool IsDirty { get; set; }

    public bool IsActive { get; set; }

    public string DisplayName => (IsDirty ? "● " : string.Empty) + Path.GetFileName(FullPath);

    /// <summary>Sintaks AvaloniaEdit yang cocok untuk ekstensi berkas ini.</summary>
    public string? SyntaxName => Path.GetExtension(FullPath).ToLowerInvariant() switch
    {
        ".cs" => "C#",
        ".json" => "JavaScript",
        ".xml" or ".csproj" or ".axaml" or ".xaml" or ".config" => "XML",
        ".md" => "MarkDown",
        ".html" or ".razor" => "HTML",
        ".css" => "CSS",
        _ => null,
    };

    public IBrush TabBackground => IsActive
        ? App.Resource("EditorBackground")
        : App.Resource("SurfaceDeep");

    public IBrush TabForeground => IsActive
        ? App.Resource("InkBright")
        : App.Resource("InkMuted");
}

/// <summary>Satu baris pada panel Logs.</summary>
public sealed class LogEntry
{
    public required string Time { get; init; }

    public required string Text { get; init; }

    public required LogLevel Level { get; init; }

    public IBrush Color => Level switch
    {
        LogLevel.Error => App.Resource("SignalError"),
        LogLevel.Warning => App.Resource("SignalWarning"),
        LogLevel.Success => App.Resource("AccentMid"),
        _ => App.Resource("InkMuted"),
    };

    /// <summary>Batang tipis di kiri; menandai keparahan tanpa mewarnai seluruh teks.</summary>
    public IBrush Accent => Level == LogLevel.Info ? Brushes.Transparent : Color;

    public static LogEntry From(LogLine line) => new()
    {
        Time = line.Timestamp.ToString("HH:mm:ss"),
        Text = line.Text,
        Level = line.Level,
    };
}
