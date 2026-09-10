using PollenRobotics.Net.Core;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Wizard.Core.Projects;

namespace PollenRobotics.Net.Wizard.Core.Templates;

/// <summary>
/// The boilerplate every template shares: project file, editor config, readme.
/// </summary>
/// <remarks>
/// Kept in one place so that a change to the SDK version or the target framework lands in every
/// template at once. Templates own their behaviour code and nothing else.
/// </remarks>
internal static class ProjectScaffold
{
    /// <summary>The SDK version generated projects reference.</summary>
    /// <remarks>
    /// Read from the assembly rather than written down, so a release cannot bump the packages and
    /// leave every generated project pinned to the version before it.
    /// </remarks>
    public static string SdkVersion => SdkInfo.Version;

    /// <summary>
    /// The publisher prefix on the NuGet package IDs. Namespaces are unprefixed, so generated code
    /// says <c>using PollenRobotics.Net.ReachyMini;</c> while the project file references
    /// <c>Gravicode.PollenRobotics.Net.ReachyMini</c>. Keep this in step with
    /// <c>PackageIdPrefix</c> in Directory.Build.props.
    /// </summary>
    public const string PackageIdPrefix = "Gravicode.";

    /// <summary>Builds the .csproj.</summary>
    public static ProjectFile CsProj(string projectName, RobotKind robot, ProjectKind kind, params string[] extraPackages)
    {
        string sdk = kind == ProjectKind.Web ? "Microsoft.NET.Sdk.Web" : "Microsoft.NET.Sdk";

        var packages = new List<string> { PackageFor(robot), "PollenRobotics.Net.Simulation" };
        packages.AddRange(extraPackages);

        var lines = new List<string>
        {
            $"""<Project Sdk="{sdk}">""",
            string.Empty,
            "  <PropertyGroup>",
            "    <OutputType>Exe</OutputType>",
            "    <TargetFramework>net10.0</TargetFramework>",
            "    <Nullable>enable</Nullable>",
            "    <ImplicitUsings>enable</ImplicitUsings>",
            $"    <RootNamespace>{Sanitise(projectName)}</RootNamespace>",
            $"    <AssemblyName>{projectName}</AssemblyName>",
        };

        if (kind == ProjectKind.Embedded)
        {
            // The robots run 64-bit ARM Linux. Publishing for the host RID produces something that
            // copies across fine and then will not start.
            lines.Add("    <RuntimeIdentifiers>linux-arm64;linux-x64</RuntimeIdentifiers>");
            lines.Add("    <InvariantGlobalization>true</InvariantGlobalization>");
        }

        lines.Add("  </PropertyGroup>");
        lines.Add(string.Empty);
        lines.Add("  <ItemGroup>");
        lines.AddRange(packages.Distinct().Select(p =>
            $"""    <PackageReference Include="{PackageIdPrefix}{p}" Version="{SdkVersion}" />"""));
        lines.Add("  </ItemGroup>");
        lines.Add(string.Empty);
        lines.Add("</Project>");

        return new ProjectFile($"{projectName}.csproj", string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    /// <summary>Builds a README describing how to run the project.</summary>
    public static ProjectFile Readme(string projectName, RobotTemplate template) =>
        new("README.md", $"""
            # {projectName}

            {template.Description}

            Generated from the **{template.Name}** template by the PollenRobotics Robot Wizard
            (Gravicode Studios, led by Kang Fadhil).

            ## Run

            ```bash
            # Against the built-in simulator - no hardware needed.
            dotnet run -- --sim

            # Against a real {RobotCatalog.For(template.Robot).DisplayName}.
            dotnet run
            ```

            ## Before you run it on hardware

            {SafetyNote(template.Robot)}

            ## Jalankan (Bahasa Indonesia)

            Gunakan `dotnet run -- --sim` untuk menjalankan di simulator, atau `dotnet run` untuk
            terhubung ke robot sungguhan. Pastikan daemon robot sudah berjalan sebelum mencoba
            koneksi ke perangkat keras.
            """);

    /// <summary>Builds an app.config carrying the AI settings, for templates that use Jack.</summary>
    public static ProjectFile AppConfig() =>
        new("appsettings.json", """
            {
              "Ai": {
                "Provider": "OpenAI",
                "Model": "",
                "ApiKey": "",
                "Temperature": 0.3,
                "MaxTokens": 4096,
                "EnableFunctionCalling": true,
                "TavilyApiKey": ""
              }
            }
            """);

    /// <summary>The package that carries the client for a robot.</summary>
    public static string PackageFor(RobotKind robot) => robot switch
    {
        RobotKind.ReachyMini => "PollenRobotics.Net.ReachyMini",
        RobotKind.MicroDuck => "PollenRobotics.Net.MicroDuck",
        RobotKind.Reachy2 => "PollenRobotics.Net.Reachy2",
        _ => "PollenRobotics.Net",
    };

    private static string SafetyNote(RobotKind robot) => robot switch
    {
        RobotKind.ReachyMini =>
            "Give the head room to move. Head pitch and roll are limited to 40 degrees and the SDK "
            + "throws rather than clamping, so a pose that is out of range fails loudly rather than "
            + "quietly doing something else.",

        RobotKind.MicroDuck =>
            "Put the duck somewhere it can fall over safely. `InitAsync` powers the servos and the "
            + "duck stands up; `RelaxAsync` cuts power and it collapses where it is.",

        RobotKind.Reachy2 =>
            "Clear the workspace before turning the arms on. Turning them off makes them compliant, "
            + "so they sag and anything a gripper is holding will drop.",

        _ => "Check the robot has room to move before starting.",
    };

    /// <summary>Turns a project name into a valid C# namespace.</summary>
    public static string Sanitise(string projectName)
    {
        string cleaned = new([.. projectName.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_')]);
        return char.IsDigit(cleaned.FirstOrDefault()) ? "_" + cleaned : cleaned;
    }
}
