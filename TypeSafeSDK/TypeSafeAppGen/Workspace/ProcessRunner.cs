using System.Diagnostics;
using System.Text;

namespace TypeSafeAppGen.Workspace;

/// <summary>Hasil satu proses: kode keluar, output gabungan (dipangkas), dan durasi.</summary>
public sealed record ProcessResult(int ExitCode, string Output, TimeSpan Duration, bool Cancelled)
{
    public bool Succeeded => ExitCode == 0 && !Cancelled;
}

/// <summary>Menjalankan perintah <c>dotnet</c> dan mengalirkan setiap baris output ke pemanggil.</summary>
public static class ProcessRunner
{
    /// <summary>Batas output yang disimpan per proses agar build panjang tidak memakan memori.</summary>
    public const int MaxCapturedChars = 200_000;

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        // Paksa pesan compiler berbahasa Inggris agar parser diagnostic selalu cocok.
        info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        var captured = new StringBuilder();
        var gate = new object();
        void Capture(string? line)
        {
            if (line is null) return;
            lock (gate)
            {
                if (captured.Length < MaxCapturedChars) captured.AppendLine(line);
            }
            onLine?.Invoke(line);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        // WaitForExitAsync kembali sebelum event output terakhir; panggil WaitForExit() untuk mengurasnya.
        process.WaitForExit();

        string output;
        lock (gate) output = captured.ToString();
        return new ProcessResult(cancelled ? -1 : process.ExitCode, output, stopwatch.Elapsed, cancelled);
    }

    public static Task<ProcessResult> DotnetAsync(IReadOnlyList<string> arguments, string workingDirectory, Action<string>? onLine = null, CancellationToken ct = default) =>
        RunAsync("dotnet", arguments, workingDirectory, onLine, ct);
}
