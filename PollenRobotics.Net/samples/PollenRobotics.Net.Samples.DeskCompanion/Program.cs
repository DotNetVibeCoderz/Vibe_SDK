// A desk companion for Reachy Mini: it idles, notices faces, and reacts.
//
// This is the worked example the templates are distilled from. It is longer than any of them on
// purpose, because the interesting problems only show up once a behaviour has to do more than one
// thing at a time: an idle loop that yields to an interaction, a tracker that owns the head until
// something else needs it, and a shutdown that parks the robot whatever happened.
//
//   dotnet run --project samples/PollenRobotics.Net.Samples.DeskCompanion -- --sim
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.ReachyMini;
using PollenRobotics.Net.ReachyMini.Transports;
using PollenRobotics.Net.Simulation;
using PollenRobotics.Net.Simulation.Robots;
using PollenRobotics.Net.Simulation.Transports;

bool useSimulator = args.Contains("--sim");

IReachyMiniTransport transport;
SimulationEngine? engine = null;
SimulatedReachyMini? model = null;

if (useSimulator)
{
    model = new SimulatedReachyMini();
    engine = new SimulationEngine(model);
    await engine.StartAsync();
    transport = new SimulatedReachyMiniTransport(model, engine);
}
else
{
    transport = new ReachyMiniDaemonTransport(ReachyMiniOptions.Default);
}

await using var mini = new ReachyMiniClient(transport, ownsTransport: true);

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

try
{
    await mini.ConnectAsync(stopping.Token);
    await mini.EnsureAwakeAsync(stopping.Token);

    // The daemon's own breathing runs underneath everything this program does, so the robot is
    // never completely still even between behaviours.
    await mini.SetWobblingAsync(true, stopping.Token);
    await mini.StartHeadTrackingAsync(weight: 0, cancellationToken: stopping.Token);

    Console.WriteLine($"Companion running against {mini.Endpoint}. Ctrl+C to stop.");

    // In the simulator there is no camera, so a face is faked on a slow cycle to exercise the
    // interaction path. Against hardware the daemon supplies the real thing.
    Task? faker = useSimulator ? FakeAFaceAsync(model!, stopping.Token) : null;

    await RunCompanionAsync(mini, stopping.Token);

    if (faker is not null)
    {
        await faker;
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Stopping.");
}
finally
{
    // Its own token: the one that was just cancelled would cancel the parking too.
    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));

    try
    {
        await mini.StopHeadTrackingAsync(shutdown.Token);
        await mini.SetWobblingAsync(false, shutdown.Token);
        await mini.GotoSleepAsync(shutdown.Token);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not park the robot: {ex.Message}");
    }

    if (engine is not null)
    {
        await engine.DisposeAsync();
    }
}

return;

// The companion loop: idle until a face appears, engage while it is there, idle again when it goes.
// (Local functions in a top-level program cannot carry XML documentation.)
static async Task RunCompanionAsync(ReachyMiniClient mini, CancellationToken cancellationToken)
{
    var random = new Random();
    bool engaged = false;
    DateTimeOffset nextIdleAction = DateTimeOffset.UtcNow.AddSeconds(4);

    // 5 Hz. This loop only decides what to do; the motions it starts run at their own pace.
    var loop = new RealtimeLoop(5);

    await loop.RunAsync(async (_, token) =>
    {
        FaceTarget face = await mini.GetTrackedFaceAsync(token);

        if (face.Detected && !engaged)
        {
            engaged = true;
            Console.WriteLine("Someone is here.");

            // Hand the head to the tracker and perk up. Antennas are the only expressive channel
            // left once tracking owns the head.
            await mini.StartHeadTrackingAsync(weight: 1.0, cancellationToken: token);
            await mini.SetAntennasAsync(50, -50, token);
            return;
        }

        if (!face.Detected && engaged)
        {
            engaged = false;
            Console.WriteLine("They have gone.");

            // Weight 0 rather than stopping: it keeps the detector warm, which is much cheaper than
            // stopping and restarting it every time someone walks past.
            await mini.StartHeadTrackingAsync(weight: 0, cancellationToken: token);
            await mini.GotoTargetAsync(
                head: HeadPose.Neutral,
                antennas: ((-20).Degrees(), 20.Degrees()),
                duration: TimeSpan.FromSeconds(1.2),
                cancellationToken: token);

            nextIdleAction = DateTimeOffset.UtcNow.AddSeconds(random.Next(4, 9));
            return;
        }

        if (engaged || DateTimeOffset.UtcNow < nextIdleAction)
        {
            return;
        }

        await IdleAsync(mini, random, token);
        nextIdleAction = DateTimeOffset.UtcNow.AddSeconds(random.Next(5, 12));
    }, cancellationToken);
}

// One small idle action.
//
// The irregular interval between actions is what makes this read as alive rather than as a loop.
// Anything on a fixed period reads as a machine within about thirty seconds of watching.
static async Task IdleAsync(ReachyMiniClient mini, Random random, CancellationToken cancellationToken)
{
    switch (random.Next(3))
    {
        case 0:
            // A glance, well inside the 65-degree head-to-body yaw budget.
            await mini.GotoTargetAsync(
                head: HeadPose.Create(yaw: random.Next(-35, 36), pitch: random.Next(-8, 9)),
                duration: TimeSpan.FromSeconds(0.9),
                cancellationToken: cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);

            await mini.GotoTargetAsync(
                head: HeadPose.Neutral,
                duration: TimeSpan.FromSeconds(1.1),
                cancellationToken: cancellationToken);
            break;

        case 1:
            await mini.SetAntennasAsync(random.Next(20, 60), random.Next(-60, -20), cancellationToken);
            await Task.Delay(300, cancellationToken);
            await mini.SetAntennasAsync(0, 0, cancellationToken);
            break;

        default:
            // A head tilt: the cheapest way to look curious.
            await mini.GotoTargetAsync(
                head: HeadPose.Create(roll: random.Next(-20, 21)),
                duration: TimeSpan.FromSeconds(0.8),
                method: InterpolationMethod.EaseInOut,
                cancellationToken: cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            await mini.GotoTargetAsync(
                head: HeadPose.Neutral,
                duration: TimeSpan.FromSeconds(1),
                cancellationToken: cancellationToken);
            break;
    }
}

// Moves a fake face in and out of view, so the simulator exercises the interaction path.
static async Task FakeAFaceAsync(SimulatedReachyMini model, CancellationToken cancellationToken)
{
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);

            // Drift across the field of view for a few seconds, then leave.
            for (double x = -0.6; x <= 0.6 && !cancellationToken.IsCancellationRequested; x += 0.05)
            {
                model.SimulatedFace = new FaceTarget(true, x, Math.Sin(x * 3) * 0.2, Angle.Zero);
                await Task.Delay(150, cancellationToken);
            }

            model.SimulatedFace = FaceTarget.None;
        }
    }
    catch (OperationCanceledException)
    {
        // Shutting down.
    }
}
