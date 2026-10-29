# DepthAI.Net Gallery

Aplikasi desktop (Avalonia) yang memamerkan apa yang bisa dikerjakan kamera OAK di lapangan:
14 use case nyata dari industri, perkantoran, pendidikan, keamanan, ritel, hiburan, sampai kota
pintar. Setiap use case menjalankan model publik dari Luxonis Model Zoo **di dalam kamera**,
menampilkan hasilnya, dan menyertakan kode C# yang bisa langsung disalin.

![DepthAI.Net Gallery: kepatuhan APD](../images/gallery/gallery-ppe.jpg)

```bash
dotnet run --project src/DepthAI.Net.Gallery
```

## Dua sumber gambar

**Gambar contoh.** Gambar dikirim dari aplikasi ke kamera lewat `HostInputNode`, dianalisis
neural network di OAK, lalu hasilnya kembali ke layar. Selama kamera bekerja, garis pindai
berwarna Turbo menyapu gambar; anotasi muncul begitu jawaban tiba. Tombol **Gambar sendiri…**
menjalankan model yang sama pada foto milik Anda.

**Kamera live.** Model yang sama berjalan pada preview kamera. Frame yang ditampilkan adalah
frame `passthrough` yang persis diinferensi, sehingga kotak selalu sejajar dengan gambarnya.
Laju hasil per detik tampil di pojok panggung.

**Tanpa kamera**, galeri menampilkan hasil yang direkam dari OAK-1 sungguhan
(`Samples/recorded.json`), lengkap dengan MxId perangkat dan waktu perekamannya di *label
spesimen*. Tidak ada hasil buatan: gambar yang belum pernah direkam ditampilkan tanpa anotasi.

Gambar contoh dibuat dengan model generatif (Nano Banana 2 lewat Rodin). Orang, plat nomor, dan
dokumen di dalamnya fiktif.

## Use case

Angka di bawah berasal dari OAK-1 (MxId `14442C10011298CD00`, USB 3, Windows). "Gambar" adalah
waktu kirim sampai hasil, termasuk transfer USB dan dekode di host. "Live" adalah hasil per detik
pada preview kamera.

| Use case | Sektor | Model | Hasil pada gambar contoh | Gambar | Live |
| --- | --- | --- | --- | --- | --- |
| Hitung arus simpang jalan | Kota pintar, Transportasi | `yolov6-nano:r2-coco-512x288` | 9 pejalan kaki, 6 kendaraan | 64 ms | 15/s |
| Kepatuhan APD di proyek | Industri, Keamanan | `ppe-detection:640x640` | 1 pelanggaran: 1 tanpa rompi | 157 ms | 8/s |
| Gerbang parkir otomatis | Transportasi, Keamanan | `license-plate-detection:640x640` | 2 plat siap dibaca | 206 ms | 8/s |
| Pemindaian label paket | Logistik, Ritel | `qrdet:nano-512x288` | 4 kode QR ditemukan | 86 ms | 15/s |
| Penyamaran wajah otomatis | Perkantoran, Keamanan | `yunet:640x480` | 4 wajah disamarkan | 121 ms | 15/s |
| Analitik pengunjung | Ritel, Hiburan | YuNet + `age-gender-recognition` + `emotion-recognition` | 5 penonton, ekspresi beragam | 1047 ms | 4/s |
| Pelatih gerak dan postur | Pendidikan, Kesehatan | `yolov8-nano-pose-estimation:coco-512x288` | 1 lunge, 1 tangan di atas, 1 squat | 71 ms | 15/s |
| Inventaris ruang kerja | Perkantoran, Robotik | `yolov8-instance-segmentation-nano:coco-512x288` | 4 kursi, 2 orang, 2 tanaman, 1 anjing | 106 ms | 15/s |
| Pemahaman adegan | Robotik, Transportasi | `deeplab-v3-plus:256x256` | Terlihat: orang, mobil | 154 ms | 10/s |
| Latar virtual konferensi | Hiburan, Perkantoran | `mediapipe-selfie-segmentation:256x144` | Latar diganti | 35 ms | 20/s |
| Kepadatan kerumunan | Kota pintar, Keamanan | `dm-count:shb-426x240` | Sekitar 227 orang, terpadat di tengah | 1462 ms | 1,3/s |
| Digitalisasi dokumen | Perkantoran, Pendidikan | `paddle-text-detection:320x576` | 14 baris teks | 174 ms | 8/s |
| Kenali objek untuk belajar | Pendidikan, Ritel | `efficientnet-lite:lite0-224x224` | monarch (82 %) | 111 ms | 15/s |
| Kios tanpa sentuh | Kesehatan, Hiburan | `mediapipe-palm-detection:192x192` | Telapak terdeteksi | 49 ms | 20/s |

