# Safety

## The SDK throws

By default, a command outside a joint's declared limits raises `RobotSafetyException`:

```
Joint 'head.pitch' limited to [-40, 40] deg, commanded 65 deg.
Set RobotSafetyOptions.ClampInsteadOfThrow to clamp instead.
```

![The gallery demonstrating a limit violation](images/gallery-dark.png)

*The safety-limits demo: the arcs go red as the joint nears its end stop, and the exception lands in
the log with the fix in the message.*

## Why not clamp

The Python SDK clamps silently. That is friendly in a REPL and dangerous in a control loop.

A servo that has been quietly clamped for ten minutes looks exactly like one that is tracking
correctly. Nothing in the telemetry distinguishes them: the reported position matches the clamped
command, the loop rate is fine, no error is raised anywhere. The behaviour is subtly wrong and there
is nothing to find.

Throwing turns that into a bug report with a line number.

Clamping is still available - you just have to ask:

```csharp
// Clamp, and warn each time.
new ReachyMiniClient(transport, RobotSafetyOptions.Permissive);

// Or tune it.
new ReachyMiniClient(transport, new RobotSafetyOptions
{
    ClampInsteadOfThrow = true,
    WarnOnClamp = false,
    MaxJointStepRadians = 0.35,   // reject a step larger than this
});
```

`MaxJointStepRadians` catches a different failure: a joint commanded from one end of its range to
the other in a single tick. Both endpoints are legal, so a limit check passes; on real hardware it
is a bang rather than a movement.

## Per-robot rules

### Reachy Mini

| Limit | Value |
|---|---|
| Head pitch, head roll | ±40° |
| Body yaw | ±160° |
| Head yaw vs body yaw | within 65° |

The head-to-body yaw constraint is the one that surprises people. With automatic body yaw on (the
default) the daemon rotates the body to satisfy it. With it off, the head is clamped and simply
stops turning, with no error - which from the outside looks like a robot that has decided not to
listen.

The simulation enforces it too. An application written against a simulator with no limits meets them
for the first time on hardware and never finds out why.

### MicroDuck

Not angles but sequence:

- **`InitAsync` before anything.** A relaxed duck accepts commands and does nothing.
- **Velocity is held indefinitely.** A one-shot command walks forever. `DriveForAsync` bounds it and
  stops in a `finally`, so a cancelled walk still stops the duck.
- **A fallen duck ignores velocity.** Run `RunFallRecoveryAsync` alongside your logic.
- **`RelaxAsync` drops it where it stands.**

Velocities are clamped into a conservative envelope rather than the servos' full range. A 25 cm
biped balancing on a learned policy falls over well before its actuators run out, and nothing
reports it.

### Reachy 2

- **Arms on before grippers.** A gripper energised on a compliant arm can swing the whole limb by
  its own reaction torque. `TurnOnAsync` does this in the right order.
- **Turning off makes everything compliant.** The arms sag; anything held drops.
- **Check reachability before committing to a sequence.** `IsReachableAsync` asks the robot, which
  solves against its true model.
- **Verify a grasp.** `IsHoldingAsync` before carrying on. A gripper closed on nothing looks, from
  across the room, exactly like a successful pick.

## Park the robot on the way out

Every template does this, and it is the habit most worth copying:

```csharp
try
{
    await mini.ConnectAsync(stopping.Token);
    // ... behaviour ...
}
catch (OperationCanceledException)
{
    Console.WriteLine("Stopping.");
}
finally
{
    // Its own token: the one that was just cancelled would cancel the parking too.
    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await mini.GotoSleepAsync(shutdown.Token);
}
```

And handle Ctrl+C by cancelling rather than dying:

```csharp
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};
```

A cancelled behaviour that leaves a robot mid-pose with torque on, or a duck still walking into
whatever is in front of it, is worse than one that never ran.

## What this SDK cannot tell you

The simulation is kinematic. It will not tell you whether a motion is dynamically stable, whether a
grasp will hold, or whether the duck will fall over. Joint limits are a floor, not a guarantee.

Clear the workspace. Give the robot room. Test on hardware at reduced speed first -
`SetSpeedLimitAsync` on Reachy 2, and smaller velocities on the duck.
