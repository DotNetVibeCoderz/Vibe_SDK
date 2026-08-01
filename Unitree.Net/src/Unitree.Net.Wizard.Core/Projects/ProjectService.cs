using System.Text;

namespace Unitree.Net.Wizard.Core.Projects;

/// <summary>
/// Creates, opens and enumerates wizard projects on disk.
/// </summary>
/// <remarks>
/// Generated projects reference the SDK by relative project path rather than by NuGet package, because
/// the SDK is not published yet. <see cref="SdkRootPath"/> is what makes that work from any folder,
/// and <see cref="TryLocateSdkRoot"/> finds it by walking up from the running application.
/// </remarks>
public sealed class ProjectService
{
    private static readonly string[] IgnoredDirectories = ["bin", "obj", ".git", ".vs", "node_modules"];

    /// <summary>Creates a service that resolves SDK references against <paramref name="sdkRootPath"/>.</summary>
    /// <param name="sdkRootPath">Absolute path to the Unitree.Net repository root.</param>
    public ProjectService(string sdkRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkRootPath);
        SdkRootPath = Path.GetFullPath(sdkRootPath);
    }

    /// <summary>The repository root that project references are resolved against.</summary>
    public string SdkRootPath { get; }

    /// <summary>
    /// Walks up from <paramref name="startPath"/> looking for the repository root.
    /// </summary>
    /// <param name="startPath">Where to start, usually the running application's directory.</param>
    /// <returns>The repository root, or <see langword="null"/> if it is not an ancestor.</returns>
    public static string? TryLocateSdkRoot(string startPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);

        for (DirectoryInfo? directory = new(startPath); directory is not null; directory = directory.Parent)
        {
            // The solution file plus a src folder is a much stronger signal than either alone — a bin
            // directory somewhere can easily contain a stray .slnx copy.
            if (File.Exists(Path.Combine(directory.FullName, "Unitree.Net.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a project from <paramref name="template"/>.
    /// </summary>
    /// <param name="parentDirectory">Directory the project folder is created inside.</param>
    /// <param name="projectName">Project, folder and assembly name.</param>
    /// <param name="template">The template to scaffold, or <see langword="null"/> for a blank project.</param>
    /// <param name="kind">Kind to use when <paramref name="template"/> is <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="projectName"/> is not a usable folder name.</exception>
    /// <exception cref="IOException">The target folder already exists and is not empty.</exception>
    public async Task<WizardProject> CreateAsync(
        string parentDirectory,
        string projectName,
        ProjectTemplate? template,
        ProjectKind kind = ProjectKind.Console)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ValidateProjectName(projectName);

        string root = Path.Combine(Path.GetFullPath(parentDirectory), projectName);

        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException($"'{root}' already exists and is not empty.");
        }

        Directory.CreateDirectory(root);

        ProjectKind effectiveKind = template?.Kind ?? kind;
        string projectFilePath = Path.Combine(root, $"{projectName}.csproj");

        await File.WriteAllTextAsync(
            projectFilePath,
            BuildProjectFile(projectName, effectiveKind, template, root),
            Encoding.UTF8);

        foreach (TemplateFile file in template?.Files ?? DefaultFiles(effectiveKind))
        {
            string target = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, file.Content, Encoding.UTF8);
        }

        await File.WriteAllTextAsync(
            Path.Combine(root, ".gitignore"),
            "bin/\nobj/\n*.user\n",
            Encoding.UTF8);

        await File.WriteAllTextAsync(
            Path.Combine(root, "README.md"),
            BuildReadme(projectName, effectiveKind, template),
            Encoding.UTF8);

        return new WizardProject(projectName, root, projectFilePath, effectiveKind, template?.Id);
    }

    /// <summary>
    /// Opens an existing project.
    /// </summary>
    /// <param name="projectFilePath">Path to a <c>.csproj</c>.</param>
    /// <exception cref="FileNotFoundException">No project file is there.</exception>
    public WizardProject Open(string projectFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);

        string full = Path.GetFullPath(projectFilePath);

        if (!File.Exists(full))
        {
            throw new FileNotFoundException("Project file not found.", full);
        }

        string root = Path.GetDirectoryName(full)!;
        string name = Path.GetFileNameWithoutExtension(full);
        string text = File.ReadAllText(full);

        // Inferred from the project file rather than remembered, so a project edited outside the
        // wizard still runs and deploys the right way.
        ProjectKind kind = text.Contains("Sdk=\"Microsoft.NET.Sdk.Web\"", StringComparison.Ordinal)
            ? ProjectKind.Web
            : text.Contains("<UseWPF>true</UseWPF>", StringComparison.Ordinal)
                ? ProjectKind.Desktop
                : text.Contains("<RuntimeIdentifier>linux-arm64", StringComparison.Ordinal)
                    ? ProjectKind.Embedded
                    : ProjectKind.Console;

        return new WizardProject(name, root, full, kind, TemplateId: null);
    }

    /// <summary>Lists the editable files in a project, excluding build output.</summary>
    /// <param name="project">The open project.</param>
    public IReadOnlyList<ProjectFile> EnumerateFiles(WizardProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (!Directory.Exists(project.RootPath))
        {
            return [];
        }

        var files = new List<ProjectFile>();

        foreach (string path in Directory.EnumerateFiles(project.RootPath, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(project.RootPath, path).Replace('\\', '/');

            if (IgnoredDirectories.Any(ignored =>
                    relative.StartsWith(ignored + '/', StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var info = new FileInfo(path);
            files.Add(new ProjectFile(relative, path, info.Length));
        }

        // The project file first, then everything else alphabetically: it is the file a person opens
        // to understand what they are looking at.
        return
        [
            .. files.OrderByDescending(file => file.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static void ValidateProjectName(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        if (projectName.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "A project name cannot contain path characters.", nameof(projectName));
        }

        // A name starting with a digit produces a namespace the compiler rejects, and the error
        // arrives much later and much less clearly than this one.
        if (char.IsDigit(projectName[0]))
        {
            throw new ArgumentException("A project name cannot start with a digit.", nameof(projectName));
        }
    }

    private string BuildProjectFile(
        string projectName,
        ProjectKind kind,
        ProjectTemplate? template,
        string projectRoot)
    {
        var text = new StringBuilder();

        text.AppendLine(kind == ProjectKind.Web
            ? "<Project Sdk=\"Microsoft.NET.Sdk.Web\">"
            : "<Project Sdk=\"Microsoft.NET.Sdk\">");

        text.AppendLine();
        text.AppendLine("  <PropertyGroup>");
        text.AppendLine("    <OutputType>Exe</OutputType>");
        text.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        text.AppendLine("    <Nullable>enable</Nullable>");
        text.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        text.AppendLine($"    <RootNamespace>{ToNamespace(projectName)}</RootNamespace>");

        if (kind == ProjectKind.Embedded)
        {
            text.AppendLine();
            text.AppendLine("    <!-- The robot's compute module is ARM64 Linux. Self-contained so the target");
            text.AppendLine("         needs no .NET installed, and invariant globalisation so it needs no ICU. -->");
            text.AppendLine("    <RuntimeIdentifier>linux-arm64</RuntimeIdentifier>");
            text.AppendLine("    <SelfContained>true</SelfContained>");
            text.AppendLine("    <PublishSingleFile>true</PublishSingleFile>");
            text.AppendLine("    <InvariantGlobalization>true</InvariantGlobalization>");
        }

        text.AppendLine("  </PropertyGroup>");

        IReadOnlyList<string> projectReferences = template?.ProjectReferencePaths ?? DefaultReferences();

        if (projectReferences.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("  <ItemGroup>");

            foreach (string reference in projectReferences)
            {
                string absolute = Path.Combine(SdkRootPath, reference.Replace('/', Path.DirectorySeparatorChar));
                string relative = Path.GetRelativePath(projectRoot, absolute);
                text.AppendLine($"    <ProjectReference Include=\"{relative}\" />");
            }

            text.AppendLine("  </ItemGroup>");
        }

        var packages = new List<string>(template?.PackageReferences ?? []);

        // Every template is a host application, and Hosting is what supplies configuration binding and
        // the service provider they all use.
        packages.Insert(0, "Microsoft.Extensions.Hosting");

        text.AppendLine();
        text.AppendLine("  <ItemGroup>");

        foreach (string package in packages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine($"    <PackageReference Include=\"{package}\" Version=\"10.0.10\" />");
        }

        text.AppendLine("  </ItemGroup>");

        text.AppendLine();
        text.AppendLine("  <ItemGroup>");
        text.AppendLine("    <None Update=\"appsettings.json\" CopyToOutputDirectory=\"PreserveNewest\" />");
        text.AppendLine("  </ItemGroup>");

        text.AppendLine();
        text.AppendLine("</Project>");

        return text.ToString();
    }

    private static IReadOnlyList<string> DefaultReferences() =>
    [
        "src/Unitree.Net.Core/Unitree.Net.Core.csproj",
        "src/Unitree.Net.Control/Unitree.Net.Control.csproj",
        "src/Unitree.Net.Sensors/Unitree.Net.Sensors.csproj",
        "src/Unitree.Net.Extensions.DependencyInjection/Unitree.Net.Extensions.DependencyInjection.csproj",
    ];

    private static IReadOnlyList<TemplateFile> DefaultFiles(ProjectKind kind) =>
    [
        new TemplateFile("Program.cs", kind == ProjectKind.Web
            ? """
using Unitree.Net.Control;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddUnitreeRobot(builder.Configuration);
builder.Services.AddUnitreeRobotHostedConnection();

WebApplication app = builder.Build();

app.MapGet("/", (UnitreeRobot robot) => new { model = robot.Model.ToString(), state = robot.State.ToString() });

app.Run();
"""
            : """
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Sensors;

// A blank robot application. It connects and prints one snapshot.
//
// Start the simulator first, or point Unitree:MulticastAddress at a real robot.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);

using IHost host = builder.Build();

var robot = host.Services.GetRequiredService<UnitreeRobot>();
var telemetry = host.Services.GetRequiredService<TelemetryHub>();

await robot.ConnectAsync();
Console.WriteLine($"Connected to {robot.Model}.");

if (telemetry.GetSnapshot() is { } snapshot)
{
    Console.WriteLine($"Battery {snapshot.Battery.StateOfCharge}%, {snapshot.FootContact.ContactCount}/4 feet loaded.");
}

await robot.DisconnectAsync();
"""),
        new TemplateFile("appsettings.json", """
{
  "Unitree": {
    "Model": "Go2",
    "Transport": "ManagedMulticast",
    "MulticastAddress": "239.255.0.1",
    "MulticastPort": 7447
  },
  "Logging": { "LogLevel": { "Default": "Information" } }
}
"""),
    ];

    private static string BuildReadme(string projectName, ProjectKind kind, ProjectTemplate? template)
    {
        var text = new StringBuilder();

        text.AppendLine($"# {projectName}");
        text.AppendLine();
        text.AppendLine(template?.Summary ?? "A Unitree.Net robot application.");
        text.AppendLine();
        text.AppendLine("Generated by the Unitree Robot Wizard — Gravicode Studios, led by Kang Fadhil.");
        text.AppendLine();
        text.AppendLine("## Running it");
        text.AppendLine();
        text.AppendLine("No robot is needed. Start the simulator, then run this:");
        text.AppendLine();
        text.AppendLine("```bash");
        text.AppendLine("dotnet run --project apps/Unitree.Net.Simulator   # or the VirtualRobot sample");
        text.AppendLine("dotnet run");
        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("Point `Unitree:MulticastAddress` in `appsettings.json` at a real robot when you have one.");

        if (kind == ProjectKind.Embedded)
        {
            text.AppendLine();
            text.AppendLine("## Deploying to the robot");
            text.AppendLine();
            text.AppendLine("This project publishes self-contained for `linux-arm64`, so the robot needs no .NET runtime:");
            text.AppendLine();
            text.AppendLine("```bash");
            text.AppendLine("dotnet publish -c Release");
            text.AppendLine("```");
            text.AppendLine();
            text.AppendLine("The wizard's **Deploy** command does this and copies the result over SSH.");
        }

        text.AppendLine();
        text.AppendLine("## Before running on real hardware");
        text.AppendLine();
        text.AppendLine("Read `docs/safety.md`. Nothing in this SDK has been validated against a physical robot yet.");

        return text.ToString();
    }

    /// <summary>Turns a project name into a valid C# namespace.</summary>
    /// <param name="projectName">The project name.</param>
    public static string ToNamespace(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        var text = new StringBuilder(projectName.Length);

        foreach (char character in projectName)
        {
            text.Append(char.IsLetterOrDigit(character) || character == '.' ? character : '_');
        }

        return char.IsDigit(text[0]) ? "_" + text : text.ToString();
    }
}
