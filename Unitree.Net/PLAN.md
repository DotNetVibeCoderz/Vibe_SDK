# PLAN — development roadmap

Status legend: ✅ done · 🚧 in progress · ⬜ planned

---

## Phase 1 — Foundation ✅

The layers everything else stands on.

| Item | Status |
|---|---|
| Solution, central package management, .NET 10 pinning | ✅ |
| `Unitree.Net.Core` — models, joints, pose maths, safety limits | ✅ |
| Unitree CRC-32 (`crc32_core`) | ✅ |
| `RealtimeLoop` — hybrid-wait scheduler, verified to 1 kHz | ✅ |
| Zero-allocation CDR reader/writer | ✅ |
| `unitree_go` messages, verified byte-identical to the C++ struct layout | ✅ |
| `unitree_api` request/response envelope | ✅ |

## Phase 2 — Transport ✅

| Item | Status |
|---|---|
| `IDdsTransport` abstraction, typed publishers and subscribers | ✅ |
| Bounded telemetry channels with drop-oldest and loss accounting | ✅ |
| `ManagedMulticastTransport` — pure managed, no native dependency | ✅ |
| `LoopbackTransport` — in-process, for tests | ✅ |
| `CycloneDdsTransport` P/Invoke bindings | ✅ |
| Native shim: C ABI, CMake, raw-CDR path via `dds_writecdr` / `dds_takecdr` | ✅ |
| **Validate the shim against real hardware** | ⬜ |

## Phase 3 — Control ✅

| Item | Status |
|---|---|
| Service client with request/response correlation | ✅ |
| `SportClient` — postures, gaits, velocity, full sport API surface | ✅ |
| `VelocityStream` — self-refreshing commands with a watchdog | ✅ |
| `LowLevelController` — 500 Hz loop, per-tick safety, latching e-stop | ✅ |
| `MotionSwitcherClient` — releasing the on-board controller | ✅ |
| `WaypointNavigator` — odometry navigation with stall detection | ✅ |

## Phase 4 — Perception and manipulation ✅

| Item | Status |
|---|---|
| `TelemetryHub` — unified snapshots over shared subscriptions | ✅ |
| Battery health including cell-imbalance detection | ✅ |
| `PointCloud2` decoding with lazy point enumeration | ✅ |
| Quintic trajectory planning with synchronised joint timing | ✅ |
| `ArmController`, `DualArmCoordinator` | ✅ |
| Camera and audio streams | ⬜ |

## Phase 5 — Integration ✅

| Item | Status |
|---|---|
| ROS 2 bridge — `Imu`, `Odometry`, `Twist` | ✅ |
| OpenTelemetry-compatible metrics | ✅ |
| ASP.NET Core health checks | ✅ |
| Firmware manager — verification, staging, health gating, rollback | ✅ |
| **A concrete `IFirmwareChannel` for real robot OTA** | ⬜ |

## Phase 6 — ML and AI ✅

| Item | Status |
|---|---|
| ML.NET gait statistics and SSA anomaly detection | ✅ |
| TorchSharp policy inference for learned locomotion | ✅ |
| Semantic Kernel engine with four providers | ✅ |
| Safety-gated robot plugins, off by default | ✅ |
| **Validate a trained policy on hardware** | ⬜ |

## Phase 7 — Applications ✅

| Item | Status |
|---|---|
| CLI — diagnose, status, monitor, postures, move, AI chat | ✅ |
| Blazor dashboard — telemetry, charts, control | ✅ |
| Virtual robot simulator (headless) | ✅ |
| Low-level control and patrol samples | ✅ |

## Phase 7b — Desktop tooling ✅

