using Avalonia;
using Avalonia.Styling;

namespace PollenRobotics.Net.Ui.Infrastructure;

/// <summary>Which theme the applications are showing.</summary>
public enum AppTheme
{
    /// <summary>Follow the operating system.</summary>
    System,

    /// <summary>Warm graphite.</summary>
    Dark,

    /// <summary>Warm paper.</summary>
    Light,
}

/// <summary>
/// Switches the theme and remembers the choice between runs.
/// </summary>
/// <remarks>
/// The preference is stored per user rather than per application, so switching one of the three
/// tools switches all of them. They are meant to read as one product.
/// </remarks>
public static class ThemeManager
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GravicodeStudios",
        "PollenRobotics",
        "theme.txt");

    /// <summary>The theme currently applied.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>Raised after the theme changes.</summary>
    public static event Action<AppTheme>? Changed;

    /// <summary>Applies a theme and stores the preference.</summary>
    public static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        application.RequestedThemeVariant = theme switch
        {
            AppTheme.Dark => ThemeVariant.Dark,
            AppTheme.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };

        Current = theme;
        Save(theme);
        Changed?.Invoke(theme);
    }

    /// <summary>Cycles dark, light, system - the order a toggle button steps through.</summary>
    public static AppTheme Toggle()
    {
        AppTheme next = Current switch
        {
            AppTheme.Dark => AppTheme.Light,
            AppTheme.Light => AppTheme.System,
            _ => AppTheme.Dark,
        };

        Apply(next);
        return next;
    }

    /// <summary>Applies the stored preference, defaulting to dark.</summary>
    /// <remarks>
    /// Dark rather than system, because the two visual applications here spend most of their screen
    /// on a 3D viewport and a code editor, and both were designed against the dark palette first.
    /// </remarks>
    public static void ApplyStored()
    {
        AppTheme stored = AppTheme.Dark;

        try
        {
            if (File.Exists(PreferencePath) &&
                Enum.TryParse(File.ReadAllText(PreferencePath).Trim(), ignoreCase: true, out AppTheme parsed))
            {
                stored = parsed;
            }
        }
        catch (IOException)
        {
            // An unreadable preference file is not worth failing startup over.
        }

        Apply(stored);
    }

    /// <summary>The glyph a toggle button should show for the current theme.</summary>
    public static string Glyph(AppTheme theme) => theme switch
    {
        AppTheme.Dark => "◐",
        AppTheme.Light => "◑",
        _ => "◓",
    };

    /// <summary>The word a toggle button's tooltip should show.</summary>
    public static string Describe(AppTheme theme) => theme switch
    {
        AppTheme.Dark => "Dark theme",
        AppTheme.Light => "Light theme",
        _ => "Following the system theme",
    };

    private static void Save(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, theme.ToString());
        }
        catch (IOException)
        {
            // The preference is a convenience. Losing it is not worth surfacing.
        }
    }
}
