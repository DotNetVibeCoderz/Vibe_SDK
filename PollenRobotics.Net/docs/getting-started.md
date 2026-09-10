# Getting started

## Requirements

- .NET 10 SDK
- No robot. Everything below runs against the built-in simulation.

## Install

```bash
dotnet add package Gravicode.PollenRobotics.Net.ReachyMini
dotnet add package Gravicode.PollenRobotics.Net.Simulation
```

Package IDs carry a `Gravicode.` prefix; namespaces do not, so the code below reads the same either
way:

```csharp
using PollenRobotics.Net.ReachyMini;
```

Working inside this repository, a `ProjectReference` into `src/` is better than the published
package — that is what the gallery, the samples and `tools/TemplateCheck` use.
[docs/publishing.md](publishing.md) covers releasing a new version.

## Build and test

```bash
dotnet build PollenRobotics.Net.slnx
dotnet run --project tests/PollenRobotics.Net.Tests
```

`dotnet test` will not work: this is an xUnit v3 project and the .NET 10 SDK no longer bridges it
through VSTest. The test project is an executable — run it.

## Is a robot there?

```bash
cd apps/PollenRobotics.Net.Cli
dotnet run -- doctor
```

`doctor` needs no robot and is the first thing to run when one will not connect. Most connection
failures are one of four things — the daemon is not running, the host name does not resolve, the
port is closed, or the code is not on the machine it thinks it is — and each has a different fix, so
it reports them separately.

On a machine with no hardware attached it will find nothing, and say so:

```
No robot answered. That is expected on a machine with no hardware attached.
Everything in this SDK runs against the simulator: add --sim to any command,
or try pollen sim reachy-mini.
```

## Your first behaviour

```bash
dotnet run -- new mini.hello DeskPet
cd DeskPet
dotnet run -- --sim
```

That scaffolds a project that already builds, opens with connection and shutdown wired up, and nods
at you.

Twenty templates are available:

```bash
dotnet run -- templates
dotnet run -- templates duck        # filter
```

## Writing it yourself

```csharp
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.ReachyMini;
using PollenRobotics.Net.ReachyMini.Transports;
using PollenRobotics.Net.Simulation.Transports;

bool useSimulator = args.Contains("--sim");

// The only line that decides where this runs.
IReachyMiniTransport transport = useSimulator
    ? new SimulatedReachyMiniTransport()
    : new ReachyMiniDaemonTransport(ReachyMiniOptions.Default);

await using var mini = new ReachyMiniClient(transport, ownsTransport: true);

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Cancel the token rather than dying, so the robot gets parked on the way out.
    e.Cancel = true;
    stopping.Cancel();
};

try
{
    await mini.ConnectAsync(stopping.Token);

    // EnsureAwake rather than WakeUp: an app that replays its greeting every restart reads as a
    // fault rather than as charm.
    await mini.EnsureAwakeAsync(stopping.Token);

    await mini.GotoTargetAsync(
        head: HeadPose.Create(z: 12, pitch: -8, mm: true),
        antennas: (45.Degrees(), (-45).Degrees()),
        duration: TimeSpan.FromSeconds(1.5),
        cancellationToken: stopping.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Stopping.");
}
finally
{
    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await mini.GotoSleepAsync(shutdown.Token);
}
```

Two habits worth forming from the start, both visible above:

- **Park the robot in a `finally`.** A cancelled behaviour that leaves a robot mid-pose with torque
  on, or a duck still walking, is worse than one that never ran.
- **Give the shutdown its own token.** The one that was just cancelled will cancel the parking too.

## Connecting to real hardware

### Reachy Mini

```csharp
// Auto-detect: tries localhost first, then reachy-mini.local.
new ReachyMiniDaemonTransport(ReachyMiniOptions.Default);

// Or be explicit.
new ReachyMiniDaemonTransport(ReachyMiniOptions.ForNetwork("192.168.1.42"));
```

The daemon is the server and your code is the client. Start it on the machine the robot is plugged
into (a Lite), or on the robot itself (Wireless).

### MicroDuck

```csharp
// On the duck.
new MicroDuckDaemonTransport(MicroDuckOptions.Default);

// From a development machine, through an SSH-forwarded socket or a TCP bridge.
new MicroDuckDaemonTransport(MicroDuckOptions.ForTcp("192.168.1.50"));
```

robotd only ever exists on the duck. Mutating calls are gated on uid/gid, so if reads work and
commands do not, check `allow_uids` in `robotd.toml`.

### Reachy 2

```csharp
await using var reachy = Reachy2Client.Connect(Reachy2Options.ForHost("reachy.local"));
await reachy.ConnectAsync();
```

Parts are discovered at connect time, so check for null rather than assuming — not every robot has
a mobile base.

## Where to go next

- [reachy-mini.md](reachy-mini.md), [microduck.md](microduck.md), [reachy2.md](reachy2.md) — the
  per-robot guides, each of which opens with the things that will bite you
- [safety.md](safety.md) — why the SDK throws
- [simulator.md](simulator.md) and [wizard.md](wizard.md) — the tools
