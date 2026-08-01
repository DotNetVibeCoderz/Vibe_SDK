# PROGRESS

Last updated: 1 August 2026

## Current state

The full solution builds clean and the test suite is green. Every layer above the transport has been
exercised end to end against the simulator. **Nothing has been run against real hardware.**

| Metric | Value |
|---|---|
| Projects | 24 (15 libraries, 4 apps, 3 samples, 1 tool, 1 test project) plus a VS Code extension |
| Build | 0 errors, 0 warnings (Debug and Release) |
| Tests | 320 passing, 0 failing, 0 skipped |
| Target | .NET 10 · TypeScript 5.7 for the extension |

```
dotnet build Unitree.Net.slnx -c Release  → Build succeeded. 0 Warning(s), 0 Error(s)
dotnet test  Unitree.Net.slnx             → Passed! Failed: 0, Passed: 320, Skipped: 0
dotnet run --project tools/Unitree.Net.TemplateCheck
                                          → All 16 templates compile.
cd tools/vscode-unitree && npm run compile → 0 errors
```

Three tools were added on top of the SDK: a 3D robot **simulator**, the **Unitree Robot Wizard** (a
code editor with an LLM assistant), and a **VS Code extension**. The first two are WPF shells over
cross-platform engines — `Unitree.Net.Simulation` and `Unitree.Net.Wizard.Core` are plain `net10.0`
and build anywhere. The extension is TypeScript and drives everything through the CLI.

## What has been verified, and how

### Wire format — verified against the IDL

Struct sizes match Unitree's IDL exactly, and the CDR body is **byte-identical to the struct's raw
memory**:

| Message | Size | Verified |
|---|---|---|
| `MotorCmd` | 36 B | ✅ |
| `MotorState` | 48 B | ✅ |
| `ImuState` | 56 B | ✅ |
| `BmsState` | 44 B | ✅ |
| `LowCmd` | 812 B body / 816 B encoded | ✅ byte-identical |
| `LowState` | 1180 B body / 1184 B encoded | ✅ byte-identical |
| `SportModeState` | 236 B body / 240 B encoded | ✅ byte-identical |

That byte-identity is the property that makes the CRC correct: Unitree computes `crc32_core` over the
C++ struct's memory, and this SDK computes it over the same struct before serialising separately. The
two only agree if the encodings coincide.

**A bug this caught.** The first implementation omitted CDR struct-array alignment, so `LowCmd`
serialised to 812 bytes instead of 816 — the CRC was correct but the payload was four bytes short of
what the firmware expects. The byte-identity test now guards against a recurrence.

### Real-time loop — measured

Measured on a developer laptop under Release:

| Requested | Actual | Mean jitter | Max jitter |
|---|---|---|---|
| 50 Hz | 50.0 Hz | 2 µs | 11 µs |
| 200 Hz | 200.0 Hz | 1 µs | 15 µs |
| 500 Hz | 500.0 Hz | 3 µs | 1881 µs |
| 1000 Hz | 1004.3 Hz | 1 µs | 59 µs |

**A bug this caught.** The original hybrid wait slept whenever more than 1 ms of slack remained. On
Windows the scheduler quantum is ~15.6 ms, so a 200 Hz loop asking for a 4 ms sleep got ~15.6 ms and ran
at roughly a third of its requested rate. The threshold is now one full quantum, below which the loop
spins — deliberately trading a core for timing accuracy. This was found by a test asserting the actual
tick rate rather than merely that the loop ran.

The 1881 µs maximum at 500 Hz is a single outlier, consistent with a GC or scheduler event; the mean
stays at 3 µs.

### End-to-end — exercised

The virtual robot publishes `rt/lowstate` at 500 Hz and `rt/sportmodestate` at 50 Hz over managed
multicast. Confirmed working through the full stack:

- CLI `status` and `monitor` connect, decode and render live telemetry.
- The Blazor dashboard reports `/health` **Healthy** against the simulator and **unhealthy** with no
  robot attached, charts live telemetry, and its control page actually moves the robot — see the
  render-mode section below for how long that last part was untrue.
- Battery discharge, motor heating and gait-driven foot contacts all move as expected.

**A bug this caught.** Transport names embed their endpoint in square brackets, which is also
Spectre.Console's markup syntax — the CLI crashed while *displaying* a perfectly valid transport name.
All runtime-derived strings are now escaped before reaching a markup string.

### Robot rigs — verified for all eight platforms

Each platform's rig is asserted to account for exactly the joints the platform claims to have, that
every joint index is driven by exactly one link, that parents appear before children, and that every
rotation axis is a unit vector.

