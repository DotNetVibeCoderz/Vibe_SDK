# Architecture

How the layers fit together, and why the boundaries are where they are.

## The shape

```
                    apps
   Gallery      Simulator      Wizard        Cli
      |             |            |            |
      +------+------+-----+------+-----+------+
             |            |            |
            Ui       Wizard.Core       Ai            Ml
             |            |            |             |
             +------------+-----+------+-------------+
                                |
        ReachyMini        MicroDuck        Reachy2      Simulation
             |                 |               |            |
             +--------+--------+-------+-------+------------+
                      |                |
                  Transport       Kinematics
                      |                |
                      +-------+--------+
                              |
                            Core
```

Nothing above depends on anything below it more than one step where it can be avoided, and nothing
below knows anything above exists. `Core` has no dependency beyond `Microsoft.Extensions.Logging.Abstractions`.

## The load-bearing decision

**Every robot client talks to a transport interface, never to a socket.**

```csharp
public interface IReachyMiniTransport : IAsyncDisposable
{
    Task SetTargetAsync(ReachyMiniTarget target, CancellationToken cancellationToken = default);
    Task GotoTargetAsync(ReachyMiniTarget target, TimeSpan duration, InterpolationMethod method, ...);
    Task<ReachyMiniState> GetStateAsync(CancellationToken cancellationToken = default);
    // ...
}
```

Two implementations ship: `ReachyMiniDaemonTransport` speaks REST and WebSocket to the real daemon,
and `SimulatedReachyMiniTransport` drives an in-process model. `ReachyMiniClient` cannot tell them
apart.

That is what makes "the same code runs on the robot and in the simulator" a fact rather than a
claim. Every sample, every gallery demo and every wizard template is written against the interface;
switching target is one line.

The simulated transports deliberately keep the asynchrony and add a couple of milliseconds of
latency. Everything they do could return a completed task synchronously — and then code that works
in the simulator would deadlock the first time it met a real network round trip.

## Layer by layer

### Core

Primitives everything else agrees on.

- `Pose`, `Angle`, `Rotation` — the geometry, and the one place the robotics convention (z up, x
  forward) meets `System.Numerics` (y up). `Pose.ToRowMajor` is the only sanctioned crossing.
- `RobotDescription` / `RobotCatalog` — the joint model for each robot, in wire order, asserted
  consistent at construction. Every layer resolves a joint by name through here rather than by a
  hard-coded index.
- `RobotSafetyOptions` / `JointLimitGuard` — limits, and what happens when one is exceeded.
- `RealtimeLoop` — a fixed-rate loop that is actually fixed rate. See below.
- `RobotLogSink` — one log every subsystem writes into.

### Kinematics

- `StewartPlatform` — the Reachy Mini neck. Closed-form inverse, iterative forward.
- `SerialChain` — forward kinematics, geometric Jacobian, damped least-squares inverse for the
  Reachy 2 arms.
- `LinearAlgebra` — the handful of small dense routines those need. Deliberately not a linear
  algebra library: every problem here is at most 7x6, and a BLAS binding would cost more than it
  saves on the ARM boards this targets.

See [kinematics.md](kinematics.md) for what is exact and what is approximate.

### Transport

- `JsonRpcChannel` — JSON-RPC 2.0 as NDJSON over a duplex stream. What MicroDuck's daemons speak.
- `JsonWebSocketChannel` — JSON messages over a client WebSocket. What the Reachy Mini daemon
  takes commands on.
- `HttpJsonClient` — REST, with transport failures and command failures raised as different
  exception types, because "the robot is off" and "you asked for something silly" need different
  messages in a UI.

### The robot packages

One per robot, each with a client, an options record, a transport interface and a daemon transport.
They do not depend on each other.

### Simulation

Kinematic models of all three robots plus in-process transports. The models are not physics: joints
track their targets exactly, contact is not modelled, nothing falls over unless the model decides
to. That is enough to develop against and deliberately not enough to validate that a motion is safe.

`SimulationEngine` owns the clock and ticks the model at a fixed rate. The viewport reads
`LatestSnapshot` whenever it paints; the two are independent. Ticking from a render callback would
tie the simulation rate to the display refresh, so a dropped frame would slow the robot down and a
144 Hz monitor would speed it up.

## Two things worth knowing

### The realtime loop does not use `PeriodicTimer`

Neither `Task.Delay` nor `PeriodicTimer` is usable for a control loop. Both are quantised to the OS
scheduler tick — about 15.6 ms on Windows by default, which is three quarters of a 50 Hz period and
eight times a 500 Hz one. A loop built on them does not run late occasionally; it runs late every
tick.

`RealtimeLoop` hybrid-waits: it sleeps while more than one scheduler quantum of slack remains, then
spins to the deadline. Spinning burns a core on purpose. Deadlines are computed from the loop's
start rather than accumulated per tick, so one slow iteration does not push every later deadline
out with it.

### Everything logs to one place

`RobotLogSink` is a bounded ring with change notification, and every subsystem writes to it:
transports, the simulation engine, the wizard's build pipeline, and JavaScript errors bubbled out of
the 3D viewport.

In a desktop application there is no console anyone will ever look at, so a subsystem failure is
otherwise completely silent. Three of the bugs listed in [PROGRESS.md](../PROGRESS.md) presented as
a blank panel with no error anywhere, and the fix in each case started with getting the failure into
this log.

The ring is capped because a 50 Hz transport logging one line per tick fills a list faster than
anyone can read it, and an unbounded log panel is a memory leak with a UI.

## The applications

| | |
|---|---|
| **Gallery** | Demos and their source, side by side, running against the simulation |
| **Simulator** | Avalonia shell + in-process Kestrel + Blazor + three.js. See [simulator.md](simulator.md) |
| **Wizard** | Editor, templates, build/run/deploy, and Jack. See [wizard.md](wizard.md) |
| **Cli** | `pollen` — diagnostics, scaffolding, driving a robot from a terminal |

The three graphical applications share `PollenRobotics.Net.Ui`: one set of theme tokens, one joint
instrument, one log panel. The theme preference is stored per user rather than per application, so
switching one switches all three — they are meant to read as one product.
