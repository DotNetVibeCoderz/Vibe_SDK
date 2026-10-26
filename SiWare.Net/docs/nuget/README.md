# SiWare.NET (Gravicode.SiWare.*)

.NET 10 SDK for Si-Ware spectral sensors. Created by Gravicode Studios, led by Kang Fadhil.
SDK .NET 10 untuk sensor spektral Si-Ware. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

> **0.1.0.** Discovery and acquisition are validated on a Si-NIR Matrix DVK over USB (Windows 10). Simulated data is always labelled `IsSimulated = true`.
> **0.1.0.** Penemuan perangkat dan akuisisi sudah divalidasi pada Si-NIR Matrix DVK lewat USB (Windows 10). Data simulasi selalu bertanda `IsSimulated = true`.

## Quick start / Mulai cepat

```csharp
using SiWare.Sdk;

await using ISpectrometer device = new SimulatedSpectrometer();
await device.ConnectAsync();

var scan = new ScanOptions(Points: 257, IntegrationMilliseconds: 100, Averages: 3);
var dark = await device.AcquireAsync(MeasurementKind.Dark, scan);
var reference = await device.AcquireAsync(MeasurementKind.Reference, scan);
var sample = await device.AcquireAsync(MeasurementKind.Sample, scan);

var absorbance = SpectralProcessing.Absorbance(sample, dark, reference);
var normalized = NativeDsp.IsAvailable
    ? NativeDsp.StandardNormalVariate(absorbance)
    : SpectralProcessing.StandardNormalVariate(absorbance);

File.WriteAllText("absorbance.csv", SpectrumFile.ToCsv(absorbance));
```

## Packages / Paket

| Package | Contents |
|---|---|
| `Gravicode.SiWare.Sdk` | Core SDK, simulator, vendor bridge, processing, optional Rust kernels. No third-party dependencies. |
| `Gravicode.SiWare.Analysis` | Savitzky-Golay, MSC, PCA, PLS calibration with cross-validation (GravicodeScience). |
| `Gravicode.SiWare.Visualization` | PNG/SVG/PDF charts of spectra, PCA, and calibration (Visual.Net / Matplotlib.Net). |
| `Gravicode.SiWare.Ai` | Advisory LLM interpretation: Hugging Face router, OpenAI-compatible APIs, or local HF.Net models. |

```csharp
var calibration = CalibrationResult.Fit(new SpectralDataset(spectra, moisture, "moisture"));
SpectrumCharts.PredictedVsMeasured(calibration).Save("pls.png");
```

## Core contents / Isi paket inti

- `SiWare.Sdk.dll`: `Spectrum`, `ISpectrometer`, `SimulatedSpectrometer`, `VendorSpectrometer`, `SpectralProcessing`, `NativeDsp`, `SpectrumFile`.
- `runtimes/win-x64/native/siware_dsp.dll`: optional Rust DSP kernels (C ABI v1). / Kernel DSP Rust opsional.
- `SiWare/vendor_bridge.py`: copied to your output directory; required by `VendorSpectrometer` together with CPython 3.10 x64 and the vendor-installed `pyDriver.pyd`. / Disalin ke folder output; dibutuhkan `VendorSpectrometer`.

The Si-Ware vendor driver is proprietary and is **not** included. This package is not affiliated with Si-Ware Systems.
Driver vendor Si-Ware bersifat proprietary dan **tidak** disertakan. Paket ini tidak berafiliasi dengan Si-Ware Systems.

Full documentation / Dokumentasi lengkap: https://github.com/DotNetVibeCoderz/Vibe_SDK/tree/main/SiWare.Net (`docs/en`, `docs/id`).
