# Hardware validation

*SiWare.NET, created by Gravicode Studios, led by Kang Fadhil.* · [Bahasa Indonesia](../id/hardware-validation.md)

This page records what is established, what is not, and what must happen before SiWare.NET claims compatibility with Si-Ware hardware or is published publicly.

## Evidence levels

| Claim | Status | Evidence |
|---|---|---|
| Vendor driver is a CPython 3.10 x64 extension | **Verified** | PE header and strings ([driver-static.md](../research/driver-static.md)) |
| Method names, argument order, return shapes, enum values | **Verified** (Python level) | Import-time introspection without creating a driver ([vendor-api.md](../research/vendor-api.md)) |
| `Error.value() == 0` means success | **Verified** | Data-only `Error()` object |
| Bridge protocol, validation, timeout, process cleanup | **Tested** | `vendor_bridge.py --self-test`, `VendorValidationTests` |
| Simulator, model, processing, file formats, Rust ABI | **Tested** | 86 xUnit tests, 9 Rust tests, UI smoke test |
| `scan_time` is in seconds | **Strong inference** | The vendor UI labels it "sec"; all vendor sample scans use 2 ([driver-ghidra.md](../research/driver-ghidra.md)) |
| `mode` meaning | **Unknown** | Linked to the driver's "default" and "compatibility" request formats only |
| Transport: UDP discovery, then TCP over USB RNDIS/NCM or Wi-Fi | **Observed** | Driver strings and the shipped INF files |
| `wvn` axis unit is cm⁻¹ | **Observed in vendor data** | 138 vendor sample spectra span 3921.57–7407.41 cm⁻¹ (= 2550–1350 nm) |
| Discovery and acquisition on a real sensor | **Validated** | Si-NIR Matrix DVK (USB NCM `303A:4000`) on Windows 10, 2026-10-26, with the vendor-kit configuration. The USB sensor announces itself about 6 s after the driver starts, so the discovery window was raised to 20 s |
| USB driver on Windows 11 build 26200 | **Fails** | Vendor `siwarenirusbdriver.inf` 1.0.0.2 is not Microsoft-signed and returns `STATUS_NOT_SUPPORTED`; the built-in NCM driver reports a hardware error. Use Windows 10 |
| Measurement accuracy against a reference instrument | **Not validated** | |
| Dark acquisition through the vendor API | **Does not exist** | No dark operation in the binding; absorbance uses the reference only |

Enum values are Python enum values, not verified wire opcodes. A successful import shows binary compatibility, not sensor compatibility.

## Hardware validation checklist

Run these steps with a supported sensor and record the results (device model, firmware via `read_fw_version`, driver SHA-256, and dates) in `Progress.md`:

1. Confirm `mode` with Si-Ware documentation or support, and check that a 2 s `scan_time` behaves as 2 seconds.
2. `siware discover --vendor-config …` lists the expected device ID. Unplugging and replugging the sensor is reflected.
3. Acquire a **Reference** and a **Sample** at each point count (65 to 4096). Check the axis range against the sensor datasheet and confirm that it is monotonic after nm conversion.
4. Compare with the same measurement in Si-Spect: same settings, and the PSD values and axis agree within the expected noise.
5. Repeat 100 acquisitions and check for resource leaks (worker process count, handles, memory).
6. Cancel mid-acquisition and pull the cable mid-acquisition. The app must surface an error, and the worker must be gone.
7. Validate absorbance against a known reference material once a verified dark/zero method is established.

## Release gate

Public NuGet publication of `Gravicode.SiWare.Sdk` requires:

- [x] Discovery and acquisition validated on one supported sensor (Si-NIR Matrix DVK, Windows 10). Checklist items 3–7 remain recommended follow-ups.
- [x] Package identity (`Gravicode.SiWare.*`) and MIT license approved by the owner
- [x] Version moved off `-preview`: 0.1.0
- [x] Documentation updated with the validated device and settings

Publishing runs through `.github/workflows/siware-net-publish.yml`: push a tag `siware-net-v<version>` (or start it manually from the Actions tab). It builds the Rust DSP, runs all tests, packs the four packages, and pushes them with the `NUGET_API_KEY` repository secret.

Until then, build packages locally (`dotnet pack`, see [Getting started](getting-started.md)). Publishing credentials must stay outside the repository.
