using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Unitree.Net.Simulation;

namespace Unitree.Net.Simulator;

/// <summary>
/// The single application window. Everything visible is Blazor; WPF supplies the frame and the
/// WebView2 host.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ServiceProvider _services;

    /// <summary>Creates the window and its Blazor service provider.</summary>
    public MainWindow()
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        // Constructed by hand rather than by type: the container will not fill a constructor's default
        // parameter values, so registering SimulationLog by type fails on its int capacity argument.
        services.AddSingleton(new SimulationLog(capacity: 800));
        services.AddSingleton<SimulationHost>();
        services.AddSingleton<SimulatorState>();

        _services = services.BuildServiceProvider();

        InitializeComponent();
        Host.Services = _services;
    }

    /// <inheritdoc />
    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // The simulation owns a multicast socket and a real-time thread. Leaving either running past
        // the window would keep the process alive with nothing on screen.
        try
        {
            await _services.GetRequiredService<SimulatorState>().DisposeAsync();
            await _services.DisposeAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Shutdown failed: {exception}");
        }
    }
}
