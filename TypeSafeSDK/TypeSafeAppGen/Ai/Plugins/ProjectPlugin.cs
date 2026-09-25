using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using TypeSafeAppGen.Workspace;

namespace TypeSafeAppGen.Ai.Plugins;

/// <summary>Build, paket NuGet, dan template: fungsi yang membuat Jack bisa menghasilkan aplikasi yang benar-benar jalan.</summary>
public sealed partial class ProjectPlugin(IJackHost host)
{
    /// <summary>Template <c>dotnet new</c> bawaan SDK yang boleh dipakai Jack.</summary>
    private static readonly string[] AllowedDotnetTemplates = ["console", "classlib", "web", "webapi", "blazor", "worker", "xunit", "mstest", "razorclasslib", "grpc", "sln"];

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex SafeToken();

    private ProjectWorkspace Workspace => host.Workspace
        ?? throw new InvalidOperationException("No project is open. Create one with create_project_from_template first.");

    [KernelFunction("build_project"), Description("Run dotnet build on the open project and return the result with every error and warning (file, line, code, message). Call after writing code and fix all errors.")]
    public async Task<string> BuildProjectAsync(CancellationToken ct = default)
    {
        var workspace = Workspace;
        var target = workspace.FindBuildTarget();
        if (target is null) return "No .csproj, .sln or .slnx found. Create a project file first.";
        var result = await host.RunDotnetAsync("Build", ["build", target, "-nologo", "-clp:NoSummary"], workspace.Root, ct);
        var diagnostics = BuildDiagnostics.Parse(result.Output);
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();

        var report = new StringBuilder();
        report.AppendLine(result.Succeeded
            ? $"Build succeeded in {result.Duration.TotalSeconds:0.0}s with {warnings.Count} warning(s)."
            : $"Build FAILED with {errors.Count} error(s) and {warnings.Count} warning(s).");
        foreach (var d in errors.Concat(warnings).Take(60))
        {
            var where = d.FilePath is null ? "" : $"{workspace.Relative(d.FilePath)}({d.Line},{d.Column}) ";
            report.AppendLine($"{where}{d.Severity.ToString().ToLowerInvariant()} {d.Code}: {d.Message}");
        }
        if (!result.Succeeded && errors.Count == 0) report.AppendLine(Tail(result.Output, 3000));
        return report.ToString();
    }

    [KernelFunction("run_tests"), Description("Run dotnet test on the open project and return the summary and any failures.")]
    public async Task<string> RunTestsAsync(CancellationToken ct = default)
    {
        var workspace = Workspace;
        var target = workspace.FindBuildTarget();
        if (target is null) return "No project file found.";
        var result = await host.RunDotnetAsync("Test", ["test", target, "-nologo"], workspace.Root, ct);
        return (result.Succeeded ? "Tests passed.\n" : "Tests FAILED.\n") + Tail(result.Output, 6000);
    }

    [KernelFunction("add_nuget_package"), Description("Add a NuGet package reference to a project in the workspace.")]
    public async Task<string> AddPackageAsync(
        [Description("Package id, for example Avalonia or Microsoft.EntityFrameworkCore.Sqlite.")] string packageId,
        [Description("Exact version, or empty for the latest stable.")] string version = "",
        [Description("Project file relative to the root; empty for the main project.")] string project = "",
        CancellationToken ct = default)
    {
        var workspace = Workspace;
        if (!SafeToken().IsMatch(packageId) || (version.Length > 0 && !SafeToken().IsMatch(version))) return "Invalid package id or version.";
        var projectPath = string.IsNullOrWhiteSpace(project) ? workspace.FindProjects().FirstOrDefault() : workspace.Resolve(project);
        if (projectPath is null) return "No .csproj found.";
        List<string> args = ["add", projectPath, "package", packageId];
        if (version.Length > 0) args.AddRange(["--version", version]);
        var result = await host.RunDotnetAsync($"Add {packageId}", args, workspace.Root, ct);
        workspace.NotifyChanged(projectPath);
        return result.Succeeded ? $"Added {packageId} {version}".TrimEnd() + $" to {workspace.Relative(projectPath)}." : "dotnet add package failed:\n" + Tail(result.Output, 3000);
    }

    [KernelFunction("dotnet_new"), Description("Create a .NET project inside the open workspace with a built-in dotnet new template: console, classlib, web, webapi, blazor, worker, xunit, mstest, razorclasslib, grpc, sln.")]
    public async Task<string> DotnetNewAsync(
        [Description("Template short name.")] string template,
        [Description("Project name, also used as the sub-folder.")] string name,
        CancellationToken ct = default)
    {
        var workspace = Workspace;
        if (!AllowedDotnetTemplates.Contains(template, StringComparer.OrdinalIgnoreCase)) return $"Template not allowed. Use one of: {string.Join(", ", AllowedDotnetTemplates)}.";
        if (!SafeToken().IsMatch(name)) return "Use letters, digits, dots, dashes, or underscores for the name.";
        List<string> args = template.Equals("sln", StringComparison.OrdinalIgnoreCase)
            ? ["new", "sln", "-n", name]
            : ["new", template, "-n", name, "-o", name, "-f", "net10.0"];
        var result = await host.RunDotnetAsync($"dotnet new {template}", args, workspace.Root, ct);
        workspace.NotifyChanged(workspace.Root);
        return result.Succeeded ? $"Created {template} project '{name}'.\n{Tail(result.Output, 800)}" : "dotnet new failed:\n" + Tail(result.Output, 3000);
    }

    [KernelFunction("list_templates"), Description("List the App Generator's ready-made app templates (3D graphics, animation, games, simulators, web, AI) with ids and descriptions.")]
    public string ListTemplates()
    {
        var builder = new StringBuilder("id | category | name | description\n");
        builder.AppendLine($"{ProjectTemplates.BlankId} | Console | Blank | Empty .NET 10 console app.");
        foreach (var t in ProjectTemplates.All())
            builder.AppendLine($"{t.Id} | {t.Category} | {t.Name} | {t.Description} Use case: {t.UseCase}");
        return builder.ToString();
    }

    [KernelFunction("create_project_from_template"), Description("Create a new project folder from an App Generator template and open it as the current project. Use when the user wants a new app and no suitable project is open.")]
    public async Task<string> CreateProjectAsync(
        [Description("Template id from list_templates, or 'blank'.")] string templateId,
        [Description("Project name; becomes the folder name and C# namespace.")] string name)
    {
        var folder = host.ProjectsFolder;
        Directory.CreateDirectory(folder);
        var mainFile = ProjectTemplates.Scaffold(templateId, name, folder);
        var projectFolder = Path.Combine(folder, name.Trim());
        await host.OpenProjectAsync(projectFolder, mainFile);
        return $"Created '{name}' from template '{templateId}' at {projectFolder} and opened it. Main file: {Path.GetFileName(mainFile)}.";
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];
}
