# Architecture

## The layering

Everything is arranged so that each layer depends only on the one below it. The practical payoff is
that the entire stack above `IDdsTransport` can be developed and tested with no robot and no native
code.

```
┌──────────────────────────────────────────────────────────────┐
│  Apps        CLI · Blazor dashboard · samples                │
├──────────────────────────────────────────────────────────────┤
│  Extensions.DependencyInjection   (wires everything)         │
├───────────────┬───────────────┬──────────────┬───────────────┤
│  Ai           │  Ml           │  Ros2        │  Diagnostics  │
│  Semantic     │  ML.NET       │  bridge      │  metrics,     │
│  Kernel       │  TorchSharp   │              │  health       │
├───────────────┴───────┬───────┴──────────────┴───────────────┤
│  Manipulation         │  Sensors                             │
│  trajectories, arms   │  IMU, LiDAR, battery, telemetry      │
├───────────────────────┴──────────────────────────────────────┤
│  Control        low-level joints · sport client · navigation │
├──────────────────────────────────────────────────────────────┤
│  Dds            IDdsTransport · typed pub/sub · participant  │
├───────────────────────────────┬──────────────────────────────┤
│  Messages   CDR codec, IDL    │  Interop   Cyclone shim      │
├───────────────────────────────┴──────────────────────────────┤
│  Core       models, joints, maths, safety, real-time loop    │
└──────────────────────────────────────────────────────────────┘
```

`Firmware` sits off to the side: it depends only on `Core`, because updating a robot's firmware has
nothing to do with controlling it.

## The transport seam

`IDdsTransport` is the single most load-bearing abstraction in the codebase. It has four members —
start, stop, publish bytes, subscribe to bytes — and three implementations:

| Implementation | Wire format | Native dependency | Reaches firmware |
|---|---|---|---|
| `CycloneDdsTransport` | RTPS via Cyclone DDS | `unitree_net_native` | **Yes** |
| `ManagedMulticastTransport` | Unitree.Net framing over UDP multicast | None | No |
| `LoopbackTransport` | Direct in-process dispatch | None | No |

Because typing sits *above* the transport rather than inside it, the control layer, the telemetry hub,
the dashboard and the test suite are all identical across the three. Switching between them is a
configuration change, not a code change.

## Why the CDR codec is hand-written

The obvious alternative — generate C# from Unitree's IDL — was rejected because the CRC forces the
issue.

Unitree computes `crc32_core` over the **C++ struct's raw memory**, walking it as 32-bit words. For the
SDK's CRC to match the firmware's, the bytes we put on the wire must be laid out exactly as the C++
struct is. That is only true if the CDR encoding and the struct layout coincide — which they do for
these messages, since every member is a fixed-size primitive or array.

So the codec is written by hand against known layouts, and a test asserts the property directly:

```csharp
ReadOnlySpan<byte> structBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<LowCmd>(in command));
cdrBody.SequenceEqual(structBytes).ShouldBeTrue();
```

If that test ever fails, the CRC is wrong and the robot will silently ignore every command. It is the
highest-value test in the suite.

The messages use `[InlineArray]` so fixed-size IDL arrays become value types embedded directly in the
struct — no heap allocation, and an implicit `Span<T>` conversion the codec reads and writes through.

## Real-time scheduling

`RealtimeLoop` runs the 500 Hz control path on a dedicated high-priority thread. It does **not** use
`PeriodicTimer` or `Task.Delay`, because both are quantised to the OS timer — roughly 15.6 ms on stock
Windows and 1 ms on Linux. A 2 ms period is far inside that quantum.

The loop hybrid-waits instead: it sleeps only while more than one scheduler quantum of slack remains,
then spins. Spinning burns a core, which is the deliberate trade for a control loop. Measured on a
developer laptop:

| Requested | Actual | Mean jitter |
|---|---|---|
| 50 Hz | 50.0 Hz | 2 µs |
| 200 Hz | 200.0 Hz | 1 µs |
| 500 Hz | 500.0 Hz | 3 µs |
| 1000 Hz | 1004 Hz | 1 µs |

A throwing tick callback is logged and the loop continues. Losing the control thread on a robot is far
worse than one bad tick.

## Backpressure

Telemetry channels are bounded and drop the **oldest** sample when full. A slow consumer must never be
able to stall the receive path — that would delay every other topic sharing the transport — and must
never grow memory without bound.

Two consumption models stay live at once:

- `Reader` — a `ChannelReader<T>` for streaming with backpressure awareness.
- `TryGetLatest` — the most recent sample, for control loops that must never process a backlog.

`DroppedCount` samples the channel depth before writing, because a `DropOldest` channel always accepts
the write and so its return value cannot report loss.

## Safety architecture

Safety is enforced at three levels, deliberately:

1. **Per-command** — `JointSafetyLimits` validates or clamps position, velocity, torque and gains, and
   rate-limits setpoint changes per tick so a bad target becomes a ramp rather than a step input.
2. **Per-tick** — `LowLevelController` checks state freshness, body attitude, motor temperature and
   battery on every control tick, latching an emergency stop on violation.
3. **Per-session** — `VelocityStream` keeps commands flowing, so the robot stops on its own when the
   process does; a `commandTimeout` covers a remote source that stops sending while the process lives.

Violations **throw** by default rather than being silently clamped. A quietly reduced torque command is
indistinguishable from a working one until the robot behaves unexpectedly under load. Teleoperation
paths, where dropping control authority mid-motion is worse, can opt into clamping via
`RobotSafetyOptions.ClampInsteadOfThrow`.

## Concurrency model

- **One robot per process.** Unitree firmware does not arbitrate between controlling hosts, so
  `UnitreeRobot` is an exclusive resource and everything is registered as a singleton.
- **Control surfaces are created lazily.** Reading telemetry never creates a `rt/lowcmd` publisher,
  which matters because the existence of that writer is itself visible to the robot.
- **Service calls are correlated by request id.** Requests and responses travel on separate topics;
  a reply whose id is unknown is discarded rather than surfaced.
- **The real-time thread never allocates on the publish path.** Messages under 2 KB serialise into a
  stack buffer; larger ones use `ArrayPool<byte>`.
