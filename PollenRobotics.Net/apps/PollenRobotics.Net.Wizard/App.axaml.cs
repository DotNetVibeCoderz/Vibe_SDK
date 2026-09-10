using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PollenRobotics.Net.Ui.Infrastructure;
using PollenRobotics.Net.Wizard.Views;

namespace PollenRobotics.Net.Wizard;

/// <summary>The Robot Wizard application.</summary>
public partial class WizardApp : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        ThemeManager.ApplyStored();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
