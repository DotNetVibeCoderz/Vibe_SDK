using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace PollenRobotics.Net.Simulator.Views;

/// <summary>
/// Hosts the viewport page inside the window on Windows, and reports honestly when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia has no cross-platform web view. On Windows, WebView2 can be parented to the native
/// handle a <see cref="NativeControlHost"/> exposes, which puts the 3D viewport inside the
/// simulator window where it belongs. Everywhere else - and on a Windows machine without the
/// WebView2 runtime - <see cref="IsAvailable"/> is false and the shell falls back to opening the
/// viewport in the default browser.
/// </para>
/// <para>
/// The fallback is a real path, not an apology. The viewport is a web page served over loopback; a
/// browser renders it identically, and on a machine with no WebView2 runtime that is the difference
/// between a working tool and a broken one.
/// </para>
/// </remarks>
public sealed class EmbeddedBrowser : NativeControlHost
{
    private object? _controller;
    private IPlatformHandle? _handle;

    /// <summary>The page to show.</summary>
    public static readonly StyledProperty<Uri?> SourceProperty =
        AvaloniaProperty.Register<EmbeddedBrowser, Uri?>(nameof(Source));

    /// <inheritdoc cref="SourceProperty" />
    public Uri? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>Raised when embedding fails, with the reason, so the shell can fall back.</summary>
    public event Action<string>? EmbeddingFailed;

    /// <summary>
    /// True when an in-window browser can be created here.
    /// </summary>
    /// <remarks>
    /// Windows only, and only when the WebView2 runtime is installed. Checking the runtime rather
    /// than just the OS matters: the package restores fine on any Windows machine and then throws
    /// at construction if the runtime is missing.
    /// </remarks>
    public static bool IsAvailable { get; } = DetectWebView2();

    private static bool DetectWebView2()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return !string.IsNullOrEmpty(
                Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception)
        {
            // WebView2Loader missing, or no runtime installed. Either way there is no in-window
            // browser to be had.
            return false;
        }
    }

    /// <inheritdoc />
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _handle = base.CreateNativeControlCore(parent);

        if (IsAvailable && OperatingSystem.IsWindows())
        {
            _ = AttachAsync(_handle.Handle);
        }

        return _handle;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task AttachAsync(nint parentHandle)
    {
        try
        {
            Microsoft.Web.WebView2.Core.CoreWebView2Environment environment =
                await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                    userDataFolder: Path.Combine(Path.GetTempPath(), "PollenRoboticsSimulator.WebView2"));

            Microsoft.Web.WebView2.Core.CoreWebView2Controller controller =
                await environment.CreateCoreWebView2ControllerAsync(parentHandle);

            _controller = controller;

            controller.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            controller.CoreWebView2.Settings.IsStatusBarEnabled = false;
            controller.CoreWebView2.Settings.AreDevToolsEnabled = true;

            UpdateBounds();

            if (Source is { } source)
            {
                controller.CoreWebView2.Navigate(source.ToString());
            }
        }
        catch (Exception ex)
        {
            EmbeddingFailed?.Invoke(ex.Message);
        }
    }

    /// <inheritdoc />
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_controller is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _controller = null;
        base.DestroyNativeControlCore(control);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        Size size = base.ArrangeOverride(finalSize);
        UpdateBounds();
        return size;
    }

    /// <summary>
    /// Keeps the WebView2 controller the same size as this control.
    /// </summary>
    /// <remarks>
    /// The controller does not follow its parent HWND on its own. Without this the page renders at
    /// whatever size it had when it was created and never resizes - which looks like a viewport
    /// that has frozen rather than one that is simply the wrong shape.
    /// </remarks>
    private void UpdateBounds()
    {
        if (_controller is not Microsoft.Web.WebView2.Core.CoreWebView2Controller controller)
        {
            return;
        }

        double scale = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;

        try
        {
            controller.Bounds = new System.Drawing.Rectangle(
                0,
                0,
                Math.Max(1, (int)(Bounds.Width * scale)),
                Math.Max(1, (int)(Bounds.Height * scale)));
        }
        catch (COMException)
        {
            // The controller has already been torn down.
        }
    }

    /// <summary>Opens a URL in the operating system's default browser.</summary>
    public static void OpenExternally(Uri address)
    {
        try
        {
            Process.Start(new ProcessStartInfo(address.ToString()) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser configured. The address is on screen for the user to copy.
        }
    }
}
