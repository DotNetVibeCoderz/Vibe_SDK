# Development progress / Progres pengembangan

- [x] Read requirements and preserve existing AGENTS.md.
- [x] Verify .NET 10.0.401 and Rust 1.97.1 toolchains.
- [x] Locate installed Si-Spect and invoke REA static inspection.
- [x] SDK, transport, processing, and data exchange.
- [x] Rust DSP and .NET integration.
- [x] CLI and acquisition workbench.
- [x] Automated tests (86 xUnit, 9 Rust, Playwright UI smoke), screenshots, local NuGet package.
- [x] English and Indonesian documentation (`README.md`, `docs/en`, `docs/id`).
- [x] Moved into the `Vibe_SDK` monorepo as `SiWare.Net/` (2026-10-20).
- [ ] Physical sensor compatibility validation (requires supported device and verified protocol). See `docs/en/hardware-validation.md`.
- [ ] Public NuGet publication (blocked by the hardware release gate and package identity/license review).

## 2026-10-20

- NuGet: package `Gravicode.SiWare.Sdk` 0.1.0-preview.1 with icon (`docs/assets/siware-net-icon.svg`), README, MIT license, XML docs, `runtimes/win-x64/native/siware_dsp.dll`, and `vendor_bridge.py` as a content file. Verified by installing it into a scratch console app: the native kernels load and the bridge script is copied.
- Fixed: the Workbench answered cancelled acquisitions with HTTP 408, and Chrome transparently resent the POST, so a cancelled acquisition silently ran again. Cancellation now returns 409; the smoke test asserts the device is idle afterwards.
- Fixed: SNV results now show the `SNV · σ` axis label, and `Normalized` has an Indonesian label.
- Screenshots: Workbench desktop EN/ID and mobile ID, captured by `scripts/ui-smoke.mjs`; CLI captured from real output by `scripts/cli-screenshot.mjs`.

## Status ringkas (Bahasa Indonesia)

SDK, DSP Rust, CLI, Workbench, pengujian, paket NuGet lokal, dan dokumentasi dwibahasa sudah selesai. Yang tersisa: validasi dengan sensor Si-Ware fisik, lalu peninjauan identitas paket dan lisensi sebelum publikasi ke nuget.org.

Research: Si-Spect uses native Python extensions; its driver is not a managed .NET DLL. REA's managed snapshot budget rejects the 422 MB executable. Focused PE resource inspection of the 4.6 MB driver succeeds. Native Ghidra is not configured in this session. No vendor binaries or credentials will be redistributed.
