using System.Diagnostics;
using Unitree.Net.Wizard.Core.Projects;

namespace Unitree.Net.Wizard.Core.Tooling;

/// <summary>Severity of a line in the wizard's output panel.</summary>
public enum OutputLevel
{
    /// <summary>Ordinary tool output.</summary>
    Info,

    /// <summary>A compiler or tool warning.</summary>
    Warning,

    /// <summary>A compiler or tool error, or a non-zero exit.</summary>
    Error,

    /// <summary>A step boundary — "Build started", "Deploy finished".</summary>
    Step,
}

/// <summary>One line of tool output.</summary>
/// <param name="Timestamp">When it was produced.</param>
/// <param name="Level">How much it matters.</param>
/// <param name="Text">The line.</param>
public readonly record struct OutputLine(DateTimeOffset Timestamp, OutputLevel Level, string Text);

/// <summary>The result of running a tool.</summary>
/// <param name="Succeeded">Whether the tool exited with code zero.</param>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="ErrorCount">Lines that looked like errors.</param>
/// <param name="WarningCount">Lines that looked like warnings.</param>
public readonly record struct ToolResult(
    bool Succeeded,
    int ExitCode,
    TimeSpan Duration,
    int ErrorCount,
    int WarningCount);

/// <summary>
/// Runs the .NET CLI against a wizard project and streams its output.
/// </summary>
/// <remarks>
/// One long-running child at a time. A project's Run is a process the operator expects to stop when
/// they press Stop, and tracking more than one would mean the button no longer has an obvious meaning.
/// </remarks>
public sealed class BuildRunner : IDisposable
{
    private readonly Action<OutputLine> _output;
    private Process? _running;
    private bool _disposed;

    /// <summary>Creates a runner that reports to <paramref name="output"/>.</summary>
    /// <param name="output">Called for each line, on a background thread.</param>
    public BuildRunner(Action<OutputLine> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>Whether a child process is running.</summary>
    public bool IsRunning => _running is { HasExited: false };

    /// <summary>Builds the project.</summary>
    /// <param name="project">The project to build.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    public Task<ToolResult> BuildAsync(WizardProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        return RunToolAsync("build", $"build \"{project.ProjectFilePath}\" --nologo", project.RootPath, cancellationToken);
    }

    /// <summary>
    /// Runs the project.
    /// </summary>
    /// <param name="project">The project to run.</param>
    /// <param name="target">Whether it should reach the simulator or a real robot.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <remarks>
    /// The run target is passed as an environment variable rather than by rewriting
    /// <c>appsettings.json</c>. Editing a file the operator can see, behind their back, is the kind of
    /// thing that makes a tool feel untrustworthy — and it would show up as an unexplained diff.
    /// </remarks>
    public Task<ToolResult> RunAsync(
        WizardProject project,
        RunTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UNITREE_RUN_TARGET"] = target.ToString(),
        };

        return RunToolAsync(
            "run",
            $"run --project \"{project.ProjectFilePath}\" --nologo",
            project.RootPath,
            cancellationToken,
            environment);
    }

    /// <summary>Publishes the project for deployment.</summary>
    /// <param name="project">The project to publish.</param>
    /// <param name="outputDirectory">Where the published output goes.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    public Task<ToolResult> PublishAsync(
        WizardProject project,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string arguments =
            $"publish \"{project.ProjectFilePath}\" -c Release -o \"{outputDirectory}\" --nologo";

        return RunToolAsync("publish", arguments, project.RootPath, cancellationToken);
    }

    /// <summary>Stops the running child process, if any.</summary>
    public void Stop()
    {
        Process? process = _running;

        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            // The whole tree: `dotnet run` spawns the application as a grandchild, so killing only the
            // direct child leaves the robot application running with nothing attached to it.
            process.Kill(entireProcessTree: true);
            Write(OutputLevel.Step, "Stopped.");
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill.
        }
    }

    private async Task<ToolResult> RunToolAsync(
        string label,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            Write(OutputLevel.Error, "Something is already running. Stop it first.");
            return new ToolResult(false, -1, TimeSpan.Zero, 1, 0);
        }

        var info = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach ((string key, string value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        // Without this the CLI emits ANSI colour codes, which arrive in the output panel as escape
        // sequences rather than as colour.
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["TERM"] = "dumb";

        long start = Stopwatch.GetTimestamp();
        int errors = 0;
        int warnings = 0;

        Write(OutputLevel.Step, $"dotnet {arguments}");

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            OutputLevel level = Classify(e.Data);

            if (level == OutputLevel.Error)
            {
                Interlocked.Increment(ref errors);
            }
            else if (level == OutputLevel.Warning)
            {
                Interlocked.Increment(ref warnings);
            }

            Write(level, e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Interlocked.Increment(ref errors);
                Write(OutputLevel.Error, e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            Write(OutputLevel.Error, $"Could not start dotnet: {exception.Message}");
            return new ToolResult(false, -1, Stopwatch.GetElapsedTime(start), 1, 0);
        }

        _running = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Stop();
        }
        finally
        {
            _running = null;
        }

        TimeSpan duration = Stopwatch.GetElapsedTime(start);
        int exitCode = process.HasExited ? process.ExitCode : -1;
        bool succeeded = exitCode == 0;

        Write(
            succeeded ? OutputLevel.Step : OutputLevel.Error,
            $"{label} {(succeeded ? "succeeded" : $"failed ({exitCode})")} in {duration.TotalSeconds:0.0} s" +
            $" — {errors} error(s), {warnings} warning(s).");

        return new ToolResult(succeeded, exitCode, duration, errors, warnings);
    }

    /// <summary>
    /// Classifies a line of MSBuild output.
    /// </summary>
    /// <remarks>
    /// Matching on ": error" and ": warning" rather than on the words alone: MSBuild always emits that
    /// exact shape, and a looser match colours every line of a program that happens to print the word
    /// "error" as though the build had failed.
    /// </remarks>
    private static OutputLevel Classify(string line) =>
        line.Contains(": error", StringComparison.OrdinalIgnoreCase) ? OutputLevel.Error
        : line.Contains(": warning", StringComparison.OrdinalIgnoreCase) ? OutputLevel.Warning
        : line.StartsWith("Build succeeded", StringComparison.OrdinalIgnoreCase) ? OutputLevel.Step
        : OutputLevel.Info;

    private void Write(OutputLevel level, string text) =>
        _output(new OutputLine(DateTimeOffset.Now, level, text));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
