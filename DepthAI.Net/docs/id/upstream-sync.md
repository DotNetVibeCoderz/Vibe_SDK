# Sinkronisasi dengan depthai-core

DepthAI.Net mengikuti [luxonis/depthai-core](https://github.com/luxonis/depthai-core). SDK ini
dibuat di atas **v3.8.0**. Rilis 0.2.0 menyinkronkan perubahan sampai **v3.11.0**
(rilis upstream 12 Oktober 2026).

Tidak semua perubahan upstream relevan. Sebagian besar menyangkut firmware, RVC4, atau
node yang berjalan di perangkat, dan semua itu baru bisa dipakai setelah shim native
`depthai-c` tersedia (lihat [runtime-native](native-runtime.md)). Yang di-port hanyalah
perilaku yang dijalankan di host dan karena itu bisa diuji tanpa kamera.

## Yang di-port

| Upstream | Rilis | Di DepthAI.Net |
| --- | --- | --- |
| `msg.save()` / `msg.load()`, berkas `.dai` | 3.10 | `DaiMessageFile` — `frame.Save(path)`, `Load`, `LoadImageFrame`, `LoadDepthFrame`, `LoadDetections` |
| `DetectionParserConfig` lewat `inputConfig` | 3.11 | `device.SendConfig("detector", new DetectionParserConfig { ... })`, `DetectionNetworkNode.IouThreshold` |
| `dai::utility::colorizeDepthFrame` | 3.9 | `DepthColorizer.Colorize` / `ColorizeToImageFrame` |
| `strides` dan `background_class` di metadata NN Archive | 3.9 | `NnArchiveConfig`, `ModelMetadata.Strides`, `YoloOutputs`, `BackgroundClass` |
| Perbaikan dekoding YOLOv6 (pusat/ukuran berbasis stride) | 3.9 | `YoloDecodingFamily.CenterSize`, dekoder grid di `YoloParser` |
| DetectionParser hanya menghitung keluaran `_yolo` untuk stride | 3.10 | `YoloParser` dan validasi `NnArchiveConfig` |
| SegmentationParser memakai `background_class` | 3.9 | `SegmentationFrame.NoClass` (255) |

### Berkas `.dai`

Formatnya adalah amplop protobuf `ProtoSerializableMessage` yang persis sama dengan upstream,
sehingga bisa dibuka dari dua arah:

```csharp
using DepthAI.Messages;

frame.Save("tangkapan");                       // → tangkapan.dai
using var loaded = DaiMessageFile.LoadImageFrame("tangkapan.dai");
```

```python
frame = dai.ImgFrame(); frame.load("tangkapan.dai")
```

Yang didukung: `ImgFrame` (BGR/RGB interleaved, BGR/RGB planar yang dimuat sebagai
interleaved, GRAY8/RAW8, NV12, RGBA, dan kedalaman RAW16 yang dimuat sebagai `DepthFrame`)
serta `ImgDetections`. Data yang tidak punya tempat di skema upstream tidak ikut
tersimpan: kalibrasi `DepthFrame`, ukuran sumber `DetectionFrame`, dan posisi spasial deteksi.

### Ambang deteksi saat runtime

```csharp
device.SendConfig("detector", new DetectionParserConfig { ConfidenceThreshold = 0.7f });
```

Ambang baru berlaku mulai frame berikutnya untuk semua stream keluaran node itu. Properti
yang `null` mempertahankan nilai yang sedang aktif. IoU tidak berpengaruh pada MobileNet-SSD,
sama seperti di upstream.

### Pewarnaan kedalaman gaya upstream

`DepthColorizer.Colorize` memakai bawaan yang sama dengan upstream: 300–12000 mm, Jet, dan
skala logaritmik. Bila `maxMillimeters <= minMillimeters`, rentang dihitung otomatis dari
persentil ke-3 dan ke-95 piksel yang valid. `ToBgr` yang lama tetap ada dan tidak berubah.

### NN Archive

`NeuralModel.LoadFromFileAsync` kini juga membaca `config.json` NN Archive, baik yang ada
di dalam arsip `.tar` / `.tar.gz` maupun yang sudah diekstrak ke folder yang sama dengan
model. Arsip `.tar.xz` perlu diekstrak dulu karena .NET tidak menyertakan dekoder xz.

## Yang belum di-port

| Upstream | Alasan |
| --- | --- |
| Pipeline multi-perangkat (`pipeline.addDevice`, relay antarperangkat) — 3.11 | Butuh runtime native. Untuk sekarang, buka beberapa `DepthAiDevice` lewat `OpenAsync(backend, device)`. |
| Node Align, Stitching, MultiDeviceCalibration, ToFStereoFusion | Node perangkat/beta; mensimulasikannya tanpa hardware hanya akan menghasilkan hasil palsu. |
| Pesan `Odometry`, VIO Basalt/RTAB-Map | SDK ini belum punya node VIO. |
| Parser namespace Beta (YuNet, SCRFD, HRNet, ...) | API upstream masih bisa berubah tanpa pemberitahuan. |
| Bootloader PoE, IFE sharing RVC4, perbaikan VideoEncoder/ImageManip | Murni sisi firmware/perangkat. |

## Verifikasi

- 101 tes Core lulus, termasuk fixture `.dai` yang dibuat dengan depthai **3.11.0** Python
  (`tests/DepthAI.Net.Core.Tests/Fixtures/depthai-3.11`).
- Arah sebaliknya juga sudah diperiksa: berkas `.dai` yang ditulis DepthAI.Net dibuka oleh
  depthai 3.11 Python dengan piksel, kedalaman, dan deteksi yang identik.
- Diuji dengan OAK-1 sungguhan (MxId `14442C10011298CD00`). Frame passthrough BGR888p
  512×384 dan deteksi YOLOv6-nano yang ditangkap depthai 3.11 dimuat DepthAI.Net, lalu
  digambar ulang dengan overlay yang tepat posisinya.
