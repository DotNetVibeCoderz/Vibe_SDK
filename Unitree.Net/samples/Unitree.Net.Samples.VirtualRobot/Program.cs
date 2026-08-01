using Unitree.Net.Core;
using Unitree.Net.Simulation;

// Virtual robot — the headless simulator.
//
// Publishes plausible telemetry AND answers the sport, motion_switcher and robot_state services, so an
// application can stand the robot up and drive it exactly as it would a real one. Point the CLI, the
// dashboard, or your own application at the same multicast group and everything downstream behaves as
// though a robot were present.
//
// It runs the same engine as the 3D simulator in apps/Unitree.Net.Simulator; this one just has no
// window, which is why it works on Linux and inside CI.
//
// It is not a physics simulator. Motion is kinematic and the battery discharges on a timer. For real
// dynamics, drive Isaac Lab or Gazebo and publish its state through this same transport.

var options = new SimulationOptions
{
    Model = Enum.TryParse(GetArgument(args, "--model"), ignoreCase: true, out RobotModel model)
        ? model
        : RobotModel.Go2,
    MulticastAddress = GetArgument(args, "--group") ?? "239.255.0.1",
    MulticastPort = int.TryParse(GetArgument(args, "--port"), out int port) ? port : 7447,
    NetworkInterface = GetArgument(args, "--interface") ?? string.Empty,
    LowStateRateHz = int.TryParse(GetArgument(args, "--rate"), out int rate) ? rate : 500,
};

var log = new SimulationLog();

// Everything the host reports goes straight to the console, so a terminal running this reads like the
// simulator's own log panel.
log.EntryWritten += (_, entry) =>
    Console.WriteLine($"{entry.Timestamp:HH:mm:ss} {entry.Level,-7} {entry.Source,-9} {entry.Message}");

await using var host = new SimulationHost(log);

using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    await host.StartAsync(options, cancellation.Token);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Could not start: {exception.Message}");
    return 1;
}

Console.WriteLine();
Console.WriteLine("The robot is resting, as it would be after power-on.");
Console.WriteLine("Your application must call StandUpAsync then BalanceStandAsync before it can drive.");
Console.WriteLine("Press Ctrl+C to stop.");

try
{
    await Task.Delay(Timeout.Infinite, cancellation.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C.
}

SimulationStatistics stats = host.GetStatistics();
await host.StopAsync();

Console.WriteLine();
Console.WriteLine($"Published {stats.LowStateCount:N0} low-state and {stats.SportStateCount:N0} sport-state messages.");
Console.WriteLine($"Loop jitter: mean {stats.MeanJitterMicroseconds:0} µs, max {stats.MaxJitterMicroseconds:0} µs.");

return 0;

static string? GetArgument(string[] arguments, string name)
{
    int index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}
