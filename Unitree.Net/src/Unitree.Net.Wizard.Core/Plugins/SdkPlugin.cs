using System.ComponentModel;
using System.Text;
using Microsoft.SemanticKernel;
using Unitree.Net.Wizard.Core.Projects;

namespace Unitree.Net.Wizard.Core.Plugins;

/// <summary>
/// Gives Jack an accurate account of the Unitree.Net API and the ability to scaffold from a template.
/// </summary>
/// <remarks>
/// This exists because the SDK is not in any model's training data. Without it Jack invents
/// plausible-looking members — <c>robot.Walk(1.0)</c>, <c>robot.Battery</c> — that do not exist, and
/// the operator only finds out at build time. The reference below is curated by hand against the real
/// public surface, so it is the one thing here that must be kept in step when the SDK changes.
/// </remarks>
public sealed class SdkPlugin
{
    private readonly Func<WizardProject?> _currentProject;
    private readonly Action<string, string>? _fileChanged;

    /// <summary>Creates the plugin.</summary>
    /// <param name="currentProject">Returns the open project, or null when none is open.</param>
    /// <param name="fileChanged">Called when a scaffolded file is written.</param>
    public SdkPlugin(Func<WizardProject?> currentProject, Action<string, string>? fileChanged = null)
    {
        ArgumentNullException.ThrowIfNull(currentProject);

        _currentProject = currentProject;
        _fileChanged = fileChanged;
    }

    /// <summary>Returns the API reference for one area of the SDK.</summary>
    /// <param name="area">Which area to describe.</param>
    [KernelFunction("describe_sdk")]
    [Description(
        "Returns the real Unitree.Net API for an area: 'overview', 'connection', 'locomotion', " +
        "'telemetry', 'lowlevel', 'navigation', 'arms', 'ai', 'ros2', 'config'. " +
        "ALWAYS call this before writing or editing code that uses the SDK — every time, including " +
        "when you are certain you remember the members. This SDK is not in any training data, so a " +
        "member you recall came from a different library and will not compile. Calling this costs " +
        "one step; guessing costs the user a failed build.")]
    public string DescribeSdk(
        [Description("The area to describe. Use 'overview' if unsure.")] string area = "overview") =>
        area.Trim().ToLowerInvariant() switch
        {
            "connection" => Connection,
            "locomotion" or "sport" or "movement" => Locomotion,
            "telemetry" or "sensors" => Telemetry,
            "lowlevel" or "low-level" or "joints" => LowLevel,
            "navigation" or "waypoints" => Navigation,
            "arms" or "manipulation" => Arms,
            "ai" or "llm" => Ai,
            "ros2" or "ros" => Ros2,
            "config" or "configuration" or "settings" => Configuration,
            _ => Overview,
        };

