<p align="center"><img src="docs/assets/siware-net-icon.png" width="96" alt="SiWare.NET icon"></p>

<h1 align="center">SiWare.NET</h1>

<p align="center">.NET 10 SDK, driver bridge, Rust DSP, and sample apps for Si-Ware spectral sensors.<br>
SDK .NET 10, jembatan driver, DSP Rust, dan aplikasi contoh untuk sensor spektral Si-Ware.</p>

<p align="center"><b>Created by Gravicode Studios, led by Kang Fadhil</b><br>
<b>Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil</b></p>

![Spectrum Workbench — absorbance view](docs/screenshots/workbench-desktop.png)

> **Status: preview.** The SDK, simulator, processing, and apps are tested. The vendor-driver bridge calls the real installed Si-Ware Python binding, but it has **not yet been validated against a physical sensor**. Simulated spectra are always marked `IsSimulated = true` and labelled *SIMULATED DATA* in every app.
>
> **Status: pratinjau.** SDK, simulator, pemrosesan, dan aplikasi sudah diuji. Jembatan driver vendor memanggil binding Python Si-Ware yang terpasang, tetapi **belum divalidasi dengan sensor fisik**. Spektrum simulasi selalu bertanda `IsSimulated = true` dan berlabel *DATA SIMULASI*.

## English

### What's inside

| Component | Path | Purpose |
|---|---|---|
| SDK | `src/SiWare.Sdk` | `Spectrum` model, `ISpectrometer` with simulator and vendor bridge, calibration and processing, JSON/CSV exchange |
| Rust DSP | `native/siware-dsp` | Dependency-free SNV, smoothing, and absorbance kernels behind a versioned C ABI |
| Workbench | `samples/SiWare.Workbench` | Web app: connect, calibrate, capture, plot, process, history, export (EN/ID) |
| CLI | `samples/SiWare.Cli` | Scriptable acquisition, demo pipeline, and file processing |
| Vendor bridge | `scripts/vendor_bridge.py` | JSON-lines worker that hosts the proprietary `pyDriver.pyd` in CPython 3.10 |
| Tests | `tests/SiWare.Sdk.Tests`, `native/siware-dsp/tests` | 86 xUnit tests, 9 Rust tests, Playwright UI smoke test |

### Quick start

Requirements: .NET SDK 10.0.100+, Rust 1.85+ (optional, for native kernels), Windows x64 for the vendor bridge.

```sh
cargo build --release --manifest-path native/siware-dsp/Cargo.toml   # optional native DSP
dotnet build SiWare.Net.slnx
dotnet test SiWare.Net.slnx
dotnet run --project samples/SiWare.Workbench                       # http://localhost:5069
dotnet run --project samples/SiWare.Cli -- demo --output artifacts/demo
```

```csharp
await using ISpectrometer device = new SimulatedSpectrometer();
await device.ConnectAsync();
var scan = new ScanOptions(Points: 257, IntegrationMilliseconds: 100, Averages: 3);
var dark = await device.AcquireAsync(MeasurementKind.Dark, scan);
var reference = await device.AcquireAsync(MeasurementKind.Reference, scan);
var sample = await device.AcquireAsync(MeasurementKind.Sample, scan);
var absorbance = SpectralProcessing.Absorbance(sample, dark, reference);
```

### Documentation

- [Getting started](docs/en/getting-started.md): install, build, run the apps, build the NuGet package
- [Architecture](docs/en/architecture.md): components, data flow, design decisions
- [API guide](docs/en/api.md): SDK types, processing, file formats, Workbench HTTP API
- [Vendor integration](docs/en/vendor-integration.md): connecting a real Si-Ware sensor through the vendor driver
- [Hardware validation](docs/en/hardware-validation.md): what is verified, what is not, and the release checklist
- Research evidence: [vendor API](docs/research/vendor-api.md), [driver static inspection](docs/research/driver-static.md)

## Bahasa Indonesia

### Isi repositori

| Komponen | Lokasi | Fungsi |
|---|---|---|
| SDK | `src/SiWare.Sdk` | Model `Spectrum`, `ISpectrometer` dengan simulator dan jembatan vendor, kalibrasi dan pemrosesan, ekspor/impor JSON/CSV |
| DSP Rust | `native/siware-dsp` | Kernel SNV, penghalusan, dan absorbansi tanpa dependensi, melalui C ABI berversi |
| Workbench | `samples/SiWare.Workbench` | Aplikasi web: hubungkan, kalibrasi, rekam, grafik, proses, riwayat, ekspor (EN/ID) |
| CLI | `samples/SiWare.Cli` | Akuisisi lewat skrip, pipeline demo, dan pemrosesan berkas |
| Jembatan vendor | `scripts/vendor_bridge.py` | Worker JSON-lines yang menjalankan `pyDriver.pyd` proprietary di CPython 3.10 |
| Pengujian | `tests/SiWare.Sdk.Tests`, `native/siware-dsp/tests` | 86 tes xUnit, 9 tes Rust, smoke test UI Playwright |

### Mulai cepat

Kebutuhan: .NET SDK 10.0.100+, Rust 1.85+ (opsional, untuk kernel native), Windows x64 untuk jembatan vendor. Perintah build dan contoh kode sama seperti bagian English di atas.

### Dokumentasi

- [Memulai](docs/id/getting-started.md): instalasi, build, menjalankan aplikasi, membuat paket NuGet
- [Arsitektur](docs/id/architecture.md): komponen, alur data, keputusan desain
- [Panduan API](docs/id/api.md): tipe SDK, pemrosesan, format berkas, HTTP API Workbench
- [Integrasi vendor](docs/id/vendor-integration.md): menghubungkan sensor Si-Ware nyata melalui driver vendor
- [Validasi perangkat keras](docs/id/hardware-validation.md): apa yang sudah dan belum terverifikasi, serta daftar periksa rilis

## Screenshots / Tangkapan layar

| Workbench (Bahasa Indonesia) | Mobile | CLI |
|---|---|---|
| ![Workbench ID](docs/screenshots/workbench-desktop-id.png) | ![Mobile](docs/screenshots/workbench-mobile-id.png) | ![CLI](docs/screenshots/cli.png) |

## License / Lisensi

MIT. See [LICENSE](LICENSE). The Si-Ware driver and Si-Spect Platform are proprietary third-party software. They are not included, and this project is not affiliated with Si-Ware Systems.
MIT. Driver Si-Ware dan Si-Spect Platform adalah perangkat lunak pihak ketiga yang proprietary. Keduanya tidak disertakan, dan proyek ini tidak berafiliasi dengan Si-Ware Systems.

Roadmap: [PLAN.md](PLAN.md) · Progress / Progres: [Progress.md](Progress.md)
