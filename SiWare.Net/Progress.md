# Development progress / Progres pengembangan

- [x] Read requirements and preserve existing AGENTS.md.
- [x] Verify .NET 10.0.401 and Rust 1.97.1 toolchains.
- [x] Locate installed Si-Spect and invoke REA static inspection.
- [x] SDK, isolated vendor bridge, simulator, processing, and data exchange.
- [x] Rust DSP and .NET integration.
- [x] CLI and acquisition workbench.
- [x] Automated tests, screenshots, local NuGet packages.
- [x] English and Indonesian documentation (`README.md`, `docs/en`, `docs/id`).
- [x] Moved into the `Vibe_SDK` monorepo as `SiWare.Net/` (2026-10-20).
- [x] Code review: acquisition-settings validation, numerical parity, vendor response checks (see `docs/review.md`).
- [x] Chemometrics (GravicodeScience), visualization (Visual.Net), and AI interpretation (HF.Net) — PLAN steps 7–10: three opt-in packages, CLI commands, Workbench AI insight, 24 tests, bilingual docs.
- [x] Vendor driver analysis with REA + Ghidra: `scan_time` in seconds, cm⁻¹ axis, no dark operation (reference-only absorbance added), UDP/TCP over USB RNDIS/NCM transport; `mode` still unknown (`docs/research/driver-ghidra.md`).
- [x] Physical sensor validation: discovery and acquisition on a Si-NIR Matrix DVK over USB on Windows 10 (2026-10-26). Accuracy against a reference instrument is still open. See `docs/en/hardware-validation.md`.
- [x] Public NuGet publication of 0.1.0 via GitHub Actions (`siware-net-publish.yml`, `NUGET_API_KEY` secret).

## 2026-10-20

- NuGet: package `Gravicode.SiWare.Sdk` 0.1.0-preview.1 with icon (`docs/assets/siware-net-icon.svg`), README, MIT license, XML docs, `runtimes/win-x64/native/siware_dsp.dll`, and `vendor_bridge.py` as a content file. Package identity and license approved.
- Fixed: cancelled Workbench acquisitions returned HTTP 408, which Chrome transparently resends, so a cancelled acquisition ran again. Cancellation now returns 409.
- Review: calibration compares recorded acquisition settings as well as sensor identity, provenance, and wavelength grid; settings survive JSON and derived results. Managed and Rust numerics agree on extreme finite inputs. Rust 1.87 is required. Validation: 90 xUnit tests, 9 Rust tests, strict Clippy, browser workflow, CLI demo, isolated NuGet consumer. See `docs/review.md`.

- Analysis/Visualization/AI: `Gravicode.SiWare.Analysis` (Savitzky-Golay, MSC, PCA, PLS1/NIPALS with K-fold component selection; GravicodeScience lacks PLS and Savitzky-Golay, so both are implemented here), `Gravicode.SiWare.Visualization` (Matplotlib.Net charts marked SIMULATED DATA when applicable), `Gravicode.SiWare.Ai` (facts-only prompts; Hugging Face router, OpenAI-compatible, or local HF.Net). Simulated moisture set: PLS 4 LV, RMSECV 0.37, R² 0.996.
- Live LLM tests: DeepSeek (OpenAI-compatible) good in EN/ID after fixing language-specific hedging and the token budget for reasoning models; HF router reachable but the account has no credit (402); local Qwen2.5-0.5B via HF.Net works offline but answers poorly.

## 2026-10-26

- Si-Spect Platform update (driver `be10d2e3…`, version `260818b00460e`): rechecked with `vendor_probe.py --inspect`, REA/Ghidra string diff, and the installation files. The API the bridge uses is unchanged, so no code changes were needed. Details: `docs/research/vendor-api.md`, `docs/research/driver-ghidra.md`.

- Continuous run mode (like Si-Spect's Single/Continuous): `ContinuousOptions` + `AcquireSeriesAsync` in the core SDK, CLI `series`, and Workbench Run mode → Continuous (background run, live chart, progress, stop). 5 SDK tests plus a browser smoke step.
- Physical sensor (Si-NIR Matrix DVK, USB NCM `303A:4000`): USB enumerates, but on Windows 11 build 26200 neither the built-in `usbncm.inf` (`STATUS_DEVICE_HARDWARE_ERROR`) nor the vendor `siwarenirusbdriver.inf` 1.0.0.2 (not Microsoft-signed; with enforcement off, `DriverEntry` returns `STATUS_NOT_SUPPORTED`) brings up the network link. Reported back to the user; the Si-Spect app's Single/Continuous is a scan-loop setting (`user_scan_settings.run_mode`), separate from the driver's `mode` argument.

- GitHub pre-release `siware-net-v0.1.0-preview.1` for hardware testing on Windows 10: self-contained CLI and Workbench (win-x64), vendor kit (bridge, probe, config, embeddable CPython 3.10.11), and the four .nupkg files. nuget.org publication stays on hold until the real-hardware test passes. The published Workbench now uses its own folder as content root.

## 2026-10-27

- Real-device test (Windows 10, Si-NIR Matrix DVK): the USB sensor fires `on_connected` about 6 s after `py_driver()` starts, past the old 5 s discovery window, so the bridge now waits up to 20 s (commit 453ef4d). The `VendorBridgeOptions.TimeoutSeconds` minimum was raised from 6 to 30 s to cover it.
- Release 0.1.0: version off `-preview`, release notes and docs updated, `siware-net-publish.yml` added, and the four packages published to nuget.org.

## Status ringkas (Bahasa Indonesia)

SDK, DSP Rust, CLI, Workbench, pengujian, paket NuGet lokal, dan dokumentasi dwibahasa sudah selesai dan sudah ditinjau. Integrasi GravicodeScience, Visual.Net, dan HF.Net juga selesai (analisis PCA/PLS, grafik, insight AI). Analisis driver dengan Ghidra juga selesai (satuan scan time = detik, tanpa dark, transport jaringan USB; arti `mode` belum diketahui). Yang tersisa: validasi dengan sensor Si-Ware fisik, lalu workflow CI publikasi NuGet.

Research: Si-Spect uses native Python extensions; its driver is not a managed .NET DLL. REA's managed snapshot budget rejects the 422 MB executable. Focused PE resource inspection of the 4.6 MB driver succeeds. Ghidra 12.1.4 analysis (2026-10-20) plus the vendor's own UI, sample data, database schema, and driver INF files resolved the scan-time unit, axis unit, dark question, and transport; see `docs/research/driver-ghidra.md`. No vendor binaries or credentials will be redistributed.
