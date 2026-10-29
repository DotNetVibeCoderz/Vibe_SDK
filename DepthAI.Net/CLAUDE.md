# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET 10 SDK for **DepthAI API V3** (Luxonis OAK cameras) plus its tooling ecosystem:
a CLI, `dotnet new` templates, a VS Code extension, sample apps, and **Jack The Code Bender** —
an Avalonia desktop wizard that generates computer vision apps with LLM assistance.

`requirements.txt` is the original product spec (written in Indonesian). It is the source of
truth for scope, not a dependency list.

## Commands

```powershell
dotnet build DepthAI.Net.slnx                      # whole solution
dotnet test                                        # all tests
dotnet test tests/DepthAI.Net.Core.Tests
dotnet test --filter "FullyQualifiedName~YoloParser"

dotnet run --project src/DepthAI.Net.Wizard        # the wizard app
dotnet run --project src/DepthAI.Net.Cli -- info   # CLI without installing it

dotnet pack src/DepthAI.Net.Templates              # -> artifacts/nuget
dotnet new install artifacts/nuget/DepthAI.Net.Templates.0.3.0.nupkg
```

Windows environment: PowerShell 5.1 is primary (no `&&`, no ternary); a Git Bash tool is also
available. The solution is `.slnx` (the .NET 10 XML format), not `.sln`.

## The native runtime — read this first

The SDK has two backends behind one interface:

- `SimulationBackend`: synthetic colour, depth, and neural tensors. Fully working everywhere.
- `NativeBackend`: P/Invokes into `depthai-c` (`native/depthai-c/`), a C ABI shim over
  depthai-core 3.11. **Working on Windows x64**, shipped as `DepthAI.Net.Runtime.win-x64`.
  No Linux runtime yet (Luxonis ships no Linux C++ prebuilt; depthai-core must be built from source).

`native/depthai-c/build.ps1` builds it: it downloads Luxonis' prebuilt win64 depthai-core,
pulls `opencv_world4130.dll` from the official OpenCV installer (the prebuilt imports it but does
not ship it), compiles the shim with VS's MSVC/CMake/Ninja, and stages everything plus an
app-local MSVC runtime in `artifacts/native/win-x64`. Core's csproj then copies those DLLs into
every project referencing it, so CLI, Wizard, samples and tests in the repo use hardware once built.

Rules for touching it:
- `depthai_c.h` and `NativeMethods.cs` mirror each other; change both or neither.
- Pull model only: depthai callbacks fill a native ready queue, .NET polls. Never call managed code from native.
- NNs run as raw `NeuralNetwork`; tensors are decoded by the .NET parsers so simulation and hardware match.
- depthai v3 closes the device when a pipeline stops; the shim reopens it in `stopSession`.
- Verified on the OAK-1 below: YOLOv6-nano boxes match depthai's on-device parser to 3 decimals.
  Stereo and IMU mappings are untested (the OAK-1 has neither); do not claim otherwise.

**What does work with real hardware:** `UsbDeviceScanner` (Core/Devices) enumerates OAK devices
straight off the USB bus via `cfgmgr32` on Windows and sysfs on Linux — no depthai-core needed.
It reports presence, boot stage, and (once booted) the real MxId. Verified against a physical
OAK-1: MxId `14442C10011298CD00`, IMX378 sensor, single colour camera, no stereo.

Registry enumeration was tried first and rejected: stale keys persist after unplug, and OAK
appears under two product IDs (unbooted `2485`, booted `F63B`), so one camera read as two.

## Upstream sync

Synced with depthai-core **v3.11.0** (built on v3.8.0). `docs/*/upstream-sync.md` lists what was
ported and what was deliberately not (device/beta nodes, multi-device pipelines). To sync again,
diff upstream tags (`git diff v3.11.0 <new> -- include src/utility protos`) and port host-side
behaviour only.

`.dai` files (`Core/Messages`) are upstream's protobuf envelope, hand-encoded to keep Core
dependency-free. The fixtures in `tests/.../Fixtures/depthai-3.11` were written by depthai 3.11
for Python. Regenerate them with a real depthai install, never by hand.

