# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET 10 SDK wrapping Unitree SDK 2 for Unitree robots — quadrupeds (Go2, B2) and humanoids (G1, H1,
R1). `requirements.md` (Indonesian) is the original spec; `PLAN.md` tracks the roadmap and `PROGRESS.md`
the current state, including what has and has not been validated.

The solution builds clean and 120 tests pass. **Nothing has been run against real hardware** — see the
"Not yet done" section of `PROGRESS.md` before claiming anything works on a robot.

## Commands

```bash
dotnet build Unitree.Net.slnx
dotnet test  Unitree.Net.slnx
dotnet test  Unitree.Net.slnx --filter "FullyQualifiedName~MessageWireFormatTests"
dotnet format

# Develop without a robot: start a simulator, then point anything at it.
dotnet run --project apps/Unitree.Net.Simulator                 # 3D, Windows-only
dotnet run --project samples/Unitree.Net.Samples.VirtualRobot   # headless
dotnet run --project apps/Unitree.Net.Cli -- status
dotnet run --project apps/Unitree.Net.Cli -- monitor
dotnet run --project apps/Unitree.Net.Dashboard

# Code editor with templates and the Jack assistant.
dotnet run --project apps/Unitree.Net.Wizard

# Scaffolds all 16 wizard templates and compiles them. Run after any change to the SDK's
# public surface — the catalogue drifts silently otherwise. Takes about two minutes.
dotnet run --project tools/Unitree.Net.TemplateCheck

# VS Code extension. Build the CLI in Release first or every command falls back to `dotnet run`,
# which rebuilds on each call and blocks on the MSBuild lock.
dotnet build apps/Unitree.Net.Cli -c Release
cd tools/vscode-unitree && npm install && npm run compile
code --extensionDevelopmentPath=.

# First thing to run whenever a robot "won't connect". Needs no robot.
dotnet run --project apps/Unitree.Net.Cli -- diagnose
```

The native shim is built separately with CMake and is **not** part of `dotnet build`; see
`native/README.md`.

## Layout

```
src/      15 libraries, layered bottom-up (see docs/architecture.md)
apps/     Unitree.Net.Cli · Unitree.Net.Dashboard (Blazor Server)
          Unitree.Net.Simulator · Unitree.Net.Wizard (WPF + BlazorWebView, Windows-only)
samples/  VirtualRobot · LowLevelControl · PatrolRobot
tools/    Unitree.Net.TemplateCheck — compiles every wizard template
          vscode-unitree — VS Code extension (TypeScript, npm)
tests/    Unitree.Net.Tests (xunit.v3 + Shouldly)
native/   unitree_net_native — Cyclone DDS shim (C ABI, CMake)
docs/     architecture, getting-started, dds-networking, safety, low-level-control,
          navigation, ai-workflow, ros2-bridge, simulator, wizard, vscode-extension
```

Dependency order: `Core` → `Messages` → `Dds` → `Control` → everything else. `Interop` sits beside
`Dds`; `Firmware` depends only on `Core`. `Simulation` sits on `Dds` + `Messages`; `Wizard.Core` on
`Ai` + `Core`.

**The two WPF apps are shells.** Everything worth testing lives in `Unitree.Net.Simulation` and
`Unitree.Net.Wizard.Core`, which are plain `net10.0`. Keep it that way — the deployment target is
Linux, and the shells exist because the user asked for WPF.

## The things that will bite you

**The CDR body must stay byte-identical to the struct layout.** Unitree computes `crc32_core` over the
C++ struct's raw memory. This SDK computes it over the same struct, then serialises to CDR separately.
Those only agree because the two encodings coincide. `MessageWireFormatTests` asserts this directly — if
it fails, the robot silently ignores every command with no diagnostic anywhere. Do not "simplify" the
codec without re-running it.

A subtlety already fixed once: a CDR struct aligns to its most-aligned member. `MotorCmd` starts with a
byte but contains floats, so it needs an explicit `Align(4)` at the start of `Write`/`Read`, or the
whole 20-element array lands two bytes early.

**Do not use `PeriodicTimer` or `Task.Delay` for control loops.** Both are quantised to the OS timer
(~15.6 ms on Windows). `RealtimeLoop` hybrid-waits: sleep only above one scheduler quantum of slack,
then spin. This burns a core deliberately. Verified at 500 Hz with 3 µs mean jitter.

**`ManagedMulticastTransport` is not RTPS.** It carries CDR in Unitree.Net's own framing and cannot
reach robot firmware. Real hardware needs `CycloneNative` plus the native shim.

**Low-level commands do nothing until the sport service releases the motors.** No error is reported.
`BeginLowLevelSessionAsync` handles the sequence.

**The simulator answers services, not just telemetry, and starts resting.** `SimulatedServiceHub`
responds on `rt/api/{sport,motion_switcher,robot_state}/request`. Before it existed, any application
that commanded motion timed out on its first `StandUpAsync` — nine of the sixteen templates. The robot
starts resting on purpose: standing it up automatically would let an application skip `StandUp` and
still work here, then fail silently on hardware.

**`VelocityStream` does not expire a command the caller is holding.** It once timed its watchdog from
the caller's last assignment, so holding one velocity for a second — every template does — stopped the
robot every 500 ms. The pump is the publisher; if the process dies the requests stop and the robot
stops on its own. Client-side expiry is opt-in via `StartVelocityStream(commandTimeout:)`.

**Escape runtime strings before putting them in Spectre.Console markup.** Transport names contain
square brackets, which is markup syntax — this crashed the CLI once.

