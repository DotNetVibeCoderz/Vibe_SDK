using System.Diagnostics;
using Unitree.Net.Wizard.Core.Projects;

// Scaffolds every wizard template into a temporary folder and compiles it.
//
// This is the assertion that matters most for the template catalogue. A template that does not
// compile is worse than no template at all, because the operator only finds out after choosing it —
// and the errors are in generated code they did not write.
//
// It lives here rather than in the test suite because it builds sixteen projects and takes a couple
// of minutes. Run it whenever the SDK's public surface changes:
//
//     dotnet run --project tools/Unitree.Net.TemplateCheck

string root = args.Length > 0
    ? args[0]
    : ProjectService.TryLocateSdkRoot(AppContext.BaseDirectory)
      ?? throw new InvalidOperationException(
          "Could not find the repository root. Pass it as the first argument.");

string workspace = Path.Combine(Path.GetTempPath(), "unitree-template-check");

if (Directory.Exists(workspace))
{
    Directory.Delete(workspace, recursive: true);
}

Directory.CreateDirectory(workspace);

var projects = new ProjectService(root);
var failures = new List<string>();
int index = 0;
long started = Stopwatch.GetTimestamp();

Console.WriteLine($"Repository: {root}");
Console.WriteLine($"Workspace:  {workspace}");
Console.WriteLine();

foreach (ProjectTemplate template in TemplateCatalog.All)
{
    index++;

    // The project name has to be a valid identifier, and template ids contain hyphens.
    string name = "Chk" + new string([.. template.Id.Where(char.IsLetterOrDigit)]);

    Console.Write($"[{index,2}/{TemplateCatalog.All.Count}] {template.Id,-24} {template.Kind,-9} ");

    WizardProject project = await projects.CreateAsync(workspace, name, template);

    var info = new ProcessStartInfo("dotnet", $"build \"{project.ProjectFilePath}\" --nologo -v q")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    using var process = Process.Start(info)!;
    string output = await process.StandardOutput.ReadToEndAsync();
    output += await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (process.ExitCode == 0)
    {
        Console.WriteLine("OK");
        continue;
    }

    Console.WriteLine("FAILED");
    failures.Add(template.Id);

    foreach (string line in output.Split('\n')
                 .Where(line => line.Contains(": error", StringComparison.Ordinal))
                 .Distinct()
                 .Take(6))
    {
        Console.WriteLine($"      {line.Trim()}");
    }
}

TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

Console.WriteLine();
Console.WriteLine(failures.Count == 0
    ? $"All {TemplateCatalog.All.Count} templates compile ({elapsed.TotalSeconds:0} s)."
    : $"{failures.Count} of {TemplateCatalog.All.Count} failed: {string.Join(", ", failures)}");

return failures.Count == 0 ? 0 : 1;