    /// <summary>Lists the project templates available.</summary>
    /// <param name="kind">Optional filter: console, desktop, web or embedded.</param>
    [KernelFunction("list_templates")]
    [Description("Lists the wizard's project templates, optionally filtered by kind.")]
    public string ListTemplates(
        [Description("Optional: 'console', 'desktop', 'web' or 'embedded'. Empty lists all.")]
        string kind = "")
    {
        IReadOnlyList<ProjectTemplate> templates =
            Enum.TryParse(kind, true, out ProjectKind parsed) ? TemplateCatalog.ByKind(parsed) : TemplateCatalog.All;

        var text = new StringBuilder($"{templates.Count} template(s):\n\n");

        foreach (ProjectTemplate template in templates)
        {
            text.AppendLine($"  {template.Id}  [{template.Kind}]");
            text.AppendLine($"    {template.Summary}");
            text.AppendLine($"    tags: {string.Join(", ", template.Tags)}");
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Returns a template's source as a worked example.</summary>
    /// <param name="templateId">The template identifier.</param>
    [KernelFunction("get_template_code")]
    [Description(
        "Returns a template's full source. Use this as a worked example of correct SDK usage before " +
        "writing something similar yourself.")]
    public string GetTemplateCode(
        [Description("The template id, from list_templates.")] string templateId)
    {
        if (TemplateCatalog.Find(templateId) is not { } template)
        {
            return $"No template called '{templateId}'. Call list_templates to see what exists.";
        }

        var text = new StringBuilder($"# {template.Name}\n{template.Summary}\n\n");

        foreach (TemplateFile file in template.Files)
        {
            text.AppendLine($"--- {file.RelativePath} ---");
            text.AppendLine(file.Content);
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Copies a template's files into the open project.</summary>
    /// <param name="templateId">The template identifier.</param>
    [KernelFunction("scaffold_from_template")]
    [Description(
        "Writes a template's files into the open project, replacing anything with the same name. " +
        "Use when the operator wants a template's behaviour as a starting point. Say what you " +
        "replaced.")]
    public string ScaffoldFromTemplate(
        [Description("The template id, from list_templates.")] string templateId)
    {
        if (_currentProject() is not { } project)
        {
            return "No project is open.";
        }

        if (TemplateCatalog.Find(templateId) is not { } template)
        {
            return $"No template called '{templateId}'.";
        }

        var written = new List<string>();

        foreach (TemplateFile file in template.Files)
        {
            string target = Path.Combine(
                project.RootPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, file.Content, Encoding.UTF8);
                written.Add(file.RelativePath);
                _fileChanged?.Invoke(file.RelativePath, file.Content);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"Wrote {written.Count} file(s), then failed on '{file.RelativePath}': {exception.Message}";
            }
        }

        // The project file is not touched: it carries references the template may need, and silently
        // rewriting it would break a project the operator had already adjusted.
        return $"Wrote {written.Count} file(s): {string.Join(", ", written)}. " +
               $"The project file was left alone — if the build fails on a missing reference, " +
               $"the template expects: {string.Join(", ", template.ProjectReferencePaths)}.";
    }

    // ------------------------------------------------------------------ reference

    private const string Overview = """
Unitree.Net — a .NET 10 SDK for Unitree robots. Namespaces and what lives in them:

  Unitree.Net.Core       UnitreeOptions, RobotModel, RobotModelInfo, GoJoint, G1Joint, EulerAngles,
                         RobotMath, RealtimeLoop, ConnectionState, UnitreeConnectionException
  Unitree.Net.Messages   LowCmd, LowState, SportModeState, Topics, MotorState, BmsState
  Unitree.Net.Dds        IDdsParticipant, DdsParticipant, ManagedMulticastTransport, LoopbackTransport
  Unitree.Net.Control    UnitreeRobot, SportClient, VelocityStream, VelocityCommand,
                         LowLevelController, MotionSwitcherClient, WaypointNavigator, Waypoint
  Unitree.Net.Sensors    TelemetryHub, TelemetrySnapshot, BatteryStatus, FootContactState
  Unitree.Net.Manipulation  ArmController, DualArmCoordinator, TrajectoryPlanner
  Unitree.Net.Ai         AiWorkflowEngine, AiOptions, LlmProvider, KernelFactory
  Unitree.Net.Ros2       Ros2Bridge, Ros2BridgeOptions
  Unitree.Net.Ml         GaitAnomalyDetector, PolicyRunner
  Unitree.Net.Diagnostics  RobotMetrics, RobotHealthCheck
  Unitree.Net.Extensions.DependencyInjection  AddUnitreeRobot, AddUnitreeAi, AddUnitreeRos2Bridge,
                         AddUnitreeDiagnostics, AddUnitreeRobotHostedConnection

The pattern nearly every application uses:

    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddUnitreeRobot(builder.Configuration);
    using IHost host = builder.Build();

    var robot = host.Services.GetRequiredService<UnitreeRobot>();
    var telemetry = host.Services.GetRequiredService<TelemetryHub>();

    await robot.ConnectAsync();

Three facts that catch people out:

 1. Velocity commands are ignored unless the robot is in balanced standing. StandUpAsync alone is not
    enough — call BalanceStandAsync after it.
 2. Low-level commands do nothing, with no error, until the sport service releases the motors.
    BeginLowLevelSessionAsync does that for you.
 3. The robot expires a velocity command about half a second after receiving it. Use
    StartVelocityStream, which resends it at 20 Hz, rather than calling MoveAsync repeatedly. Set
    Command once and it keeps going; you do not need to reassign it in a loop.

Nothing in this SDK has been run against real hardware. Never imply otherwise.
""";

    private const string Connection = """
Connecting — Unitree.Net.Control.UnitreeRobot

    RobotModel Model { get; }
    ConnectionState State { get; }              // Disconnected, Connecting, Connected, Stale, Faulted
    UnitreeOptions Options { get; }
    IDdsParticipant Participant { get; }
    SportClient Sport { get; }
    MotionSwitcherClient MotionSwitcher { get; }
    LowLevelController LowLevel { get; }
    event EventHandler<ConnectionStateChangedEventArgs> StateChanged;

    Task ConnectAsync(CancellationToken ct = default);       // throws UnitreeConnectionException
    Task DisconnectAsync(CancellationToken ct = default);
    Task<LowLevelController> BeginLowLevelSessionAsync(...);
    bool TryGetLowState(out LowState state);
    bool TryGetSportState(out SportModeState state);
    ConnectionState RefreshConnectionState();
    ValueTask DisposeAsync();

Example:

    try
    {
        await robot.ConnectAsync(cancellationToken);
    }
    catch (UnitreeConnectionException exception)
    {
        // Nothing answered — almost always the network interface, the multicast group, or a firewall.
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
""";

    private const string Locomotion = """
Locomotion — Unitree.Net.Control.SportClient, reached as robot.Sport

    Task StandUpAsync(CancellationToken ct = default);
    Task StandDownAsync(CancellationToken ct = default);
    Task BalanceStandAsync(CancellationToken ct = default);   // REQUIRED before velocity commands
    Task StopMoveAsync(CancellationToken ct = default);
    Task DampAsync(CancellationToken ct = default);
    Task RecoveryStandAsync(CancellationToken ct = default);
    Task SitAsync(CancellationToken ct = default);
    Task RiseSitAsync(CancellationToken ct = default);
    Task HelloAsync(CancellationToken ct = default);
    Task StretchAsync(CancellationToken ct = default);
    Task MoveAsync(VelocityCommand command, CancellationToken ct = default);
    void Move(VelocityCommand command);
    Task SetBodyHeightAsync(float offsetMetres, CancellationToken ct = default);
    Task SetFootRaiseHeightAsync(float metres, CancellationToken ct = default);
    Task SetEulerAsync(EulerAngles orientation, CancellationToken ct = default);
    Task SwitchGaitAsync(GaitType gait, CancellationToken ct = default);
    Task SetSpeedLevelAsync(int level, CancellationToken ct = default);
    VelocityStream StartVelocityStream(int updateRateHz = 20, TimeSpan? commandTimeout = null);

    readonly record struct VelocityCommand(float Forward, float Lateral, float YawRate);
    VelocityCommand.Stop is the zero command.

VelocityCommand and EulerAngles live in Unitree.Net.Core, not in Unitree.Net.Control — a file that
imports only Control will not compile.

The hosted setup is Host.CreateApplicationBuilder, which returns a HostApplicationBuilder with a
Services property. It has no ConfigureServices method; that belongs to the older IHostBuilder from
Host.CreateDefaultBuilder.

    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddUnitreeRobot(builder.Configuration);
    using IHost host = builder.Build();

Continuous driving. The stream resends the command for you, so a crash or a closed terminal ends in a
stop rather than a runaway — the resends stop with the process and the robot's own expiry fires.

By default the stream does not expire a command you are deliberately holding: holding one velocity for
a few seconds is ordinary. Pass commandTimeout only when the command's source is remote and can vanish
while your process keeps running — a ROS 2 publisher, or a browser button:

    robot.Sport.StartVelocityStream(commandTimeout: TimeSpan.FromMilliseconds(500));

    await robot.Sport.StandUpAsync();
    await robot.Sport.BalanceStandAsync();

    using VelocityStream stream = robot.Sport.StartVelocityStream();
    stream.Command = new VelocityCommand(0.5f, 0f, 0f);
    await Task.Delay(3000);
    stream.Stop();
""";

    private const string Telemetry = """
Telemetry — Unitree.Net.Sensors.TelemetryHub, resolved from the service provider

    BatteryStatus? GetBattery();
    FootContactState? GetFootContact();
    EulerAngles? GetOrientation();
    TelemetrySnapshot? GetSnapshot();
    IAsyncEnumerable<LowState> StreamLowStateAsync(CancellationToken ct = default);
    IAsyncEnumerable<SportModeState> StreamSportStateAsync(CancellationToken ct = default);
    long LowStateCount { get; }
    long SportStateCount { get; }
    DateTimeOffset? LastLowStateAt { get; }

    readonly record struct TelemetrySnapshot(
        DateTimeOffset Timestamp, EulerAngles Orientation, Vector3 AngularVelocity,
        Vector3 LinearAcceleration, BatteryStatus Battery, FootContactState FootContact,
        int MaxMotorTemperatureCelsius, Vector3 OdometryPosition, float BodyHeight, Vector3 Velocity);

    readonly record struct BatteryStatus(...) with
        StateOfCharge (0-100), VoltageVolts, CurrentAmps, CycleCount, CellImbalanceMillivolts,
        IsCharging, HasCellImbalanceWarning, EstimateRemaining(float capacityAmpHours = 8f)

    readonly record struct FootContactState(short FrontRight, short FrontLeft, short RearRight, short RearLeft)
        with ContactCount, IsFullStance, IsAirborne, and ContactThreshold = 20.

The getters return null until the first message arrives. Handle that rather than dereferencing —
"no telemetry yet" and "battery at 0%" are very different things.
""";

    private const string LowLevel = """
Low-level control — Unitree.Net.Control.LowLevelController

READ docs/safety.md FIRST. At this level nothing sits between your numbers and the motors.

    LowLevelController controller = await robot.BeginLowLevelSessionAsync(cancellationToken: ct);

BeginLowLevelSessionAsync is not optional. The sport service owns the motors until it is told to let
go, and commands sent before that are silently ignored — no error anywhere.

    void Start(Action<ControlTickContext> onTick);   // runs at 500 Hz
    void SetJoint(int index, float position, float velocity, float torque, float kp, float kd);
    void Stop();
    void EmergencyStop();                            // latching; needs an explicit reset
    LoopStatistics Statistics { get; }

The control law is impedance:  tau = tau_ff + kp*(q_des - q) + kd*(dq_des - dq)

Sensible starting gains are kp 20-60 and kd 1-3. Higher kp is stiffer and less forgiving of a
mistake. Joint indices come from GoJoint: FrontRightHip = 0 through RearLeftCalf = 11.

Safety limits throw by default rather than clamping, because a silently clamped command is
indistinguishable from a working one until the robot behaves oddly under load.
""";

    private const string Navigation = """
Navigation — Unitree.Net.Control.WaypointNavigator

    var navigator = new WaypointNavigator(robot, new NavigationOptions { UpdateRateHz = 20 });

    Task<NavigationResult> GoToAsync(Waypoint waypoint, CancellationToken ct = default);
    Task<NavigationResult> FollowRouteAsync(IEnumerable<Waypoint> route, CancellationToken ct = default);

    readonly record struct Waypoint(Vector3 Position, float ToleranceMetres = 0.15f, float? FinalHeading = null);
    Waypoint.At(float x, float y, float toleranceMetres = 0.15f)

    enum NavigationResult { Arrived, Cancelled, Stalled, NoOdometry }

    NavigationOptions: DistanceGain, HeadingGain, TurnInPlaceThreshold, HeadingTolerance,
                       UpdateRateHz, StallTimeout, StallDistanceThreshold

This is a proportional controller over dead-reckoned odometry, with no obstacle avoidance and no
map. Odometry drifts and resets on power cycle, so a long route will not close its loop. For
anything that must repeat precisely, use the robot's own avoidance service or Nav2 through the ROS 2
bridge.
""";

    private const string Arms = """
Manipulation — Unitree.Net.Manipulation, for G1, H1-2 and R1

    var arm = new ArmController(robot, ArmSide.Left);
    var both = new DualArmCoordinator(robot);

    Task MoveToAsync(float[] jointAngles, TimeSpan duration, CancellationToken ct = default);
    Task MoveBothAsync(float[] left, float[] right, TimeSpan duration, CancellationToken ct = default);

Trajectories are quintic polynomials with zero velocity AND zero acceleration at both ends, so a
move starts and stops smoothly rather than jerking. Joint timing is synchronised across the chain:
every joint starts and finishes together instead of each running at its own maximum, because the
path matters as much as the end pose when there are things nearby.

Check the platform first — RobotModelInfo.HasArms(robot.Model). Quadrupeds have none.

Arm chains are seven joints on G1 and R1: shoulder pitch/roll/yaw, elbow, wrist roll/pitch/yaw.
""";

    private const string Ai = """
AI workflows — Unitree.Net.Ai.AiWorkflowEngine

    builder.Services.AddUnitreeAi(builder.Configuration);
    var engine = provider.GetRequiredService<AiWorkflowEngine>();

    Task<string> AskAsync(string question, CancellationToken ct = default);
    IAsyncEnumerable<string> AskStreamingAsync(string question, CancellationToken ct = default);
    Kernel Kernel { get; }

Providers: OpenAI, Anthropic, Gemini, Ollama. Ollama is the default because it needs no key and
nothing leaves the machine, which suits a robot in the field.

Motion is gated twice and both gates default to closed:
    ExposeMotionFunctions          the model cannot even see motion functions
    AllowAutomaticFunctionCalling  the model may propose a call but nothing executes it

With both off it is a genuinely useful diagnostic assistant with no physical risk. This is
supervisory only — model latency is seconds, and nothing here belongs near a balance controller.
""";

    private const string Ros2 = """
ROS 2 bridge — Unitree.Net.Ros2

    builder.Services.AddUnitreeRos2Bridge(options =>
    {
        options.ImuTopic = "rt/imu";
        options.OdometryTopic = "rt/odom";
        options.CommandVelocityTopic = "rt/cmd_vel";
        options.PublishRateHz = 50;
        options.AcceptVelocityCommands = false;   // defaults to false, deliberately
    });

Publishes sensor_msgs/Imu and nav_msgs/Odometry. Optionally subscribes geometry_msgs/Twist.

AcceptVelocityCommands off by default is not timidity: turning it on means anything on the DDS
domain can drive the robot, including a teleop node someone left running in another room.

Frames already agree — ROS 2 and Unitree both use x forward, y left, z up, so no axis remapping is
needed. The bridge does rotate odometry twist into the child frame, because nav_msgs expects it in
base_link while the robot reports world frame.
""";

    private const string Configuration = """
Configuration — bound from the "Unitree" section

    {
      "Unitree": {
        "Model": "Go2",
        "Transport": "ManagedMulticast",
        "MulticastAddress": "239.255.0.1",
        "MulticastPort": 7447,
        "NetworkInterface": "",
        "ConnectTimeoutSeconds": 10,
        "Ai": {
          "Provider": "Ollama",
          "ModelId": "llama3.2",
          "Temperature": 0.2,
          "ExposeMotionFunctions": false,
          "AllowAutomaticFunctionCalling": false
        }
      }
    }

Model:      Go2, Go2W, B2, B2W, G1, H1, H12, R1
Transport:  ManagedMulticast (development and the simulator), CycloneNative (real hardware),
            Loopback (tests)

ManagedMulticast is not RTPS. It carries CDR in this SDK's own framing and cannot reach robot
firmware — it exists so the whole stack develops against the simulator. Real hardware needs
CycloneNative plus the native shim in native/.

API keys belong in user secrets or environment variables, never in appsettings.json:

    export UNITREE_Unitree__Ai__ApiKey="sk-..."
""";
}
