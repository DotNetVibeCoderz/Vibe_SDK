# API guide

*SiWare.NET, created by Gravicode Studios, led by Kang Fadhil.* · [Bahasa Indonesia](../id/api.md)

All types live in the `SiWare.Sdk` namespace.

## Acquire spectra

```csharp
using SiWare.Sdk;

await using ISpectrometer device = new SimulatedSpectrometer(seed: 42);
await device.ConnectAsync(cancellationToken);
Console.WriteLine($"{device.Device.Name} via {device.Device.Transport}, simulated: {device.Device.IsSimulated}");

var options = new ScanOptions(Points: 257, IntegrationMilliseconds: 100, Averages: 3);
Spectrum sample = await device.AcquireAsync(MeasurementKind.Sample, options, cancellationToken);
await device.DisconnectAsync();
```

| Member | Description |
|---|---|
| `DeviceInfo Device` | `Id`, `Name`, `Transport`, `IsSimulated` |
| `bool IsConnected` | Current connection state |
| `ConnectAsync(ct)` | Opens the session; for the vendor bridge, starts the worker and discovers a sensor |
| `AcquireAsync(kind, options, ct)` | Acquires `Sample`, `Dark`, or `Reference`; throws `InvalidOperationException` when not connected |
| `DisconnectAsync(ct)` / `DisposeAsync()` | Releases the device and worker process |

`ScanOptions` validates `Points` (2–4096), `IntegrationMilliseconds` (1–60,000), and `Averages` (1–100). The vendor bridge additionally limits `Points` to 65, 129, 257, 513, 1024, 2048, or 4096, and supports only `Sample` and `Reference`.

## Calibrate and process

```csharp
var dark = await device.AcquireAsync(MeasurementKind.Dark, options);
var reference = await device.AcquireAsync(MeasurementKind.Reference, options);
var sample = await device.AcquireAsync(MeasurementKind.Sample, options);

Spectrum absorbance = SpectralProcessing.Absorbance(sample, dark, reference);
Spectrum smoothed = SpectralProcessing.Smooth(absorbance, window: 5);      // odd window, 1..Count
Spectrum snv = SpectralProcessing.StandardNormalVariate(smoothed);         // Kind = Normalized

// Same operations through the Rust kernels, chosen explicitly:
if (NativeDsp.IsAvailable)
    snv = NativeDsp.StandardNormalVariate(NativeDsp.Smooth(absorbance, 5));
```

- `Absorbance` requires the kinds `Sample`, `Dark`, and `Reference` in that order, from the same device, provenance, acquisition settings (when present), and wavelength grid (checked by `SpectralProcessing.Match`). Every corrected signal must be positive; otherwise an `ArgumentException` names the failing point.
- Every operation returns a new `Spectrum` with a new `Id`. Inputs are never modified.
- `NativeDsp` throws `PlatformNotSupportedException` when the library is missing. It never falls back to managed code.

## Files

```csharp
string json = SpectrumFile.ToJson(spectrum);            // full fidelity, including provenance
Spectrum restored = SpectrumFile.FromJson(json);        // re-validated on load

string csv = SpectrumFile.ToCsv(spectrum);              // header: wavelength_nm,value
Spectrum imported = SpectrumFile.FromCsv(csv, deviceId: "lab-01", isSimulated: false, MeasurementKind.Sample);
```

JSON uses web casing and string enums (`SpectrumFile.JsonOptions`):

```json
{
  "id": "6da78ecc-e892-4d97-b9b8-1dcb92c82b1f",
  "deviceId": "SIM-001",
  "capturedAt": "2026-10-20T05:16:43.1+00:00",
  "kind": "Absorbance",
  "isSimulated": true,
  "wavelengths": [1350, 1354.6875],
  "values": [0.1243, 0.1250],
  "count": 2
}
```

CSV uses invariant culture and round-trip number formatting, and must have exactly the `wavelength_nm,value` header. Because CSV carries no provenance, `FromCsv` makes the caller state it.

## Errors

| Exception | Meaning |
|---|---|
| `ArgumentException` / `ArgumentOutOfRangeException` | Invalid spectrum, options, mismatched calibration, numeric domain error |
| `InvalidOperationException` | Operation not valid in the current state (not connected) |
| `NotSupportedException` | Measurement kind not supported by the device (Dark on the vendor bridge) |
| `IOException` | Vendor worker or device failure, unexpected response |
| `OperationCanceledException` | Cancelled by the token, or the vendor timeout elapsed |
| `PlatformNotSupportedException` | Native DSP missing, or vendor bridge used outside Windows x64 |
| `FormatException` | Malformed CSV |
| `System.Text.Json.JsonException` | Malformed JSON |

## Workbench HTTP API

Base URL: `http://localhost:5069` by default. JSON bodies use camelCase, and enums are strings.

| Method | Path | Body / query | Result |
|---|---|---|---|
| GET | `/api/status` | | `connected`, `busy`, `device`, `calibration {dark, reference}`, `historyCount`, `nativeAvailable`, `supportsDark`, `insightAvailable`, `insightModel` |
| POST | `/api/connect` | | Status |
| POST | `/api/disconnect` | | Status; clears calibration |
| POST | `/api/acquire` | `{ "kind": "Sample", "points": 257, "integrationMilliseconds": 100, "averages": 1 }` | `Spectrum` |
| POST | `/api/cancel` | | `200`; the running acquisition then returns `409` |
| GET | `/api/history` | | Newest first: `id`, `kind`, `capturedAt`, `points`, `isSimulated` |
| GET | `/api/spectra/{id}` | | `Spectrum` |
| POST | `/api/process/{id}` | `{ "operation": "absorbance" \| "snv" \| "smooth", "window": 5 }` | New `Spectrum` (added to history) |
| POST | `/api/insight/{id}` | `{ "language": "en" \| "id" }` | `text`, `provider`, `model`, `disclaimer`, `isAiGenerated`, `facts`, `spectrumId`. Requires an LLM configured on the server (see [Analysis, charts, and AI](analysis.md#workbench)) |
| GET | `/api/export/{id}` | `?format=csv\|json` | File download |

Errors return `{ "error": "message" }` with `400` (validation), `403` (cross-origin POST), `404` (unknown or expired spectrum), `409` (busy, not connected, or cancelled), or `502` (device, worker, or LLM provider failure). Only same-origin POSTs are accepted, so call the API from the served page, a non-browser client, or a reverse proxy on the same host.

```sh
curl -X POST http://localhost:5069/api/connect
curl -X POST http://localhost:5069/api/acquire -H "Content-Type: application/json" -d '{"kind":"Sample"}'
```

New acquisitions include optional `acquisition` metadata (`points`, `integrationMilliseconds`, `averages`). JSON and processing preserve it. Calibration rejects differing settings, including a missing value on only one side. Legacy/CSV inputs without metadata require the caller to verify matching acquisition conditions.
