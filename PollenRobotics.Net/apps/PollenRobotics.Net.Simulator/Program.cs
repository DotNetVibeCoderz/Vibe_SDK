using System.Runtime.InteropServices;
using Avalonia;
using PollenRobotics.Net.Simulator.Hosting;

namespace PollenRobotics.Net.Simulator;

/// <summary>
/// Entry point.
/// </summary>
/// <remarks>
/// Kestrel starts first and on a background thread, then the main thread is handed to Avalonia.
/// Both frameworks want to own the process lifetime and neither will yield, so the order matters:
/// Avalonia's <c>StartWithClassicDesktopLifetime</c> blocks until the window closes, and anything
/// started after it never runs.
/// </remarks>
internal static partial class Program
{
    /// <summary>Starts the simulator.</summary>
    [STAThread]
    public static void Main(string[] args)
    {
        HideConsoleWindow();

        try
        {
            SimulatorApp.Viewport = ViewportHost.StartAsync(SimulatorApp.Engine).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // The window is still worth showing without the viewport: the status and log panels
            // work, and the log will say why the 3D is missing.
            SimulatorApp.Engine.Log.Error("viewport", $"Could not start the 3D viewport host: {ex.Message}");
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Builds the Avalonia application. Named by convention for the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<SimulatorApp>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();

    /// <summary>
    /// Hides the console window this process is given because it is built as a console app.
    /// </summary>
    /// <remarks>
    /// See the note on OutputType in the project file: the Web SDK will not emit the framework's
    /// static web assets for a WinExe, so the simulator is a console app that hides its console.
    /// The window is only present when the process owns one - launched from a terminal it inherits
    /// that terminal instead, and <c>GetConsoleWindow</c> returns the terminal's handle, so this
    /// only hides a console the process created for itself.
    /// </remarks>
    private static void HideConsoleWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            nint console = GetConsoleWindow();

            if (console != nint.Zero && GetConsoleProcessList(new uint[2], 2) <= 1)
            {
                const int Hide = 0;
                ShowWindow(console, Hide);
            }
        }
        catch (DllNotFoundException)
        {
            // No console subsystem. Nothing to hide.
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleProcessList([Out] uint[] processList, uint count);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);
}
