# Progress

What exists, what is verified, and what is not. The distinction in the last two columns is the
point of this file: a great deal here builds, runs and looks right without ever having touched a
robot.

Last updated: 10 September 2026. Published to nuget.org at 0.1.0 on the same day.

---

## The one thing to read first

**Nothing in this repository has been run against physical Pollen Robotics hardware.**

Everything has been exercised against the built-in simulation, which implements the same interfaces
the real transports do. That catches API mistakes, threading mistakes and logic mistakes. It cannot
catch a wire-format mistake, because the simulation is on the near side of the wire.

Where a wire format was guessable but not confirmable, the code says so at the point it matters.

---

## Verified

Run and observed working, in the simulation or on this machine.

| Area | Evidence |
|---|---|
| Solution builds | `dotnet build PollenRobotics.Net.slnx` — 0 warnings, 0 errors |
| Tests | 37 pass (`dotnet run --project tests/PollenRobotics.Net.Tests`) |
| Templates | 20/20 scaffold and compile (`tools/PollenRobotics.Net.TemplateCheck`) |
| Reachy Mini simulation | Head pose, antennas, body yaw, gotos, easing curves, idle breath, motor modes, the 65-degree yaw constraint |
| MicroDuck simulation | Init, velocity intents, gait, the seven action slots, falls and recovery, the velocity watchdog |
| Reachy 2 simulation | Per-part movement queue, local IK, grippers, both arms in step |
| Safety limits | Violations throw through the whole stack; clamping works when asked for |
| Kinematics | Stewart solver zeroes at home, covers the published tilt envelope, stays continuous across a sweep; arm IK converges and respects limits |
| CLI | `doctor`, `joints`, `templates`, `new`, `sim`, `mini`, `duck` all run |
| Gallery | All 11 demos run against the simulation |
| Simulator | Kestrel + Blazor + three.js in one process; all three rigs render and animate; theme follows the shell; camera follows a travelling robot |
| Wizard | Project scaffold, open, edit, save; build with error navigation; run against the simulator |
| Jack | Answered live against an OpenAI-compatible endpoint, called the SDK reference functions, and produced code using real members of this SDK |
| Sample | `samples/DeskCompanion` runs end to end against the simulation - idles, notices a face, engages, parks on exit |
| Packaging | `dotnet pack` produces 11 NuGet packages into `artifacts/packages`, IDs prefixed `Gravicode.` |
| Package install | A fresh `dotnet new console` restored `Gravicode.PollenRobotics.Net.ReachyMini` and `.Simulation` and ran a head goto against the simulation |
| Published | 11 packages at 0.1.0 on nuget.org, from commit `b2ec200`, tag `pollenrobotics-v0.1.0` |

---

## Written but not verified against hardware

These are implemented against Pollen's published documentation. They are the parts most likely to
need correcting when a robot is first plugged in.

### Reachy Mini — daemon transport

| What | Confidence | Note |
|---|---|---|
| REST paths (`/status`, `/joints`, `/head_pose`, `/imu`, `/recorded_data`) | Good | Named in the Python SDK's own client |
| Command names (`SetFullTargetCmd`, `GotoTaskRequest`, …) | Good | The Rust command enum variants, as the Python SDK sends them |
| Externally-tagged JSON envelope `{"SetFullTargetCmd": {...}}` | **Assumed** | serde's default for that enum shape. If the daemon uses a different tagging, commands will be silently ignored |
| WebSocket path `/ws` | **Assumed** | Pollen documents "a REST API and WebSocket at :8000" but not the path. Settable via `ReachyMiniOptions.WebSocketPath` |
| Head pose as a flat row-major 4x4 | Good | Confirmed against the JavaScript SDK's documented wire shape |
| Antenna order `[right, left]` | Good | Documented |

### MicroDuck — robotd transport

| What | Confidence | Note |
|---|---|---|
| NDJSON JSON-RPC 2.0 over a Unix socket | Good | Stated in the architecture document |
| Socket path `/run/robotd.sock` | Good | Documented |
| `robot.*` namespace | Good | Documented |
| Individual method names and parameter shapes | **Assumed** | Pollen documents the namespace and the `robotctl` verbs but not the per-method signatures. These follow the CLI's vocabulary and may not match |
| Joint names and limits | **Assumed** | The servo count and loop rate are published; the per-joint table is not. Correct these against `robotctl monitor` |

