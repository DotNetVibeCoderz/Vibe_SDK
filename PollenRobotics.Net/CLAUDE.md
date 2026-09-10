# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`PollenRobotics.Net` — an unofficial .NET 10 SDK for Pollen Robotics hardware (Reachy Mini,
MicroDuck, Reachy 2), plus an Avalonia gallery, a 3D simulator and an LLM-assisted code editor.
`requirements.md` (Indonesian) is the original spec; `PLAN.md` is the roadmap and `PROGRESS.md`
tracks what is verified.

The solution builds clean and 40 tests pass. **Nothing has been run against physical hardware** —
read the "Written but not verified" section of `PROGRESS.md` before claiming anything works on a
robot.

## Commands

```bash
dotnet build PollenRobotics.Net.slnx

# Tests are an xUnit v3 executable. `dotnet test` does NOT work: the .NET 10 SDK no longer bridges
# xUnit v3 through VSTest, and the error it gives does not say so.
dotnet run --project tests/PollenRobotics.Net.Tests
dotnet run --project tests/PollenRobotics.Net.Tests -- --filter-class "*StewartPlatformTests"

# Scaffolds all 20 templates and compiles them. Run after any change to the SDK's public surface —
# the catalogue drifts silently otherwise. Takes about 90 seconds.
dotnet run --project tools/PollenRobotics.Net.TemplateCheck
dotnet run --project tools/PollenRobotics.Net.TemplateCheck -- --filter mini. --keep --verbose

# The three desktop apps. None needs a robot.
dotnet run --project apps/PollenRobotics.Net.Gallery
dotnet run --project apps/PollenRobotics.Net.Simulator
dotnet run --project apps/PollenRobotics.Net.Wizard -- --project <folder>

# First thing to run whenever a robot "won't connect". Needs no robot.
cd apps/PollenRobotics.Net.Cli && dotnet run -- doctor

# Refreshes the vendored Reachy 2 protos from Pollen.
pwsh tools/sync-protos.ps1
```

## Layout

```
src/      Core · Kinematics · Transport · ReachyMini · MicroDuck · Reachy2
          Simulation · Ml · Ai · Ui · Wizard.Core
apps/     Gallery · Simulator (Avalonia+Blazor+three.js) · Wizard · Cli (`pollen`)
tools/    TemplateCheck — compiles every template; sync-protos.ps1
tests/    PollenRobotics.Net.Tests (xunit.v3 + Shouldly)
docs/     architecture, getting-started, per-robot guides, kinematics, simulator, wizard, safety
          docs/images/ holds the screenshots used by README.md and the docs
```

Dependency order: `Core` → `Kinematics`/`Transport` → the three robot packages → `Simulation` →
`Ai`/`Wizard.Core` → apps. `Core` depends on nothing but logging abstractions.

## The load-bearing design decision

**Every robot client talks to a transport interface, never to a socket.** `ReachyMiniClient` takes
an `IReachyMiniTransport`; the daemon transport and the in-process simulation transport both
implement it, and the client cannot tell them apart. That is what makes "the same code runs on the
robot and in the simulator" true rather than aspirational, and it is why every sample, gallery demo
and template is written against the interface.

The simulated transports keep the asynchrony and add ~2 ms of latency deliberately. Everything they
do could complete synchronously — and then code that works in the simulator would deadlock the first
time it met a real network round trip.

## The things that will bite you

**`OutputType=WinExe` on a `Microsoft.NET.Sdk.Web` project silently drops the framework's static web
assets.** wwwroot still serves, `_framework/blazor.web.js` 404s, and the page renders as a static
prerender where no event handler and no `OnAfterRenderAsync` ever runs. The simulator is `Exe` and
hides its own console. This cost hours; it reports nothing anywhere.

**`@rendermode` belongs on the page component, not on `RouteView`.** On `RouteView` the framework
serialises `RouteData`, which carries a `System.Type`, and the page returns HTTP 500.

**Use `MapStaticAssets()`, not `UseStaticFiles()`, and call `UseStaticWebAssets()` explicitly.** The
framework's own files come from the endpoints manifest, and ASP.NET Core only wires that up
automatically in Development — a desktop tool runs in Production.

**An Avalonia control derived from `ContentControl` gets no template.** Control themes match on the
exact runtime type, so it draws nothing — no error, no warning. Override
`StyleKeyOverride => typeof(ContentControl)`. This made the whole chat thread render blank with the
conversation intact in memory.

**Resource lookup walks the logical tree, so a control built with an object initializer resolves
every theme brush to null.** `new MarkdownView { Markdown = ... }` renders before it has a parent.
Re-render on `OnAttachedToLogicalTree`, and never assign a null brush to `Foreground` — that paints
nothing rather than falling back.

**`NativeControlHost` needs a manifest with a `supportedOS` list**, or Avalonia refuses to create
the child window and the whole layout fails, not just the control.

