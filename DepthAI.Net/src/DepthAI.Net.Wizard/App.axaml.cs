using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using DepthAI.Wizard.App.Views;

namespace DepthAI.Wizard.App;

public partial class App : Application
{
    /// <summary>Nama berkas tempat preferensi UI disimpan, di samping app.config.</summary>
    private const string ThemeFileName = "theme.txt";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Mencari brush bertema menurut kunci. Dipakai kode yang membangun tampilan di
    /// C# alih-alih XAML, sehingga warnanya tetap bersumber dari satu palet.
    /// </summary>
    public static IBrush Resource(string key)
    {
        if (Current is not null
            && Current.TryGetResource(key, Current.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            RequestedThemeVariant = LoadThemePreference();

            // Argumen pertama, bila berupa folder, dibuka sebagai proyek. Ini membuat
            // wizard bisa dipanggil dari terminal atau file manager seperti editor lain.
            var startupProject = desktop.Args?.FirstOrDefault(Directory.Exists);
            desktop.MainWindow = new MainWindow(startupProject);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Bergantian antara tema gelap dan terang, lalu menyimpan pilihannya.</summary>
    public static void ToggleTheme()
    {
        if (Current is null)
        {
            return;
        }

        var next = Current.ActualThemeVariant == ThemeVariant.Dark
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        Current.RequestedThemeVariant = next;
        SaveThemePreference(next);
    }

    private static ThemeVariant LoadThemePreference()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ThemeFileName);

        try
        {
            // Gelap adalah bawaan: alat pengembangan biasanya dipakai berjam-jam,
            // sering di ruangan redup.
            return File.Exists(path) && File.ReadAllText(path).Trim() == "Light"
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }
        catch (IOException)
        {
            return ThemeVariant.Dark;
        }
    }

    private static void SaveThemePreference(ThemeVariant variant)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, ThemeFileName),
                variant == ThemeVariant.Light ? "Light" : "Dark");
        }
        catch (IOException)
        {
            // Preferensi tema tidak cukup penting untuk mengganggu pengguna bila gagal disimpan.
        }
    }
}
