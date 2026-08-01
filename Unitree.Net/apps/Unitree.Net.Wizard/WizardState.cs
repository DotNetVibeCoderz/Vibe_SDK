using System.Collections.Concurrent;
using Unitree.Net.Wizard.Core;
using Unitree.Net.Wizard.Core.Chat;
using Unitree.Net.Wizard.Core.Projects;
using Unitree.Net.Wizard.Core.Tooling;

namespace Unitree.Net.Wizard;

/// <summary>
/// One file open in the editor.
/// </summary>
/// <param name="file">The project file this document edits.</param>
public sealed class OpenDocument(ProjectFile file)
{
    /// <summary>The project file this document edits.</summary>
    public ProjectFile File { get; } = file;

    /// <summary>The text as the editor currently has it.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The text as it was last read from or written to disk.</summary>
    public string SavedText { get; set; } = string.Empty;

    /// <summary>Whether the buffer differs from disk.</summary>
    public bool IsDirty => !string.Equals(Text, SavedText, StringComparison.Ordinal);
}

/// <summary>
/// Everything the wizard's UI reads and mutates.
/// </summary>
/// <remarks>
/// The Blazor components hold no state of their own beyond transient dialog fields. Keeping it all
/// here is what lets the menu bar, the file tree, the editor and Jack all act on the same project
/// without any of them owning it.
/// </remarks>
public sealed class WizardState : IDisposable
{
    private const int MaxOutputLines = 2000;

    private readonly ConcurrentQueue<OutputLine> _output = new();
    private readonly List<OpenDocument> _documents = [];
    private CancellationTokenSource? _toolCancellation;
    private bool _disposed;

