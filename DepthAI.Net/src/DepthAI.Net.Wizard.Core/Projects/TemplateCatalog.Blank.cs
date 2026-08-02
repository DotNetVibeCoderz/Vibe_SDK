using System.Text;

namespace DepthAI.Wizard.Projects;

/// <summary>
/// Fragmen berkas yang dipakai berulang oleh template. Dikumpulkan di satu tempat
/// supaya versi paket dan struktur proyek hanya perlu diperbarui sekali.
/// </summary>
/// <remarks>
/// Isi template ditulis sebagai raw string tanpa interpolasi: teksnya mengandung token
/// <c>{{ProjectName}}</c> yang akan bentrok dengan sintaks interpolasi C#. Bagian dinamis
/// disusun lewat penggabungan string biasa.
/// </remarks>
internal static class TemplateFragments
{
    /// <summary>Versi SDK yang dirujuk proyek hasil generate.</summary>
    public const string SdkVersion = "0.1.0";

    /// <summary>
    /// Placeholder yang diganti scaffolder dengan referensi SDK yang sesuai —
    /// PackageReference untuk pemakaian normal, ProjectReference saat wizard
    /// berjalan dari dalam repo SDK.
    /// </summary>
    public const string SdkReferenceToken = "<!--SDK_REFERENCE-->";

    private const string ConsoleHead = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>{{ProjectNamespace}}</RootNamespace>
          </PropertyGroup>

          <ItemGroup>
        """;

    private const string DesktopHead = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <BuiltInComInteropSupport>true</BuiltInComInteropSupport>
            <ApplicationManifest>app.manifest</ApplicationManifest>
            <RootNamespace>{{ProjectNamespace}}</RootNamespace>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="Avalonia" Version="12.1.1" />
            <PackageReference Include="Avalonia.Desktop" Version="12.1.1" />
            <PackageReference Include="Avalonia.Themes.Fluent" Version="12.1.1" />
            <PackageReference Include="Avalonia.Fonts.Inter" Version="12.1.1" />
        """;

    private const string WebHead = """
        <Project Sdk="Microsoft.NET.Sdk.Web">

          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>{{ProjectNamespace}}</RootNamespace>
          </PropertyGroup>

          <ItemGroup>
        """;

    private const string CsprojTail = """

          </ItemGroup>

        </Project>
        """;

    public static string ConsoleCsproj(params string[] extraPackages)
        => BuildCsproj(ConsoleHead, extraPackages);

    public static string DesktopCsproj(params string[] extraPackages)
        => BuildCsproj(DesktopHead, extraPackages);

    public static string WebCsproj(params string[] extraPackages)
        => BuildCsproj(WebHead, extraPackages);

    private static string BuildCsproj(string head, string[] extraPackages)
    {
        var builder = new StringBuilder(head);
        builder.AppendLine();
        builder.Append("    ").Append(SdkReferenceToken);

        foreach (var package in extraPackages)
        {
            builder.AppendLine().Append("    ").Append(package);
        }

        builder.Append(CsprojTail);
        return builder.ToString();
    }

    public const string AppManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
          <assemblyIdentity version="1.0.0.0" name="{{ProjectName}}.Desktop" />
          <application xmlns="urn:schemas-microsoft-com:asm.v3">
            <windowsSettings>
              <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware>
            </windowsSettings>
          </application>
        </assembly>
        """;

    /// <summary>Bootstrap Avalonia yang sama untuk semua template desktop.</summary>
    public const string AvaloniaProgram = """
        using Avalonia;

        namespace {{ProjectNamespace}};

        internal static class Program
        {
            [STAThread]
            public static void Main(string[] args) => BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);

            public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
        }
        """;

    public const string AvaloniaAppAxaml = """
        <Application xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     x:Class="{{ProjectNamespace}}.App"
                     RequestedThemeVariant="Dark">
          <Application.Styles>
            <FluentTheme />
          </Application.Styles>
        </Application>
        """;

    public const string AvaloniaAppCode = """
        using Avalonia;
        using Avalonia.Controls.ApplicationLifetimes;
        using Avalonia.Markup.Xaml;

        namespace {{ProjectNamespace}};

        public partial class App : Application
        {
            public override void Initialize() => AvaloniaXamlLoader.Load(this);

            public override void OnFrameworkInitializationCompleted()
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.MainWindow = new MainWindow();
                }

                base.OnFrameworkInitializationCompleted();
            }
        }
        """;

    private const string ReadmeTail = """


        > Dibuat dengan **Jack The Code Bender** — DepthAI.Net oleh Gravicode Studios, dipimpin Kang Fadhil.

        ## Menjalankan / Running

        ```bash
        dotnet run
        ```

        ## Tanpa kamera OAK / Without an OAK camera

        SDK otomatis memakai backend simulasi bila runtime native tidak ditemukan, jadi
        aplikasi ini tetap berjalan dan menampilkan data sintetis. Untuk memaksa mode
        simulasi walaupun ada hardware, pakai `DepthAiOptions.Simulated`.

        The SDK falls back to a simulation backend when the native runtime is missing, so
        this app still runs and shows synthetic data. To force simulation even with
        hardware attached, pass `DepthAiOptions.Simulated`.
        """;

    /// <summary>Menyusun README dwibahasa dengan deskripsi khusus template.</summary>
    public static string Readme(string indonesian, string english)
        => "# {{ProjectName}}" + Environment.NewLine + Environment.NewLine
            + indonesian + Environment.NewLine + Environment.NewLine
            + "*" + english + "*" + Environment.NewLine
            + ReadmeTail;

    public const string GitIgnore = """
        bin/
        obj/
        .vs/
        *.user
        capture/
        """;
}
