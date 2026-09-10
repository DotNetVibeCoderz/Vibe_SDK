using Avalonia;

namespace PollenRobotics.Net.Wizard;

/// <summary>Entry point for the Robot Wizard.</summary>
internal static class Program
{
    /// <summary>A project folder passed on the command line, opened once the window is up.</summary>
    public static string? StartupProject { get; private set; }

    /// <summary>Starts the application.</summary>
    /// <param name="args">
    /// Pass a project folder, or <c>--project &lt;folder&gt;</c>, to open it on startup. Useful for
    /// scripting the wizard and for jumping straight back into the project you were last in.
    /// </param>
    [STAThread]
    public static void Main(string[] args)
    {
        int flag = Array.IndexOf(args, "--project");

        StartupProject = flag >= 0 && flag + 1 < args.Length
            ? args[flag + 1]
            : args.FirstOrDefault(a => !a.StartsWith('-') && Directory.Exists(a));

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Builds the Avalonia application. Named by convention for the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<WizardApp>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
