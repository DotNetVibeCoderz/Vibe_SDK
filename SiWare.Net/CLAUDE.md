# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

SiWare.NET: a .NET 10 SDK, Rust DSP library, CLI, and web workbench for Si-Ware spectral sensors. Requirements live in `SiWareNet.txt` (partly in Bahasa Indonesia); `AGENTS.md` holds repo conventions. Keep `PLAN.md` (roadmap) and `Progress.md` (checklist + research notes) updated when milestones change. User docs are bilingual: `README.md`, `docs/en/*`, `docs/id/*` — change both languages together.

This project lives in the `Vibe_SDK` monorepo as `SiWare.Net/` (sibling SDKs: `DepthAI.Net`, `PollenRobotics.Net`, …). Run every command from this folder; it has its own solution, `global.json`, and build props. The monorepo root `.gitignore` already ignores `artifacts/` and `*.nupkg`.

## Commands

```sh
dotnet build SiWare.Net.slnx
dotnet test SiWare.Net.slnx
dotnet test tests/SiWare.Sdk.Tests --filter "FullyQualifiedName~ProcessingTests"   # single class/test
dotnet format SiWare.Net.slnx

dotnet run --project samples/SiWare.Cli -- help            # demo scan discover process info dataset analyze calibrate plot explain
dotnet run --project samples/SiWare.Workbench              # http://localhost:5069 by default

# Rust DSP (run inside native/siware-dsp)
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test
cargo build --release      # required before .NET can use NativeDsp

dotnet pack src/SiWare.Sdk -c Release -o artifacts/packages   # NuGet: Gravicode.SiWare.Sdk
node scripts/render-icon.mjs      # docs/assets/siware-net-icon.svg -> src/SiWare.Sdk/icon.png
node scripts/cli-screenshot.mjs   # docs/screenshots/cli.png from real CLI output
node scripts/insight-screenshot.mjs  # live AI insight check; needs a Workbench on :5091 with an LLM configured
```

- The package bundles `runtimes/win-x64/native/siware_dsp.dll` (only if the release build exists at pack time) and `scripts/vendor_bridge.py` as a content file. When repacking the same version for a local consumer, clear `~/.nuget/packages/gravicode.siware.sdk` first. Public publication is gated by `docs/en/hardware-validation.md`.
- Workbench cancellation maps to HTTP 409, never 408 — browsers auto-resend 408 responses, which would rerun a cancelled acquisition.

- `Directory.Build.props` sets `TreatWarningsAsErrors=true` for every project — warnings break the build.
- `Directory.Build.targets` copies `native/siware-dsp/target/release/siware_dsp.{dll,so,dylib}` into every project's output **only if it already exists**. Build the Rust crate in release first, otherwise `NativeDsp.IsAvailable` is false and native integration tests fail. For managed-only tests use `dotnet test --filter "FullyQualifiedName!~NativeDspTests"`; native API calls throw `PlatformNotSupportedException`. The crate needs Rust 1.87+ (`usize::is_multiple_of`).
- Calibration (`SpectralProcessing.Match`) compares recorded acquisition settings as well as device, provenance, and grid; spectra without settings (legacy JSON, CSV) only match each other. See `docs/review.md`.
- Tests use xUnit and run without hardware.
- UI smoke test (Playwright, installed locally under ignored `.tools/ui/`): start the workbench, then `node scripts/ui-smoke.mjs`. It defaults to `http://127.0.0.1:5088` (not the launchSettings port) — run the workbench with `--urls http://127.0.0.1:5088` or set `SIWARE_TEST_URL`. It also writes screenshots to `docs/screenshots/`.
- Vendor bridge: `.tools/python310/python.exe scripts/vendor_bridge.py --self-test` (no vendor code loaded); `.tools/python310/python.exe scripts/vendor_probe.py --inspect` (imports the installed `pyDriver.pyd`, read-only metadata).

## Architecture

