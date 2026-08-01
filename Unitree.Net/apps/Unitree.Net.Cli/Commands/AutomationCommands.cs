using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Sensors;
using Unitree.Net.Wizard.Core.Projects;
using Unitree.Net.Wizard.Core.Tooling;

namespace Unitree.Net.Cli.Commands;

/// <summary>
/// Machine-readable commands, written for tools rather than for people.
/// </summary>
/// <remarks>
/// <para>
/// The VS Code extension drives the SDK entirely through these. That is deliberate: the alternative
/// was to reimplement the template catalogue and the telemetry decoding in TypeScript, which would
/// then drift from the C# every time either changed.
/// </para>
/// <para>
/// Everything here writes JSON to stdout and nothing else. Progress and errors go to stderr, so a
/// caller can parse stdout without having to strip anything out of it.
/// </para>
/// </remarks>
internal static class AutomationCommands
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Writes the template catalogue as JSON.</summary>
    internal static int ListTemplates()
    {
        var payload = TemplateCatalog.All.Select(template => new
        {
            id = template.Id,
            name = template.Name,
            summary = template.Summary,
            kind = template.Kind.ToString(),
            tags = template.Tags,
            files = template.Files.Count,
        });

        Console.Out.WriteLine(JsonSerializer.Serialize(payload, Json));
        return 0;
    }

    /// <summary>
    /// Scaffolds a project.
    /// </summary>
    /// <param name="arguments">
    /// <c>--name</c>, <c>--output</c>, and either <c>--template</c> or <c>--kind</c> for a blank one.
    /// </param>
    internal static async Task<int> NewProjectAsync(string[] arguments)
    {
        string? name = Argument(arguments, "--name");
        string output = Argument(arguments, "--output") ?? Directory.GetCurrentDirectory();
        string? templateId = Argument(arguments, "--template");
        string kindText = Argument(arguments, "--kind") ?? "Console";

        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("--name is required.");
            return 2;
        }

        if (!Enum.TryParse(kindText, ignoreCase: true, out ProjectKind kind))
        {
            Console.Error.WriteLine($"'{kindText}' is not a project kind. Use Console, Desktop, Web or Embedded.");
            return 2;
        }

        ProjectTemplate? template = null;

        if (!string.IsNullOrWhiteSpace(templateId))
        {
            template = TemplateCatalog.Find(templateId);

            if (template is null)
            {
                Console.Error.WriteLine($"No template called '{templateId}'. Run 'unitree templates' to list them.");
                return 2;
            }
        }

        string? sdkRoot = ProjectService.TryLocateSdkRoot(AppContext.BaseDirectory);

        if (sdkRoot is null)
        {
            // Generated projects reference the SDK by relative path, so without the root every
            // scaffolded project fails to restore with an error that says nothing about the cause.
            Console.Error.WriteLine(
                "Could not find the Unitree.Net repository above this tool. Projects would reference an SDK that is not there.");
            return 3;
        }

        try
        {
            WizardProject project = await new ProjectService(sdkRoot)
                .CreateAsync(output, name, template, kind)
                .ConfigureAwait(false);

            Console.Out.WriteLine(JsonSerializer.Serialize(
                new
                {
                    name = project.Name,
                    rootPath = project.RootPath,
                    projectFilePath = project.ProjectFilePath,
                    kind = project.Kind.ToString(),
                    templateId = project.TemplateId,
                },
                Json));

            return 0;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 4;
        }
    }

    /// <summary>
    /// Publishes a project and copies it to the robot over SSH.
    /// </summary>
    /// <param name="arguments">
    /// <c>--project</c>, plus <c>--host</c>, <c>--user</c>, <c>--password</c> or <c>--key</c>,
    /// <c>--remote</c> and <c>--service</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the deployment.</param>
    /// <remarks>
    /// Progress goes to stderr line by line so a caller can show it live. This has never been run
    /// against a real robot — see <c>PROGRESS.md</c>.
    /// </remarks>
    internal static async Task<int> DeployAsync(string[] arguments, CancellationToken cancellationToken)
    {
        string? projectPath = Argument(arguments, "--project");

        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            Console.Error.WriteLine("--project must point at an existing .csproj.");
            return 2;
        }

        var options = new DeploymentOptions
        {
            Host = Argument(arguments, "--host") ?? "192.168.123.18",
            User = Argument(arguments, "--user") ?? "unitree",
            Password = Argument(arguments, "--password") ?? string.Empty,
            PrivateKeyPath = Argument(arguments, "--key") ?? string.Empty,
            RemoteDirectory = Argument(arguments, "--remote") ?? "/home/unitree/apps",
            InstallService = arguments.Contains("--service"),
        };

        if (int.TryParse(Argument(arguments, "--port"), out int port))
        {
            options.Port = port;
        }

        void Report(OutputLine line) =>
            Console.Error.WriteLine($"[{line.Level.ToString().ToLowerInvariant()}] {line.Text}");

        using var builder = new BuildRunner(Report);
        var deployment = new DeploymentService(builder, Report);

        WizardProject project = new ProjectService(
            ProjectService.TryLocateSdkRoot(AppContext.BaseDirectory) ?? Directory.GetCurrentDirectory())
            .Open(projectPath);

        bool ok = await deployment.DeployAsync(project, options, cancellationToken).ConfigureAwait(false);

        Console.Out.WriteLine(JsonSerializer.Serialize(
            new { deployed = ok, project = project.Name, host = options.Host, remote = options.RemoteDirectory }, Json));

        return ok ? 0 : 5;
    }

    /// <summary>
    /// Connects, emits one telemetry snapshot as JSON, and exits.
    /// </summary>
    /// <param name="provider">Configured services.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    internal static async Task<int> ProbeAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var robot = provider.GetRequiredService<UnitreeRobot>();
        var telemetry = provider.GetRequiredService<TelemetryHub>();

        try
        {
            await robot.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UnitreeConnectionException exception)
        {
            // Reported as data rather than as a non-zero exit: "no robot answered" is a normal state
            // for a tool that polls, and a caller should be able to show it without special-casing.
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new { connected = false, model = robot.Model.ToString(), error = exception.Message }, Json));

            return 0;
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(Describe(robot, telemetry), Json));
        await robot.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Streams telemetry as newline-delimited JSON until cancelled.
    /// </summary>
    /// <param name="provider">Configured services.</param>
    /// <param name="arguments"><c>--interval</c> in milliseconds, default 500.</param>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <remarks>
    /// One object per line, flushed each time. A consumer reading line by line needs no framing, and
    /// the flush is what stops a reader waiting on a buffer that never fills.
    /// </remarks>
    internal static async Task<int> StreamAsync(
        IServiceProvider provider,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var robot = provider.GetRequiredService<UnitreeRobot>();
        var telemetry = provider.GetRequiredService<TelemetryHub>();

        int interval = int.TryParse(Argument(arguments, "--interval"), out int parsed)
            ? Math.Clamp(parsed, 50, 10_000)
            : 500;

        try
        {
            await robot.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UnitreeConnectionException exception)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new { connected = false, model = robot.Model.ToString(), error = exception.Message }, Json));
            await Console.Out.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 3;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(Describe(robot, telemetry), Json));
            await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await robot.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static object Describe(UnitreeRobot robot, TelemetryHub telemetry)
    {
        TelemetrySnapshot? snapshot = telemetry.GetSnapshot();

        return new
        {
            connected = robot.State == ConnectionState.Connected,
            state = robot.State.ToString(),
            model = robot.Model.ToString(),
            transport = robot.Options.Transport.ToString(),
            endpoint = $"{robot.Options.MulticastAddress}:{robot.Options.MulticastPort}",
            lowStateCount = telemetry.LowStateCount,
            sportStateCount = telemetry.SportStateCount,
            timestamp = DateTimeOffset.UtcNow,
            telemetry = snapshot is not { } s ? null : new
            {
                batteryPercent = s.Battery.StateOfChargePercent,
                packVoltage = s.Battery.PackVoltage,
                currentAmps = s.Battery.CurrentAmps,
                cycleCount = s.Battery.CycleCount,
                cellImbalanceMillivolts = s.Battery.CellImbalanceMillivolts,
                estimatedMinutesRemaining = s.Battery.EstimateRemaining()?.TotalMinutes,
                maxMotorTemperatureCelsius = s.MaxMotorTemperatureCelsius,
                rollDegrees = float.RadiansToDegrees(s.Orientation.Roll),
                pitchDegrees = float.RadiansToDegrees(s.Orientation.Pitch),
                yawDegrees = float.RadiansToDegrees(s.Orientation.Yaw),
                bodyHeight = s.BodyHeight,
                speed = s.Velocity.Length(),
                odometryX = s.OdometryPosition.X,
                odometryY = s.OdometryPosition.Y,
                feetLoaded = s.FootContact.ContactCount,
                isFullStance = s.FootContact.IsFullStance,
                isAirborne = s.FootContact.IsAirborne,
            },
        };
    }

    private static string? Argument(string[] arguments, string name)
    {
        int index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }
}
