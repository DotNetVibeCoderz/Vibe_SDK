using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Unitree.Net.Wizard;

/// <summary>
/// The single application window. Everything visible is Blazor; WPF supplies the frame, the WebView2
/// host, and the native file dialogs.
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
        services.AddSingleton<WizardState>();
        services.AddSingleton<NativeDialogs>();

        _services = services.BuildServiceProvider();

        InitializeComponent();
        Host.Services = _services;
    }

    /// <inheritdoc />
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        var state = _services.GetRequiredService<WizardState>();

        // Unsaved work is the one thing worth interrupting a close for. Everything else — a running
        // build, an open chat — can be abandoned safely.
        if (state.HasUnsavedChanges)
        {
            MessageBoxResult answer = MessageBox.Show(
                "Save changes before closing?",
                "Unitree Robot Wizard",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                state.SaveAllAsync().GetAwaiter().GetResult();
            }
        }

        state.PersistSettings();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        try
        {
            _services.Dispose();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Shutdown failed: {exception}");
        }
    }
}