**`src/SiWare.Sdk`** — the single library. Everything hangs off `ISpectrometer` (`Devices.cs`): `ConnectAsync` / `AcquireAsync(MeasurementKind, ScanOptions)` / `IAsyncDisposable`. Two implementations:
- `SimulatedSpectrometer` — deterministic (seeded) fake; default for CLI and workbench. Its spectra are flagged `IsSimulated` and the UI shows a "SIMULATED DATA" badge. Never present simulated output as real readings.
- `VendorSpectrometer` — talks to the real Si-Ware driver indirectly. The vendor driver is a CPython 3.10 x64 native extension (`C:\Program Files (x86)\Si-Spect Platform\Analyze\pyDriver.pyd`), not a .NET or C API. So the SDK spawns `scripts/vendor_bridge.py` under a Python 3.10 interpreter and exchanges JSON-lines requests (`discover`, `acquire`) over stdin/stdout, with a timeout and process kill since the binding has no shutdown method. Configured by `VendorBridgeOptions` (see `scripts/vendor-config.example.json`); scan-time unit and mode must be explicitly confirmed by the user, not guessed. Only Sample (`run_psd`) and Reference (`run_background`+`read_background`) are supported; Dark is not. Point counts are limited to the vendor `CommonWaveNumber` grid (65…4096). Wavenumbers (cm⁻¹) are converted to nm and sorted.

`Spectrum` (immutable, wavelength + values + kind + metadata) and `SpectrumFile` (JSON/CSV I/O, shared `JsonOptions`) are the data model. `SpectralProcessing` is the managed implementation of absorbance/SNV/smoothing; `NativeDsp` exposes the same operations via P/Invoke to the Rust ABI v1 and **never silently falls back** to managed code — callers choose explicitly.

**`native/siware-dsp`** — dependency-free Rust `cdylib` with C header `include/siware_dsp.h`. ABI contract (status codes 0/1/2, transactional output, disjoint output buffer, panics caught at the boundary) is documented in its `README.md`; keep the C# `DllImport` signatures, header, and Rust exports in sync and bump `siware_dsp_abi_version` on breaking changes.

**`samples/SiWare.Workbench`** — ASP.NET Core minimal API + static `wwwroot` (vanilla JS/CSS, strict CSP: no inline scripts/styles). `WorkbenchSession` is a singleton holding the connected `ISpectrometer`, dark/reference/sample history, and processing. Setting env var `SIWARE_VENDOR_CONFIG` to a config JSON swaps in `VendorSpectrometer`. POSTs from foreign origins are rejected; exceptions map to HTTP codes in the middleware in `Program.cs`. UI is bilingual (EN/ID, stored in `localStorage` `siware-language`).

**Opt-in packages** (the core SDK must stay dependency-free):
- `src/SiWare.Analysis` — GravicodeScience (GraviNum `NdArray`, GraviLearn PCA/metrics/KFold, GraviFrame). `SpectralDataset` (one grid, same kind/provenance/acquisition), `Preprocessing` (Savitzky-Golay, MSC), `PcaResult`, `PlsRegression` (PLS1 NIPALS, implements GraviLearn `IEstimator`), `CalibrationResult` (K-fold component selection), `SimulatedSampleSet`, `SpectralDatasetFile`.
- `src/SiWare.Visualization` — Matplotlib.Net (Visual.Net) figures; titles append "SIMULATED DATA" for simulated input.
- `src/SiWare.Ai` — `SpectrumFacts` builds invariant-culture facts; only facts are sent. `ChatCompletionsInterpreter` (HF router preset via `HF_TOKEN`, or any OpenAI-compatible URL), `LocalModelInterpreter` (HF.Net, ChatML). Keys come from env vars only.
- Shared package metadata lives in `src/Directory.Build.props`; all packages share `docs/nuget/README.md` and the icon.

**`samples/SiWare.Cli`** — hand-rolled argument parsing; `AnalysisCommands.cs` adds `dataset`, `analyze`, `calibrate`, `plot`, `explain`. The Workbench `InsightService` enables `/api/insight/{id}` when `HF_TOKEN` or `SIWARE_LLM_BASE_URL`/`_API_KEY`/`_MODEL` are set.

**`docs/research/`** — evidence about the vendor driver (`vendor-api.md` has verified method signatures and enum values). Respect its stated limits: enum values are Python-level, not verified wire opcodes; units/mode semantics still need physical-device validation. Do not redistribute vendor binaries.

## Project requirements to honor

- Docs (README, docs, native README) are bilingual English + Bahasa Indonesia, include screenshots, and carry the attribution "Created by Gravicode Studios, led by Kang Fadhil" / "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil" — also shown in the apps.
- Sample/support app UIs should be polished; use the local `frontend-design` skill (`.claude/skills/frontend-design/`).
- NuGet packages need a custom icon. The brief references credential files outside the repo (NuGet key, LLM/HF tokens) — read them only when needed and never copy their contents into the repo, logs, or docs.
- The brief suggests GravicodeScience / Visual.Net / HF.Net (github.com/DotNetVibeCoderz) for numeric, charting, and ML needs in .NET.
- `.tools/` (embedded Python 3.10, Playwright) is git-ignored local tooling.