    /// <summary>Creates the state and restores the operator's settings.</summary>
    public WizardState()
    {
        Settings = WizardSettingsStore.Load();

        string sdkRoot = ProjectService.TryLocateSdkRoot(AppContext.BaseDirectory)
            ?? Directory.GetCurrentDirectory();

        Projects = new ProjectService(sdkRoot);
        Builder = new BuildRunner(WriteOutput);
        Deployment = new DeploymentService(Builder, WriteOutput);
        ChatStore = new ChatSessionStore();

        Jack = new JackAssistant(
            Settings,
            Projects,
            () => CurrentProject,
            OnJackWroteFile);

        Sessions = [.. ChatStore.LoadAll()];

        if (Sessions.Count == 0)
        {
            Sessions.Add(new ChatSession());
        }

        ActiveSession = Sessions[0];

        WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Step, "Unitree Robot Wizard ready."));
        WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Info, $"SDK root: {sdkRoot}"));

        if (ProjectService.TryLocateSdkRoot(AppContext.BaseDirectory) is null)
        {
            // Without the SDK root, generated projects reference paths that do not exist and every
            // build fails with a restore error that says nothing about the real cause.
            WriteOutput(new OutputLine(
                DateTimeOffset.Now,
                OutputLevel.Warning,
                "Could not find the Unitree.Net repository above this application. New projects will " +
                "not reference the SDK correctly until the wizard is run from inside the repository."));
        }
    }

    /// <summary>Raised whenever something the UI shows has changed.</summary>
    public event Action? Changed;

    /// <summary>Raised for each line of tool output.</summary>
    public event Action<OutputLine>? OutputWritten;

    /// <summary>Raised when Jack writes a file, with its project-relative path.</summary>
    public event Action<string>? FileWrittenByJack;

    /// <summary>Persisted settings.</summary>
    public WizardSettings Settings { get; }

    /// <summary>Project scaffolding and enumeration.</summary>
    public ProjectService Projects { get; }

    /// <summary>Build, run and publish.</summary>
    public BuildRunner Builder { get; }

    /// <summary>Deployment over SSH.</summary>
    public DeploymentService Deployment { get; }

    /// <summary>Chat session persistence.</summary>
    public ChatSessionStore ChatStore { get; }

    /// <summary>The assistant.</summary>
    public JackAssistant Jack { get; }

    /// <summary>The open project, or <see langword="null"/>.</summary>
    public WizardProject? CurrentProject { get; private set; }

    /// <summary>Files in the open project.</summary>
    public IReadOnlyList<ProjectFile> ProjectFiles { get; private set; } = [];

    /// <summary>Documents open in the editor, in tab order.</summary>
    public IReadOnlyList<OpenDocument> Documents => _documents;

    /// <summary>The focused document, or <see langword="null"/>.</summary>
    public OpenDocument? ActiveDocument { get; private set; }

    /// <summary>Chat sessions, most recently updated first.</summary>
    public List<ChatSession> Sessions { get; }

    /// <summary>The session shown in the chat panel.</summary>
    public ChatSession ActiveSession { get; private set; }

    /// <summary>Whether Jack is producing a reply.</summary>
    public bool IsJackThinking { get; private set; }

    /// <summary>A short description of what the wizard is doing, for the status bar.</summary>
    public string StatusText { get; private set; } = "Ready";

    /// <summary>One-based cursor line in the editor.</summary>
    public int CursorLine { get; private set; } = 1;

    /// <summary>One-based cursor column in the editor.</summary>
    public int CursorColumn { get; private set; } = 1;

    /// <summary>Records the editor's cursor position.</summary>
    /// <param name="line">One-based line.</param>
    /// <param name="column">One-based column.</param>
    public void SetCursor(int line, int column)
    {
        if (CursorLine == line && CursorColumn == column)
        {
            return;
        }

        CursorLine = line;
        CursorColumn = column;
        Changed?.Invoke();
    }

    /// <summary>Whether a build, run or deploy is in progress.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Whether any open document differs from disk.</summary>
    public bool HasUnsavedChanges => _documents.Any(document => document.IsDirty);

    // ------------------------------------------------------------------- projects

    /// <summary>Creates a project and opens it.</summary>
    /// <param name="parentDirectory">Where the project folder is created.</param>
    /// <param name="name">Project name.</param>
    /// <param name="template">Template to scaffold, or null for a blank project.</param>
    /// <param name="kind">Kind to use when no template is given.</param>
    public async Task CreateProjectAsync(
        string parentDirectory,
        string name,
        ProjectTemplate? template,
        ProjectKind kind)
    {
        try
        {
            WizardProject project = await Projects
                .CreateAsync(parentDirectory, name, template, kind)
                .ConfigureAwait(false);

            WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Step,
                $"Created {name} ({template?.Name ?? "blank " + kind.ToString().ToLowerInvariant()}) at {project.RootPath}"));

            OpenProject(project);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Error, $"Could not create project: {exception.Message}"));
            Changed?.Invoke();
        }
    }

    /// <summary>Opens an existing project by its project file.</summary>
    /// <param name="projectFilePath">Path to a <c>.csproj</c>.</param>
    public void OpenProjectFile(string projectFilePath)
    {
        try
        {
            OpenProject(Projects.Open(projectFilePath));
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Error, $"Could not open project: {exception.Message}"));
            Changed?.Invoke();
        }
    }

    private void OpenProject(WizardProject project)
    {
        CloseProject(save: false);

        CurrentProject = project;
        RefreshFiles();

        Settings.RecentProjects.Remove(project.ProjectFilePath);
        Settings.RecentProjects.Insert(0, project.ProjectFilePath);

        // The entry point is what a person wants to see first, and opening nothing at all makes a
        // freshly created project look like it failed.
        ProjectFile? entry = ProjectFiles.FirstOrDefault(file =>
            file.RelativePath.Equals("Program.cs", StringComparison.OrdinalIgnoreCase));

        if (entry is { } startFile)
        {
            _ = OpenDocumentAsync(startFile);
        }

        StatusText = $"{project.Name} — {project.Kind}";
        Changed?.Invoke();
    }

    /// <summary>Closes the open project and its documents.</summary>
    /// <param name="save">Whether to write unsaved documents first.</param>
    public void CloseProject(bool save)
    {
        if (CurrentProject is null)
        {
            return;
        }

        if (save)
        {
            SaveAllAsync().GetAwaiter().GetResult();
        }

        WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Info, $"Closed {CurrentProject.Name}."));

        CurrentProject = null;
        ProjectFiles = [];
        _documents.Clear();
        ActiveDocument = null;
        StatusText = "Ready";
        Changed?.Invoke();
    }

    /// <summary>Re-reads the project's file list from disk.</summary>
    public void RefreshFiles()
    {
        ProjectFiles = CurrentProject is null ? [] : Projects.EnumerateFiles(CurrentProject);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ documents

    /// <summary>Opens a file in the editor, or focuses it if already open.</summary>
    /// <param name="file">The file to open.</param>
    public async Task OpenDocumentAsync(ProjectFile file)
    {
        OpenDocument? existing = _documents.FirstOrDefault(document =>
            string.Equals(document.File.AbsolutePath, file.AbsolutePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            ActiveDocument = existing;
            Changed?.Invoke();
            return;
        }

        string text;

        try
        {
            text = await File.ReadAllTextAsync(file.AbsolutePath).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Error,
                $"Could not open {file.RelativePath}: {exception.Message}"));
            Changed?.Invoke();
            return;
        }

        var document = new OpenDocument(file) { Text = text, SavedText = text };
        _documents.Add(document);
        ActiveDocument = document;
        Changed?.Invoke();
    }

    /// <summary>Focuses an already-open document.</summary>
    /// <param name="document">The document to focus.</param>
    public void Activate(OpenDocument document)
    {
        ActiveDocument = document;
        Changed?.Invoke();
    }

    /// <summary>Closes a document, discarding unsaved changes.</summary>
    /// <param name="document">The document to close.</param>
    public void CloseDocument(OpenDocument document)
    {
        int index = _documents.IndexOf(document);

        if (index < 0)
        {
            return;
        }

        _documents.RemoveAt(index);

        if (ActiveDocument == document)
        {
            // Focus the neighbour rather than nothing, so closing a tab does not empty the editor.
            ActiveDocument = _documents.Count == 0
                ? null
                : _documents[Math.Min(index, _documents.Count - 1)];
        }

        Changed?.Invoke();
    }

    /// <summary>Records an edit made in the editor.</summary>
    /// <param name="text">The buffer's new content.</param>
    public void UpdateActiveText(string text)
    {
        if (ActiveDocument is not { } document)
        {
            return;
        }

        bool wasDirty = document.IsDirty;
        document.Text = text;

        // Only re-render when the dirty marker would actually change. Otherwise every keystroke
        // re-renders the whole shell.
        if (wasDirty != document.IsDirty)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Writes the focused document to disk.</summary>
    public async Task SaveActiveAsync()
    {
        if (ActiveDocument is not { } document)
        {
            return;
        }

        await WriteAsync(document).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Writes every modified document to disk.</summary>
    public async Task SaveAllAsync()
    {
        foreach (OpenDocument document in _documents.Where(document => document.IsDirty).ToList())
        {
            await WriteAsync(document).ConfigureAwait(false);
        }

        Changed?.Invoke();
    }

    private async Task WriteAsync(OpenDocument document)
    {
        try
        {
            await File.WriteAllTextAsync(document.File.AbsolutePath, document.Text).ConfigureAwait(false);
            document.SavedText = document.Text;
            WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Info, $"Saved {document.File.RelativePath}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Error,
                $"Could not save {document.File.RelativePath}: {exception.Message}"));
        }
    }

    // --------------------------------------------------------------------- tools

    /// <summary>Builds the open project.</summary>
    public Task BuildAsync() => RunToolAsync("Building", async token =>
    {
        await SaveAllAsync().ConfigureAwait(false);
        ToolResult result = await Builder.BuildAsync(CurrentProject!, token).ConfigureAwait(false);
        return result.Succeeded ? "Build succeeded" : $"Build failed — {result.ErrorCount} error(s)";
    });

    /// <summary>Runs the open project.</summary>
    public Task RunAsync() => RunToolAsync("Running", async token =>
    {
        await SaveAllAsync().ConfigureAwait(false);

        if (Settings.RunTarget == RunTarget.Simulator)
        {
            WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Info,
                "Target is the simulator. Start Unitree.Net.Simulator first if it is not already publishing."));
        }

        ToolResult result = await Builder.RunAsync(CurrentProject!, Settings.RunTarget, token).ConfigureAwait(false);
        return result.Succeeded ? "Finished" : $"Exited with {result.ExitCode}";
    });

    /// <summary>Publishes and deploys the open project to the robot.</summary>
    public Task DeployAsync() => RunToolAsync("Deploying", async token =>
    {
        await SaveAllAsync().ConfigureAwait(false);

        if (CurrentProject!.Kind != ProjectKind.Embedded)
        {
            WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Warning,
                $"{CurrentProject.Name} is a {CurrentProject.Kind} project. Only Embedded projects publish " +
                "self-contained for the robot's ARM64 module; this will copy a build the robot may not run."));
        }

        bool ok = await Deployment.DeployAsync(CurrentProject, Settings.Deployment, token).ConfigureAwait(false);
        return ok ? "Deployed" : "Deploy failed";
    });

    /// <summary>Checks that the configured robot is reachable.</summary>
    public Task TestConnectionAsync() => RunToolAsync("Testing connection", async token =>
    {
        bool ok = await Deployment.TestConnectionAsync(Settings.Deployment, token).ConfigureAwait(false);
        return ok ? "Robot reachable" : "Robot unreachable";
    });

    /// <summary>Stops whatever tool is running.</summary>
    public void StopTool()
    {
        _toolCancellation?.Cancel();
        Builder.Stop();
        IsBusy = false;
        StatusText = "Stopped";
        Changed?.Invoke();
    }

    private async Task RunToolAsync(string label, Func<CancellationToken, Task<string>> work)
    {
        if (CurrentProject is null)
        {
            WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Error, "No project is open."));
            Changed?.Invoke();
            return;
        }

        if (IsBusy)
        {
            WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Warning, "Something is already running."));
            Changed?.Invoke();
            return;
        }

        IsBusy = true;
        StatusText = label;
        Changed?.Invoke();

        _toolCancellation = new CancellationTokenSource();

        try
        {
            StatusText = await work(_toolCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
        }
        catch (Exception exception)
        {
            WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Error, exception.Message));
            StatusText = "Failed";
        }
        finally
        {
            _toolCancellation?.Dispose();
            _toolCancellation = null;
            IsBusy = false;
            RefreshFiles();
            Changed?.Invoke();
        }
    }

    // ---------------------------------------------------------------------- chat

    /// <summary>Starts a new chat session and makes it active.</summary>
    public void NewSession()
    {
        var session = new ChatSession();
        Sessions.Insert(0, session);
        ActiveSession = session;
        Changed?.Invoke();
    }

    /// <summary>Switches to an existing session.</summary>
    /// <param name="session">The session to show.</param>
    public void SelectSession(ChatSession session)
    {
        ActiveSession = session;
        Changed?.Invoke();
    }

    /// <summary>Deletes a session, leaving at least one behind.</summary>
    /// <param name="session">The session to delete.</param>
    public void DeleteSession(ChatSession session)
    {
        ChatStore.Delete(session);
        Sessions.Remove(session);

        // A chat panel with no session at all has no usable state, so deleting the last one starts a
        // fresh one rather than leaving an empty panel.
        if (Sessions.Count == 0)
        {
            Sessions.Add(new ChatSession());
        }

        if (ActiveSession == session)
        {
            ActiveSession = Sessions[0];
        }

        Changed?.Invoke();
    }

    /// <summary>Clears the active session's messages, keeping the session.</summary>
    public void ResetSession()
    {
        ActiveSession.Reset();
        ChatStore.Save(ActiveSession);
        Changed?.Invoke();
    }

    /// <summary>
    /// Sends a message to Jack and streams the reply into the session.
    /// </summary>
    /// <param name="text">What the operator typed.</param>
    /// <param name="attachments">Files attached to the message.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    public async Task SendToJackAsync(
        string text,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        if (IsJackThinking || (string.IsNullOrWhiteSpace(text) && attachments.Count == 0))
        {
            return;
        }

        ChatSession session = ActiveSession;

        session.Add(new ChatMessage(
            Guid.NewGuid().ToString("n"), ChatRole.User, text, DateTimeOffset.Now, attachments, []));

        var reply = new ChatMessage(
            Guid.NewGuid().ToString("n"), ChatRole.Assistant, string.Empty, DateTimeOffset.Now, [], []);

        session.Add(reply);

        IsJackThinking = true;
        Changed?.Invoke();

        var body = new System.Text.StringBuilder();

        try
        {
            await foreach (string chunk in Jack.StreamReplyAsync(session, cancellationToken).ConfigureAwait(false))
            {
                body.Append(chunk);

                // Through ReplaceLast rather than the list indexer: this runs on the stream's
                // continuation thread while the UI is rendering the same conversation.
                session.ReplaceLast(reply with { Text = body.ToString() });
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            session.ReplaceLast(reply with { Text = body.Append("\n\n_(stopped)_").ToString() });
        }
        catch (Exception exception)
        {
            session.ReplaceLast(reply with { Text = $"Something went wrong: {exception.Message}" });
        }
        finally
        {
            IsJackThinking = false;
            ChatStore.Save(session);

            // Files Jack wrote during the turn are on disk but not in the tree yet.
            RefreshFiles();
            Changed?.Invoke();
        }
    }

    private void OnJackWroteFile(string relativePath, string content)
    {
        // Reflect the write into an open buffer, so the editor is not showing text that no longer
        // matches the file and about to overwrite Jack's work on the next save.
        OpenDocument? document = _documents.FirstOrDefault(open =>
            string.Equals(open.File.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

        if (document is not null)
        {
            document.Text = content;
            document.SavedText = content;
        }

        FileWrittenByJack?.Invoke(relativePath);
        WriteOutput(new OutputLine(DateTimeOffset.Now, OutputLevel.Info, $"Jack wrote {relativePath}"));
    }

    // -------------------------------------------------------------------- output

    /// <summary>The retained tool output, oldest first.</summary>
    public IReadOnlyList<OutputLine> Output => [.. _output];

    /// <summary>Discards the output panel's contents.</summary>
    public void ClearOutput()
    {
        while (_output.TryDequeue(out _))
        {
            // Drain.
        }

        Changed?.Invoke();
    }

    private void WriteOutput(OutputLine line)
    {
        _output.Enqueue(line);

        while (_output.Count > MaxOutputLines && _output.TryDequeue(out _))
        {
            // A build of a large solution can emit tens of thousands of lines. Bounded, because the
            // panel is a place to look at the last failure, not an archive.
        }

        OutputWritten?.Invoke(line);
    }

    /// <summary>Writes a line into the output panel.</summary>
    /// <param name="level">How much it matters.</param>
    /// <param name="text">The line.</param>
    public void Write(OutputLevel level, string text) =>
        WriteOutput(new OutputLine(DateTimeOffset.Now, level, text));

    /// <summary>Saves the settings to <c>app.config</c>.</summary>
    public void PersistSettings()
    {
        try
        {
            WizardSettingsStore.Save(Settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save settings: {exception.Message}");
        }
    }

    /// <summary>Applies changed settings and rebuilds Jack's kernel.</summary>
    public void ApplySettings()
    {
        PersistSettings();
        Jack.InvalidateKernel();
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _toolCancellation?.Cancel();
        _toolCancellation?.Dispose();
        Builder.Dispose();
        Jack.Dispose();
    }
}
