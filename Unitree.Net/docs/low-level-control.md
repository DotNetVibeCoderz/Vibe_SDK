# Low-level control

Direct joint control. This is the only part of the SDK that can damage hardware or injure someone.
Read [Safety](safety.md) first, and put the robot on a stand.

## What "low-level" means

You publish `rt/lowcmd` at 500 Hz. Each message carries, per motor, a target position, velocity,
feed-forward torque, and the two gains that define the joint's impedance. The motor controller applies:

```
τ = τ_ff + Kp·(q_des − q) + Kd·(dq_des − dq)
```

The consequence people miss: with both gains at zero the joint only ever sees `τ_ff` and behaves as a
pure torque source. `MotorCmd.Position(...)` sets the gains for you; a hand-built `MotorCmd` with a
target position and no gains does nothing.

## Three preconditions

All three must hold or the robot ignores you, silently:

1. **The sport service has released the motors.** While an on-board motion controller is active it
   drives the motors itself and overwrites `rt/lowcmd` entirely.
2. **Commands are continuous.** The robot treats a gap in the stream as a fault.
3. **Every command carries a valid CRC.** Bad-CRC messages are dropped with no diagnostic.

`BeginLowLevelSessionAsync` handles the first two, and the serialisation path handles the third.

## A session

```csharp
await robot.ConnectAsync();

// Releases the motion controller, waits for the robot to settle, starts the 500 Hz loop.
LowLevelController controller = await robot.BeginLowLevelSessionAsync();

try
{
    controller.SetJointPosition(GoJoint.FrontRightThigh, 0.9f, kp: 45f, kd: 2f);

    // Setpoints are applied on the next tick. The loop republishes continuously in between, so your
    // code sets targets at whatever rate suits it.
    await Task.Delay(TimeSpan.FromSeconds(1));
}
finally
{
    controller.Stop();   // publishes a final damping command, then halts the loop
}
```

## Joint indices

Quadrupeds use `GoJoint`. Legs are front/rear × right/left, three joints each in hip → thigh → calf
order:

```
 0–2   FR: hip, thigh, calf        6–8   RR: hip, thigh, calf
 3–5   FL: hip, thigh, calf        9–11  RL: hip, thigh, calf
```

Slots 12–19 exist in the message but are unactuated on a Go2. Leave them idle; a zeroed struct with
`Mode = Servo` would command them to position zero instead.

Humanoids use `G1Joint` — legs 0–11, waist 12–14, left arm 15–21, right arm 22–28.

## Smooth motion

Do not step between poses. Plan a trajectory:

```csharp
var sink = new LowLevelJointSink(controller);
var legs = new ArmController(sink, [.. Enumerable.Range(0, GoJoint.Count)],
                             new ArmGains(45f, 2f),
                             new TrajectoryLimits(MaxVelocity: 0.8f, MaxAcceleration: 1.5f));

var start = new float[GoJoint.Count];
controller.TryGetState(out LowState state);

for (int i = 0; i < GoJoint.Count; i++)
{
    start[i] = state.MotorState[i].Q;      // plan from where the robot actually is
}

await legs.ExecuteAsync(TrajectoryPlanner.Plan(start, standPose, legs.Limits));
```

Trajectories are quintic, which gives zero velocity **and zero acceleration** at both endpoints. A cubic
or trapezoidal profile leaves a step in acceleration at the boundaries; on a geared joint that step is
what you hear as a knock and feel as backlash.

Planning from the measured pose rather than a nominal one matters just as much. Assuming a starting
posture is how a "smooth" trajectory becomes a step input on its first tick.

## Rate limiting

Every position command is rate-limited to `MaxPositionDeltaPerTick` (0.01 rad by default). At 500 Hz
that permits roughly 5 rad/s of setpoint slew.

This is what turns a large setpoint jump into a ramp. Without it, an application that computes a bad
target — or an operator dragging a slider — delivers a step input, and the impedance law answers a step
with a torque spike.

Rate-limiter history is cleared by `SetAllDamping()`, `SetJointTorque()` and `ClearEmergencyStop()`,
because after any of those the last commanded position no longer reflects reality.

## Emergency stop

```csharp
controller.EmergencyStop("obstacle detected");   // latches; every joint goes to damping
// …
controller.ClearEmergencyStop();                 // deliberately explicit
```

The loop keeps publishing while stopped, so the robot continues to see a valid command stream as it
settles. `SetJointPosition` throws while the stop is latched.

The controller latches one itself on stale state, a detected fall, motor over-temperature, or low
battery.

## Loop health

```csharp
LoopStatistics stats = controller.LoopStatistics;
Console.WriteLine($"{stats.TickCount} ticks, mean jitter {stats.MeanJitterMicroseconds:0} µs");
Console.WriteLine($"{stats.OverrunCount} overruns, {stats.MissedDeadlineCount} missed deadlines");
```

- **Overruns** — the callback took longer than the period. A few are fine; a rising count means your
  per-tick work is too slow.
- **Missed deadlines** — the loop fell more than a full period behind and resynchronised rather than
  sprinting to catch up. A burst of back-to-back ticks would push a jerky command sequence at the motors.
- **Mean jitter** should sit in single-digit microseconds. Tens of milliseconds means something is
  preempting the control thread.

## Writing tick callbacks

The callback runs on the real-time thread. It must not block, await, or allocate. Anything slower than
the period shows up as jitter, and at 500 Hz an allocation per tick is 500 allocations a second in the
one place a GC pause is least acceptable.

A throwing callback is logged and the loop continues — losing the control thread is worse than one bad
tick — but it will not fix itself.

## Learned policies

`PolicyRunner` loads a TorchScript policy exported from Isaac Lab or Legged Gym:

```csharp
using var policy = PolicyRunner.Load("policy.pt", ObservationSpec.Go2Default);

controller.StateUpdated += state =>
{
    ReadOnlySpan<float> observation = policy.BuildObservation(in state, command);
    float[] targets = policy.Evaluate(observation);
    controller.SetAllJointPositions(targets, kp: 25f, kd: 0.5f);
};
```

The observation layout must match what the policy was trained on, element for element. A mismatch does
not throw — the network happily consumes any correctly-sized vector — it produces confident nonsense,
which on a robot means a fall. Keep `ObservationSpec` in step with the training environment.

TorchSharp needs a libtorch backend that the `TorchSharp` package does not include. Add `TorchSharp-cpu`
(or a CUDA variant) to the application project, and check `PolicyRunner.IsBackendAvailable` at startup
rather than discovering the problem mid-gait.
