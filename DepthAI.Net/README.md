# DepthAI.Net

SDK .NET untuk **DepthAI API V3** — kamera OAK dari Luxonis — lengkap dengan CLI, template proyek,
sample, dan **Jack The Code Bender**, wizard pembuat aplikasi computer vision berbantuan LLM.

*A .NET SDK for DepthAI API V3 (Luxonis OAK cameras), with a CLI, project templates, samples,
and Jack The Code Bender — an LLM-assisted computer vision app wizard.*

Dibuat oleh **Gravicode Studios**, dipimpin **Kang Fadhil**.

---

## Isi / Contents

| | |
| --- | --- |
| [Mulai cepat](#mulai-cepat--quick-start) | Jalankan sesuatu dalam 2 menit |
| [Status runtime native](#status-runtime-native--native-runtime-status) | **Baca ini dulu** sebelum menghubungkan kamera |
| [Paket](#paket--packages) | Apa yang ada di dalam repo |
| [Konsep inti](#konsep-inti--core-concepts) | Pipeline, stream, frame, inferensi |
| [CLI](#cli) | `depthai-dotnet-cli` |
| [Wizard](#jack-the-code-bender) | Aplikasi desktop dengan asisten LLM |
| [Dokumentasi lengkap](#dokumentasi--documentation) | Referensi API dan tutorial |

---

## Mulai cepat / Quick start

```bash
git clone https://github.com/DotNetVibeCoderz/Vibe_SDK
cd Vibe_SDK/DepthAI.Net
dotnet build

# Menjalankan sample deteksi objek — berjalan tanpa kamera sekalipun
dotnet run --project samples/console/DepthAI.Sample.PipelineRunner
```

Kode paling sederhana yang berguna:

```csharp
using DepthAI;
using DepthAI.Pipelines;
using DepthAI.Streaming;

await using var device = await DepthAiDevice.OpenAsync();

var pipeline = Pipeline.CreateBuilder()
    .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
    .StreamOut("rgb.preview", "video")
    .Build(device.Capabilities);

await device.StartAsync(pipeline);

using var subscription = device.GetStream<ImageFrame>("video")
    .Subscribe(frame => Console.WriteLine($"{frame.Width}x{frame.Height}"));

Console.ReadLine();
await device.StopAsync();
```

---

## Status runtime native / Native runtime status

SDK ini punya dua backend di balik satu antarmuka:

| Backend | Kegunaan | Status |
| --- | --- | --- |
| `SimulationBackend` | Data sintetis: warna, kedalaman, deteksi | Berfungsi penuh, di semua OS |
| `NativeBackend` | Hardware OAK sungguhan lewat shim `depthai-c` di atas depthai-core 3.11 | **Windows x64**, lewat paket `DepthAI.Net.Runtime.win-x64` |

```xml
<PackageReference Include="DepthAI.Net" Version="0.3.0" />
<PackageReference Include="DepthAI.Net.Runtime.win-x64" Version="0.3.0"
                  Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
```

Dengan paket runtime terpasang dan kamera tercolok, `DepthAiDevice.OpenAsync()` memakai hardware;
tanpa keduanya SDK jatuh ke simulasi, jadi aplikasi tetap bisa dikembangkan tanpa kamera.

Sudah diverifikasi dengan OAK-1 sungguhan: enumerasi, kemampuan (`CamA=IMX378`), telemetri,
preview kamera, YOLOv6 di perangkat dengan hasil yang sama persis dengan parser depthai, dan
encoder MJPEG 1080p. Stereo dan IMU sudah dipetakan tapi belum diuji karena OAK-1 tidak memilikinya.
Linux belum punya paket runtime.

Detail arsitektur, cara membangun shim, dan batasannya ada di
[docs/id/native-runtime.md](docs/id/native-runtime.md).

### Deteksi USB tanpa pustaka native

`UsbDeviceScanner` membaca perangkat OAK langsung dari bus USB (cfgmgr32 di Windows, sysfs di
Linux) tanpa depthai-core, sehingga `depthai-dotnet-cli info` bisa membedakan "tidak ada kamera"
dari "ada kamera tapi runtime native belum terpasang". Dua keadaan itu butuh tindakan berbeda.

---

## Paket / Packages

| Paket | Isi |
| --- | --- |
| `DepthAI.Net` | SDK inti: pipeline, perangkat, stream, inferensi, imaging dasar |
| `DepthAI.Net.Imaging.ImageSharp` | Konversi ke `Image<Rgb24>`, encoding PNG/JPEG, depth 16-bit |
| `DepthAI.Net.Imaging.SkiaSharp` | Konversi ke `SKBitmap`, jalur tercepat untuk Avalonia dan MAUI |
| `DepthAI.Net.Imaging.SystemDrawing` | Konversi ke `Bitmap` untuk WinForms dan WPF (khusus Windows) |
| `DepthAI.Net.Runtime.win-x64` | Runtime native untuk hardware OAK di Windows x64 (depthai-c + depthai-core 3.11) |
| `DepthAI.Net.Cli` | Tool `depthai-dotnet-cli` |
| `DepthAI.Net.Templates` | Template `dotnet new` |
| `DepthAI.Net.Wizard.Core` | Sistem proyek, template, dan lapisan asisten wizard |

SDK inti sengaja **tidak** bergantung pada pustaka imaging mana pun, sehingga aplikasi bebas
memilih ImageSharp, SkiaSharp, atau System.Drawing tanpa menyeret dua di antaranya.

---

## Konsep inti / Core concepts

### Pipeline

Pipeline adalah graf node yang dijalankan perangkat. Ada dua gaya penulisan:

```csharp
// Fluent, untuk susunan yang lazim
var pipeline = Pipeline.CreateBuilder()
    .AddColorCamera("rgb", c => c.WithPreview(640, 400))
    .AddStereoDepth("stereo")                        // sekaligus menambah kamera mono kiri/kanan
    .AddObjectDetection(model, "rgb.preview", "detector")
    .StreamOut("detector.detections", "detections")
    .Build(device.Capabilities);

// Graf eksplisit, untuk susunan tidak biasa
var p = Pipeline.Create();
var camera = p.AddColorCamera("rgb");
var encoder = p.AddVideoEncoder("enc", e => e.Profile = VideoProfile.H265Main);
camera.Video.LinkTo(encoder.Input);
p.AddOutputStream(encoder.Bitstream, "video");
```

`AddObjectDetection` otomatis menyisipkan node resize bila ukuran preview kamera berbeda dari
ukuran input model — ketidakcocokan itu penyebab paling sering deteksi "berjalan tapi tidak
menemukan apa-apa".

Pipeline bisa disimpan sebagai JSON dan diubah tanpa menyentuh kode:

```csharp
await pipeline.SaveToFileAsync("app.pipeline.json");
var loaded = await Pipeline.LoadFromFileAsync("app.pipeline.json",
    new PipelineLoadOptions { ModelResolver = name => model });
```

### Siklus hidup frame

Frame memakai buffer dari `ArrayPool` dan **dibuang segera setelah callback selesai**.
Bila frame perlu hidup lebih lama, salin:

```csharp
using var s = device.GetStream<ImageFrame>("video").Subscribe(frame =>
{
    var copy = frame.Clone();               // aman disimpan
    var previous = Interlocked.Exchange(ref _latest, copy);
    previous?.Dispose();
});
```

Memakai frame yang sudah dibuang melempar `ObjectDisposedException` alih-alih diam-diam
membaca memori yang sudah didaur ulang.

### Kedalaman

Nilai `0` pada peta kedalaman berarti **tidak ada pengukuran** — oklusi, permukaan tanpa
tekstur, atau di luar jangkauan. Bukan jarak nol.

```csharp
float? distance = frame.GetDistanceMeters(x, y);   // null = tidak terukur
float[,] matrix = frame.ToMeterMatrix();           // NaN untuk piksel kosong
```

API-nya sengaja nullable supaya kasus ini tidak lolos diam-diam menjadi "objek sangat dekat".

### Berkas `.dai` / `.dai` files

Frame dan deteksi bisa disimpan dalam format `.dai` milik depthai-core 3.10+, dan berkasnya
bisa dibuka dari Python/C++ depthai maupun sebaliknya.

```csharp
using DepthAI.Messages;

frame.Save("tangkapan");                              // → tangkapan.dai
using var depth = DaiMessageFile.LoadDepthFrame("depth.dai");
```

Fitur yang disinkronkan dari depthai-core 3.9–3.11 tercantum di
[docs/id/upstream-sync.md](docs/id/upstream-sync.md).

### Stream reaktif

Stream adalah `IObservable<T>` biasa, jadi bisa dikomposisi dengan System.Reactive bila mau,
tapi operator yang paling sering dipakai sudah tersedia tanpa dependensi tambahan:

```csharp
device.GetStream<ImageFrame>("video").Throttle(TimeSpan.FromMilliseconds(80));
device.GetStream<DetectionFrame>("detections").Where(f => f.Count > 0);
await foreach (var frame in device.GetStream<DepthFrame>("depth").ToAsyncEnumerable()) { }
```

---

## CLI

```bash
dotnet tool install -g DepthAI.Net.Cli
```

| Perintah | Fungsi |
| --- | --- |
| `depthai-dotnet-cli info` | Versi SDK, status runtime native, perangkat USB yang terdeteksi |
| `depthai-dotnet-cli devices list` | Perangkat yang bisa dibuka, plus yang terpasang tapi belum bisa dibuka |
| `depthai-dotnet-cli devices watch` | Memantau hotplug |
| `depthai-dotnet-cli pipeline new <preset>` | Membuat berkas pipeline JSON |
| `depthai-dotnet-cli pipeline validate <file>` | Memeriksa pipeline terhadap kemampuan perangkat |
| `depthai-dotnet-cli pipeline deploy <file>` | Menjalankan dan melaporkan throughput tiap stream |
| `depthai-dotnet-cli model info <file>` | Metadata model |
| `depthai-dotnet-cli capture -o ./out` | Menangkap RGB dan kedalaman ke berkas |

Tambahkan `--simulate` untuk memaksa simulasi, atau `--require-hardware` agar gagal alih-alih
menyimulasikan.

---

## Template proyek / Project templates

```bash
dotnet new install DepthAI.Net.Templates

dotnet new depthai-console -n VisiSaya --pipeline stereo-depth --fps 30
dotnet new depthai-desktop -n ViewerSaya
dotnet new depthai-web     -n WebSaya
```

Wizard memuat katalog yang jauh lebih luas — 16 template computer vision, dari penghitung orang
sampai inspeksi kualitas dan kepatuhan APD.

---

## Jack The Code Bender

Aplikasi desktop mirip code editor untuk membangun aplikasi computer vision, dengan asisten LLM
yang benar-benar memahami SDK ini.

```bash
dotnet run --project src/DepthAI.Net.Wizard
```

- **Editor** dengan tab, cari/ganti, ke baris, dan toggle nomor baris
- **Build, Run, Deploy** langsung dari menu, dengan panel Logs dan status bar
- **Galeri template**: 16 aplikasi computer vision siap pakai
- **Panel chat** multi-sesi dengan lampiran gambar dan dokumen, render Markdown penuh
- **Empat penyedia LLM**: OpenAI, Anthropic, Gemini, Ollama — bisa ditukar saat berjalan
- **Tema gelap dan terang**

Asisten punya fungsi yang dieksekusi sungguhan: menulis berkas ke proyek, membuat pipeline JSON,
memvalidasinya, memindai perangkat, mencari di internet, membaca URL, menghitung, dan membaca
referensi API agar tidak menebak nama tipe.

Konfigurasi ada di `app.config`. Kunci API sebaiknya lewat variabel lingkungan
(`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, `TAVILY_API_KEY`) — variabel lingkungan
selalu menang atas berkas, dan tidak ikut ter-commit.

---

## Sample

| Sample | Isi |
| --- | --- |
| `samples/console/DepthAI.Sample.PipelineRunner` | Deteksi objek, keluaran konsol |
| `samples/console/DepthAI.Sample.CaptureRgbd` | Merekam pasangan RGB dan kedalaman ke disk |
| `samples/console/DepthAI.Sample.PeopleCounter` | Menghitung lintasan orang pada garis virtual |
| `samples/avalonia/DepthAI.Sample.DepthViewer` | Warna dan kedalaman berdampingan, jarak di kursor |
| `samples/avalonia/DepthAI.Sample.DetectionDashboard` | Video dengan overlay deteksi |
| `samples/avalonia/DepthAI.Sample.FaceBlur` | Sensor privasi otomatis |
| `samples/web/DepthAI.Sample.BlazorLive` | Blazor Server dengan inferensi live |
| `samples/web/DepthAI.Sample.VisionApi` | REST API yang memaparkan deteksi dan kedalaman |

Sample dihasilkan dari katalog template yang sama dengan yang dipakai wizard, jadi kodenya
tidak pernah menyimpang.

---

## Membangun / Building

```bash
dotnet build DepthAI.Net.slnx          # seluruh solusi
dotnet test                            # semua test (114)
dotnet test tests/DepthAI.Net.Core.Tests
dotnet test --filter "FullyQualifiedName~YoloParser"
```

Semua test berjalan tanpa kamera: backend simulasi menjalankan jalur kode yang sama dengan
hardware, termasuk parser inferensi sungguhan.

---

## Dokumentasi / Documentation

| Bahasa Indonesia | English |
| --- | --- |
| [Referensi API](docs/id/api-reference.md) | [API reference](docs/en/api-reference.md) |
| [Tutorial](docs/id/tutorials.md) | [Tutorials](docs/en/tutorials.md) |
| [Runtime native](docs/id/native-runtime.md) | [Native runtime](docs/en/native-runtime.md) |
| [Galeri](docs/id/gallery.md) | [Gallery](docs/en/gallery.md) |
| [Sinkronisasi depthai-core](docs/id/upstream-sync.md) | [depthai-core sync](docs/en/upstream-sync.md) |

---

## Lisensi / License

MIT. Lihat [LICENSE](LICENSE).

Dibuat oleh **Gravicode Studios**, dipimpin **Kang Fadhil**.