| Item | Status |
|---|---|
| `Unitree.Net.Simulation` — rig-driven kinematics for all eight platforms | ✅ |
| Simulator: WPF + Blazor + Three.js viewport, status and log panels | ✅ |
| Simulator: telemetry ribbon showing measured topic rates | ✅ |
| `Unitree.Net.Wizard.Core` — templates, scaffolding, build, SSH deploy | ✅ |
| Wizard: Monaco editor, menus, tabs, output panel, status bar | ✅ |
| Wizard: 16 project templates, all verified to compile | ✅ |
| Jack The Code Bender — multi-session chat, attachments, Markdown | ✅ |
| Kernel functions: SDK reference, project I/O, search, fetch, time, maths | ✅ |
| 32 example prompts across nine categories | ✅ |
| CLI automation surface: `templates`, `new`, `probe`, `stream`, `deploy` | ✅ |
| VS Code extension: project creation, connect, run, debug, deploy | ✅ |
| VS Code: robot status panel, log channel, run-target status bar | ✅ |
| Simulated `sport`, `motion_switcher` and `robot_state` services | ✅ |
| Dashboard made interactive — it had no render mode and never was | ✅ |
| **Exercise the wizard's deploy path against real hardware** | ⬜ |
| **A cross-platform host for both shells (Photino or Blazor Server)** | ⬜ |
| **Publish the extension to the marketplace** | ⬜ |

## Phase 8 — Hardware validation ⬜

Nothing in this phase can be done without a robot. It is the gap between "correct by construction" and
"proven".

| Item | Status |
|---|---|
| Build the native shim against `unitree_sdk2` and confirm discovery | ⬜ |
| Confirm CRC acceptance on real firmware | ⬜ |
| Measure control-loop jitter on Jetson and Raspberry Pi | ⬜ |
| Verify the sport API surface per platform | ⬜ |
| Validate the safety envelope against real motor limits | ⬜ |
| Confirm ROS 2 interoperability with a live Nav2 stack | ⬜ |

## Phase 9 — Distribution ⬜

| Item | Status |
|---|---|
| NuGet packaging and symbol publication | ⬜ |
| CI: build, test, and cross-compile the shim for ARM64 | ⬜ |
| CI: run `tools/Unitree.Net.TemplateCheck` so template drift fails the build | ⬜ |
| Native binaries shipped in a runtime package | ⬜ |
| API documentation site generated from XML docs | ⬜ |
| Versioning policy and changelog | ⬜ |

## Phase 10 — Beyond ⬜

| Item | Notes |
|---|---|
| `unitree_hg` humanoid message set | G1 and H1 low-level control |
| Isaac Lab integration | Publish simulator state through the same transport |
| Visual Studio project templates | Spec calls for a VS extension |
| Reinforcement-learning training harness | Spec calls for a training suite |
| Multi-robot coordination | One process per robot; a fleet layer above |

---

## Design decisions in the tooling

**The rig is one description.** A robot's geometry, its kinematics and its 3D form all come from the
same `RobotRig`, and the rig asserts its own joint count against the platform's. That check has caught
two real modelling errors already — H1's joint count and the wheeled variants' missing wheels.

**Templates are compiled, not trusted.** `tools/Unitree.Net.TemplateCheck` scaffolds and builds all
sixteen. Every one of them failed the first time it ran. A template written from memory of an API is a
template that does not work.

**Jack is given the API rather than trusted to remember it.** `describe_sdk` exists because this SDK
is not in any model's training data. It is curated by hand and must be updated when the SDK changes —
that is the cost of the function being useful.

**Jack cannot write outside the open project.** Every path is resolved and compared against the project
root. A model can be talked into anything; the guard is what makes that harmless.

**The simulator refuses what the robot refuses.** It starts resting and will not drive until it has
been stood up. Anything it accepts that hardware would reject is a trap it has laid for the operator.

**"It renders" is not evidence that anything works.** The dashboard rendered perfectly for its whole
life with no render mode, so not one button did anything. Verify behaviour, not appearance.

---

## Design decisions worth keeping

**The transport seam is the most valuable thing in the codebase.** It is why the whole stack develops
and tests without hardware. Resist anything that leaks transport specifics upward.

**The wire format is verified, not assumed.** The test asserting that the CDR body equals the struct's
raw memory is what makes the CRC trustworthy. If it ever fails, the robot silently ignores every
command — keep it.

**Safety violations throw by default.** A silently clamped command is indistinguishable from a working
one until the robot behaves unexpectedly under load.

**Language models do not get motion by default.** Two separate opt-ins, and readiness re-checked inside
every motion function rather than only in the prompt.
