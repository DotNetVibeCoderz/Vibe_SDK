namespace PollenRobotics.Net.Simulator;

/// <summary>
/// The palette the 3D viewport should use, shared between the Avalonia shell and the Blazor page.
/// </summary>
/// <remarks>
/// The two halves of the simulator live in the same process but not in the same UI framework, so
/// the theme cannot simply be inherited. The shell writes here when the user switches theme, and
/// the viewport page reads it on load - which is what stops the 3D flashing the wrong palette
/// before the first frame arrives.
/// </remarks>
public static class ViewportTheme
{
    /// <summary>The palette name the JavaScript side understands: <c>dark</c> or <c>light</c>.</summary>
    public static string Current { get; private set; } = "dark";

    /// <summary>Raised when the palette changes, so an open viewport can follow.</summary>
    public static event Action<string>? Changed;

    /// <summary>Sets the palette. Anything other than <c>light</c> is treated as dark.</summary>
    public static void Set(string theme)
    {
        string resolved = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";

        if (resolved == Current)
        {
            return;
        }

        Current = resolved;
        Changed?.Invoke(resolved);
    }
}