| Platform | Joints | Modelled as |
|---|---|---|
| Go2, B2 | 12 | 4 × (hip, thigh, calf), fixed foot pads |
| Go2-W, B2-W | 16 | The same legs plus 4 driven wheels |
| G1 | 29 | Legs 2×6, waist 3, arms 2×7 |
| H1 | 19 | Legs 2×5 (pitch-only ankle), waist yaw, arms 2×4 (no wrist) |
| H1-2 | 27 | Legs 2×6, waist yaw, arms 2×7 |
| R1 | 26 | Legs 2×6, waist 2, arms 2×6 |

**Two bugs this caught.** H1 was first modelled with 21 joints — its ankle is pitch-only and its arms
have no wrists, which is what makes it 19. And Go2-W and B2-W were modelled with 12 joints and fixed
feet, when the whole point of the W variants is four driven wheels; they now roll, with the wheel rate
derived from ground speed and wheel radius.

A third followed from the second: the humanoid gait originally used hard-coded joint indices, which
put H1's right leg at index 6 when its pitch-only ankle puts it at 5. Its right leg never moved. Joint
indices are now resolved from the rig by link name.

### Wizard templates — every one compiles

`tools/Unitree.Net.TemplateCheck` scaffolds all 16 templates into a temporary folder and builds them.

**What this caught.** All 16 failed on the first run. The templates were plausible but written against
a remembered API rather than the real one: `AddUnitreeRobot` needs a `using` for the DI namespace,
`BatteryStatus` exposes `StateOfChargePercent` and `PackVoltage` rather than `StateOfCharge` and
`VoltageVolts`, `NavigationResult` is `Arrived` rather than `Reached`, `LowLevelController` takes no
tick callback, `DualArmCoordinator` is built from two `ArmController`s, and .NET 10's `Router` replaced
the `<NotFound>` child element with a `NotFoundPage` parameter.

This is exactly the failure mode the wizard's `describe_sdk` function exists to prevent in generated
code — and the templates demonstrate that a curated reference is not optional.

### VS Code extension — exercised against the simulator

Loaded in an Extension Development Host with the repository open. Verified live: the status panel
showed `Connected`, `Go2`, `ManagedMulticast · 239.255.0.1:7447`, battery with an estimated runtime,
pack voltage and current, hottest motor, ground contact, speed, body height, pose, odometry and
message counts, all updating. The status bar read `Go2 · 86%`. The templates view listed all sixteen
templates grouped by kind. `unitree new` scaffolded a project from the CLI.

**Two bugs this caught.** `unitree probe` emitted its JSON underneath a logging line, because the
console logger also writes to stdout — a single info line is enough to make the output unparsable, and
the extension simply saw nothing. Logging is now cleared for the machine-readable commands, and the
extension parses the last line that actually looks like JSON rather than the last non-empty one.

And the extension originally invoked the CLI with `dotnet run`, which restores and builds on every
call. The telemetry stream and a template listing then blocked on the same MSBuild lock, so the
Templates view stayed empty for a minute and timed out — which reads as a broken extension rather than
a slow one. It now prefers an already-built `unitree.dll`.

### The simulator now answers the sport service

**Found by an operator, not by a test.** A hand-written `DanceBot` compiled, connected, and then died
on `StandUpAsync` with `Service 'sport' did not respond to API 1004 within 5 s`.

The simulator published telemetry and answered nothing. Any application that commanded motion —
**nine of the sixteen templates** — connected, read state happily, and timed out on its first posture
call. The repeated claim that "every template runs against the simulator without edits" was therefore
false for most of them: they compiled and they connected, but they could not run.

`SimulatedServiceHub` now answers `sport`, `motion_switcher` and `robot_state` on the
`rt/api/<service>/request` topics, driving the same `SimulatedRobot` the viewport shows. Postures,
velocity, stop, and the settings calls all work; tricks are accepted rather than refused, because
refusing them makes the simulator look broken when the application is fine.

Two decisions came out of it:

- **The simulator starts resting**, as a robot does after power-on, and refuses to drive until it has
  been stood up. Standing it up automatically would let an application skip `StandUp` and
  `BalanceStand` and still work — then fail silently on hardware, which is the exact trap the
  simulator exists to expose.
- **The headless sample and the 3D simulator now run the same engine.** `Unitree.Net.Samples.VirtualRobot`
  had its own copy of the kinematics; it would have needed a second copy of the service layer too.

### The dashboard was never interactive

Asked whether the dashboard also works against the simulator, and checking rather than assuming,
turned up a bug that had been there the whole time: **no page declared a render mode**, and neither
`App.razor` nor `Routes.razor` set one. The whole application was static server-side rendering.

