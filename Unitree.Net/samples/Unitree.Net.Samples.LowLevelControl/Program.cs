using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Manipulation;
using Unitree.Net.Messages.Go;

// Low-level joint control.
//
//   ⚠ This sample commands motors directly. Put the robot on a stand, or on the floor with clear space,
//     before running it. The safety envelope is the only thing between this code and the hardware.
//
// It demonstrates the full low-level sequence:
//   1. connect and confirm telemetry is flowing
//   2. release the on-board motion controller — without this, rt/lowcmd is silently overwritten
//   3. capture the robot's actual pose as the trajectory start
//   4. plan and execute a smooth motion to a crouch and back
//
// Everything runs through LowLevelController, so per-joint limits, rate limiting, fall detection and
// the state-staleness watchdog all apply.

IConfigurationRoot configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("UNITREE_")
    .AddCommandLine(args)
    .Build();

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
services.AddUnitreeRobot(configuration);

await using ServiceProvider provider = services.BuildServiceProvider();
var robot = provider.GetRequiredService<UnitreeRobot>();
ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Sample");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

logger.LogInformation("Connecting…");
await robot.ConnectAsync(cancellation.Token);

if (!robot.TryGetLowState(out LowState initialState))
{
    logger.LogError("Connected, but no low-level state arrived. Cannot plan from an unknown pose.");
    return 1;
}

Console.WriteLine();
Console.WriteLine("About to take direct control of the motors.");
Console.WriteLine("Confirm the robot is on a stand or has clear space, then press Enter (Ctrl+C to abort).");
Console.ReadLine();

// Releases the sport service and starts the 500 Hz publish loop.
LowLevelController controller = await robot.BeginLowLevelSessionAsync(cancellationToken: cancellation.Token);

try
{
    var sink = new LowLevelJointSink(controller);

    // Plan from where the robot actually is, not from a nominal pose. Assuming a starting posture is how
    // a "smooth" trajectory turns into a step input on the first tick.
    var startPose = new float[GoJoint.Count];

    for (int i = 0; i < GoJoint.Count; i++)
    {
        startPose[i] = initialState.MotorState[i].Q;
    }

    float[] standPose =
    [
        0.0f, 0.80f, -1.50f,
        0.0f, 0.80f, -1.50f,
        0.0f, 1.00f, -1.50f,
        0.0f, 1.00f, -1.50f,
    ];

    float[] crouchPose =
    [
        0.0f, 1.25f, -2.30f,
        0.0f, 1.25f, -2.30f,
        0.0f, 1.35f, -2.30f,
        0.0f, 1.35f, -2.30f,
    ];

    // A conservative gain pair: stiff enough to hold the body weight, soft enough that a modelling
    // error shows up as sag rather than a fight against the ground.
    var gains = new ArmGains(45f, 2.0f);
    var limits = new TrajectoryLimits(MaxVelocity: 0.8f, MaxAcceleration: 1.5f);

    var legs = new ArmController(sink, [.. Enumerable.Range(0, GoJoint.Count)], gains, limits, logger);

    logger.LogInformation("Moving to the stand pose…");
    await legs.ExecuteAsync(TrajectoryPlanner.Plan(startPose, standPose, limits), gains, cancellation.Token);
    await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);

    for (int cycle = 0; cycle < 3 && !cancellation.IsCancellationRequested; cycle++)
    {
        logger.LogInformation("Cycle {Cycle}: crouching.", cycle + 1);
        await legs.ExecuteAsync(TrajectoryPlanner.Plan(standPose, crouchPose, limits), gains, cancellation.Token);

        logger.LogInformation("Cycle {Cycle}: standing.", cycle + 1);
        await legs.ExecuteAsync(TrajectoryPlanner.Plan(crouchPose, standPose, limits), gains, cancellation.Token);

        if (controller.IsEmergencyStopped)
        {
            logger.LogError("Emergency stop latched mid-sequence; abandoning the routine.");
            break;
        }

        ReportLoopHealth(controller, logger);
    }
}
catch (OperationCanceledException)
{
    logger.LogWarning("Cancelled.");
}
catch (SafetyViolationException ex)
{
    logger.LogError(
        "Safety limit '{Limit}' rejected a command: requested {Requested:0.###}, limit {Value:0.###}.",
        ex.LimitName,
        ex.Requested,
        ex.Limit);
}
finally
{
    // Stop() publishes a final damping command before halting the loop. Cutting the command stream
    // while the joints are holding a posture makes the robot collapse.
    logger.LogInformation("Stopping low-level control; leaving the joints damped.");
    controller.Stop();
}

return 0;

static void ReportLoopHealth(LowLevelController controller, ILogger logger)
{
    LoopStatistics stats = controller.LoopStatistics;

    logger.LogInformation(
        "Control loop: {Ticks:N0} ticks, mean jitter {Mean:0} µs, max {Max:0} µs, {Overruns} overruns, {Missed} missed.",
        stats.TickCount,
        stats.MeanJitterMicroseconds,
        stats.MaxJitterMicroseconds,
        stats.OverrunCount,
        stats.MissedDeadlineCount);
}
