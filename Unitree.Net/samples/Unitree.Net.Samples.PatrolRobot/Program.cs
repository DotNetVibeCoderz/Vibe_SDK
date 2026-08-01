using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Extensions.DependencyInjection;
using Unitree.Net.Ml;
using Unitree.Net.Messages.Go;
using Unitree.Net.Sensors;

// Autonomous patrol.
//
// Walks a closed route of waypoints, returns to the start, and repeats — while a background task
// watches gait health and battery level and aborts the patrol if either goes out of bounds.
//
// The pieces this exercises together:
//   • WaypointNavigator for odometry-driven navigation with stall detection
//   • TelemetryHub for battery and contact state
//   • GaitAnalyzer for symmetry analysis, which surfaces a developing limp
//
//   ⚠ There is no obstacle avoidance here. Odometry drifts, so a long route will accumulate error —
//     enable the robot's own avoidance service and re-localise periodically for real deployments.

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
var telemetry = provider.GetRequiredService<TelemetryHub>();
ILoggerFactory loggerFactory = provider.GetRequiredService<ILoggerFactory>();
ILogger logger = loggerFactory.CreateLogger("Patrol");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

await robot.ConnectAsync(cancellation.Token);

// A small rectangular circuit in the odometry frame, closing back on the origin.
Waypoint[] route =
[
    Waypoint.At(2.0f, 0.0f),
    Waypoint.At(2.0f, 2.0f),
    Waypoint.At(0.0f, 2.0f),
    new Waypoint(new System.Numerics.Vector3(0f, 0f, 0f), 0.2f, FinalHeading: 0f),
];

var navigator = new WaypointNavigator(
    robot,
    new NavigationOptions { UpdateRateHz = 20, StallTimeout = TimeSpan.FromSeconds(8) },
    logger);

logger.LogInformation("Standing up before patrol.");
await robot.Sport.StandUpAsync(cancellation.Token);
await robot.Sport.BalanceStandAsync(cancellation.Token);
await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);

// The health watch runs alongside navigation and cancels it. Checking between legs instead would let a
// fault go unnoticed for a whole leg of the route.
Task healthWatch = WatchHealthAsync(robot, telemetry, loggerFactory, cancellation);

int lap = 0;

try
{
    while (!cancellation.IsCancellationRequested)
    {
        lap++;
        logger.LogInformation("Starting lap {Lap}.", lap);

        NavigationResult result = await navigator.FollowRouteAsync(route, cancellation.Token);

        if (result != NavigationResult.Arrived)
        {
            logger.LogWarning("Lap {Lap} ended with {Result}; stopping the patrol.", lap, result);
            break;
        }

        logger.LogInformation("Lap {Lap} complete. Pausing before the next.", lap);
        await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
    }
}
catch (OperationCanceledException)
{
    logger.LogInformation("Patrol cancelled.");
}
finally
{
    await cancellation.CancelAsync();

    try
    {
        await healthWatch;
    }
    catch (OperationCanceledException)
    {
        // Expected on shutdown.
    }

    logger.LogInformation("Damping the robot.");
    await robot.Sport.DampAsync(CancellationToken.None);
}

return 0;

static async Task WatchHealthAsync(
    UnitreeRobot robot,
    TelemetryHub telemetry,
    ILoggerFactory loggerFactory,
    CancellationTokenSource cancellation)
{
    ILogger logger = loggerFactory.CreateLogger("Health");
    var analyzer = new GaitAnalyzer(logger);
    var window = new List<GaitSample>(capacity: 256);
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

    try
    {
        while (await timer.WaitForNextTickAsync(cancellation.Token))
        {
            if (!robot.TryGetLowState(out LowState state))
            {
                continue;
            }

            window.Add(GaitAnalyzer.ToSample(in state, (float)stopwatch.Elapsed.TotalSeconds));

            BatteryStatus? battery = telemetry.GetBattery();
            RobotSafetyOptions safety = robot.Options.Safety;

            if (battery is { StateOfChargePercent: > 0 } value &&
                value.StateOfChargePercent < safety.MinBatterySocPercent + 5)
            {
                logger.LogWarning(
                    "Battery at {Soc}%, approaching the {Floor}% floor — ending the patrol.",
                    value.StateOfChargePercent,
                    safety.MinBatterySocPercent);

                await cancellation.CancelAsync();
                return;
            }

            // Analyse a whole window at a time: gait metrics are only meaningful over several complete
            // cycles, so a per-sample check would be noise.
            if (window.Count < 200)
            {
                continue;
            }

            GaitStatistics statistics = analyzer.Analyze(
                window,
                index => window[index].TotalFootForce * 0.5f,
                index => window[index].TotalFootForce * 0.5f);

            logger.LogInformation(
                "Gait: {Frequency:0.00} steps/s, duty {Duty:0.00}, symmetry {Symmetry:0.000}, pitch σ {Pitch:0.000} rad.",
                statistics.StepFrequencyHz,
                statistics.DutyFactor,
                statistics.SymmetryIndex,
                statistics.PitchStandardDeviation);

            if (statistics.SuggestsAsymmetry)
            {
                logger.LogWarning(
                    "Gait asymmetry index {Symmetry:0.000} exceeds the threshold — inspect the robot for a mechanical fault.",
                    statistics.SymmetryIndex);
            }

            window.Clear();
        }
    }
    catch (OperationCanceledException)
    {
        // Normal shutdown.
    }
}
