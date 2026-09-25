using TypeSafeAppGen.Workspace;

namespace TypeSafeAppGen.Ai;

/// <summary>Snapshot tab editor aktif yang bisa dibaca Jack.</summary>
public sealed record ActiveEditorSnapshot(string? Path, string Text, int CaretLine, string? Selection);

/// <summary>
/// Jembatan dari kernel functions ke aplikasi. Kernel function bisa berjalan di thread pool,
/// jadi implementasinya wajib memindahkan kerja UI ke UI thread.
/// </summary>
public interface IJackHost
{
    ProjectWorkspace? Workspace { get; }
    string ProjectsFolder { get; }
    Task<ActiveEditorSnapshot?> GetActiveEditorAsync();
    Task OpenFileAsync(string fullPath, int? line = null);
    Task OpenProjectAsync(string folder, string? fileToOpen = null);

    /// <summary>Menjalankan <c>dotnet</c> dengan output mengalir ke panel Output dan diagnostic ke panel Problems.</summary>
    Task<ProcessResult> RunDotnetAsync(string title, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct);
}