It looked completely normal. Buttons rendered, the connection badge said Connected, the layout was
right. But no event handler was ever attached, so the control page did nothing at all, and the
telemetry page showed a single snapshot frozen at the moment it was rendered — its timer called
`StateHasChanged` into a component that no longer existed.

The earlier note in this file that "the Blazor dashboard renders" was accurate and useless: rendering
was all it had been checked for.

`<Routes @rendermode="InteractiveServer" />` fixes it. Verified objectively rather than by eye —
`unitree probe` against the simulator before and after pressing **Stand up**:

```
before   bodyHeight 0.098   feetLoaded 0   isAirborne true
after    bodyHeight 0.326   feetLoaded 4   isFullStance true
```

and the page's own log showing `Stand up — sent`. The dashboard's control page therefore also depended
on the sport service that landed in the same session; before that it could not have worked either.

### The About dialog was cut off, and what that turned up

Reported by the operator: the About dialog's right-hand side was clipped. The cause was one long
unbreakable string — the settings-file path — in a grid whose items default to `min-width: auto` and
so refuse to shrink below their content. It widened the dialog past its own 520 px box and clipped
every line. The dialog is now laid out as a definition list with `min-width: 0` throughout, and
`.modal-body` clips horizontally so no future dialog can repeat it.

Opening the settings dialog to check for a regression showed the persona **without** the SDK-lookup
rule added earlier that day. `Save` wrote the resolved system prompt back to `app.config` on every
close, so the file held a frozen 1284-character copy of whatever the built-in persona said when it was
first written — and every later improvement to it, including the rule added specifically because Jack
shipped code that did not compile, silently never arrived.

`Ai.SystemPromptIsCustom` now records whether the operator actually edited the prompt. False or absent
— which covers every file written before this existed — means the code's default wins. Verified
against the stale config still on disk: the new rule appears, the 1284-character copy is ignored.

### VelocityStream stopped the robot every half second

The same run surfaced a second bug. `VelocityStream` timed its watchdog from the caller's last
*assignment*, so holding one velocity for a second — a dance step, a leg of a patrol, anything the
templates do — tripped it. The robot stopped 500 ms after every command and the log filled with
warnings.

The stream is itself the publisher: its pump resends at 20 Hz, which is what satisfies the robot's own
command expiry. Timing from the caller conflated "the application died" with "the application is
holding a steady velocity". The guarantee that matters never depended on it — if the process dies or
the stream is disposed, the pump stops, no request arrives, and the robot stops on its own.

Client-side expiry is now opt-in: `StartVelocityStream(commandTimeout: …)`. Both behaviours are tested.

### Jack writing code — instructions did not work, a check did

Driven end to end through the wizard: New Project from a template → Build (succeeded) → asked Jack to
write a new file → Build.

The first file he wrote used `BatteryStatus.StateOfCharge`, which does not exist, and the build failed
with two errors. He had not called `describe_sdk` — the exact function that exists to prevent this.

