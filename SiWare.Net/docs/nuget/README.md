# Gravicode.SiWare.Sdk

.NET 10 SDK for Si-Ware spectral sensors. Created by Gravicode Studios, led by Kang Fadhil.
SDK .NET 10 untuk sensor spektral Si-Ware. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

> **Preview.** The simulator and processing are tested. Vendor hardware support is not yet validated on a physical device, and simulated data is always labelled `IsSimulated = true`.
> **Pratinjau.** Simulator dan pemrosesan sudah diuji. Dukungan perangkat vendor belum divalidasi pada perangkat fisik; data simulasi selalu bertanda `IsSimulated = true`.

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

## Contents / Isi paket

- `SiWare.Sdk.dll`: `Spectrum`, `ISpectrometer`, `SimulatedSpectrometer`, `VendorSpectrometer`, `SpectralProcessing`, `NativeDsp`, `SpectrumFile`.
- `runtimes/win-x64/native/siware_dsp.dll`: optional Rust DSP kernels (C ABI v1). / Kernel DSP Rust opsional.
- `SiWare/vendor_bridge.py`: copied to your output directory; required by `VendorSpectrometer` together with CPython 3.10 x64 and the vendor-installed `pyDriver.pyd`. / Disalin ke folder output; dibutuhkan `VendorSpectrometer`.

The Si-Ware vendor driver is proprietary and is **not** included. This package is not affiliated with Si-Ware Systems.
Driver vendor Si-Ware bersifat proprietary dan **tidak** disertakan. Paket ini tidak berafiliasi dengan Si-Ware Systems.

Full documentation / Dokumentasi lengkap: see the `docs/` folder of the SiWare.Net repository (`docs/en`, `docs/id`).
