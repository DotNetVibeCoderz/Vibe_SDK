using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PollenRobotics.Net.Simulation;
using PollenRobotics.Net.Simulator.Hosting;
using PollenRobotics.Net.Simulator.Views;
using PollenRobotics.Net.Ui.Infrastructure;

namespace PollenRobotics.Net.Simulator;

/// <summary>The simulator's Avalonia application.</summary>
public partial class SimulatorApp : Application
{
    /// <summary>The simulation both halves of the app share.</summary>
    public static SimulationEngine Engine { get; } = new();

    /// <summary>The in-process web host serving the 3D viewport.</summary>
    public static ViewportHost? Viewport { get; set; }

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        ThemeManager.ApplyStored();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();

            desktop.ShutdownRequested += async (_, _) =>
            {
                await Engine.DisposeAsync();

                if (Viewport is not null)
                {
                    await Viewport.DisposeAsync();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
