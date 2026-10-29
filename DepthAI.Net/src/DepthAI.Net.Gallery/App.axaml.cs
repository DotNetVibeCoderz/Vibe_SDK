using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using DepthAI.Gallery.Views;

namespace DepthAI.Gallery;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Brush tema aktif; abu-abu bila kuncinya tidak ada, supaya salah ketik terlihat.</summary>
    public static IBrush Resource(string key)
        => Current is not null && Current.TryGetResource(key, Current.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Gray;

    public override void OnFrameworkInitializationCompleted()
    {
        if (Program.Options.Light)
        {
            RequestedThemeVariant = ThemeVariant.Light;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