### Reachy 2 — gRPC

| What | Confidence | Note |
|---|---|---|
| Wire contract | **High** | The `.proto` files are Pollen's own, vendored verbatim |
| Port 50051 | Good | Conventional and widely reported |
| Part discovery, arm/head/hand/base calls | Good | Generated directly from the protos |
| Local arm link lengths | **Approximate** | Not from Pollen's URDF. Prefer the robot's own IK service |

---

## Deliberately approximate

Not defects — decisions, documented where they are made.

- **Stewart neck geometry.** Pollen publishes the mechanism and the pose envelope, not the link
  dimensions. `StewartGeometry.ForEnvelope` derives the rod and horn from the envelope rather than
  guessing them, so the model provably covers what the robot claims. Real branch angles will differ.
- **Reachy 2 arm chain.** Right proportions, not Pollen's URDF.
- **MicroDuck gait.** A scripted periodic gait whose phase tracks commanded speed. The real duck
  walks on a learned policy. This model will happily walk where a real duck falls over.
- **The 3D rigs.** Built from primitives. Pollen publishes no meshes under a licence this project
  could vendor, and a shape that moves correctly reads better than an accurate one that does not.

---

## Bugs found and fixed during development

Kept because each one names a trap that is easy to fall into again.

| Bug | How it presented | Cause |
|---|---|---|
| Neck arcs read 118 deg at rest, half of them red | Looked like a robot in trouble on a still screen | The Stewart solver had no home reference. A rotary platform has no reason for its horn angles to be zero when the platform is level; it now calibrates at home and reports relative to it |
| Branch angles jumped mid-sweep | A servo would slam across its travel | The branch equation has two solutions and the code took whichever `asin` returned. It now prefers the one nearer zero |
| Approximate geometry could not reach the published envelope | `SolveInverse` threw inside the documented limits | The platform triad was rotated 60 degrees against the base, a convention borrowed from linear Stewart platforms. A rotary branch cannot absorb the resulting swing |
| Every neck branch read exactly 0 while the status said "moving" | Silent | The idle breath was written back into the head pose each tick instead of applied as an offset, so the pose integrated out of the workspace and the solver threw into a `catch` that reported zeros |
| Blazor viewport rendered but never became interactive | Blank canvas, no error | `OutputType=WinExe` on a Web SDK project drops the framework's static web assets, so `_framework/blazor.web.js` 404s |
| Viewport page returned HTTP 500 | Blank canvas | `@rendermode` on `RouteView` makes the framework serialise `RouteData`, which carries a `System.Type` |
| Chat thread appeared empty with a full conversation in memory | Blank panel with working buttons | Two causes: `MarkdownView` derived from `ContentControl` without a style key so it had no template, and it rendered before being attached to the tree so every theme brush resolved to null |
| The duck walked out of frame | Viewport looked frozen | The camera did not follow a robot that travels |
| `pollen sim` died when piped | "The handle is invalid" | `Console.SetCursorPosition` throws when stdout is redirected |
| Twenty templates scaffolded, four did not compile | Only visible on Build | Template code is written by hand against the SDK and nothing recompiles it. `tools/TemplateCheck` now does |

---

## Not done

- No hardware validation of any kind.
- **Media.** Camera frames and audio are modelled in the transport interface but not implemented;
  the Reachy Mini daemon exposes them over WebRTC/GStreamer, which is a substantial dependency.
- **Recorded moves.** Record and replay work through the SDK; daemon-side `playMove` with audio,
  and the gzip+base64 upload path, are not implemented.
- **Reachy 2 simulation transport.** The simulated Reachy 2 is driven directly rather than through
  an `IReachy2Transport`, because the gRPC client talks to Pollen's generated stubs rather than to
  an interface of ours. Reachy 2 templates therefore have no `--sim` switch.
- **Head tracking** is plumbed end to end but the simulated face has to be set by the caller; there
  is no vision pipeline behind it.
- **Prefix reservation.** `Gravicode.*` is not reserved on nuget.org. Until it is, the prefix is a
  naming convention rather than a claim, and nothing stops someone else publishing under it. See
  [docs/publishing.md](docs/publishing.md).
- **TorchSharp** is referenced and available but no component uses it yet; the ONNX runner covers
  the inference case.
