using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DepthAI.Wizard.Build;

namespace DepthAI.Wizard.App.Views;

/// <summary>Dialog lompat ke baris tertentu.</summary>
public partial class GoToLineDialog : Window
{
    private readonly int _lineCount;

    /// <summary>Konstruktor tanpa argumen dibutuhkan pemuat XAML pada masa desain.</summary>
    public GoToLineDialog() : this(1) { }

    public GoToLineDialog(int lineCount)
    {
        InitializeComponent();

        _lineCount = Math.Max(1, lineCount);
        RangeText.Text = $"Nomor baris (1 sampai {_lineCount})";

        Opened += (_, _) =>
        {
            LineBox.Focus();
            LineBox.SelectAll();
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void OnGo(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept()
    {
        if (int.TryParse(LineBox.Text, CultureInfo.InvariantCulture, out var line))
        {
            Close(Math.Clamp(line, 1, _lineCount));
            return;
        }

        RangeText.Text = $"Masukkan angka antara 1 dan {_lineCount}.";
        RangeText.Foreground = App.Resource("SignalError");
    }
}

/// <summary>Pilihan yang dikumpulkan dialog deploy.</summary>
public sealed record DeployOptions(string OutputDirectory, string? RuntimeIdentifier, bool SelfContained);

/// <summary>Dialog konfigurasi publish.</summary>
public partial class DeployDialog : Window
{
    private const string PortableOption = "Portable (butuh .NET terpasang)";

    public DeployDialog() : this(Path.Combine(Environment.CurrentDirectory, "publish")) { }

    public DeployDialog(string defaultOutput)
    {
        InitializeComponent();

        OutputBox.Text = defaultOutput;

        var current = DotnetRunner.CurrentRuntimeIdentifier;
        var identifiers = new List<string>
        {
            PortableOption,
            current,
        };

        // RID lain yang lazim ditawarkan agar deploy silang tidak perlu diketik manual.
        identifiers.AddRange(new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" }
            .Where(rid => rid != current));

        RidBox.ItemsSource = identifiers;
        RidBox.SelectedIndex = 1;
    }

    private void OnDeploy(object? sender, RoutedEventArgs e)
    {
        var rid = RidBox.SelectedItem as string;

        Close(new DeployOptions(
            OutputBox.Text ?? "publish",
            rid == PortableOption ? null : rid,
            SelfContainedBox.IsChecked ?? false));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}

/// <summary>Dialog Tentang, memuat kredit dan status runtime.</summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        WizardVersion.Text = typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        SdkVersion.Text = DepthAi.Version;
        DotnetVersion.Text = Environment.Version.ToString();

        NativeStatus.Text = DepthAi.IsNativeAvailable
            ? $"tersedia — depthai-core {DepthAi.NativeVersion}"
            : $"tidak tersedia — {DepthAi.NativeUnavailableReason}";

        NativeStatus.Foreground = DepthAi.IsNativeAvailable
            ? App.Resource("AccentMid")
            : App.Resource("SignalWarning");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
