using Renci.SshNet;
using Unitree.Net.Wizard.Core.Projects;

namespace Unitree.Net.Wizard.Core.Tooling;

/// <summary>
/// Where and how a project is deployed.
/// </summary>
public sealed class DeploymentOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Wizard:Deployment";

    /// <summary>Robot hostname or address. Unitree's default on the wired link is 192.168.123.18.</summary>
    public string Host { get; set; } = "192.168.123.18";

    /// <summary>SSH port.</summary>
    public int Port { get; set; } = 22;

    /// <summary>SSH user. Unitree ships <c>unitree</c> on the Jetson module.</summary>
    public string User { get; set; } = "unitree";

    /// <summary>SSH password. Leave empty when using a key.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Path to a private key file. Preferred over a password.</summary>
    public string PrivateKeyPath { get; set; } = string.Empty;

    /// <summary>Directory on the robot that applications are deployed into.</summary>
    public string RemoteDirectory { get; set; } = "/home/unitree/apps";

    /// <summary>Whether to install and enable a systemd unit so the app starts on boot.</summary>
    public bool InstallService { get; set; }

    /// <summary>Whether the configuration is complete enough to attempt a connection.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(User)
        && (!string.IsNullOrWhiteSpace(Password) || File.Exists(PrivateKeyPath));
}

/// <summary>
/// Publishes a project and copies it to the robot over SSH.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="ProjectKind.Embedded"/> projects are meant to be deployed. They publish
/// self-contained for <c>linux-arm64</c>, so the robot's compute module needs no .NET installed —
/// which matters because you generally cannot install one on a robot you did not image yourself.
/// </para>
/// <para>
/// This has never been run against a real robot. See <c>PROGRESS.md</c>.
/// </para>
/// </remarks>
public sealed class DeploymentService
{
    private readonly BuildRunner _builder;
    private readonly Action<OutputLine> _output;

    /// <summary>Creates a deployment service.</summary>
    /// <param name="builder">Used to publish before copying.</param>
    /// <param name="output">Called for each line of progress.</param>
    public DeploymentService(BuildRunner builder, Action<OutputLine> output)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(output);

