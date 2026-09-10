using System.Diagnostics;
using System.Text.RegularExpressions;
using PollenRobotics.Net.Core.Diagnostics;
using PollenRobotics.Net.Wizard.Core.Projects;

namespace PollenRobotics.Net.Wizard.Core.Build;

/// <summary>Severity of a compiler message.</summary>
public enum DiagnosticSeverity
{
    /// <summary>A warning.</summary>
    Warning,

    /// <summary>An error. The build failed.</summary>
    Error,
}

/// <summary>One compiler message, parsed out of the build output.</summary>
/// <param name="Severity">Warning or error.</param>
/// <param name="Code">Compiler code, e.g. CS0103.</param>
/// <param name="Message">The text.</param>
/// <param name="File">Absolute path to the file, when the message names one.</param>
/// <param name="Line">1-based line, when the message names one.</param>
/// <param name="Column">1-based column, when the message names one.</param>
public readonly record struct BuildDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? File,
    int Line,
    int Column)
{
    /// <summary>Renders the diagnostic the way the log panel shows it.</summary>
    public override string ToString() => File is null
        ? $"{Code}: {Message}"
        : $"{Path.GetFileName(File)}({Line},{Column}): {Code}: {Message}";
}

/// <summary>What a build produced.</summary>
/// <param name="Succeeded">True when the compiler returned zero.</param>
/// <param name="Diagnostics">Errors and warnings, in the order they were emitted.</param>
/// <param name="Duration">Wall-clock time.</param>
/// <param name="RawOutput">The full output, for the log panel.</param>
public readonly record struct BuildResult(
    bool Succeeded,
    IReadOnlyList<BuildDiagnostic> Diagnostics,
    TimeSpan Duration,
    string RawOutput)
{
    /// <summary>Errors only.</summary>
    public IEnumerable<BuildDiagnostic> Errors => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>A one-line summary for the status bar.</summary>
    public string Summary()
    {
        int errors = Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        return Succeeded
            ? $"Build succeeded in {Duration.TotalSeconds:0.#}s ({warnings} warning{(warnings == 1 ? string.Empty : "s")})."
            : $"Build failed in {Duration.TotalSeconds:0.#}s ({errors} error{(errors == 1 ? string.Empty : "s")}, {warnings} warning{(warnings == 1 ? string.Empty : "s")}).";
    }
}

/// <summary>
/// Builds, runs and deploys wizard projects by driving the .NET CLI.
/// </summary>
/// <remarks>
/// <para>
/// Shelling out to <c>dotnet</c> rather than hosting MSBuild in-process is deliberate: the wizard
/// is itself a .NET application, and loading a second MSBuild into the same process is a reliable
/// way to end up with two conflicting versions of the same assembly. The cost is a process launch
/// per build, which nobody notices next to the compile itself.
/// </para>
/// <para>
/// Output is streamed rather than collected, so the log panel fills as the build runs. A build that
/// prints nothing for twenty seconds and then everything at once reads as a hang.
/// </para>
/// </remarks>
public sealed partial class ProjectRunner(RobotLogSink log)
{
    private readonly RobotLogSink _log = log;
    private Process? _running;

    /// <summary>True while a run started by <see cref="RunAsync"/> is still going.</summary>
    public bool IsRunning => _running is { HasExited: false };

    /// <summary>Raised when the running process writes a line to stdout or stderr.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Raised when the running process exits, with its exit code.</summary>
    public event Action<int>? Exited;

