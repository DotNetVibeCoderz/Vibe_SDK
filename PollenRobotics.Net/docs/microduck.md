# MicroDuck

A 25 cm biped, 15 servos, walking on reinforcement-learning policies at 50 Hz.

![MicroDuck walking in the simulator](images/simulator-microduck.png)

*The gait model under a forward velocity intent. The joint arcs show the two legs half a cycle
apart; the camera follows, because a walking robot leaves a fixed frame within seconds.*

## A different mental model

You do not command joint angles. A policy does that, fifty times a second. You send **intents**:

- a **velocity** for the walk policy to track
- an **action slot** to run to completion

Reaching past that to the servos means replacing the policy, which is `robotctl policy load`
territory rather than an SDK call.

## The things that will bite you

**`InitAsync` first, always.** A relaxed duck accepts commands and does nothing. No error, no
warning - it simply does not move.

**Velocity is held, not consumed.** The daemon keeps the last velocity indefinitely, so a one-shot
`DriveAsync` walks forever. Use `DriveForAsync` for a bounded walk, or keep sending and stop
explicitly.

**A fallen duck ignores velocity.** Without a recovery watcher your application looks hung rather
than tipped over.

**`RelaxAsync` drops it where it stands.** Make sure that is somewhere it can fall.

## Walking

```csharp
await duck.InitAsync();

// Bounded, resent at the loop rate, stopped afterwards - even if you cancel.
await duck.DriveForAsync(DuckVelocity.Forward(0.12), TimeSpan.FromSeconds(3));

// Or drive it yourself from a control loop.
await duck.DriveAsync(new DuckVelocity(0.1, 0.05, 0.8));
await duck.StopAsync();
```

`DriveForAsync` runs its stop in a `finally` with its own timeout, so a cancelled walk still stops
the duck. A cancelled walk that leaves the robot walking is worse than no walk at all.

Velocities are clamped into a deliberately conservative envelope. A 25 cm biped balancing on a
learned policy has a much narrower stable range than its servos suggest, and past it the policy
stops tracking and the duck falls over - which nothing reports.

## Action slots

Seven slots, each holding an ONNX policy.

```csharp
await duck.SitAsync();                    // sitstand
await duck.StandUpAsync();                // stand
await duck.PickAsync();                   // ground_pick
await duck.KickAsync(leftFoot: true);     // kick_left
await duck.RecoverAsync();                // roulade
await duck.QuackAsync();
```

Actions block until they finish, so consecutive awaits sequence correctly. Zero the velocity before
one, or the duck tries to walk out of its own kick.

`robotctl policy load walk my-policy.onnx` replaces what "walk" means. `ListSkillsAsync` reports
what is currently loaded.

## Falling over

```csharp
// Start once, alongside your own logic, and forget about it.
_ = duck.RunFallRecoveryAsync(cancellationToken);
```

It watches at 5 Hz and runs the roulade and stand sequence when `state.IsFallen` goes true. Without
it, a fall means every subsequent command is silently ignored.

## Sensing

```csharp
MicroDuckState state = await duck.GetStateAsync();
state.BodyRpy;          // from the fused orientation - useful for a tip-over guard
state.BatteryVolts;
state.LoopRateHz;       // should sit at 50

MicroDuckHealth health = await duck.GetHealthAsync();
if (!health.IsHealthy) { /* health.Warnings says why */ }

TofFrame? depth = await duck.ReadTimeOfFlightAsync();   // null when there is no sensor
if (depth is { } frame && frame.NearestMeters < 0.25) { /* something is close */ }
```

The time-of-flight sensor is an 8x8 grid. `NearestMeters` skips the non-finite cells, which is what
the sensor reports where it saw nothing.

## Connecting

robotd listens on a Unix socket and only ever exists on the duck.

```csharp
// On the duck.
MicroDuckOptions.Default                                   // /run/robotd.sock

// From a development machine.
MicroDuckOptions.ForSocket("/tmp/robotd-forwarded.sock")   // SSH-forwarded
MicroDuckOptions.ForTcp("192.168.1.50")                    // socat or similar
```

Windows has supported `AF_UNIX` since Windows 10 1803, so the forwarded case works from a
development machine.

If reads work and commands do not, that is the uid/gid gate: robotd allows read-only calls to anyone
and gates mutating ones on `allow_uids` / `allow_gids` in `robotd.toml`.

## The velocity watchdog

On by default. If no velocity command has been sent for half a second, the transport sends a zero.

The watchdog measures from the last command **sent**, not from the last distinct value, so a caller
holding one velocity for a second - which every teleoperation loop does - keeps sending and never
trips it. A caller that has crashed stops, and it does.

```csharp
MicroDuckOptions.Default with { VelocityWatchdog = null }   // turn it off
```

## Joint names

The servo count and the loop rate are published; the per-joint table is not. The names and limits in
`RobotCatalog.MicroDuck` follow the usual biped convention and are conservative. Correct them
against `robotctl monitor` output on a real duck - see [PROGRESS.md](../PROGRESS.md).
