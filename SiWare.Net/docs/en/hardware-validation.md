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
| Discovery, acquisition, and shutdown on a real sensor | **Not validated** | No device available during development |
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

- [ ] Hardware checklist completed on at least one supported sensor
- [ ] Package identity reviewed (name and relationship to the Si-Ware trademark) and the license confirmed
- [ ] Version moved off `-preview` only after hardware validation
- [ ] Documentation updated with validated device models and settings

Until then, build packages locally (`dotnet pack`, see [Getting started](getting-started.md)). Publishing credentials must stay outside the repository.
