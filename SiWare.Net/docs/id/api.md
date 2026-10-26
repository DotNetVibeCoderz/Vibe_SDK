# Panduan API

*SiWare.NET, dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/api.md)

Semua tipe berada di namespace `SiWare.Sdk`.

## Akuisisi spektrum

```csharp
using SiWare.Sdk;

await using ISpectrometer device = new SimulatedSpectrometer(seed: 42);
await device.ConnectAsync(cancellationToken);
Console.WriteLine($"{device.Device.Name} via {device.Device.Transport}, simulasi: {device.Device.IsSimulated}");

var options = new ScanOptions(Points: 257, IntegrationMilliseconds: 100, Averages: 3);
Spectrum sample = await device.AcquireAsync(MeasurementKind.Sample, options, cancellationToken);
await device.DisconnectAsync();
```

| Anggota | Keterangan |
|---|---|
| `DeviceInfo Device` | `Id`, `Name`, `Transport`, `IsSimulated` |
| `bool IsConnected` | Status koneksi saat ini |
| `ConnectAsync(ct)` | Membuka sesi; untuk jembatan vendor, menjalankan worker dan menemukan sensor |
| `AcquireAsync(kind, options, ct)` | Merekam `Sample`, `Dark`, atau `Reference`; melempar `InvalidOperationException` jika belum terhubung |
| `DisconnectAsync(ct)` / `DisposeAsync()` | Melepas perangkat dan proses worker |

`ScanOptions` memvalidasi `Points` (2–4096), `IntegrationMilliseconds` (1–60.000), dan `Averages` (1–100). Jembatan vendor juga membatasi `Points` pada 65, 129, 257, 513, 1024, 2048, atau 4096, dan hanya mendukung `Sample` serta `Reference`.

### Pengukuran kontinu

```csharp
var continuous = new ContinuousOptions(Measurements: 10, Interval: TimeSpan.FromSeconds(5));
await foreach (var spectrum in device.AcquireSeriesAsync(MeasurementKind.Sample, options, continuous, cancellationToken: ct))
    Console.WriteLine($"{spectrum.CapturedAt:T} rata-rata {spectrum.Values.Average():G4}");
```

`AcquireSeriesAsync` setara dengan mode jalan kontinu di Si-Spect. Interval dihitung dari awal satu pengukuran ke awal pengukuran berikutnya; jika satu akuisisi lebih lama dari interval, akuisisi berikutnya langsung dimulai tanpa mengejar ketertinggalan. `ContinuousOptions` menerima 1–10.000 pengukuran dan interval 0 detik hingga 1 jam. Membatalkan token menghentikan seri; spektrum yang sudah dihasilkan tetap valid. Berikan `TimeProvider` untuk mengendalikan waktu tunggu dalam pengujian.

## Kalibrasi dan pemrosesan

```csharp
var dark = await device.AcquireAsync(MeasurementKind.Dark, options);
var reference = await device.AcquireAsync(MeasurementKind.Reference, options);
var sample = await device.AcquireAsync(MeasurementKind.Sample, options);

Spectrum absorbance = SpectralProcessing.Absorbance(sample, dark, reference);
Spectrum smoothed = SpectralProcessing.Smooth(absorbance, window: 5);      // jendela ganjil, 1..Count
Spectrum snv = SpectralProcessing.StandardNormalVariate(smoothed);         // Kind = Normalized

// Operasi yang sama dengan kernel Rust, dipilih secara eksplisit:
if (NativeDsp.IsAvailable)
    snv = NativeDsp.StandardNormalVariate(NativeDsp.Smooth(absorbance, 5));
```

- `Absorbance` memerlukan jenis `Sample`, `Dark`, dan `Reference` dengan urutan itu, dari perangkat dan provenance yang sama serta pada grid panjang gelombang yang sama (diperiksa oleh `SpectralProcessing.Match`). Setiap sinyal terkoreksi wajib positif; jika tidak, `ArgumentException` menyebutkan titik yang bermasalah.
- Setiap operasi menghasilkan `Spectrum` baru dengan `Id` baru. Input tidak pernah diubah.
- `NativeDsp` melempar `PlatformNotSupportedException` jika library tidak tersedia. Metode ini tidak pernah beralih ke kode managed.

## Berkas

