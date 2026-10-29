using Avalonia;
using DepthAI.Gallery.Showcase;

namespace DepthAI.Gallery;

/// <summary>
/// Titik masuk DepthAI.Net Gallery.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>--record [use-case]</c>: merekam ulang hasil dari kamera ke <c>Samples/recorded.json</c>.</item>
/// <item><c>--live-check [use-case…]</c>: menguji mode kamera live tanpa jendela dan tanpa menyimpan frame.</item>
/// <item><c>--screenshot &lt;folder&gt;</c>: membuka jendela, memotret tiap use case, lalu keluar.</item>
/// <item><c>--usecase &lt;id&gt;</c>, <c>--light</c>: use case awal dan tema terang, untuk tangkapan layar.</item>
/// </list>
/// </remarks>
internal static class Program
{
    public static GalleryLaunchOptions Options { get; private set; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        var record = Array.IndexOf(args, "--record");
        if (record >= 0)
        {
            var only = record + 1 < args.Length && !args[record + 1].StartsWith("--", StringComparison.Ordinal) ? args[record + 1] : null;
            return Recorder.RunAsync(only).GetAwaiter().GetResult();
        }

        var liveCheck = Array.IndexOf(args, "--live-check");
        if (liveCheck >= 0)
        {
            return Recorder.LiveCheckAsync([.. args.Skip(liveCheck + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))]).GetAwaiter().GetResult();
        }

        Options = GalleryLaunchOptions.Parse(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}

/// <summary>Pilihan baris perintah untuk mode jendela.</summary>
internal sealed record GalleryLaunchOptions
{
    public string? ScreenshotDirectory { get; init; }

    public string? InitialUseCase { get; init; }

    public bool Light { get; init; }

    /// <summary>Use case yang dipotret pada mode tangkapan layar; kosong berarti semua.</summary>
    public IReadOnlyList<string> ScreenshotUseCases { get; init; } = [];

    public static GalleryLaunchOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        return new GalleryLaunchOptions
        {
            ScreenshotDirectory = Value("--screenshot"),
            InitialUseCase = Value("--usecase"),
            Light = args.Contains("--light"),
            ScreenshotUseCases = Value("--shots")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
        };
    }
}