**A `RobotRig` is one description of a robot, used by both the kinematics and the 3D viewport.** It
asserts its own joint count against `RobotModelInfo.GetActuatedJointCount`. That check has caught two
real errors: H1 modelled with 21 joints (its ankle is pitch-only and its arms have no wrists, hence
19) and the wheeled variants modelled without their four drive wheels. Never resolve a humanoid joint
index by hard-coding — H1's leg layout shifts every index after it. Look it up by link name.

**Wizard templates and `SdkPlugin`'s reference are hand-written against the SDK's public surface, and
nothing recompiles them by accident.** After changing a public API, run
`tools/Unitree.Net.TemplateCheck`. Every one of the 16 templates failed the first time it ran, against
an API that had been remembered rather than read.

**A setting whose default is meaningful must not be written back verbatim.** The wizard saved the
resolved system prompt into `app.config` on every close, so the file froze whatever the built-in
persona said that day and every later improvement to it silently never reached anyone who had run the
app once. `Ai.SystemPromptIsCustom` now records whether the operator actually edited it; with the flag
false or absent, the code's default wins.

**When a public member is renamed, add the old name to `SdkLint`.** Telling the assistant to look the
API up does not work — it skips the lookup precisely when it is confident, which is when it is wrong.
A check in the tool result does work: Jack rewrote the same file six times until the lint went quiet,
then it compiled. Keep the table in step with the SDK; that is the whole cost of it being useful.

**BlazorWebView needs an OS version in the target framework.** `net10.0-windows` builds fine and then
dies at runtime on "Could not load Microsoft.Windows.SDK.NET" — it needs
`net10.0-windows10.0.19041.0` for the CsWinRT projections. `UseWPF` also swaps the implicit-using set
for the WPF one, which drops `System.IO`.

**A Blazor Hybrid `IJSRuntime` is not an `IJSInProcessRuntime`.** The JavaScript runs in the same
process, but interop still goes over the WebView's message channel. Code that casts and falls back
compiles, runs, and silently takes the fallback forever — that is how the chat panel rendered every
code block as raw Markdown.

**A Blazor Web App without a render mode is static, and looks completely normal.** The dashboard
shipped that way: buttons rendered, the connection badge said Connected, and not one event handler was
attached. `App.razor` sets `<Routes @rendermode="InteractiveServer" />`; keep it. "It renders" is not
evidence that a Blazor page works.

**`unitree probe` and `unitree stream` must print nothing on stdout but JSON.** The console logger
writes there too, and one info line is enough to make the output unparsable. Logging is cleared for
those commands; keep it that way when adding more machine-readable ones.

**Anything mutated while the UI renders it needs a snapshot.** `List<T>`'s indexer setter bumps the
version counter, so rewriting a streamed chat message from a continuation thread throws "collection
was modified" in the middle of a render. `ChatSession` locks and hands out copies.

## Conventions

- Standard C# naming; `.editorconfig` enforces it. File-scoped namespaces.
- **No allocation on hot paths** (500 Hz control loop, DDS receive). Use `Span<T>`, `[InlineArray]`,
  stack buffers under 2 KB, `ArrayPool<byte>` above. No LINQ in per-tick code.
- Safety violations **throw** by default; clamping is opt-in via `RobotSafetyOptions.ClampInsteadOfThrow`.
- Public API carries XML docs. `GenerateDocumentationFile` is on, so missing `<param>` tags warn.
- Central package management — add versions to `Directory.Packages.props`, not to csproj files.
- Comments explain *why*, especially where behaviour looks arbitrary but is protocol- or
  safety-motivated. The existing code sets this density; match it.

## Cross-platform

Development happens on Windows; deployment targets Ubuntu 20.04/22.04 on x86_64 **and ARM** (Jetson,
Raspberry Pi). The dashboard is Blazor rather than WPF for exactly this reason. Anything
platform-specific belongs behind an abstraction.

## Adding a DDS message type

Three places must agree, or endpoint creation fails with `UN_UNKNOWN_TYPE`:

1. `Unitree.Net.Messages` — implement `ICdrSerializable<T>`.
2. `CycloneDdsTransport.ResolveTypeName` — map the topic to the DDS type name.
3. `native/unitree_net_native/src/unitree_net_native.cpp` — add the generated header and a row in
   `find_descriptor()`.

## Razor gotchas hit repeatedly

Both WPF apps host Blazor, and Razor's attribute parser is unforgiving:

- A string literal inside a double-quoted attribute does not parse. `@onclick="Do("x")"` and
  `placeholder="@(cond ? "a" : "b")"` both fail with a confusing `CS1525`. Use a single-quoted
  attribute or lift the expression into a property.
- Vendored JS is served from `wwwroot` by static web assets. Relative module imports resolve against
  the *importing file*, not the page — `./lib/...` from `/js/viewport.js` becomes `/js/lib/...`.
- Since three.js r167 `three.module.js` re-exports `./three.core.js`. Vendoring only the former gives
  "Failed to fetch dynamically imported module", which reads like a missing file.
- Both apps route browser errors into their own log panel. In a desktop app there is no console anyone
  will look at, so a JavaScript failure is otherwise completely silent.

## Documentation to maintain

`docs/`, bilingual `README.md` (Indonesian + English), `PLAN.md`, `PROGRESS.md`. All four are mandated
by the spec. Update `PROGRESS.md` when something is genuinely verified — it distinguishes "builds" from
"tested against hardware", and that distinction is the point.

## Attribution

The desktop tooling (simulator, Robot Wizard, Jack The Code Bender) is credited to **Gravicode
Studios**, led by **Kang Fadhil**, in the README, the docs, the wizard's About dialog and the
simulator's sidebar. Keep that credit in place.
