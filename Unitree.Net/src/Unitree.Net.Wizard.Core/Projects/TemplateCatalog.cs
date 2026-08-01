namespace Unitree.Net.Wizard.Core.Projects;

/// <summary>
/// The templates offered by <c>New Project → From Template</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every template produces a project that builds and runs against the simulator without edits. That
/// constraint is deliberate: a template that needs a robot before it will even start is a template
/// nobody can evaluate, and the whole point of the simulator is that they do not have to.
/// </para>
/// <para>
/// Generated code carries the same safety posture as the SDK — motion is gated on readiness, and
/// nothing assumes the robot is upright because the previous line asked it to stand.
/// </para>
/// </remarks>
public static class TemplateCatalog
{
    /// <summary>Every template, in the order the gallery shows them.</summary>
    public static IReadOnlyList<ProjectTemplate> All { get; } = Build();

    /// <summary>Finds a template by identifier.</summary>
    /// <param name="id">The template identifier.</param>
    /// <returns>The template, or <see langword="null"/> if no template has that identifier.</returns>
    public static ProjectTemplate? Find(string id) =>
        All.FirstOrDefault(template => string.Equals(template.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Templates producing applications of a given kind.</summary>
    /// <param name="kind">The kind to filter by.</param>
    public static IReadOnlyList<ProjectTemplate> ByKind(ProjectKind kind) =>
        [.. All.Where(template => template.Kind == kind)];

    /// <summary>Searches names, summaries and tags.</summary>
    /// <param name="query">Free text. Empty returns everything.</param>
    public static IReadOnlyList<ProjectTemplate> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return All;
        }

        string needle = query.Trim();

        return
        [
            .. All.Where(template =>
                template.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || template.Summary.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || template.Tags.Any(tag => tag.Contains(needle, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    // Project references are stored relative to the repository root and rewritten to the project's own
    // location when it is created, so a project can be scaffolded anywhere on disk.
    private const string CoreRef = "src/Unitree.Net.Core/Unitree.Net.Core.csproj";
    private const string ControlRef = "src/Unitree.Net.Control/Unitree.Net.Control.csproj";
    private const string SensorsRef = "src/Unitree.Net.Sensors/Unitree.Net.Sensors.csproj";
    private const string DiRef = "src/Unitree.Net.Extensions.DependencyInjection/Unitree.Net.Extensions.DependencyInjection.csproj";
    private const string AiRef = "src/Unitree.Net.Ai/Unitree.Net.Ai.csproj";
    private const string Ros2Ref = "src/Unitree.Net.Ros2/Unitree.Net.Ros2.csproj";
    private const string MlRef = "src/Unitree.Net.Ml/Unitree.Net.Ml.csproj";

    /// <summary>The <c>appsettings.json</c> every hosted template ships with.</summary>
    private const string AppSettings = """
{
  "Unitree": {
    "Model": "Go2",
    "Transport": "ManagedMulticast",
    "MulticastAddress": "239.255.0.1",
    "MulticastPort": 7447,
    "NetworkInterface": "",
    "ConnectTimeoutSeconds": 10
  },
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft": "Warning" }
  }
}
""";

    private static ProjectTemplate Template(
        string id,
        string name,
        string summary,
        ProjectKind kind,
        string[] tags,
        string program,
        string[] projectReferences,
        (string Path, string Content)[]? extraFiles = null,
        string[]? packages = null)
    {
        var files = new List<TemplateFile> { new("Program.cs", program) };

        foreach ((string path, string content) in extraFiles ?? [])
        {
            files.Add(new TemplateFile(path, content));
        }

        return new ProjectTemplate(
            id, name, summary, kind, tags, files, packages ?? [], projectReferences);
    }

    private static ProjectTemplate[] Build() =>
    [
        // ------------------------------------------------------------------ console

        Template(
            "telemetry-monitor",
            "Telemetry Monitor",
            "Connects and prints live battery, orientation, temperature and foot contact.",
            ProjectKind.Console,
            ["telemetry", "starter", "monitoring"],
            """
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Sensors;

// Telemetry monitor — the smallest useful robot application.
//
// Start the simulator (or a real robot on the same network) and run this. It connects, waits for the
// first state message, then prints a snapshot once a second until you stop it.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);

using IHost host = builder.Build();

var robot = host.Services.GetRequiredService<UnitreeRobot>();
var telemetry = host.Services.GetRequiredService<TelemetryHub>();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

Console.WriteLine($"Connecting to {robot.Model} on {robot.Options.MulticastAddress}...");

try
{
    await robot.ConnectAsync(cancellation.Token);
}
catch (UnitreeConnectionException exception)
{
    // Nothing answered. Almost always the network interface, the multicast group, or a firewall —
    // `unitree diagnose` distinguishes the three without needing a robot.
    Console.Error.WriteLine($"Could not connect: {exception.Message}");
    return 1;
}

Console.WriteLine("Connected. Press Ctrl+C to stop.");
Console.WriteLine();

while (!cancellation.IsCancellationRequested)
{
    if (telemetry.GetSnapshot() is { } snapshot)
    {
        Console.WriteLine(
            $"{snapshot.Timestamp:HH:mm:ss}  " +
            $"battery {snapshot.Battery.StateOfChargePercent,3}%  " +
            $"{snapshot.Battery.PackVoltage,5:0.0} V  " +
            $"yaw {float.RadiansToDegrees(snapshot.Orientation.Yaw),6:0.0}deg  " +
            $"motor {snapshot.MaxMotorTemperatureCelsius,2} C  " +
            $"feet {snapshot.FootContact.ContactCount}/4");
    }
    else
    {
        Console.WriteLine("waiting for telemetry...");
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

await robot.DisconnectAsync();
Console.WriteLine("Disconnected.");
return 0;
""",
            [CoreRef, ControlRef, SensorsRef, DiRef],
            [("appsettings.json", AppSettings)]),

        Template(
            "patrol-route",
            "Patrol Route",
            "Walks a closed loop of waypoints, checking battery before each leg.",
            ProjectKind.Console,
            ["navigation", "waypoints", "patrol"],
            """
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Sensors;

// Patrol route — walks a rectangle and returns to the start, repeating until stopped.
//
// Odometry drifts, so a long patrol will not close its loop exactly. That is a property of dead
// reckoning, not a bug here: for anything that must repeat precisely, localise against a map.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);

using IHost host = builder.Build();

var robot = host.Services.GetRequiredService<UnitreeRobot>();
var telemetry = host.Services.GetRequiredService<TelemetryHub>();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

await robot.ConnectAsync(cancellation.Token);
Console.WriteLine("Connected.");

// Balanced standing is what makes the robot accept velocity commands at all. Standing up alone is
// not enough, and the failure is silent.
await robot.Sport.StandUpAsync(cancellation.Token);
await robot.Sport.BalanceStandAsync(cancellation.Token);

Waypoint[] route =
[
    Waypoint.At(2.0f, 0.0f),
    Waypoint.At(2.0f, 2.0f),
    Waypoint.At(0.0f, 2.0f),
    Waypoint.At(0.0f, 0.0f),
];

var navigator = new WaypointNavigator(robot);
int lap = 0;

while (!cancellation.IsCancellationRequested)
{
    // Checked every lap rather than only at startup: a patrol runs for hours, and the interesting
    // failure is the battery going flat halfway round, not at the beginning.
    if (telemetry.GetBattery() is { StateOfChargePercent: < 20 } battery)
    {
        Console.WriteLine($"Battery at {battery.StateOfChargePercent}% — ending patrol.");
        break;
    }

    lap++;
    Console.WriteLine($"Lap {lap}");

    NavigationResult result = await navigator.FollowRouteAsync(route, cancellation.Token);

    if (result != NavigationResult.Arrived)
    {
        Console.WriteLine($"Route ended early: {result}");
        break;
    }
}

await robot.Sport.StopMoveAsync();
await robot.Sport.StandDownAsync();
await robot.DisconnectAsync();
Console.WriteLine($"Patrol finished after {lap} lap(s).");
""",
            [CoreRef, ControlRef, SensorsRef, DiRef],
            [("appsettings.json", AppSettings)]),

        Template(
            "low-level-control",
            "Low-Level Joint Control",
            "Runs a 500 Hz impedance loop over the 12 leg joints with a latching e-stop.",
            ProjectKind.Console,
            ["low-level", "impedance", "500hz", "advanced"],
            """
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Extensions.DependencyInjection;

// Low-level joint control.
//
// READ docs/safety.md BEFORE RUNNING THIS ON A ROBOT. At this level there is no balance controller
// between your numbers and the motors. Put the robot on a stand, clear the area, and keep a hand on
// the power.
//
// This example holds a gentle standing pose. It is deliberately boring: the useful thing to copy is
// the session setup and the shape of the tick, not the motion.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);

using IHost host = builder.Build();
var robot = host.Services.GetRequiredService<UnitreeRobot>();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

await robot.ConnectAsync(cancellation.Token);

// The sport service owns the motors until it is told to let go. Skipping this step produces no
// error at all — the commands are simply ignored, which is the single most confusing failure in the
// whole SDK. BeginLowLevelSessionAsync performs the release and waits for it to take effect.
LowLevelController controller = await robot.BeginLowLevelSessionAsync(
    cancellationToken: cancellation.Token);

float[] standingPose =
[
    0.0f, 0.80f, -1.55f,
    0.0f, 0.80f, -1.55f,
    0.0f, 1.00f, -1.55f,
    0.0f, 1.00f, -1.55f,
];

// Modest gains. Stiff enough to hold the pose, soft enough that a mistake pushes the leg rather
// than snapping it.
const float Kp = 40f;
const float Kd = 2.0f;

for (int joint = 0; joint < GoJoint.Count; joint++)
{
    controller.SetJointPosition(joint, standingPose[joint], Kp, Kd);
}

Console.WriteLine("Holding standing pose at 500 Hz. Ctrl+C to stop.");
controller.Start();

try
{
    await Task.Delay(Timeout.Infinite, cancellation.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C.
}

// Damping ramps torque to zero instead of dropping the robot.
controller.Stop();
await robot.Sport.DampAsync();
await robot.DisconnectAsync();

LoopStatistics stats = controller.LoopStatistics;
Console.WriteLine($"{stats.TickCount:N0} ticks, mean jitter {stats.MeanJitterMicroseconds:0} us.");
""",
            [CoreRef, ControlRef, DiRef],
            [("appsettings.json", AppSettings)]),

        Template(
            "battery-guardian",
            "Battery Guardian",
            "Watches the pack and walks the robot home before it runs out.",
            ProjectKind.Console,
            ["battery", "safety", "autonomy"],
            """
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Sensors;

// Battery guardian — a supervisor that brings the robot home while it still can.
//
// The reserve is expressed in charge rather than time because time-to-empty depends on what the
// robot is doing. Walking home costs more than standing still, which is exactly when you need it.

const int ReturnThresholdPercent = 25;
const int ShutdownThresholdPercent = 10;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);

using IHost host = builder.Build();
var robot = host.Services.GetRequiredService<UnitreeRobot>();
var telemetry = host.Services.GetRequiredService<TelemetryHub>();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

await robot.ConnectAsync(cancellation.Token);
Console.WriteLine("Guardian active.");

var dock = Waypoint.At(0f, 0f);
var navigator = new WaypointNavigator(robot);
bool returning = false;

while (!cancellation.IsCancellationRequested)
{
    BatteryStatus? battery = telemetry.GetBattery();

    if (battery is null)
    {
        Console.WriteLine("No battery telemetry — is the robot connected?");
        await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
        continue;
    }

    BatteryStatus status = battery.Value;
    TimeSpan? remaining = status.EstimateRemaining();

    Console.WriteLine(
        $"{status.StateOfChargePercent,3}%  {status.PackVoltage:0.0} V  " +
        $"{(remaining is { } left ? $"~{left.TotalMinutes:0} min left" : "runtime unknown")}" +
        $"{(status.HasCellImbalanceWarning ? "  [cell imbalance]" : string.Empty)}");

    if (status.StateOfChargePercent <= ShutdownThresholdPercent)
    {
        Console.WriteLine("Below hard floor — sitting down now.");
        await robot.Sport.StopMoveAsync();
        await robot.Sport.StandDownAsync();
        break;
    }

    if (status.StateOfChargePercent <= ReturnThresholdPercent && !returning)
    {
        returning = true;
        Console.WriteLine("Reserve reached — returning to dock.");

        await robot.Sport.BalanceStandAsync(cancellation.Token);
        NavigationResult result = await navigator.GoToAsync(dock, cancellation.Token);

        Console.WriteLine($"Return finished: {result}");
        await robot.Sport.StandDownAsync();
        break;
    }

    await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
}

await robot.DisconnectAsync();
""",
            [CoreRef, ControlRef, SensorsRef, DiRef],
            [("appsettings.json", AppSettings)]),

        Template(
            "gait-logger",
            "Gait Data Logger",
            "Records joint states to CSV at full rate for offline analysis or training.",
            ProjectKind.Console,
            ["logging", "data", "machine-learning", "csv"],
            """
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Messages.Go;
using Unitree.Net.Sensors;

// Gait data logger — writes every low-state message to CSV.
//
// Buffered rather than written per sample: at 500 Hz an unbuffered write per message would dominate
// the loop and the file would record the logger's own overhead as much as the robot's motion.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);

using IHost host = builder.Build();
var robot = host.Services.GetRequiredService<UnitreeRobot>();
var telemetry = host.Services.GetRequiredService<TelemetryHub>();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

await robot.ConnectAsync(cancellation.Token);

string path = args.FirstOrDefault() ?? $"gait-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
await using var writer = new StreamWriter(path, append: false, Encoding.UTF8, bufferSize: 1 << 16);

var header = new StringBuilder("tick,roll,pitch,yaw,foot_fr,foot_fl,foot_rr,foot_rl");

for (int joint = 0; joint < GoJoint.Count; joint++)
{
    string name = GoJoint.GetName(joint);
    header.Append(CultureInfo.InvariantCulture, $",{name}_q,{name}_dq,{name}_tau");
}

await writer.WriteLineAsync(header.ToString());

Console.WriteLine($"Logging to {path}. Ctrl+C to stop.");

long rows = 0;
var line = new StringBuilder(1024);

await foreach (LowState state in telemetry.StreamLowStateAsync(cancellation.Token))
{
    line.Clear();
    line.Append(CultureInfo.InvariantCulture, $"{state.Tick}");
    line.Append(CultureInfo.InvariantCulture, $",{state.ImuState.Rpy[0]:0.#####}");
    line.Append(CultureInfo.InvariantCulture, $",{state.ImuState.Rpy[1]:0.#####}");
    line.Append(CultureInfo.InvariantCulture, $",{state.ImuState.Rpy[2]:0.#####}");

    for (int foot = 0; foot < 4; foot++)
    {
        line.Append(CultureInfo.InvariantCulture, $",{state.FootForce[foot]}");
    }

    for (int joint = 0; joint < GoJoint.Count; joint++)
    {
        line.Append(CultureInfo.InvariantCulture, $",{state.MotorState[joint].Q:0.#####}");
        line.Append(CultureInfo.InvariantCulture, $",{state.MotorState[joint].Dq:0.#####}");
        line.Append(CultureInfo.InvariantCulture, $",{state.MotorState[joint].TauEst:0.#####}");
    }

    await writer.WriteLineAsync(line.ToString());

    if (++rows % 2500 == 0)
    {
        Console.WriteLine($"{rows:N0} rows");
    }
}

await writer.FlushAsync(CancellationToken.None);
await robot.DisconnectAsync();
Console.WriteLine($"Wrote {rows:N0} rows to {path}.");
""",
            [CoreRef, ControlRef, SensorsRef, DiRef],
            [("appsettings.json", AppSettings)]),

        Template(
            "teleop-keyboard",
            "Keyboard Teleoperation",
            "Drives the robot from the arrow keys through a watchdog-backed velocity stream.",
            ProjectKind.Console,
            ["teleop", "control", "keyboard", "starter"],
            """
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Extensions.DependencyInjection;

// Keyboard teleoperation.
//
// Commands go through a VelocityStream rather than one-shot MoveAsync calls. The stream refreshes the
// command continuously and stops the robot if refreshes stop arriving — so releasing the key, closing
// the terminal, or the process dying all produce the same safe outcome.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);

using IHost host = builder.Build();
var robot = host.Services.GetRequiredService<UnitreeRobot>();

await robot.ConnectAsync();
await robot.Sport.StandUpAsync();
await robot.Sport.BalanceStandAsync();

using VelocityStream stream = robot.Sport.StartVelocityStream();

Console.WriteLine("Arrow keys drive, space stops, Q quits.");
Console.WriteLine("The robot stops on its own if commands stop arriving — that is the watchdog.");

const float Speed = 0.5f;
const float Turn = 0.8f;
bool running = true;

while (running)
{
    ConsoleKeyInfo key = Console.ReadKey(intercept: true);

    stream.Command = key.Key switch
    {
        ConsoleKey.UpArrow => new VelocityCommand(Speed, 0f, 0f),
        ConsoleKey.DownArrow => new VelocityCommand(-Speed * 0.6f, 0f, 0f),
        ConsoleKey.LeftArrow => new VelocityCommand(0f, 0f, Turn),
        ConsoleKey.RightArrow => new VelocityCommand(0f, 0f, -Turn),
        _ => VelocityCommand.Stop,
    };

    if (key.Key == ConsoleKey.Q)
    {
        running = false;
    }

    Console.WriteLine($"  vx {stream.Command.Forward,5:0.00}  wz {stream.Command.YawRate,5:0.00}");
}

stream.Stop();
await robot.Sport.StandDownAsync();
await robot.DisconnectAsync();
""",
            [CoreRef, ControlRef, DiRef],
            [("appsettings.json", AppSettings)]),

        // --------------------------------------------------------------------- web

        Template(
            "web-telemetry-api",
            "REST Control API",
            "Minimal API exposing telemetry and gated motion endpoints over HTTP.",
            ProjectKind.Web,
            ["web", "api", "rest", "integration"],
            """
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Sensors;

// REST control API.
//
// Motion endpoints are behind a switch that defaults to off. An HTTP endpoint that moves a robot is
// reachable by anything that can reach the port, and "it is only on the lab network" has a way of
// becoming untrue.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddUnitreeRobot(builder.Configuration);
builder.Services.AddUnitreeRobotHostedConnection();
builder.Services.AddUnitreeDiagnostics();

bool allowMotion = builder.Configuration.GetValue("Robot:AllowMotion", false);

WebApplication app = builder.Build();

app.MapHealthChecks("/health");

app.MapGet("/api/status", (UnitreeRobot robot, TelemetryHub telemetry) =>
{
    TelemetrySnapshot? snapshot = telemetry.GetSnapshot();

    return Results.Ok(new
    {
        model = robot.Model.ToString(),
        state = robot.State.ToString(),
        connected = robot.State == ConnectionState.Connected,
        battery = snapshot?.Battery.StateOfChargePercent,
        voltage = snapshot?.Battery.PackVoltage,
        motorTemperature = snapshot?.MaxMotorTemperatureCelsius,
        feetLoaded = snapshot?.FootContact.ContactCount,
        yawDegrees = snapshot is { } s ? float.RadiansToDegrees(s.Orientation.Yaw) : (float?)null,
    });
});

app.MapGet("/api/battery", (TelemetryHub telemetry) =>
    telemetry.GetBattery() is { } battery
        ? Results.Ok(new
        {
            percent = battery.StateOfChargePercent,
            volts = battery.PackVoltage,
            amps = battery.CurrentAmps,
            cycles = battery.CycleCount,
            imbalanceMillivolts = battery.CellImbalanceMillivolts,
            estimatedMinutesRemaining = battery.EstimateRemaining()?.TotalMinutes,
        })
        : Results.NotFound("No battery telemetry yet."));

if (allowMotion)
{
    app.MapPost("/api/stand", async (UnitreeRobot robot) =>
    {
        await robot.Sport.StandUpAsync();
        await robot.Sport.BalanceStandAsync();
        return Results.Ok(new { ok = true });
    });

    app.MapPost("/api/stop", async (UnitreeRobot robot) =>
    {
        await robot.Sport.StopMoveAsync();
        return Results.Ok(new { ok = true });
    });

    app.MapPost("/api/move", async (MoveRequest request, UnitreeRobot robot, TelemetryHub telemetry) =>
    {
        // Re-checked here rather than trusted from a previous call: an HTTP API has no session, so
        // whatever the last caller did tells you nothing about the robot's current state.
        if (telemetry.GetFootContact() is not { IsFullStance: true })
        {
            return Results.Conflict("The robot is not in full stance.");
        }

        await robot.Sport.MoveAsync(new VelocityCommand(request.Forward, request.Lateral, request.YawRate));
        return Results.Ok(new { ok = true });
    });
}

app.Run();

internal sealed record MoveRequest(float Forward, float Lateral, float YawRate);
""",
            [CoreRef, ControlRef, SensorsRef, DiRef],
            [("appsettings.json", """
{
  "Unitree": {
    "Model": "Go2",
    "Transport": "ManagedMulticast",
    "MulticastAddress": "239.255.0.1",
    "MulticastPort": 7447
  },
  "Robot": {
    "AllowMotion": false
  },
  "Logging": { "LogLevel": { "Default": "Information" } }
}
""")]),

        Template(
            "web-dashboard",
            "Live Web Dashboard",
            "Blazor Server page charting battery, temperature and speed as they arrive.",
            ProjectKind.Web,
            ["web", "blazor", "dashboard", "charts"],
            """
using RobotDashboard.Components;
using Unitree.Net.Extensions.DependencyInjection;

// Live web dashboard. Blazor Server so the telemetry stream stays on the server and the browser only
// receives rendered diffs — a 500 Hz feed does not belong on a WebSocket to a phone.
//
// The components declare a fixed namespace in _Imports.razor rather than inheriting the project's,
// so this file compiles whatever the operator named the project.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddUnitreeRobot(builder.Configuration);
builder.Services.AddUnitreeRobotHostedConnection();
builder.Services.AddUnitreeDiagnostics();

WebApplication app = builder.Build();

app.UseStaticFiles();
app.MapHealthChecks("/health");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
""",
            [CoreRef, ControlRef, SensorsRef, DiRef],
            [
                ("Components/App.razor", """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Robot dashboard</title>
    <base href="/" />
    <HeadOutlet />
</head>
<body style="font-family:system-ui;margin:0;background:#101418;color:#e8eef6">
    <Routes />
    <script src="_framework/blazor.web.js"></script>
</body>
</html>
"""),
                ("Components/Routes.razor", """
@* Router takes a NotFoundPage parameter in .NET 10; the old <NotFound> child element is gone, and
   leaving one in stops Found's Context from binding at all. *@
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="routeData" />
    </Found>
</Router>
"""),
                ("Components/_Imports.razor", """
@namespace RobotDashboard.Components
@* Routing must be imported or <Router> is not resolved as a component at all — Razor then treats it
   as plain markup and Found's Context silently binds nothing. *@
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@* Render modes are static members of RenderMode, so `@rendermode InteractiveServer` only resolves
   with this static using in scope. *@
@using static Microsoft.AspNetCore.Components.Web.RenderMode
@using Unitree.Net.Control
@using Unitree.Net.Sensors
@using RobotDashboard.Components
"""),
                ("Components/Home.razor", """
@page "/"
@rendermode InteractiveServer
@implements IDisposable
@inject UnitreeRobot Robot
@inject TelemetryHub Telemetry

<main style="max-width:900px;margin:0 auto;padding:2rem 1.5rem">
    <h1 style="font-size:1.25rem;letter-spacing:.02em">@Robot.Model — @Robot.State</h1>

    @if (_snapshot is { } s)
    {
        <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:1rem">
            <Tile Label="Battery" Value="@($"{s.Battery.StateOfChargePercent}%")" />
            <Tile Label="Pack" Value="@($"{s.Battery.PackVoltage:0.0} V")" />
            <Tile Label="Hottest motor" Value="@($"{s.MaxMotorTemperatureCelsius} °C")" />
            <Tile Label="Feet loaded" Value="@($"{s.FootContact.ContactCount} / 4")" />
            <Tile Label="Speed" Value="@($"{s.Velocity.Length():0.00} m/s")" />
            <Tile Label="Body height" Value="@($"{s.BodyHeight:0.000} m")" />
        </div>
    }
    else
    {
        <p style="color:#8b97a8">Waiting for telemetry…</p>
    }
</main>

@code {
    private TelemetrySnapshot? _snapshot;
    private Timer? _timer;

    protected override void OnInitialized() =>
        // Four times a second. The robot publishes at 500 Hz; re-rendering at that rate would send
        // more diffs than a browser can paint and tell the reader nothing extra.
        _timer = new Timer(_ =>
        {
            _snapshot = Telemetry.GetSnapshot();
            InvokeAsync(StateHasChanged);
        }, null, 0, 250);

    public void Dispose() => _timer?.Dispose();
}
"""),
                ("Components/Tile.razor", """
<div style="background:#171d25;border:1px solid #2a3441;border-radius:8px;padding:.9rem 1rem">
    <div style="font-size:.7rem;letter-spacing:.12em;text-transform:uppercase;color:#8b97a8">@Label</div>
    <div style="font-size:1.6rem;font-weight:600;font-variant-numeric:tabular-nums">@Value</div>
</div>

@code {
    [Parameter, EditorRequired] public string Label { get; set; } = string.Empty;
    [Parameter, EditorRequired] public string Value { get; set; } = string.Empty;
}
"""),
                ("appsettings.json", AppSettings),
            ]),

        // ---------------------------------------------------------------- embedded

        Template(
            "embedded-inspection",
            "Autonomous Inspection Agent",
            "Runs on the robot: patrols stations, records readings, reports anomalies.",
            ProjectKind.Embedded,
            ["embedded", "autonomy", "inspection", "jetson"],
            """
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Sensors;

// Autonomous inspection agent, published for the robot's own compute module.
//
// Running on the robot rather than beside it removes the wireless link from the control path. The
// link still matters for reporting, but a dropout no longer stops the inspection — it only delays
// the upload, which is why results are written to disk first and sent afterwards.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);

using IHost host = builder.Build();
var robot = host.Services.GetRequiredService<UnitreeRobot>();
var telemetry = host.Services.GetRequiredService<TelemetryHub>();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

await robot.ConnectAsync(cancellation.Token);
await robot.Sport.StandUpAsync(cancellation.Token);
await robot.Sport.BalanceStandAsync(cancellation.Token);

(string Name, Waypoint Where)[] stations =
[
    ("pump-a", Waypoint.At(3.0f, 0.0f)),
    ("valve-b", Waypoint.At(3.0f, 2.5f)),
    ("panel-c", Waypoint.At(0.0f, 2.5f)),
];

var navigator = new WaypointNavigator(robot);
var readings = new List<object>();

foreach ((string name, Waypoint where) in stations)
{
    Console.WriteLine($"-> {name}");
    NavigationResult result = await navigator.GoToAsync(where, cancellation.Token);

    if (result != NavigationResult.Arrived)
    {
        Console.WriteLine($"   could not reach {name}: {result}");
        readings.Add(new { station = name, reached = false, reason = result.ToString() });
        continue;
    }

    // Settle before reading. Arriving and measuring in the same instant records the robot's own
    // motion as part of the measurement.
    await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);

    if (telemetry.GetSnapshot() is { } snapshot)
    {
        readings.Add(new
        {
            station = name,
            reached = true,
            at = snapshot.Timestamp,
            tiltDegrees = float.RadiansToDegrees(Math.Abs(snapshot.Orientation.Pitch)),
            motorTemperature = snapshot.MaxMotorTemperatureCelsius,
            battery = snapshot.Battery.StateOfChargePercent,
        });
    }
}

await robot.Sport.StandDownAsync();
await robot.DisconnectAsync();

string report = JsonSerializer.Serialize(readings, new JsonSerializerOptions { WriteIndented = true });
string path = $"inspection-{DateTime.Now:yyyyMMdd-HHmmss}.json";
await File.WriteAllTextAsync(path, report, CancellationToken.None);

Console.WriteLine($"Wrote {path}");
""",
            [CoreRef, ControlRef, SensorsRef, DiRef],
            [("appsettings.json", AppSettings)]),

        Template(
            "embedded-follow-me",
            "Follow-Me Behaviour",
            "Holds a set distance from a target bearing, with a hard stop when contact is lost.",
            ProjectKind.Embedded,
            ["embedded", "behaviour", "following"],
            """
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Extensions.DependencyInjection;

// Follow-me behaviour.
//
// The tracker here is a stand-in that walks a circle: replace ReadTarget with a real detector — a
// depth camera, a UWB tag, a person detector. Everything around it is the part worth keeping, in
// particular that losing the target commands a stop rather than holding the last command.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);

using IHost host = builder.Build();
var robot = host.Services.GetRequiredService<UnitreeRobot>();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

await robot.ConnectAsync(cancellation.Token);
await robot.Sport.StandUpAsync(cancellation.Token);
await robot.Sport.BalanceStandAsync(cancellation.Token);

using VelocityStream stream = robot.Sport.StartVelocityStream();

const float StandoffMetres = 1.2f;
const float MaxSpeed = 0.9f;

DateTimeOffset lastSeen = DateTimeOffset.UtcNow;
double phase = 0;

Console.WriteLine($"Following at {StandoffMetres:0.0} m. Ctrl+C to stop.");

while (!cancellation.IsCancellationRequested)
{
    (bool found, float rangeMetres, float bearingRadians) = ReadTarget(ref phase);

    if (found)
    {
        lastSeen = DateTimeOffset.UtcNow;

        // Proportional on range error and bearing. The dead band stops the robot shuffling when it
        // is already at the right distance, which is most of the time.
        float rangeError = rangeMetres - StandoffMetres;
        float forward = Math.Abs(rangeError) < 0.15f ? 0f : Math.Clamp(rangeError * 0.9f, -MaxSpeed, MaxSpeed);
        float yaw = Math.Clamp(bearingRadians * 1.4f, -1.2f, 1.2f);

        stream.Command = new VelocityCommand(forward, 0f, yaw);
    }
    else if (DateTimeOffset.UtcNow - lastSeen > TimeSpan.FromSeconds(1.5))
    {
        // Coasting on the last command after losing the target is how a following robot walks into
        // something. Stop, and wait to be found again.
        stream.Command = VelocityCommand.Stop;
    }

    await Task.Delay(50, CancellationToken.None);
}

stream.Stop();
await robot.Sport.StandDownAsync();
await robot.DisconnectAsync();

// Replace this with a real detector. Returns whether a target is visible, its range in metres and
// its bearing in radians (positive to the left).
static (bool Found, float Range, float Bearing) ReadTarget(ref double phase)
{
    phase += 0.02;
    return (true, 1.2f + (0.6f * (float)Math.Sin(phase)), 0.3f * (float)Math.Cos(phase * 0.7));
}
""",
            [CoreRef, ControlRef, DiRef],
            [("appsettings.json", AppSettings)]),

        Template(
            "embedded-fleet-reporter",
            "Fleet Reporter",
            "Posts periodic telemetry to a back end and survives the link going down.",
            ProjectKind.Embedded,
            ["embedded", "fleet", "http", "telemetry"],
            """
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Sensors;

// Fleet reporter — posts a summary upstream on a schedule.
//
// The queue is bounded and drops the oldest entry when full. A robot that loses its uplink for an
// hour should not come back and flood the back end with an hour of stale samples, and it certainly
// should not run itself out of memory holding them.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);
builder.Services.AddHttpClient();

using IHost host = builder.Build();

var robot = host.Services.GetRequiredService<UnitreeRobot>();
var telemetry = host.Services.GetRequiredService<TelemetryHub>();
var clients = host.Services.GetRequiredService<IHttpClientFactory>();

string endpoint = host.Services.GetRequiredService<IConfiguration>()
    .GetValue("Fleet:Endpoint", "http://localhost:5000/api/telemetry")!;
string robotId = Environment.MachineName;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

await robot.ConnectAsync(cancellation.Token);

var pending = new Queue<object>();
const int MaxPending = 240;

Console.WriteLine($"Reporting to {endpoint} every 15 s.");

while (!cancellation.IsCancellationRequested)
{
    if (telemetry.GetSnapshot() is { } snapshot)
    {
        if (pending.Count >= MaxPending)
        {
            pending.Dequeue();
        }

        pending.Enqueue(new
        {
            robotId,
            at = snapshot.Timestamp,
            battery = snapshot.Battery.StateOfChargePercent,
            volts = snapshot.Battery.PackVoltage,
            motorTemperature = snapshot.MaxMotorTemperatureCelsius,
            feetLoaded = snapshot.FootContact.ContactCount,
            x = snapshot.OdometryPosition.X,
            y = snapshot.OdometryPosition.Y,
        });
    }

    while (pending.Count > 0)
    {
        HttpClient client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            HttpResponseMessage response =
                await client.PostAsJsonAsync(endpoint, pending.Peek(), cancellation.Token);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Back end returned {(int)response.StatusCode}; will retry.");
                break;
            }

            pending.Dequeue();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"Uplink down ({exception.GetType().Name}); {pending.Count} queued.");
            break;
        }
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(15), cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

await robot.DisconnectAsync();
""",
            [CoreRef, ControlRef, SensorsRef, DiRef],
            [("appsettings.json", """
{
  "Unitree": {
    "Model": "Go2",
    "Transport": "ManagedMulticast",
    "MulticastAddress": "239.255.0.1",
    "MulticastPort": 7447
  },
  "Fleet": { "Endpoint": "http://localhost:5000/api/telemetry" },
  "Logging": { "LogLevel": { "Default": "Information" } }
}
""")],
            ["Microsoft.Extensions.Http"]),

        // ---------------------------------------------------------------------- AI

        Template(
            "ai-assistant",
            "Conversational Robot Assistant",
            "Natural-language supervision through Semantic Kernel, motion off by default.",
            ProjectKind.Console,
            ["ai", "llm", "semantic-kernel", "assistant"],
            """
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Ai;
using Unitree.Net.Control;
using Unitree.Net.Extensions.DependencyInjection;

// Conversational robot assistant.
//
// Supervisory only. A language model answers in seconds; nothing here belongs anywhere near a balance
// controller. With motion functions disabled — the default — it is a genuinely useful diagnostic
// assistant that can read every sensor and explain what it sees, and cannot move anything.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);
builder.Services.AddUnitreeAi(builder.Configuration);

using IHost host = builder.Build();

var robot = host.Services.GetRequiredService<UnitreeRobot>();
var engine = host.Services.GetRequiredService<AiWorkflowEngine>();

await robot.ConnectAsync();

Console.WriteLine("Ask about the robot. Blank line to quit.");
Console.WriteLine("Try: \"why won't it walk?\" or \"how is the battery holding up?\"");
Console.WriteLine();

while (true)
{
    Console.Write("you> ");
    string? question = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(question))
    {
        break;
    }

    Console.Write("robot> ");

    await foreach (string chunk in engine.AskStreamingAsync(question))
    {
        Console.Write(chunk);
    }

    Console.WriteLine();
    Console.WriteLine();
}

await robot.DisconnectAsync();
""",
            [CoreRef, ControlRef, SensorsRef, AiRef, DiRef],
            [("appsettings.json", """
{
  "Unitree": {
    "Model": "Go2",
    "Transport": "ManagedMulticast",
    "MulticastAddress": "239.255.0.1",
    "MulticastPort": 7447,
    "Ai": {
      "Provider": "Ollama",
      "ModelId": "llama3.2",
      "Temperature": 0.2,
      "MaxTokens": 1024,
      "ExposeMotionFunctions": false,
      "AllowAutomaticFunctionCalling": false
    }
  },
  "Logging": { "LogLevel": { "Default": "Warning" } }
}
""")]),

        Template(
            "anomaly-watch",
            "Gait Anomaly Watch",
            "Learns the robot's normal gait and flags departures with ML.NET.",
            ProjectKind.Console,
            ["machine-learning", "ml.net", "anomaly", "monitoring"],
            """
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Messages.Go;
using Unitree.Net.Ml;
using Unitree.Net.Sensors;

// Gait anomaly watch.
//
// Establishes a baseline from the robot's own behaviour rather than from a fixed threshold, because
// "normal" depends on the surface, the payload and the gait. A limp on carpet and a limp on tile do
// not look the same in absolute terms, but both are departures from what the robot was just doing.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);

using IHost host = builder.Build();
var robot = host.Services.GetRequiredService<UnitreeRobot>();
var telemetry = host.Services.GetRequiredService<TelemetryHub>();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

await robot.ConnectAsync(cancellation.Token);

var analyzer = new GaitAnalyzer();
var window = new List<GaitSample>();
var clock = System.Diagnostics.Stopwatch.StartNew();
int received = 0;

// A window rather than a running detector, because ML.NET's spike detection works over a series.
// 200 samples at the rate below is about twenty seconds of walking — long enough to describe a gait,
// short enough that the robot changing surface shows up as a change rather than being averaged away.
const int WindowSize = 200;

Console.WriteLine("Collecting a baseline. Let the robot walk normally for half a minute.");

await foreach (LowState state in telemetry.StreamLowStateAsync(cancellation.Token))
{
    // Down-sampled: consecutive 500 Hz samples are nearly identical, so feeding all of them makes
    // the model confident about noise rather than about gait.
    if (++received % 25 != 0)
    {
        continue;
    }

    window.Add(GaitAnalyzer.ToSample(in state, (float)clock.Elapsed.TotalSeconds));

    if (window.Count < WindowSize)
    {
        continue;
    }

    GaitStatistics statistics = analyzer.Analyze(window);
    IReadOnlyList<GaitAnomalyPrediction> anomalies = analyzer.DetectAnomalies(window);
    int flagged = anomalies.Count(prediction => prediction.IsAnomaly);

    Console.WriteLine(
        $"step {statistics.StepFrequencyHz:0.00} Hz  " +
        $"duty {statistics.DutyFactor:0.00}  " +
        $"symmetry {statistics.SymmetryIndex:0.00}  " +
        $"contacts {statistics.MeanContactCount:0.0}  " +
        $"{(flagged > 0 ? $"** {flagged} anomaly sample(s) **" : "normal")}");

    // Slide by half a window so a departure is reported promptly rather than once per window.
    window.RemoveRange(0, WindowSize / 2);
}

await robot.DisconnectAsync();
""",
            [CoreRef, ControlRef, SensorsRef, MlRef, DiRef],
            [("appsettings.json", AppSettings)]),

        Template(
            "ros2-bridge-node",
            "ROS 2 Bridge Node",
            "Republishes telemetry as sensor_msgs and nav_msgs for Nav2 and RViz.",
            ProjectKind.Embedded,
            ["ros2", "nav2", "integration", "bridge"],
            """
using Microsoft.Extensions.Hosting;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Ros2;

// ROS 2 bridge node.
//
// Unitree already speaks RTPS with ROS 2's topic mangling, so there is no protocol translation to do
// — only message-type translation. The payoff is that Nav2, RViz and `ros2 bag` work against the
// robot without any of them knowing this SDK exists.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddUnitreeRobot(builder.Configuration);
builder.Services.AddUnitreeRobotHostedConnection();

builder.Services.AddUnitreeRos2Bridge(options =>
{
    options.ImuTopic = "rt/imu";
    options.OdometryTopic = "rt/odom";
    options.CommandVelocityTopic = "rt/cmd_vel";
    options.PublishRateHz = 50;

    // Off deliberately. Publishing telemetry outward is read-only and harmless; accepting cmd_vel
    // means anything on the DDS domain can drive the robot, including a teleop node someone left
    // running in another room.
    options.AcceptVelocityCommands = false;
});

using IHost host = builder.Build();

Console.WriteLine("Bridging. Check with:  ros2 topic hz /imu");
await host.RunAsync();
""",
            [CoreRef, ControlRef, Ros2Ref, DiRef],
            [("appsettings.json", AppSettings)]),

        Template(
            "arm-pick-place",
            "Dual-Arm Pick and Place",
            "Coordinated arm trajectories on a humanoid, with synchronised joint timing.",
            ProjectKind.Console,
            ["manipulation", "arms", "humanoid", "g1"],
            """
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Manipulation;

// Dual-arm pick and place.
//
// Joint timing is synchronised across the chain, so every joint starts and finishes together rather
// than each running at its own maximum. Unsynchronised joints reach the same end pose by a different
// path, and the path is what collides with things.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);

using IHost host = builder.Build();
var robot = host.Services.GetRequiredService<UnitreeRobot>();

if (!RobotModelInfo.HasArms(robot.Model))
{
    Console.Error.WriteLine($"{robot.Model} has no arms. Set Unitree:Model to G1, H1-2 or R1.");
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

await robot.ConnectAsync(cancellation.Token);

// Arms are driven at the low level, so the sport service has to release the motors first — the same
// gate every low-level path goes through, and the same silent failure if it is skipped.
LowLevelController controller = await robot.BeginLowLevelSessionAsync(
    cancellationToken: cancellation.Token);

var sink = new LowLevelJointSink(controller);

// The seven joints of each arm chain: shoulder pitch/roll/yaw, elbow, wrist roll/pitch/yaw.
int[] leftChain = [.. Enumerable.Range(G1Joint.LeftArmStart, G1Joint.ArmChainLength)];
int[] rightChain = [.. Enumerable.Range(G1Joint.RightArmStart, G1Joint.ArmChainLength)];

var left = new ArmController(sink, leftChain);
var right = new ArmController(sink, rightChain);
var coordinator = new DualArmCoordinator(left, right);

controller.Start();

float[] home = [0.0f, 0.2f, 0.0f, 0.5f, 0.0f, 0.0f, 0.0f];
float[] reach = [0.9f, 0.3f, 0.0f, 1.1f, 0.0f, 0.2f, 0.0f];
float[] lift = [0.4f, 0.3f, 0.0f, 1.4f, 0.0f, 0.2f, 0.0f];

Console.WriteLine("Home.");
await coordinator.MoveBothAsync(home, home, cancellation.Token);

Console.WriteLine("Reach.");
await coordinator.MoveBothAsync(reach, reach, cancellation.Token);

// A real gripper closes here. Without force feedback there is nothing to confirm the grasp, so the
// pause is the only thing standing in for "did it work" — treat it as a placeholder, not a check.
await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);

Console.WriteLine("Lift.");
await coordinator.MoveBothAsync(lift, lift, cancellation.Token);

Console.WriteLine("Home.");
await coordinator.MoveBothAsync(home, home, cancellation.Token);

controller.Stop();
await robot.DisconnectAsync();
return 0;
""",
            [CoreRef, ControlRef, "src/Unitree.Net.Manipulation/Unitree.Net.Manipulation.csproj", SensorsRef, DiRef],
            [("appsettings.json", """
{
  "Unitree": {
    "Model": "G1",
    "Transport": "ManagedMulticast",
    "MulticastAddress": "239.255.0.1",
    "MulticastPort": 7447
  },
  "Logging": { "LogLevel": { "Default": "Information" } }
}
""")]),

        Template(
            "desktop-control-panel",
            "Desktop Control Panel",
            "A windowed operator console with posture buttons and a live readout.",
            ProjectKind.Desktop,
            ["desktop", "operator", "console", "control"],
            """
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Unitree.Net.Control;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Sensors;

// Desktop control panel.
//
// A terminal UI rather than a windowing toolkit, so the same binary runs on the operator's laptop and
// over SSH on the robot. Swap the render loop for WPF or Avalonia if a real window is wanted; the
// service wiring below is identical either way.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddUnitreeRobot(builder.Configuration);

using IHost host = builder.Build();
var robot = host.Services.GetRequiredService<UnitreeRobot>();
var telemetry = host.Services.GetRequiredService<TelemetryHub>();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

await robot.ConnectAsync(cancellation.Token);

var render = Task.Run(async () =>
{
    while (!cancellation.IsCancellationRequested)
    {
        Console.SetCursorPosition(0, 0);
        Console.WriteLine($"  {robot.Model}   {robot.State,-12}                    ");
        Console.WriteLine("  ─────────────────────────────────────────────");

        if (telemetry.GetSnapshot() is { } s)
        {
            Console.WriteLine($"  Battery      {s.Battery.StateOfChargePercent,3} %        ");
            Console.WriteLine($"  Pack         {s.Battery.PackVoltage,5:0.0} V      ");
            Console.WriteLine($"  Motor        {s.MaxMotorTemperatureCelsius,3} C        ");
            Console.WriteLine($"  Feet loaded  {s.FootContact.ContactCount} / 4        ");
            Console.WriteLine($"  Speed        {s.Velocity.Length(),5:0.00} m/s    ");
        }
        else
        {
            Console.WriteLine("  waiting for telemetry...                  ");
        }

        Console.WriteLine();
        Console.WriteLine("  [1] stand  [2] balance  [3] sit  [4] damp  [Q] quit");

        await Task.Delay(200, CancellationToken.None);
    }
});

Console.Clear();
Console.CursorVisible = false;

while (!cancellation.IsCancellationRequested)
{
    ConsoleKeyInfo key = Console.ReadKey(intercept: true);

    switch (key.Key)
    {
        case ConsoleKey.D1: await robot.Sport.StandUpAsync(); break;
        case ConsoleKey.D2: await robot.Sport.BalanceStandAsync(); break;
        case ConsoleKey.D3: await robot.Sport.SitAsync(); break;
        case ConsoleKey.D4: await robot.Sport.DampAsync(); break;
        case ConsoleKey.Q: cancellation.Cancel(); break;
    }
}

await render;
Console.CursorVisible = true;
await robot.DisconnectAsync();
""",
            [CoreRef, ControlRef, SensorsRef, DiRef],
            [("appsettings.json", AppSettings)]),
    ];
}