Laju live dibatasi FPS kamera yang dipilih per use case (2–20 fps), jadi angka itu adalah batas
bawah kemampuan model, bukan batas atasnya.

### Hitung arus simpang jalan

![Hitung arus simpang jalan](../images/gallery/gallery-traffic.jpg)

Dinas perhubungan menyetel durasi lampu dari hitungan per simpang. Yang dikirim ke pusat hanya
angka, bukan video warga.

### Kepatuhan APD di proyek

Pengawas K3 mendapat peringatan saat ada yang masuk area kerja tanpa helm atau rompi. Kotak hijau
berarti APD terpakai, merah berarti pelanggaran (gambar di bagian atas halaman ini).

### Gerbang parkir otomatis

![Gerbang parkir otomatis](../images/gallery/gallery-plates.jpg)

Model menemukan plat; membaca hurufnya adalah tahap OCR kedua pada potongan plat.

### Pemindaian label paket

![Pemindaian label paket](../images/gallery/gallery-parcels.jpg)

Satu palet dipindai sekaligus dari jarak dua meter. Model menemukan letak kode QR, isinya dibaca
di host dengan pustaka QR biasa.

### Penyamaran wajah otomatis

![Penyamaran wajah otomatis](../images/gallery/gallery-privacy.jpg)

Wajah dipikselkan sebelum gambar disimpan atau dikirim. Pada gambar lobi, YuNet menemukan 4 dari
6 wajah yang kecil; ini batas model 640×480 untuk wajah jauh.

### Analitik pengunjung

![Analitik pengunjung](../images/gallery/gallery-audience.jpg)

Kaskade tiga model dalam satu pipeline: YuNet menemukan wajah, lalu potongan tiap wajah dikirim
kembali ke kamera lewat dua `HostInputNode` lain untuk umur/gender dan emosi. Umur dan gender
adalah perkiraan statistik dari penampilan, bukan identitas; pakai sebagai angka agregat.

### Pelatih gerak dan postur

![Pelatih gerak dan postur](../images/gallery/gallery-posture.jpg)

17 sendi per orang, lalu aturan sederhana dari sudut lutut dan posisi pergelangan tangan
mengenali squat, lunge, dan tangan di atas kepala.

### Inventaris ruang kerja

![Inventaris ruang kerja](../images/gallery/gallery-instances.jpg)

Segmentasi instans memberi bentuk tiap objek. Perkalian koefisien mask dengan prototipe
dilakukan di host untuk kotak yang lolos NMS saja.

### Pemahaman adegan

![Pemahaman adegan](../images/gallery/gallery-scene.jpg)

Setiap piksel diberi kelas VOC. DeepLab 256×256 kehilangan objek kecil seperti kucing di pagar;
yang besar (orang, mobil) tertangkap.

### Latar virtual konferensi

![Latar virtual konferensi](../images/gallery/gallery-background.jpg)

Mask orang 256×144 diperbesar dan diperhalus, lalu latar diganti gradien Depth Field.

### Kepadatan kerumunan

![Kepadatan kerumunan](../images/gallery/gallery-crowd.jpg)

Jumlah orang adalah jumlah seluruh peta kepadatan. Model ini dilatih untuk kerumunan padat
dilihat dari atas; pada jalan yang lengang hitungannya berlebih, dan di RVC2 butuh sekitar 1,5
detik per gambar.

### Digitalisasi dokumen

![Digitalisasi dokumen](../images/gallery/gallery-documents.jpg)

Setiap baris teks ditemukan agar hanya potongannya yang dikirim ke OCR. Dekodernya memakai kotak
sejajar sumbu, jadi cocok untuk halaman yang lurus; halaman miring perlu poligon seperti di
PaddleOCR.

### Kenali objek untuk belajar

![Kenali objek untuk belajar](../images/gallery/gallery-specimen.jpg)

Lima tebakan teratas dari 1.000 kategori ImageNet. Label dibiarkan dalam bahasa Inggris aslinya.