**Do not use `PeriodicTimer` or `Task.Delay` for control loops.** Both are quantised to the OS
scheduler (~15.6 ms on Windows). `RealtimeLoop` hybrid-waits: sleep above one quantum of slack, then
spin. It burns a core deliberately, and it computes deadlines from the loop start so one slow tick
does not push every later one out.

**The Stewart solver has two solutions per branch, and no home reference of its own.** Taking
whichever `asin` returns makes a branch angle jump by most of a revolution mid-trajectory. A rotary
platform has no reason for its horn angles to be zero at the home pose, so the solver calibrates
there and reports relative to it — without that a neutral head reads as 118 degrees of travel.

**Never fold a continuous offset back into the state it modifies.** The idle breath was written into
the head pose each tick instead of applied as an offset; the pose integrated out of the workspace,
the solver threw into a `catch`, and every branch reported zero while the status said "moving".

**Templates are hand-written against the SDK and nothing recompiles them.** Run `TemplateCheck`
after any public API change. Four of twenty failed the first time it ran.

**A wizard-generated folder must contain exactly one `*.*proj` file.** MSBuild resolves a bare
`dotnet build` or `dotnet run` by globbing that pattern, so the metadata file is `.pollen.json` and
must never be named `.pollenproj` again. The wizard itself cannot catch this — it always passes the
`.csproj` path explicitly — but it is the first command every generated README tells the user to run.

**`Console.SetCursorPosition` throws when stdout is redirected.** Guard in-place terminal updates
with `Console.IsOutputRedirected`.

**Raw string literals need more quotes than anything they contain.** Several templates emit code
containing `"""`, so their own literal is `""""`.

**Avalonia 12 replaced the clipboard `DataObject` API.** Use `DataTransfer` + `DataTransferItem`
with `IClipboard.SetDataAsync`.

## Conventions

- Standard C# naming; `.editorconfig` enforces it. File-scoped namespaces.
- Central package management — versions go in `Directory.Packages.props`, not in csproj files.
- **NuGet IDs carry a `Gravicode.` prefix; assembly names and namespaces do not.** The prefix is set
  once in `PackageIdPrefix` (Directory.Build.props, applied by Directory.Build.targets) and mirrored
  in `ProjectScaffold.PackageIdPrefix` for what generated projects reference. Never prefix a
  namespace — every `using` in the docs, templates and samples assumes they are unprefixed.
- Public API carries XML docs. `GenerateDocumentationFile` is on.
- Safety violations **throw** by default; clamping is opt-in via
  `RobotSafetyOptions.ClampInsteadOfThrow`. Do not switch a caller to `Permissive` to make an error
  go away.
- Resolve a joint **by name** through `RobotDescription`, never by a hard-coded index. A variant
  with a different joint count shifts every index after the one that changed.
- Comments explain *why*, especially where behaviour looks arbitrary but is protocol- or
  safety-motivated. The existing code sets the density; match it.
- Every subsystem logs to a shared `RobotLogSink`. In a desktop app there is no console anyone will
  look at, so a failure that does not reach the log panel is invisible.

## The Reachy 2 protos are vendored, not written

`src/PollenRobotics.Net.Reachy2/Protos/*.proto` are Pollen's own files, copied verbatim from
`reachy2-sdk-api` under Apache 2.0. Field numbers must match the robot exactly, so they are never
hand-edited — refresh with `tools/sync-protos.ps1`.

This is also why Reachy 2 has no simulation transport: its client talks to generated gRPC stubs
rather than to an interface of ours.

## What is approximate, and where it says so

Pollen publishes mechanisms and envelopes but not CAD, so some dimensions are derived rather than
known. Each is documented at the point it is used and summarised in `docs/kinematics.md`:

- Reachy Mini's Stewart geometry — link lengths derived from the published envelope by
  `StewartGeometry.ForEnvelope`, not guessed
- Reachy 2's arm link lengths — right proportions, not Pollen's URDF
- MicroDuck's joint table and its gait — a scripted gait standing in for a learned policy
- The 3D rigs — primitives, because no meshes ship under a licence this project could vendor

The solvers themselves are exact. On hardware, prefer the robot's own kinematics.

## Documentation to maintain

`docs/`, the bilingual `README.md` (English + Indonesian), `PLAN.md`, `PROGRESS.md`. All four are
mandated by the spec. Update `PROGRESS.md` when something is genuinely verified — it distinguishes
"builds" from "tested against hardware", and that distinction is the point.

Screenshots live in `docs/images/` and are referenced from both README sections and the per-tool
docs. Recapture them when the UI changes materially.

## Attribution

Applications and documentation credit **Gravicode Studios**, led by **Kang Fadhil** — in the README,
the docs, the wizard's About dialog and the status bar of all three desktop apps. Keep that in place.

## Credentials

Outside the repository, one level up: `..\testkey.txt` (LLM keys for end-to-end testing of Jack) and
`..\PackageCredentials.txt` (NuGet). Read them when a task needs them; never copy their contents into
tracked files. For local testing, pass them as `Ai__*` environment variables rather than editing
`appsettings.json`.