```csharp
string json = SpectrumFile.ToJson(spectrum);            // lengkap, termasuk provenance
Spectrum restored = SpectrumFile.FromJson(json);        // divalidasi ulang saat dimuat

string csv = SpectrumFile.ToCsv(spectrum);              // header: wavelength_nm,value
Spectrum imported = SpectrumFile.FromCsv(csv, deviceId: "lab-01", isSimulated: false, MeasurementKind.Sample);
```

JSON memakai penamaan camelCase dan enum berupa string (`SpectrumFile.JsonOptions`). Contoh:

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

CSV memakai kultur invarian dan format angka round-trip, dan wajib memiliki header persis `wavelength_nm,value`. Karena CSV tidak membawa provenance, `FromCsv` mewajibkan pemanggil menyebutkannya.

## Galat

| Exception | Arti |
|---|---|
| `ArgumentException` / `ArgumentOutOfRangeException` | Spektrum, opsi, atau kalibrasi tidak valid; galat domain numerik |
| `InvalidOperationException` | Operasi tidak valid pada status saat ini (belum terhubung) |
| `NotSupportedException` | Jenis pengukuran tidak didukung perangkat (Dark pada jembatan vendor) |
| `IOException` | Galat worker atau perangkat vendor, respons tidak terduga |
| `OperationCanceledException` | Dibatalkan oleh token, atau batas waktu vendor terlampaui |
| `PlatformNotSupportedException` | DSP native tidak tersedia, atau jembatan vendor dipakai di luar Windows x64 |
| `FormatException` | CSV rusak |
| `System.Text.Json.JsonException` | JSON rusak |

## HTTP API Workbench

URL dasar bawaan: `http://localhost:5069`. Body JSON memakai camelCase, dan enum berupa string.

| Metode | Path | Body / query | Hasil |
|---|---|---|---|
| GET | `/api/status` | | `connected`, `busy`, `device`, `calibration {dark, reference}`, `historyCount`, `nativeAvailable`, `supportsDark`, `insightAvailable`, `insightModel` |
| POST | `/api/connect` | | Status |
| POST | `/api/disconnect` | | Status; kalibrasi dihapus |
| POST | `/api/acquire` | `{ "kind": "Sample", "points": 257, "integrationMilliseconds": 100, "averages": 1 }` | `Spectrum` |
| POST | `/api/series` | `{ "measurements": 10, "intervalSeconds": 5, "points": 257, "integrationMilliseconds": 100, "averages": 1 }` | Status. Memulai pengukuran kontinu `Sample` di latar belakang; `status.series` melaporkan `running`, `completed`, `total`, `intervalSeconds`, dan `error` |
| POST | `/api/cancel` | | `200`; akuisisi yang sedang berjalan lalu mengembalikan `409`, dan pengukuran kontinu berhenti |
| GET | `/api/history` | | Terbaru lebih dulu: `id`, `kind`, `capturedAt`, `points`, `isSimulated` |
| GET | `/api/spectra/{id}` | | `Spectrum` |
| POST | `/api/process/{id}` | `{ "operation": "absorbance" \| "snv" \| "smooth", "window": 5 }` | `Spectrum` baru (masuk riwayat) |
| POST | `/api/insight/{id}` | `{ "language": "en" \| "id" }` | `text`, `provider`, `model`, `disclaimer`, `isAiGenerated`, `facts`, `spectrumId`. Memerlukan LLM yang dikonfigurasi di server (lihat [Analisis, grafik, dan AI](analysis.md#workbench)) |
| GET | `/api/export/{id}` | `?format=csv\|json` | Unduhan berkas |

Galat dikembalikan sebagai `{ "error": "pesan" }` dengan kode `400` (validasi), `403` (POST lintas origin), `404` (spektrum tidak ada atau sudah kedaluwarsa), `409` (sibuk, belum terhubung, atau dibatalkan), atau `502` (galat perangkat, worker, atau penyedia LLM). Hanya POST dari origin yang sama yang diterima, jadi panggil API dari halaman yang disajikan Workbench, dari klien non-browser, atau dari reverse proxy di host yang sama.

```sh
curl -X POST http://localhost:5069/api/connect
curl -X POST http://localhost:5069/api/acquire -H "Content-Type: application/json" -d '{"kind":"Sample"}'
```

Akuisisi baru menyimpan metadata opsional `acquisition` (`points`, `integrationMilliseconds`, `averages`). JSON dan pemrosesan mempertahankannya. Kalibrasi menolak pengaturan berbeda, termasuk metadata yang hanya hilang pada satu sisi. Untuk berkas lama/CSV tanpa metadata, pemanggil harus memastikan kondisi akuisisinya sesuai.