### Kios tanpa sentuh

![Kios tanpa sentuh](../images/gallery/gallery-touchless.jpg)

Telapak tangan terbuka menjadi tombol di udara untuk kios rumah sakit, lift, atau mesin antrean.

## Kode contoh

Tab **Kode** menampilkan program lengkap untuk use case yang dipilih: versi gambar memakai
`HostInputNode` dan `InferAsync`, versi live memakai kamera dan langganan stream. Tombol
**Salin kode** menyalinnya.

![Tab Kode](../images/gallery/gallery-audience-code.jpg)

Model YOLO biasa (lalu lintas, APD, plat, QR) dan klasifikasi langsung memberi `DetectionFrame`
atau `ClassificationFrame` dari SDK. Model lain memberi `NeuralTensorFrame`; dekodernya ada di
`src/DepthAI.Net.Gallery/Inference/Decoders.cs` dan sengaja ditulis tanpa ketergantungan pada UI
supaya bisa disalin ke proyek sendiri:

| Dekoder | Model | Catatan |
| --- | --- | --- |
| `YoloDetections` | YOLOv6 R2, YOLOv8 (format Luxonis) | Membungkus `YoloParser` SDK |
| `YoloGrid` | Grid TLBR | Menyimpan sel asal untuk data tambahan |
| `PoseDecoder` | YOLOv8-pose | Titik kunci sudah dalam piksel model; skor masih logit |
| `InstanceSegmentationDecoder` | YOLOv8-seg | Koefisien × prototipe di host |
| `YuNetDecoder` | YuNet (libfacedetection) | Prior 17610 untuk 640×480, rumus OpenCV 4.5.4 |
| `PalmDecoder` | MediaPipe palm | 2016 anchor SSD |
| `TextDetectionDecoder` | PaddleOCR DB | Komponen terhubung + offset unclip |

## Tema terang

![Tema terang](../images/gallery/gallery-posture-light.jpg)

Galeri memakai sistem visual Depth Field yang sama dengan Jack The Code Bender. Aksen diambil
dari peta warna Turbo, peta yang sama dengan pewarna kedalaman SDK, dan garis pindai di panggung
adalah depth ribbon yang diputar tegak.

## Model

Arsip model tidak ikut di repo. Saat use case pertama kali dibuka, galeri mengunduhnya dari
Luxonis Model Zoo (endpoint yang sama dengan `dai::getModelFromZoo`, tanpa kunci API) dan
menyimpannya di:

- Windows: `%LOCALAPPDATA%\DepthAI.Net\Gallery\models`
- Linux: `~/.local/share/DepthAI.Net/Gallery/models`

Total 15 model sekitar 150 MB; model kerumunan sendiri 81 MB. Arsip `.tar.xz` tidak bisa dibuka
.NET tanpa pustaka tambahan, jadi `config.json` tiap model disalin ke `ModelConfigs/` dan dipasang
sebagai berkas pendamping `.tar.json` yang dikenali `NeuralModel.LoadFromFileAsync`.

## Opsi baris perintah

| Opsi | Kegunaan |
| --- | --- |
| `--record [use-case]` | Menjalankan use case pada gambar contohnya di kamera dan menulis ulang `Samples/recorded.json` di sumber repo |
| `--live-check [use-case…]` | Menguji mode live tanpa jendela; mencetak laju dan hasil, tidak menyimpan frame |
| `--screenshot <folder>` | Memotret jendela untuk tiap use case (`--shots ppe,audience:code` untuk sebagian) |
| `--usecase <id>`, `--light` | Use case awal dan tema terang |

Mode live sengaja tidak dipotret untuk dokumentasi: isinya ruangan tempat kamera berada.

## Yang sudah dan belum diverifikasi

- Ke-14 use case dijalankan di OAK-1 pada Windows, baik gambar maupun live. Hasil dan angka di
  halaman ini berasal dari sana.
- Linux x64 dan ARM64 belum pernah membuka kamera, termasuk galeri ini.
- OAK-1 tidak punya stereo, jadi tidak ada use case kedalaman di galeri.
- Saat pipeline berganti, depthai-core kadang mencatat "Device crashed, but no crash dump could
  be extracted". Itu reboot perangkat yang memang terjadi setiap pipeline berhenti; pipeline
  berikutnya tetap berjalan.
