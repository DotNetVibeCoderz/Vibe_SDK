using Microsoft.Win32;

namespace Unitree.Net.Wizard;

/// <summary>
/// The handful of things a WebView cannot do: pick a real file or folder from the operator's disk.
/// </summary>
/// <remarks>
/// A Blazor <c>InputFile</c> works for attachments because the bytes are what matters. Opening a
/// project needs a genuine path, and the browser file picker deliberately does not give one out.
/// </remarks>
public sealed class NativeDialogs
{
    /// <summary>Asks for a project file to open.</summary>
    /// <param name="initialDirectory">Where the dialog starts.</param>
    /// <returns>The chosen path, or <see langword="null"/> if cancelled.</returns>
    public string? PickProjectFile(string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open project",
            Filter = "C# project (*.csproj)|*.csproj|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>Asks for a folder.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="initialDirectory">Where the dialog starts.</param>
    /// <returns>The chosen folder, or <see langword="null"/> if cancelled.</returns>
    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog { Title = title };

        if (Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>Asks for a private key file.</summary>
    /// <returns>The chosen path, or <see langword="null"/> if cancelled.</returns>
    public string? PickPrivateKey()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an SSH private key",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
