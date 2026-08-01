# Safety

A Go2 weighs about 15 kg and a B2 about 60 kg. They move fast, they are strong enough to injure, and
low-level control removes every protection the on-board controller normally provides. Read this before
running anything in `docs/low-level-control.md`.

## The three layers

Safety is enforced in three places, and each catches something the others cannot.

### 1. Per-command — `JointSafetyLimits`

Applied before a command reaches the outbound buffer:

| Limit | Go2 default | What it prevents |
|---|---|---|
| `MaxPosition` / `MinPosition` | ±π rad | Commanding a joint past its mechanical stop |
| `MaxVelocity` | 20 rad/s | Runaway joint speed |
| `MaxTorque` | 23.7 N·m | Overcurrent and gearbox damage |
| `MaxKp` | 150 | Oscillation from excessive stiffness |
| `MaxKd` | 10 | Instability from excessive damping |
| `MaxPositionDeltaPerTick` | 0.01 rad | A setpoint jump becoming a torque spike |

Non-finite values are rejected outright. A `NaN` setpoint propagates through the impedance law and
produces undefined motor behaviour rather than an obvious failure.

**Rate limiting is the one people skip and regret.** At 500 Hz, 0.01 rad per tick permits about 5 rad/s
of setpoint slew — fast enough for normal gaits, slow enough that a bad target cannot become a step
input. The on-board impedance controller answers a step with a torque spike.

### 2. Per-tick — `LowLevelController`

Checked on every control tick, before the command is published:

- **State staleness** — no low-level state for longer than `StateTimeout` (200 ms) latches an emergency
  stop. A stale link means you are commanding blind.
- **Fall detection** — roll or pitch beyond `FallDetectionAngle` (50°).
- **Motor temperature** — above `MaxMotorTemperatureCelsius` (80 °C).
- **Battery** — below `MinBatterySocPercent` (15%). A robot that browns out mid-stride falls.

An emergency stop sets every joint to damping and **latches**. Clearing it requires an explicit
`ClearEmergencyStop()` call, which also discards the rate-limiter's position history — otherwise the
first new setpoint would slew from where the joint used to be rather than where it is now.

### 3. Per-session — what actually stops the robot

The robot expires a velocity command roughly 500 ms after receiving it — that is `CommandWatchdog`, and
it describes the *robot's* behaviour, not a timer in this SDK. It is why continuous motion needs the
command resent, and why `VelocityStream` pumps at 20 Hz.

**That pump is the safety property.** If the process dies, is killed, or the stream is disposed, no
further request arrives and the robot stops on its own within half a second. Disposing the stream also
sends an explicit stop, so the robot halts whether the code path completes normally, throws, or is
cancelled.

By default the stream does **not** expire a command the caller is deliberately holding. Holding one
velocity for a few seconds is ordinary — a leg of a patrol, a dance step — and treating it as a fault
made the stream fight its own pump: the robot stopped half a second after every command.

Where the thing driving the robot is *remote*, that is not enough, and the stream takes an opt-in:

```csharp
using VelocityStream stream = robot.Sport.StartVelocityStream(
    commandTimeout: TimeSpan.FromMilliseconds(500));
```

Use it whenever a command's source can vanish while your process keeps running:

| Caller | Timeout | Why |
|---|---|---|
| ROS 2 bridge | **Yes** | A `cmd_vel` publisher can die, or the network can drop, while the bridge is fine |
| Dashboard drive pad | **Yes** | A browser tab can close mid-press; `StopDrive` never runs |
| A bounded move — `unitree move --seconds 5` | No | The duration is fixed and disposal stops the robot |
| A control loop that reassigns every tick | No | It is already refreshing |

Getting this backwards is not theoretical. Defaulting it *on* silently truncated every timed move to
half a second; leaving it *off* for the ROS 2 bridge would let a robot coast after its operator's node
died. Both are in the repository's history.

## Throw or clamp

Limit violations **throw** `SafetyViolationException` by default. This is deliberate: a silently clamped
torque command is indistinguishable from a working one until the robot behaves unexpectedly under load,
and by then the cause is several layers away.

Set `RobotSafetyOptions.ClampInsteadOfThrow` only where a rejected command is worse than a reduced one —
teleoperation, typically, where throwing would drop the operator's control authority mid-motion.

## Damping, not idle

The correct way to end a session, and the correct response to a fall, is **damping** — not idle.

| Mode | Behaviour |
|---|---|
| `MotorCmd.Idle` | No torque at all. The robot **falls**. |
| `MotorCmd.Damping(kd)` | Resists motion without holding a posture. The robot **settles**. |

`LowLevelController.Stop()` publishes a final damping command before halting the loop, because cutting
the command stream while joints are holding a posture makes the robot collapse.

## Before a low-level session

1. Put the robot **on a stand** for anything new. A stand turns a bug into a noise instead of a fall.
2. Confirm telemetry is arriving — `unitree status`.
3. Check the battery. Low charge behaves unpredictably under load.
4. Clear the area. Recovery motions are large and fast.
5. Know how to stop it: physical remote, `unitree damp`, or Ctrl+C on any sample here.

Then use `BeginLowLevelSessionAsync`, which performs the full sequence — release the on-board motion
controller, let the robot settle, start publishing — rather than doing it by hand.

## The failure that wastes the most time

**Low-level commands do nothing, and nothing reports an error.**

Three causes, in order of likelihood:

1. **The sport service still owns the motors.** It drives them at 500 Hz and simply overwrites anything
   on `rt/lowcmd`. Release it via `MotionSwitcherClient.EnsureReleasedAsync()`.
2. **The CRC is wrong.** The firmware drops bad-CRC messages without any diagnostic. The serialisation
   path refreshes the CRC automatically; a hand-built message does not.
3. **The wrong network interface.** See [`dds-networking.md`](dds-networking.md).

## Language models and motion

The AI workflow engine can command motion, and that is gated behind **two separate opt-ins**:

```json
{
  "Unitree:Ai": {
    "ExposeMotionFunctions": false,        // the model cannot even see motion functions
    "AllowAutomaticFunctionCalling": false // the model may propose, but not execute
  }
}
```

With both off — the default — the model reads telemetry and explains what it sees, which makes a useful
diagnostic assistant with no physical risk.

Even with both on, every motion function re-checks readiness before acting. A model may call functions
in any order it likes, including asking the robot to walk without ever checking whether it is upright,
so that check cannot live only in the prompt.

## Configuring the envelope

```json
{
  "Unitree": {
    "Safety": {
      "Velocity": { "MaxForward": 0.6, "MaxLateral": 0.4, "MaxYawRate": 0.8 },
      "MinBatterySocPercent": 15,
      "MaxMotorTemperatureCelsius": 80,
      "FallDetectionAngle": 0.87,
      "CommandWatchdog": "00:00:00.500",
      "StateTimeout": "00:00:00.200",
      "ClampInsteadOfThrow": false
    }
  }
}
```

Defaults are deliberately below the hardware maxima. Raise them only with the robot on a stand, and
only one at a time.