    /// <summary>Compiles a project.</summary>
    public async Task<BuildResult> BuildAsync(WizardProject project, bool release = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var stopwatch = Stopwatch.StartNew();
        var output = new System.Text.StringBuilder();
        var diagnostics = new List<BuildDiagnostic>();

        _log.Info("build", $"Building {project.Name} ({(release ? "Release" : "Debug")})...");

        int exitCode = await RunDotnetAsync(
            ["build", project.ProjectFilePath, "-c", release ? "Release" : "Debug", "--nologo"],
            project.Directory,
            line =>
            {
                output.AppendLine(line);

                if (TryParseDiagnostic(line, out BuildDiagnostic diagnostic))
                {
                    // MSBuild repeats every diagnostic in its summary, so the same error arrives
                    // twice. Showing it twice makes a one-error build look like a two-error one.
                    if (!diagnostics.Contains(diagnostic))
                    {
                        diagnostics.Add(diagnostic);
                    }
                }

                OutputReceived?.Invoke(line);
            },
            cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();

        var result = new BuildResult(exitCode == 0, diagnostics, stopwatch.Elapsed, output.ToString());

        if (result.Succeeded)
        {
            _log.Info("build", result.Summary());
        }
        else
        {
            _log.Error("build", result.Summary());

            foreach (BuildDiagnostic error in result.Errors.Take(10))
            {
                _log.Error("build", error.ToString());
            }
        }

        return result;
    }

    /// <summary>
    /// Runs a project, streaming its output.
    /// </summary>
    /// <param name="project">The project to run.</param>
    /// <param name="target">Simulator or robot. The simulator adds <c>--sim</c>.</param>
    /// <param name="extraArguments">Anything else to pass to the program.</param>
    /// <param name="cancellationToken">Stops the program.</param>
    public async Task<int> RunAsync(
        WizardProject project,
        RunTarget target,
        IEnumerable<string>? extraArguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (IsRunning)
        {
            _log.Warn("run", "Something is already running. Stop it first.");
            return -1;
        }

        var arguments = new List<string> { "run", "--project", project.ProjectFilePath, "--no-build", "--" };

        if (target == RunTarget.Simulator)
        {
            arguments.Add("--sim");
        }

        if (extraArguments is not null)
        {
            arguments.AddRange(extraArguments);
        }

        _log.Info("run", $"Running {project.Name} against the {(target == RunTarget.Simulator ? "simulator" : "robot")}.");

        int exitCode = await RunDotnetAsync(arguments, project.Directory, line => OutputReceived?.Invoke(line), cancellationToken, track: true)
            .ConfigureAwait(false);

        _log.Info("run", $"Exited with code {exitCode}.");
        Exited?.Invoke(exitCode);
        return exitCode;
    }

    /// <summary>Stops a running program.</summary>
    public void Stop()
    {
        if (_running is not { HasExited: false } process)
        {
            return;
        }

        try
        {
            // Kill the whole tree: `dotnet run` launches the program as a child, and killing only
            // the launcher leaves the program running and still holding the robot.
            process.Kill(entireProcessTree: true);
            _log.Info("run", "Stopped.");
        }
        catch (InvalidOperationException)
        {
            // It exited between the check and the kill. Nothing to do.
        }
    }

    /// <summary>
    /// Publishes a project for the robot and copies it across with scp.
    /// </summary>
    /// <param name="project">The project to deploy.</param>
    /// <param name="host">SSH destination, e.g. <c>pollen@reachy-mini.local</c>.</param>
    /// <param name="remoteDirectory">Where to put it on the robot.</param>
    /// <param name="runtimeIdentifier">Target RID. The robots are 64-bit ARM Linux.</param>
    /// <param name="cancellationToken">Cancels the deploy.</param>
    /// <remarks>
    /// Publishing self-contained for <c>linux-arm64</c> is what makes this work without installing
    /// a .NET runtime on the robot. Publishing for the development machine's RID produces something
    /// that copies across fine and then refuses to start, with an error that does not mention
    /// architecture.
    /// </remarks>
    public async Task<bool> DeployAsync(
        WizardProject project,
        string host,
        string remoteDirectory = "/opt/pollen-apps",
        string runtimeIdentifier = "linux-arm64",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        string publishDirectory = Path.Combine(project.Directory, "bin", "deploy", runtimeIdentifier);

        _log.Info("deploy", $"Publishing for {runtimeIdentifier}...");

        int publish = await RunDotnetAsync(
            [
                "publish", project.ProjectFilePath,
                "-c", "Release",
                "-r", runtimeIdentifier,
                "--self-contained", "true",
                "-o", publishDirectory,
                "--nologo",
            ],
            project.Directory,
            line => OutputReceived?.Invoke(line),
            cancellationToken).ConfigureAwait(false);

        if (publish != 0)
        {
            _log.Error("deploy", "Publish failed; nothing was copied.");
            return false;
        }

        string target = $"{host}:{remoteDirectory}/{project.Name}";
        _log.Info("deploy", $"Copying to {target}...");

        int copy = await RunProcessAsync(
            "scp",
            ["-r", publishDirectory, target],
            project.Directory,
            line => OutputReceived?.Invoke(line),
            cancellationToken).ConfigureAwait(false);

        if (copy != 0)
        {
            _log.Error("deploy", "scp failed. Check that key-based SSH to the robot works from a terminal.");
            return false;
        }

        _log.Info("deploy", $"Deployed. Run it on the robot with: {remoteDirectory}/{project.Name}/{project.Name}");
        return true;
    }

    private async Task<int> RunDotnetAsync(
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<string> onOutput,
        CancellationToken cancellationToken,
        bool track = false) =>
        await RunProcessAsync("dotnet", arguments, workingDirectory, onOutput, cancellationToken, track).ConfigureAwait(false);

    private async Task<int> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<string> onOutput,
        CancellationToken cancellationToken,
        bool track = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.Error("build", $"Could not start '{fileName}': {ex.Message}. Is it on PATH?");
            return -1;
        }

        if (track)
        {
            _running = process;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            throw;
        }
        finally
        {
            if (track)
            {
                _running = null;
            }
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Pulls file, line, column, severity and code out of an MSBuild diagnostic line.
    /// </summary>
    /// <remarks>
    /// The format is stable across MSBuild versions and is what makes go-to-error work in the
    /// editor: without the line number a build failure is a wall of text the user has to search.
    /// </remarks>
    private static bool TryParseDiagnostic(string line, out BuildDiagnostic diagnostic)
    {
        diagnostic = default;

        Match match = DiagnosticPattern().Match(line);
        if (!match.Success)
        {
            return false;
        }

        diagnostic = new BuildDiagnostic(
            match.Groups["severity"].Value.Equals("error", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticSeverity.Error
                : DiagnosticSeverity.Warning,
            match.Groups["code"].Value,
            match.Groups["message"].Value.Trim(),
            match.Groups["file"].Success ? match.Groups["file"].Value : null,
            match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) : 0,
            match.Groups["column"].Success ? int.Parse(match.Groups["column"].Value) : 0);

        return true;
    }

    [GeneratedRegex(
        @"^(?<file>[^(]+)\((?<line>\d+),(?<column>\d+)\)\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Z]+\d+)\s*:\s*(?<message>.+?)(?:\s*\[[^\]]+\])?$",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex DiagnosticPattern();
}
