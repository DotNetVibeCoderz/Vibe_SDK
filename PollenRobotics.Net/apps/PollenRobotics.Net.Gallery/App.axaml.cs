using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PollenRobotics.Net.Gallery.Views;
using PollenRobotics.Net.Ui.Infrastructure;

namespace PollenRobotics.Net.Gallery;

/// <summary>The gallery application.</summary>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        // The stored preference is shared across all three tools, so switching the theme in one
        // switches it everywhere.
        ThemeManager.ApplyStored();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