Strengthening the instruction did not fix it. The persona was made unconditional ("call it every time,
including when you are certain"), the function description was rewritten to say ALWAYS, and the same
request produced the same wrong member. **Confidence, not uncertainty, is what stops a model reaching
for a lookup tool, and no wording addresses that.**

What did work is a deterministic check in the tool result. `write_project_file` now lints the content
against a table of known-wrong members and returns the correction. The observed sequence:

```
18:27:11  Jack wrote Behaviours/BatteryGuard.cs     ← rejected
18:27:13  Jack wrote Behaviours/BatteryGuard.cs     ← rejected
   … four more …
18:27:22  Jack wrote Behaviours/BatteryGuard.cs     ← clean
18:29:08  build succeeded in 7.8 s — 0 error(s), 0 warning(s)
```

Jack's own reply: *"Now, I'll write this corrected version to the project file."* A tool result cannot
be skipped the way a prompt can.

`SdkLint` covers the traps actually observed while building the template catalogue — the battery
members, `NavigationResult.Reached`, `LowLevelController.Statistics` and `SetJoint`, the
`DualArmCoordinator` constructor, `GaitAnomalyDetector`, the missing dependency-injection `using`, and
the removed `<NotFound>` element. It is a lint for known traps, not a compiler; the build still proves
the code. Ten cases are covered by tests, including one asserting that correct code is *not* flagged —
a lint that cries wolf gets ignored.

### Chat streaming — a concurrency bug found by using it

Jack's first real reply crashed the wizard with "collection was modified; enumeration operation may
not execute". A streamed reply is rewritten on every chunk from the stream's continuation thread while
the UI enumerates the same list to render it, and `List<T>`'s indexer setter bumps the version counter.
`ChatSession` now mutates under a lock and readers take a snapshot; a test drives 20,000 concurrent
rewrites against a reader to hold that.

The same session also showed code blocks and tables arriving as raw Markdown. Two causes: the custom
`marked` renderer hooks were written against an older API than the vendored version, and — the real
one — Blazor Hybrid's `IJSRuntime` is **not** an `IJSInProcessRuntime`, so the synchronous render path
never ran and silently fell back to plain text. Rendering is now async and enhancement happens on the
sanitised DOM rather than through renderer hooks, which is version-independent.

### Firmware — rollback paths exercised

Success, health-check failure with rollback, and a failed rollback are each covered by tests using an
in-memory channel with injectable failures.

**A bug this caught.** Version comparison treated `1.2.3-beta` as equal to `1.2.3`, which would have let
a pre-release satisfy a `MinimumCurrentVersion` gate that exists specifically to require the released
build. Pre-release suffixes now sort before their release, per semver.

## Test coverage by area

| Area | Tests | Focus |
|---|---|---|
| Wire format | 15 | Struct sizes, byte-identity, round-trips, CRC validation |
| Core | 33 | CRC, angle wrapping, gimbal lock, safety limits, options |
| CDR codec | 10 | Alignment, string terminators, overflow, endianness rejection |
| Transport | 11 | Delivery, fan-out, drop-oldest, malformed frames, real multicast |
| Trajectories | 10 | Endpoint rest, limit compliance, synchronisation, interpolation |
| Arm control | 6 | Goal convergence, feedback absence, dual-arm timing |
| Real-time loop | 5 | Rate accuracy, throwing callbacks, monotonic context |
| ROS 2 | 5 | Round-trips, frame conversion |
| Telemetry | 7 | Pack voltage, cell imbalance, fall detection, motor modes |
| Firmware | 12 | Install, skip, rollback, verification, version ordering |
| Robot rigs | 40 | Joint counts, tree shape, axis normality, wheel joints, standing height |
| Simulation | 26 | Gait, odometry, contacts, thermal equilibrium, CRC validity, wrap safety, sport service |
| Templates | 71 | Catalogue integrity, scaffolding, reference resolution, name validation |
| Wizard tooling | 47 | Expression evaluator, path-traversal refusal, sessions, attachments, prompts, SDK lint |

## Not yet done

### Requires hardware

Nothing below can be closed without a robot:

- The native shim has never been compiled against `unitree_sdk2` or run.
- CRC acceptance is verified by construction, not by a robot accepting a command.
- Loop timing has not been measured on Jetson or Raspberry Pi.
- The safety envelope's defaults are conservative estimates, not measured motor limits.
- Sport API availability varies by platform and firmware; the constants are documented, not probed.

### Known gaps

| Gap | Impact |
|---|---|
| `unitree_hg` messages not implemented | No low-level control for G1/H1; high-level works. The simulator publishes only `rt/sportmodestate` for humanoids and says so |
| No concrete `IFirmwareChannel` | OTA orchestration exists; the transport to the robot does not |
| No camera or audio streams | Topics are named; no decoding |
| ROS 2 bridge does not republish point clouds | Decoding exists in `Unitree.Net.Sensors` |
| No NuGet packaging or CI | Projects are marked packable; nothing publishes them |
| Deployment never run against a robot | Publish, SFTP copy and systemd install are written but unexercised, in both the wizard and the extension |
| The extension has no automated tests | It is verified by running it; the logic it wraps is tested in C# |
| Simulator is kinematic, not dynamic | Motion is generated, not integrated. It will not tell you whether a controller is stable |
| Both desktop shells are Windows-only | WPF and WebView2. The engines underneath are cross-platform |

### Deliberate non-goals

- **Obstacle avoidance in `WaypointNavigator`.** It is a proportional controller over drifting odometry.
  Use the robot's own avoidance service, or Nav2 through the bridge.
- **`ManagedMulticastTransport` reaching firmware.** It is not RTPS and is not intended to be.
- **LLMs making safety decisions.** The safety envelope is deterministic code and stays that way.

## Next steps

1. Build the native shim against a real `unitree_sdk2` checkout and confirm discovery.
2. Confirm on hardware that a `LowCmd` produced here is accepted — the one assumption everything rests on.
3. Measure loop jitter on the actual robot host.
4. Implement `unitree_hg` for humanoid low-level control, so the simulator can publish humanoid
   low-level state and the wizard can generate G1 low-level code.
5. Set up CI, and run `tools/Unitree.Net.TemplateCheck` in it — the template catalogue drifts silently
   otherwise.
6. Exercise the deploy path against a real Jetson module, from both the wizard and the extension.
7. Publish the VS Code extension.

---

Unitree.Net tooling — simulator, Robot Wizard and VS Code extension — dibuat oleh
**Gravicode Studios**, dipimpin **Kang Fadhil**.
