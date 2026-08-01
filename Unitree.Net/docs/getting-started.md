# Getting started

## Prerequisites

- .NET 10 SDK
- A Unitree robot, or nothing at all — the virtual robot covers most development

## Without a robot

The virtual robot publishes plausible telemetry on the managed multicast transport, so everything above
the transport behaves as though hardware were present.

```bash
# Terminal 1
dotnet run --project samples/Unitree.Net.Samples.VirtualRobot

# Terminal 2
dotnet run --project apps/Unitree.Net.Cli -- status
dotnet run --project apps/Unitree.Net.Cli -- monitor
dotnet run --project apps/Unitree.Net.Dashboard
```

The battery discharges, motors heat up with joint activity, the trot gait cycles foot contacts and
odometry advances — enough movement to exercise thresholds, charts and alerts properly.

It is not a physics simulator. Anything depending on real dynamics — a learned policy, a balance
controller — needs Isaac Lab or Gazebo publishing through the same transport.

## With a robot

1. **Connect by Ethernet.** Direct cable is what Unitree assume and avoids every multicast problem.
2. **Configure the interface.** This is the single most important setting:

   ```json
   { "Unitree": { "Model": "Go2", "Transport": "CycloneNative", "NetworkInterface": "eth0" } }
   ```

3. **Build the native shim** — see [`native/README.md`](../native/README.md).
4. **Verify:**

   ```bash
   dotnet run --project apps/Unitree.Net.Cli -- diagnose
   dotnet run --project apps/Unitree.Net.Cli -- status
   ```

If `status` connects, everything else will.

## Your first application

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Extensions.DependencyInjection;

IConfigurationRoot configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole());
services.AddUnitreeRobot(configuration);

await using ServiceProvider provider = services.BuildServiceProvider();
var robot = provider.GetRequiredService<UnitreeRobot>();

await robot.ConnectAsync();

await robot.Sport.StandUpAsync();
await robot.Sport.BalanceStandAsync();   // required before velocity commands are accepted

using VelocityStream stream = robot.Sport.StartVelocityStream();
stream.Command = new VelocityCommand(Forward: 0.3f, Lateral: 0f, YawRate: 0f);
await Task.Delay(TimeSpan.FromSeconds(3));
stream.Stop();

await robot.Sport.DampAsync();           // the safe way to finish
```

Two things trip people up here:

- **`BalanceStandAsync` is not optional.** Velocity commands are silently ignored in any other mode.
- **`MoveAsync` expires.** The robot applies it for a few hundred milliseconds, then stops.
  `StartVelocityStream` resends it at 20 Hz, so you set the command once and the robot keeps going. If
  your process dies the resends stop and so does the robot — see [safety](safety.md) for when to add an
  explicit `commandTimeout` on top of that.

## Reading telemetry

```csharp
var telemetry = provider.GetRequiredService<TelemetryHub>();

if (telemetry.GetSnapshot() is { } snapshot)
{
    Console.WriteLine($"Battery {snapshot.Battery.StateOfChargePercent}%");
    Console.WriteLine($"Hottest motor {snapshot.MaxMotorTemperatureCelsius} °C");
    Console.WriteLine($"Feet loaded {snapshot.FootContact.ContactCount}/4");
}

// Or stream it.
await foreach (LowState state in telemetry.StreamLowStateAsync(cancellationToken))
{
    Console.WriteLine(state.ImuState.ToEuler());
}
```

## In a host

```csharp
builder.Services.AddUnitreeRobot(builder.Configuration);
builder.Services.AddUnitreeRobotHostedConnection();   // connects on start, reconnects with backoff
builder.Services.AddUnitreeDiagnostics();             // metrics + /health
builder.Services.AddUnitreeAi(builder.Configuration); // optional
builder.Services.AddUnitreeRos2Bridge();              // optional
```

A failed connection does **not** abort host startup. A dashboard should still come up and report the
robot as unreachable — taking the process down leaves the operator with no interface to diagnose from.

## Configuration sources

In precedence order, lowest first:

1. `appsettings.json`
2. `appsettings.Local.json` (git-ignored — put your interface name here)
3. `UNITREE_`-prefixed environment variables
4. `--Key=Value` command-line arguments

```bash
dotnet run --project apps/Unitree.Net.Cli -- monitor --Unitree:NetworkInterface=eth0
```

API keys belong in user secrets or environment variables, never in `appsettings.json`.

## Where to go next

| You want to | Read |
|---|---|
| Nothing connects | [DDS networking](dds-networking.md) |
| Command joints directly | [Low-level control](low-level-control.md), then [Safety](safety.md) |
| Understand the design | [Architecture](architecture.md) |
| Drive to waypoints | [Navigation](navigation.md) |
| Talk to the robot in English | [AI workflows](ai-workflow.md) |
| Use Nav2 or RViz | [ROS 2 bridge](ros2-bridge.md) |
