namespace DepthAI.Wizard.Projects;

public static partial class TemplateCatalog
{
    /// <summary>
    /// Menyusun berkas untuk aplikasi desktop Avalonia. Semua template desktop memakai
    /// kerangka yang sama dan hanya berbeda pada isi jendela, pipeline, dan langganan
    /// stream — jadi bagian itu saja yang di-parameterkan.
    /// </summary>
    private static IReadOnlyList<TemplateFile> DesktopShell(
        string title,
        string body,
        string codeBehind,
        string readmeId,
        string readmeEn,
        params string[] extraPackages)
    {
        var packages = new List<string>
        {
            """<PackageReference Include="DepthAI.Net.Imaging.SkiaSharp" Version="0.1.0" />""",
        };
        packages.AddRange(extraPackages);

        var window = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="{{ProjectNamespace}}.MainWindow"
                    Width="1100" Height="720"
                    Background="#0E1417"
                    Title="__TITLE__">
              <Grid RowDefinitions="Auto,*,Auto" Margin="16">

                <StackPanel Grid.Row="0" Spacing="2" Margin="0,0,0,12">
                  <TextBlock Text="__TITLE__" Foreground="#E8F1F2" FontSize="20" FontWeight="SemiBold" />
                  <TextBlock x:Name="Subtitle" Text="Menghubungkan…" Foreground="#8CA3AD" FontSize="12" />
                </StackPanel>

            __BODY__

                <Border Grid.Row="2" Margin="0,12,0,0" Padding="12,8"
                        Background="#141C21" CornerRadius="6">
                  <TextBlock x:Name="Status" Text="Siap" Foreground="#8CA3AD" FontSize="12" />
                </Border>

              </Grid>
            </Window>
            """
            .Replace("__TITLE__", title, StringComparison.Ordinal)
            .Replace("__BODY__", Indent(body, 4), StringComparison.Ordinal);

        return
        [
            new("{{ProjectName}}.csproj", TemplateFragments.DesktopCsproj([.. packages])),
            new("app.manifest", TemplateFragments.AppManifest),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(readmeId, readmeEn)),
            new("Program.cs", TemplateFragments.AvaloniaProgram),
            new("App.axaml", TemplateFragments.AvaloniaAppAxaml),
            new("App.axaml.cs", TemplateFragments.AvaloniaAppCode),
            new("MainWindow.axaml", window),
            new("MainWindow.axaml.cs", codeBehind),
        ];
    }

    /// <summary>
    /// Menghasilkan code-behind jendela. Pola siklus hidupnya sama di semua template:
    /// buka perangkat saat jendela muncul, langgan stream, lepaskan saat ditutup.
    /// </summary>
    private static string DesktopCodeBehind(string pipeline, string subscribe, string extraMembers = "")
        => """
            using Avalonia.Controls;
            using Avalonia.Media.Imaging;
            using Avalonia.Threading;
            using DepthAI;
            using DepthAI.Imaging;
            using DepthAI.Inference;
            using DepthAI.Pipelines;
            using DepthAI.Streaming;
            using SkiaSharp;

            namespace {{ProjectNamespace}};

            public partial class MainWindow : Window
            {
                private readonly List<IDisposable> _subscriptions = [];
                private DepthAiDevice? _device;

                public MainWindow()
                {
                    InitializeComponent();
                    Opened += OnOpened;
                    Closing += OnClosing;
                }

                private async void OnOpened(object? sender, EventArgs e)
                {
                    try
                    {
                        _device = await DepthAiDevice.OpenAsync();

                        Subtitle.Text = _device.IsSimulated
                            ? $"{_device.Info.Name} — data simulasi, tidak ada hardware terdeteksi"
                            : $"{_device.Info.Name} · {_device.Info.SerialNumber}";

                        var pipeline = __PIPELINE__;

                        await _device.StartAsync(pipeline);

            __SUBSCRIBE__

                        Status.Text = "Berjalan";
                    }
                    catch (Exception ex)
                    {
                        Status.Text = $"Gagal memulai: {ex.Message}";
                    }
                }

                private async void OnClosing(object? sender, WindowClosingEventArgs e)
                {
                    foreach (var subscription in _subscriptions)
                    {
                        subscription.Dispose();
                    }

                    _subscriptions.Clear();

                    if (_device is not null)
                    {
                        await _device.DisposeAsync();
                        _device = null;
                    }
                }

                /// <summary>
                /// Menampilkan frame pada sebuah Image. Konversi dilakukan di thread pemanggil
                /// lalu hanya penugasan bitmap yang dipindahkan ke UI thread, supaya render
                /// tidak menahan antrean dispatcher.
                /// </summary>
                private static void ShowFrame(Image target, ImageFrame frame)
                {
                    using var skia = frame.ToBitmap();
                    var bitmap = ToAvaloniaBitmap(skia);
                    Dispatcher.UIThread.Post(() => target.Source = bitmap);
                }

                private static void ShowDepth(Image target, DepthFrame frame)
                {
                    using var skia = frame.ToBitmap(DepthColorMap.Turbo, 0.3f, 4.5f);
                    var bitmap = ToAvaloniaBitmap(skia);
                    Dispatcher.UIThread.Post(() => target.Source = bitmap);
                }

                private static Bitmap ToAvaloniaBitmap(SKBitmap source)
                {
                    using var image = SKImage.FromBitmap(source);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    using var stream = new MemoryStream(data.ToArray());
                    return new Bitmap(stream);
                }
            __EXTRA__
            }
            """
            .Replace("__PIPELINE__", pipeline, StringComparison.Ordinal)
            .Replace("__SUBSCRIBE__", Indent(subscribe, 12), StringComparison.Ordinal)
            .Replace("__EXTRA__", extraMembers.Length > 0 ? Environment.NewLine + Indent(extraMembers, 4) : string.Empty,
                StringComparison.Ordinal);

    /// <summary>Menggeser tiap baris sejauh <paramref name="spaces"/>, menjaga baris kosong tetap kosong.</summary>
    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join(Environment.NewLine, lines.Select(l => l.Length == 0 ? l : pad + l));
    }
}
