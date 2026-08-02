using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DepthAI.Wizard.Build;

/// <summary>Tingkat keparahan satu baris log.</summary>
public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Satu baris pada panel Logs.</summary>
public sealed record LogLine(DateTimeOffset Timestamp, LogLevel Level, string Text, string Source)
{
    public override string ToString() => $"{Timestamp:HH:mm:ss}  {Text}";
}

/// <summary>Hasil menjalankan perintah dotnet.</summary>
public sealed record RunResult(int ExitCode, TimeSpan Duration, int ErrorCount, int WarningCount)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Menjalankan perintah <c>dotnet</c> pada proyek dan menyiarkan keluarannya baris demi baris.
/// </summary>
/// <remarks>
/// Keluaran dipakai apa adanya, bukan lewat MSBuild API, supaya wizard tidak terikat versi
/// SDK tertentu: aplikasi hanya membutuhkan <c>dotnet</c> yang ada di PATH pengguna.
/// </remarks>
public sealed class DotnetRunner
{
    /// <summary>Dipicu untuk tiap baris keluaran, termasuk baris ringkasan yang dibuat runner.</summary>
    public event EventHandler<LogLine>? LogEmitted;

    /// <summary>Proses yang sedang berjalan, bila ada. Dipakai untuk menghentikan aplikasi.</summary>
    public Process? RunningProcess { get; private set; }

    public bool IsRunning => RunningProcess is { HasExited: false };

    /// <summary>Membangun proyek.</summary>
    public Task<RunResult> BuildAsync(
        string projectFile,
        string configuration = "Debug",
        CancellationToken cancellationToken = default)
        => ExecuteAsync("build", $"build \"{projectFile}\" -c {configuration} --nologo", projectFile, cancellationToken);

    /// <summary>Menjalankan proyek. Prosesnya dibiarkan hidup sampai dihentikan.</summary>
    public Task<RunResult> RunAsync(
        string projectFile,
        string configuration = "Debug",
        CancellationToken cancellationToken = default)
        => ExecuteAsync("run", $"run --project \"{projectFile}\" -c {configuration} --nologo", projectFile, cancellationToken);

    /// <summary>
    /// Mem-publish proyek menjadi bundel yang siap disebar.
    /// </summary>
    /// <param name="runtimeIdentifier">
    /// RID target, misalnya <c>win-x64</c>. Null mem-publish portable yang membutuhkan
    /// runtime .NET terpasang di mesin tujuan.
    /// </param>
    /// <param name="selfContained">Menyertakan runtime .NET di dalam hasil publish.</param>
    public Task<RunResult> DeployAsync(
        string projectFile,
        string outputDirectory,
        string? runtimeIdentifier = null,
        bool selfContained = false,
        CancellationToken cancellationToken = default)
    {
        var arguments = $"publish \"{projectFile}\" -c Release -o \"{outputDirectory}\" --nologo";

        if (!string.IsNullOrWhiteSpace(runtimeIdentifier))
        {
            arguments += $" -r {runtimeIdentifier}";
            arguments += selfContained ? " --self-contained true" : " --self-contained false";
        }

        return ExecuteAsync("deploy", arguments, projectFile, cancellationToken);
    }

    /// <summary>Menghentikan proses yang sedang berjalan.</summary>
    public void Stop()
    {
        var process = RunningProcess;
        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            // Seluruh pohon proses dimatikan: `dotnet run` memunculkan aplikasi
            // sebagai proses anak, dan mematikan induknya saja akan meninggalkannya.
            process.Kill(entireProcessTree: true);
            Emit(LogLevel.Warning, "Proses dihentikan.", "run");
        }
        catch (InvalidOperationException)
        {
            // Proses sudah keburu keluar di antara pengecekan dan pemanggilan Kill.
        }
    }

    private async Task<RunResult> ExecuteAsync(
        string source,
        string arguments,
        string projectFile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFile);

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))
            ?? Directory.GetCurrentDirectory();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Keluaran MSBuild berwarna memakai escape ANSI yang mengotori panel log.
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["TERM"] = "dumb";

        Emit(LogLevel.Info, $"dotnet {arguments}", source);

        var clock = Stopwatch.StartNew();
        var errors = 0;
        var warnings = 0;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            var level = ClassifyLine(e.Data);
            if (level == LogLevel.Error)
            {
                Interlocked.Increment(ref errors);
            }
            else if (level == LogLevel.Warning)
            {
                Interlocked.Increment(ref warnings);
            }

            Emit(level, e.Data, source);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Interlocked.Increment(ref errors);
                Emit(LogLevel.Error, e.Data, source);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Emit(LogLevel.Error,
                $"Tidak bisa menjalankan 'dotnet': {ex.Message}. Pastikan .NET SDK terpasang dan ada di PATH.",
                source);

            return new RunResult(-1, clock.Elapsed, 1, 0);
        }

        RunningProcess = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
        finally
        {
            RunningProcess = null;
            clock.Stop();
        }

        var result = new RunResult(process.ExitCode, clock.Elapsed, errors, warnings);

        Emit(
            result.Succeeded ? LogLevel.Success : LogLevel.Error,
            result.Succeeded
                ? $"Selesai dalam {clock.Elapsed.TotalSeconds:F1}s — {warnings} peringatan."
                : $"Gagal dengan kode {process.ExitCode} setelah {clock.Elapsed.TotalSeconds:F1}s — {errors} error.",
            source);

        return result;
    }

    /// <summary>
    /// Menentukan tingkat keparahan dari format diagnostik MSBuild
    /// (<c>path(line,col): error CS0103: pesan</c>).
    /// </summary>
    private static LogLevel ClassifyLine(string line)
    {
        if (line.Contains(": error ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("error ", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Error;
        }

        if (line.Contains(": warning ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("warning ", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Warning;
        }

        return line.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Success
            : LogLevel.Info;
    }

    private void Emit(LogLevel level, string text, string source)
        => LogEmitted?.Invoke(this, new LogLine(DateTimeOffset.Now, level, text, source));

    /// <summary>RID yang cocok untuk mesin saat ini; dipakai sebagai bawaan dialog deploy.</summary>
    public static string CurrentRuntimeIdentifier
    {
        get
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "x64",
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return $"win-{architecture}";
            }

            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? $"osx-{architecture}"
                : $"linux-{architecture}";
        }
    }
}
