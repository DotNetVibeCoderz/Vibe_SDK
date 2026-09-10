// Scaffolds every template in the catalogue and compiles it.
//
// Run this after any change to the SDK's public surface. Templates are written against that
// surface by hand and nothing recompiles them by accident, so the catalogue drifts silently: a
// renamed method leaves eighteen templates that scaffold cleanly, look right in the editor and fail
// the moment a user presses Build.
//
//   dotnet run --project tools/PollenRobotics.Net.TemplateCheck
//   dotnet run --project tools/PollenRobotics.Net.TemplateCheck -- --keep --filter mini.
//
// It rewrites each generated .csproj to reference the local source projects instead of the
// published packages, because the packages do not exist until release. That validates the generated
// *code*, which is the part that drifts; it does not validate the package references themselves.
using System.Diagnostics;
using System.Text.RegularExpressions;
using PollenRobotics.Net.Wizard.Core.Templates;
using PollenRobotics.Net.Wizard.Core.Projects;

string? filter = ReadOption(args, "--filter");
bool keep = args.Contains("--keep");
bool verbose = args.Contains("--verbose");

string repositoryRoot = FindRepositoryRoot()
    ?? throw new InvalidOperationException("Could not find the repository root. Run this from inside the repository.");

string workspace = Path.Combine(Path.GetTempPath(), $"pollen-template-check-{DateTime.Now:HHmmss}");
Directory.CreateDirectory(workspace);

IReadOnlyList<RobotTemplate> templates = string.IsNullOrWhiteSpace(filter)
    ? TemplateCatalog.All
    : [.. TemplateCatalog.All.Where(t => t.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))];

if (templates.Count == 0)
{
    Console.Error.WriteLine($"No template matches '{filter}'. Known ids:");

    foreach (RobotTemplate template in TemplateCatalog.All)
    {
        Console.Error.WriteLine($"  {template.Id}");
    }

    return 2;
}

Console.WriteLine($"Checking {templates.Count} of {TemplateCatalog.Count} templates in {workspace}");
Console.WriteLine();

var failures = new List<(string Id, string Reason)>();
var stopwatch = Stopwatch.StartNew();

foreach (RobotTemplate template in templates)
{
    string projectName = "Check" + new string([.. template.Id.Split('.').Select(Capitalise).SelectMany(s => s)]);

    Console.Write($"  {template.Id,-26} ");

    try
    {
        WizardProject project = await TemplateCatalog.ScaffoldAsync(template, projectName, workspace);
        RewriteToProjectReferences(project.ProjectFilePath, repositoryRoot);

        (int exitCode, string output) = await BuildAsync(project.ProjectFilePath);

        if (exitCode == 0)
        {
            Console.WriteLine("ok");
        }
        else
        {
            Console.WriteLine("FAILED");
            failures.Add((template.Id, FirstError(output)));

            if (verbose)
            {
                Console.WriteLine(Indent(output));
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("ERROR");
        failures.Add((template.Id, ex.Message));
    }
}

stopwatch.Stop();

Console.WriteLine();
Console.WriteLine($"{templates.Count - failures.Count}/{templates.Count} templates compiled in {stopwatch.Elapsed.TotalSeconds:0}s.");

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failures:");

    foreach ((string id, string reason) in failures)
    {
        Console.WriteLine($"  {id}");
        Console.WriteLine($"    {reason}");
    }

    Console.WriteLine();
    Console.WriteLine($"Scaffolded projects left in {workspace} for inspection.");
    return 1;
}

if (!keep)
{
    try
    {
        Directory.Delete(workspace, recursive: true);
    }
    catch (IOException)
    {
        // A file lock from the build we just ran. Not worth failing the check over.
    }
}
else
{
    Console.WriteLine($"Scaffolded projects kept in {workspace}.");
}

return 0;

static string? ReadOption(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string Capitalise(string value) =>
    value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

// Walks up from the executable looking for the solution, so the tool works from any directory.
static string? FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);

    while (directory is not null)
    {
        if (directory.EnumerateFiles("PollenRobotics.Net.slnx").Any())
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return null;
}

// Swaps PackageReference for ProjectReference so the template compiles against the working tree
// rather than against packages that have not been published yet.
static void RewriteToProjectReferences(string projectFilePath, string repositoryRoot)
{
    string content = File.ReadAllText(projectFilePath);

    content = Regex.Replace(
        content,
        """<PackageReference Include="(?:Gravicode\.)?(?<name>PollenRobotics\.Net[^"]*)" Version="[^"]*" />""",
        match =>
        {
            // The package ID carries the publisher prefix; the project directory does not.
            string name = match.Groups["name"].Value;
            string path = Path.Combine(repositoryRoot, "src", name, $"{name}.csproj");
            return $"""<ProjectReference Include="{path}" />""";
        });

    File.WriteAllText(projectFilePath, content);
}

static async Task<(int ExitCode, string Output)> BuildAsync(string projectFilePath)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = Path.GetDirectoryName(projectFilePath)!,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    startInfo.ArgumentList.Add("build");
    startInfo.ArgumentList.Add(projectFilePath);
    startInfo.ArgumentList.Add("--nologo");
    startInfo.ArgumentList.Add("-v");
    startInfo.ArgumentList.Add("quiet");

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start dotnet. Is it on PATH?");

    string stdout = await process.StandardOutput.ReadToEndAsync();
    string stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    return (process.ExitCode, stdout + stderr);
}

static string FirstError(string output)
{
    foreach (string line in output.Split('\n'))
    {
        if (line.Contains(": error ", StringComparison.Ordinal))
        {
            return line.Trim();
        }
    }

    return output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(no output)";
}

static string Indent(string text) =>
    string.Join(Environment.NewLine, text.Split('\n').Select(l => "      " + l.TrimEnd()));
