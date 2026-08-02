# Tutorial

Tiga latihan yang membangun satu sama lain. Semuanya berjalan tanpa kamera OAK — SDK memakai
backend simulasi bila runtime native tidak ada.

---

## 1. Deteksi objek

**Hasil:** aplikasi konsol yang mencetak objek yang terlihat beserta keyakinannya.

### Membuat proyek

```bash
dotnet new depthai-console -n DeteksiObjek
cd DeteksiObjek
```

### Menyiapkan model

Untuk hardware sungguhan, muat berkas `.blob`:

```csharp
var model = await NeuralModel.LoadFromFileAsync("mobilenet-ssd.blob");
```

Bila ada `mobilenet-ssd.json` di sebelahnya bergaya Luxonis, label dan ukuran input dibaca
otomatis. Untuk mulai sekarang tanpa berkas model:

```csharp
var model = NeuralModel.CreatePlaceholder(
    ModelFamily.MobileNetSsd,
    labels: ["person", "bottle", "chair"],
    inputWidth: 300,
    inputHeight: 300,
    confidenceThreshold: 0.5f);
```

### Menyusun pipeline

```csharp
await using var device = await DepthAiDevice.OpenAsync();

var pipeline = Pipeline.CreateBuilder()
    .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
    .AddObjectDetection(model, "rgb.preview", "detector")
    .StreamOut("detector.detections", "detections")
    .Build(device.Capabilities);
```

Preview 640×480 tidak cocok dengan input model 300×300, jadi builder menyisipkan node resize
otomatis. Ketidakcocokan ukuran adalah penyebab paling sering deteksi berjalan tapi tidak
menemukan apa pun — jadi jangan dilewatkan bila menyusun graf secara manual.

### Membaca hasil

```csharp
await device.StartAsync(pipeline);

using var subscription = device.GetStream<DetectionFrame>("detections").Subscribe(frame =>
{
    foreach (var detection in frame.Detections)
    {
        Console.WriteLine($"{detection.Label,-12} {detection.Confidence:P0}  {detection.Box}");
    }
});

Console.ReadLine();
await device.StopAsync();
```

`Box` ternormalisasi 0..1, jadi tetap benar walau frame di-resize di host. Untuk piksel:

```csharp
var (x, y, width, height) = detection.Box.ToPixels(frameWidth, frameHeight);
```

### Menyetel sensitivitas

Ambang keyakinan bisa diubah saat berjalan tanpa memuat ulang model:

```csharp
.AddObjectDetection(model, "rgb.preview", "detector", node => node.ConfidenceThreshold = 0.65f)
```

---

## 2. Stereo depth

**Hasil:** membaca jarak sungguhan ke objek dalam meter.

### Susunan minimal

```csharp
await using var device = await DepthAiDevice.OpenAsync();

var pipeline = Pipeline.CreateBuilder()
    .AddColorCamera("rgb", camera => camera.WithPreview(640, 400))
    .AddStereoDepth("stereo", depth =>
    {
        depth.Preset = DepthPreset.HighDensity;
        depth.LeftRightCheck = true;
        depth.AlignTo = CameraSocket.Rgb;
    })
    .StreamOut("rgb.preview", "video")
    .StreamOut("stereo.depth", "depth")
    .Build(device.Capabilities);
```

`AddStereoDepth` membuat dan menyambungkan sepasang kamera mono untuk Anda.

`AlignTo = CameraSocket.Rgb` penting: tanpa itu, piksel kedalaman dan piksel warna merujuk
titik dunia yang berbeda, dan overlay akan meleset.

### Membaca jarak

```csharp
using var subscription = device.GetStream<DepthFrame>("depth").Subscribe(frame =>
{
    var distance = frame.GetDistanceMeters(frame.Width / 2, frame.Height / 2);

    Console.WriteLine(distance is null
        ? "tengah frame: tidak ada pengukuran"
        : $"tengah frame: {distance:F2} m");
});
```

**Nilai kosong itu normal.** Permukaan tanpa tekstur, area teroklusi, dan objek di luar
jangkauan tidak menghasilkan pengukuran. `GetDistanceMeters` mengembalikan `null` justru
supaya kasus itu tidak lolos diam-diam menjadi "jarak nol".

### Memilih preset

| Preset | Kapan dipakai |
| --- | --- |
| `HighDensity` | Ingin sesedikit mungkin lubang; tepi objek boleh kasar |
| `HighAccuracy` | Membuang pengukuran meragukan; lebih berlubang tapi lebih dipercaya |
| `FastAccuracy` | Latensi rendah untuk kendali robot |
| `Default` | Titik awal yang seimbang |

