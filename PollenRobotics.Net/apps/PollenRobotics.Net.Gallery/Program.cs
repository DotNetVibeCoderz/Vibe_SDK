using Avalonia;

namespace PollenRobotics.Net.Gallery;

/// <summary>Entry point for the gallery.</summary>
internal static class Program
{
    /// <summary>Starts the application.</summary>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>Builds the Avalonia application. Named by convention for the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