        _builder = builder;
        _output = output;
    }

    /// <summary>
    /// Publishes <paramref name="project"/> and copies it to the robot.
    /// </summary>
    /// <param name="project">The project to deploy.</param>
    /// <param name="options">Where to deploy it.</param>
    /// <param name="cancellationToken">Cancels the deployment.</param>
    /// <returns><see langword="true"/> if every step succeeded.</returns>
    public async Task<bool> DeployAsync(
        WizardProject project,
        DeploymentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured)
        {
            Write(OutputLevel.Error, "Deployment is not configured — set a host, user and password or key.");
            return false;
        }

        string staging = Path.Combine(Path.GetTempPath(), "unitree-wizard", project.Name);

        Write(OutputLevel.Step, $"Publishing {project.Name}...");
        ToolResult publish = await _builder.PublishAsync(project, staging, cancellationToken).ConfigureAwait(false);

        if (!publish.Succeeded)
        {
            Write(OutputLevel.Error, "Publish failed; nothing was copied.");
            return false;
        }

        string remoteRoot = $"{options.RemoteDirectory.TrimEnd('/')}/{project.Name}";

        try
        {
            using SshClient shell = CreateSshClient(options);
            using SftpClient transfer = CreateSftpClient(options);

            Write(OutputLevel.Info, $"Connecting to {options.User}@{options.Host}:{options.Port}...");
            await shell.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await transfer.ConnectAsync(cancellationToken).ConfigureAwait(false);

            Write(OutputLevel.Info, $"Remote directory {remoteRoot}");
            shell.RunCommand($"mkdir -p '{remoteRoot}'");

            int copied = UploadDirectory(transfer, staging, remoteRoot, cancellationToken);
            Write(OutputLevel.Info, $"Copied {copied} file(s).");

            // The published single file loses its executable bit in transit — SFTP does not carry
            // Unix permissions, and without this the robot reports "permission denied" on start.
            string executable = $"{remoteRoot}/{project.Name}";
            shell.RunCommand($"chmod +x '{executable}'");

            if (options.InstallService)
            {
                InstallSystemdUnit(shell, project.Name, remoteRoot, options.User);
            }

            Write(OutputLevel.Step, $"Deployed to {options.Host}:{remoteRoot}");
            Write(OutputLevel.Info, options.InstallService
                ? $"Started as a service. Logs:  journalctl -u {project.Name} -f"
                : $"Run it with:  ssh {options.User}@{options.Host} '{executable}'");

            return true;
        }
        catch (OperationCanceledException)
        {
            Write(OutputLevel.Error, "Deployment cancelled.");
            return false;
        }
        catch (Exception exception)
        {
            Write(OutputLevel.Error, $"Deployment failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>Checks that the robot is reachable and reports what is on the other end.</summary>
    /// <param name="options">Connection settings to test.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    public async Task<bool> TestConnectionAsync(
        DeploymentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured)
        {
            Write(OutputLevel.Error, "Deployment is not configured.");
            return false;
        }

        try
        {
            using SshClient shell = CreateSshClient(options);
            await shell.ConnectAsync(cancellationToken).ConfigureAwait(false);

            SshCommand identity = shell.RunCommand("uname -srm && echo -n 'dotnet: ' && (dotnet --version 2>/dev/null || echo 'not installed')");

            foreach (string line in identity.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Write(OutputLevel.Info, line.Trim());
            }

            Write(OutputLevel.Step, $"{options.Host} reachable.");
            return true;
        }
        catch (Exception exception)
        {
            Write(OutputLevel.Error, $"Could not reach {options.Host}: {exception.Message}");
            return false;
        }
    }

    private static SshClient CreateSshClient(DeploymentOptions options) =>
        new(BuildConnectionInfo(options));

    private static SftpClient CreateSftpClient(DeploymentOptions options) =>
        new(BuildConnectionInfo(options));

    private static ConnectionInfo BuildConnectionInfo(DeploymentOptions options)
    {
        // A key is preferred whenever one is configured: it is the only option that does not require
        // a robot password to be sitting in a settings file.
        AuthenticationMethod authentication = File.Exists(options.PrivateKeyPath)
            ? new PrivateKeyAuthenticationMethod(options.User, new PrivateKeyFile(options.PrivateKeyPath))
            : new PasswordAuthenticationMethod(options.User, options.Password);

        return new ConnectionInfo(options.Host, options.Port, options.User, authentication)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private int UploadDirectory(
        SftpClient transfer,
        string localRoot,
        string remoteRoot,
        CancellationToken cancellationToken)
    {
        int count = 0;

        foreach (string path in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(localRoot, path).Replace('\\', '/');
            string remotePath = $"{remoteRoot}/{relative}";
            string remoteDirectory = remotePath[..remotePath.LastIndexOf('/')];

            EnsureRemoteDirectory(transfer, remoteDirectory);

            using FileStream source = File.OpenRead(path);
            transfer.UploadFile(source, remotePath, canOverride: true);
            count++;
        }

        return count;
    }

    private static void EnsureRemoteDirectory(SftpClient transfer, string path)
    {
        if (transfer.Exists(path))
        {
            return;
        }

        // Built up one segment at a time: SFTP has no mkdir -p, and creating a nested path in one call
        // fails as soon as any intermediate directory is missing.
        var current = new System.Text.StringBuilder();

        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current.Append('/').Append(segment);
            string partial = current.ToString();

            if (!transfer.Exists(partial))
            {
                transfer.CreateDirectory(partial);
            }
        }
    }

    private void InstallSystemdUnit(SshClient shell, string name, string remoteRoot, string user)
    {
        // Restart=always with a delay, because a robot application that dies at three in the morning
        // should come back rather than wait for someone to notice.
        string unit = $"""
[Unit]
Description={name} (Unitree.Net)
After=network-online.target

[Service]
Type=simple
User={user}
WorkingDirectory={remoteRoot}
ExecStart={remoteRoot}/{name}
Restart=always
RestartSec=5
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

[Install]
WantedBy=multi-user.target
""";

        Write(OutputLevel.Info, $"Installing systemd unit {name}.service...");

        shell.RunCommand($"echo '{unit.Replace("'", "'\\''")}' | sudo tee /etc/systemd/system/{name}.service > /dev/null");
        shell.RunCommand("sudo systemctl daemon-reload");
        shell.RunCommand($"sudo systemctl enable {name}");
        SshCommand restart = shell.RunCommand($"sudo systemctl restart {name}");

        if (restart.ExitStatus != 0)
        {
            Write(OutputLevel.Warning, $"systemctl restart returned {restart.ExitStatus}: {restart.Error.Trim()}");
        }
    }

    private void Write(OutputLevel level, string text) =>
        _output(new OutputLine(DateTimeOffset.Now, level, text));
}