The NuGet icon is `icon.png`, drawn by `tools/branding/make_icon.py`. Edit the script, not the PNG.

## Architecture

```
src/
  DepthAI.Net.Core/            SDK — no imaging dependencies at all
    Backends/                  IDepthAiBackend, DevicePacket (the narrow contract)
    Interop/                   P/Invoke + NativeBackend (pull model, not callbacks)
    Simulation/                SyntheticScene, SimulationPlan
    Pipelines/                 node graph, fluent builder, JSON round-trip, presets
    Streaming/                 Frame, ImageFrame, DepthFrame, IObservable plumbing
    Inference/                 NeuralModel, parsers (YOLO, MobileNet-SSD, classification, segmentation)
    Imaging/                   PixelConverter, DepthColorizer, FrameOverlay (dependency-free)
    Devices/                   DeviceInfo, DeviceWatcher, UsbDeviceScanner
  DepthAI.Net.Imaging.*        thin adapters: ImageSharp, SkiaSharp, SystemDrawing
  DepthAI.Net.Runtime.win-x64/ no code; packs artifacts/native/win-x64 as runtimes/win-x64/native
  DepthAI.Net.Cli/             Spectre.Console.Cli
  DepthAI.Net.Templates/       dotnet new template package
  DepthAI.Net.Wizard.Core/     project system, 16 CV templates, Semantic Kernel layer
  DepthAI.Net.Wizard/          Avalonia UI
native/depthai-c/              C ABI shim (C++17) + build.ps1
samples/                       generated from the wizard's template catalogue
tools/vscode-depthai/          plain JavaScript, no build step
```

### Invariants worth preserving

**Frames are pooled and disposed after the callback returns.** Anything that outlives the
callback must `Clone()`. Using a disposed frame throws rather than reading recycled memory.

**Depth `0` means "no measurement", not zero distance.** `GetDistanceMeters` returns `float?`
so the case cannot slip through silently. `ToMeterMatrix` uses `NaN`.

**Core must not take an imaging dependency.** Adapters exist so apps can pick one stack.
`PixelConverter`/`DepthColorizer`/`FrameOverlay` stay in Core and emit plain BGR bytes.

**The simulation backend is not a stub.** It emits tensors in genuine MobileNet-SSD and YOLO
layouts, decoded by the same parsers hardware uses. Keep it that way — it is why tests and
samples are meaningful without a camera.

**Model payloads never go into pipeline JSON**, only names. `PipelineLoadOptions.ModelResolver`
reattaches them.

**Wizard-generated projects reference the SDK two ways.** `ProjectScaffolder` emits
`PackageReference` normally, but `ProjectReference` when it detects it is running inside this
repo (`FindSdkRepositoryRoot` looks for `DepthAI.Net.slnx` or `.sln`). Without that, generated
projects cannot build here because the packages are unpublished.

### Wizard specifics

- Settings live in `app.config` (the spec asked for it). API keys are read from environment
  variables first and are **not** written back to the file if that is where they came from.
- Semantic Kernel has no official Anthropic connector, so `AnthropicChatCompletionService`
  is hand-written against the Messages API, including tool-use loops.
- The visual system is "Depth Field": accents sampled from the same Turbo colour map as
  `DepthColorizer`, warm = near = primary action. The signature element is the depth ribbon
  in the status bar. Invoke the `frontend-design` skill before reshaping the UI.
- XAML comments must not contain `--` (XML forbids it). Decorative dashed separators break
  the Avalonia build with a confusing `AVLN1001`.

## Conventions

- **Documentation and user-facing strings are Indonesian**; code identifiers and XML doc tags
  are English. Docs ship in both languages under `docs/id/` and `docs/en/`.
- **Attribution:** docs and the About screen credit *Gravicode Studios, dipimpin Kang Fadhil*.
- Comments explain *why*, not *what* — several exist specifically to record rejected
  alternatives (registry vs cfgmgr32, callbacks vs polling, IoU vs containment for PPE).