`Subpixel` menaikkan presisi untuk objek jauh; `ExtendedDisparity` memperluas jangkauan dekat.
Keduanya memakai blok perangkat keras yang sama, jadi tidak bisa aktif bersamaan — validasi
akan menolaknya.

### Deteksi dengan koordinat 3D

Menggabungkan deteksi dan kedalaman memberi posisi 3D per objek:

```csharp
var pipeline = Pipeline.CreateBuilder()
    .AddColorCamera("rgb", camera => camera.WithPreview(640, 400))
    .AddSpatialObjectDetection(model, "rgb.preview", "detector")
    .StreamOut("detector.detections", "detections")
    .Build(device.Capabilities);

using var subscription = device.GetStream<DetectionFrame>("detections").Subscribe(frame =>
{
    foreach (var detection in frame.Detections)
    {
        if (detection.Spatial is { } spatial)
        {
            Console.WriteLine($"{detection.Label} pada {spatial.Z:F2} m");
        }
    }
});
```

Jarak diambil dari bagian tengah kotak, bukan seluruhnya — `BoundingBoxScaleFactor` mengatur
seberapa besar bagian yang dipakai, supaya piksel latar di tepi kotak tidak mencemari hasilnya.

---

## 3. Model kustom

**Hasil:** menjalankan model Anda sendiri, dan menangani keluarannya.

### Format yang didukung

| Format | Catatan |
| --- | --- |
| `.blob` | OpenVINO terkompilasi untuk MyriadX; paling langsung |
| `.superblob` | Satu berkas berisi beberapa varian jumlah SHAVE |
| `.onnx` | Dikompilasi on-the-fly |

### Metadata

Berkas `.blob` polos tidak memuat label maupun ukuran input, jadi keduanya harus disediakan —
lewat berkas `.json` pendamping bergaya Luxonis, atau langsung dalam kode:

```csharp
var model = await NeuralModel.LoadFromFileAsync("yolov8n.blob", new ModelMetadata
{
    Family = ModelFamily.Yolo,
    InputWidth = 640,
    InputHeight = 640,
    Labels = File.ReadAllLines("coco.names"),
    ConfidenceThreshold = 0.5f,
    IouThreshold = 0.5f,
});
```

### Tata letak YOLO

Parser mengenali dua tata letak dan memilihnya dari **bentuk tensor**, bukan nama model:

- anchor-free (v8/v10/v11): `[1, 4 + nc, anchors]`, tanpa skor objectness
- berbasis anchor (v5/v6/v7): `[1, anchors, 5 + nc]`, kolom ke-5 adalah objectness

Jadi biasanya tidak ada yang perlu dikonfigurasi.

### Keluaran yang tidak biasa

Bila arsitekturnya tidak cocok dengan parser bawaan, biarkan keluarga model sebagai `Raw` dan
proses tensornya sendiri:

```csharp
using var subscription = device.GetStream<NeuralTensorFrame>("nn").Subscribe(frame =>
{
    var tensor = frame.First;
    ReadOnlySpan<float> values = tensor.Span;
    // proses lanjut dengan ML.NET, TorchSharp, atau kode Anda sendiri
});
```

Atau implementasikan `IInferenceParser` dan kembalikan tipe frame Anda sendiri.

### Memverifikasi model

```bash
depthai-dotnet-cli model info yolov8n.blob
depthai-dotnet-cli model upload yolov8n.blob --verify
```

`--verify` menjalankan pipeline singkat dan memastikan model benar-benar mengeluarkan hasil,
bukan sekadar termuat.

---

## Kesalahan yang sering terjadi

**Frame kosong atau exception setelah dipakai.** Frame dibuang setelah callback selesai.
Panggil `Clone()` bila perlu menyimpannya.

**Deteksi berjalan tapi tidak menemukan apa pun.** Hampir selalu ukuran input model tidak
cocok dengan sumber. Pakai `AddObjectDetection`, yang menanganinya otomatis.

**Overlay meleset dari objek.** Gambar di atas keluaran `passthrough` neural network, bukan
preview kamera — keduanya tidak tersinkron.

**Kedalaman terbaca nol di mana-mana.** Nol berarti tidak ada pengukuran. Pakai
`GetDistanceMeters` yang mengembalikan `null`, dan periksa pencahayaan serta tekstur permukaan.

**Aplikasi tersendat pada fps tinggi.** Turunkan laju render dengan `Throttle`, dan pastikan
konversi piksel tidak dilakukan di UI thread.
