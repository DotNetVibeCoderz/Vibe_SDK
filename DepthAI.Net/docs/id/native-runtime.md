# Runtime native / Native runtime

Dokumen ini menjelaskan bagaimana DepthAI.Net berbicara dengan hardware OAK sungguhan:
shim `depthai-c`, cara memasangnya, cara membangunnya, dan batasannya.

## Keadaan sekarang

| Kemampuan | Windows x64 | Linux / macOS |
| --- | --- | --- |
| Deteksi perangkat USB (ada/tidak, tahap boot, MxId) | ✅ tanpa pustaka native | ✅ Linux (sysfs) |
| Membuka perangkat, kemampuan, telemetri | ✅ `DepthAI.Net.Runtime.win-x64` | ❌ belum ada paket runtime |
| Pipeline kamera, NN, encoder → stream .NET | ✅ | ❌ |
| Seluruh SDK di atas backend simulasi | ✅ | ✅ |

Diverifikasi pada perangkat sungguhan (OAK-1, MxId `14442C10011298CD00`, IMX378, USB 3):

| Uji | Hasil |
| --- | --- |
| `depthai-dotnet-cli info` | Runtime native tersedia (depthai-core 3.11.0) |
| `depthai-dotnet-cli devices info` | 1 kamera warna, `CamA=IMX378`, USB Super, 16 SHAVE, suhu chip & DDR terbaca |
| Preview 512×384 → YOLOv6-nano (NN Archive) → `YoloParser` .NET | 22 fps; kotak `[0.711, 0.293 → 0.934, 0.591]` vs parser on-device depthai `[0.711, 0.29 → 0.933, 0.591]` |
| Video 1080p NV12 → VideoEncoder MJPEG | 30 fps, JPEG 1920×1080 valid |
| Stop lalu start pipeline kedua pada handle yang sama | berhasil (perangkat dibuka ulang otomatis) |
| Aplikasi baru yang hanya mereferensikan paket NuGet | frame kamera diterima tanpa menyalin DLL manual |

Stereo depth dan IMU sudah dipetakan di shim tapi **belum diuji di hardware**: OAK-1 tidak
punya kamera mono maupun IMU.

## Memakai di aplikasi

Tambahkan paket runtime di samping SDK:

```xml
<PackageReference Include="DepthAI.Net" Version="0.4.0" />
<PackageReference Include="DepthAI.Net.Runtime.win-x64" Version="0.4.0"
                  Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
```

Tidak ada konfigurasi lain. `DepthAiDevice.OpenAsync()` memakai hardware bila runtime
native termuat dan ada perangkat, selain itu jatuh ke simulasi. Untuk memaksa hardware:

```csharp
await using var device = await DepthAiDevice.OpenAsync(
    new DepthAiOptions { Backend = BackendSelection.NativeOnly });
```

Paket runtime berisi `depthai-c.dll`, `depthai-core.dll` 3.11.0, `opencv_world4130.dll`,
`dynamic_calibration.dll`, `libusb-1.0.dll`, dan runtime MSVC app-local, sehingga tidak
butuh VC++ Redistributable. Ukurannya sekitar 54 MB terkompresi.

Template `dotnet new` dan proyek buatan Jack The Code Bender sudah menyertakan referensi ini.
Di dalam repo, `DepthAI.Net.Core.csproj` menyalin DLL dari `artifacts/native/win-x64` (bila
sudah dibangun) ke setiap proyek yang mereferensikannya: CLI, Wizard, sample, dan tes.

## Arsitektur

```
DepthAI.Net  →  P/Invoke  →  depthai-c (shim C, tipe POD)  →  depthai-core 3.11 (C++)
```

depthai-core memaparkan template dan tipe STL yang tidak punya ABI stabil, jadi P/Invoke
langsung ke sana rapuh. Shim C dengan tipe POD adalah batas yang benar. Kontraknya ada di
[`native/depthai-c/include/depthai_c.h`](../../native/depthai-c/include/depthai_c.h) dan
dicerminkan persis oleh [`NativeMethods.cs`](../../src/DepthAI.Net.Core/Interop/NativeMethods.cs).

Keputusan desain yang dijaga:

- **Model pull, bukan callback ke .NET.** Callback queue depthai berjalan di thread depthai
  dan hanya mengisi antrean siap-ambil (maks. 64 paket, yang tertua dibuang). `dai_device_poll`
  menariknya. Tidak ada delegate managed yang dipanggil dari native.
- **Pipeline dikirim sebagai JSON.** Shim mengurai satu string dan membangun `dai::Pipeline`.
- **Exception tidak pernah menyeberang batas C.** Semuanya menjadi kode status, dan detailnya
  bisa dibaca lewat `dai_last_error` (per thread).
- **NN dijalankan sebagai `NeuralNetwork` mentah.** Tensor diurai di host oleh parser .NET yang
  sama dengan jalur simulasi, jadi hasil simulasi dan hardware konsisten.

### Pemetaan node

| Node .NET | depthai-core v3 | Catatan |
| --- | --- | --- |
| `ColorCamera` | `Camera` + `requestOutput` | `preview` sesuai ukuran/urutan warna; `video` NV12 seukuran sensor; `isp`; `still` = keluaran resolusi penuh |
| `MonoCamera` | `Camera` + `requestOutput` | ukuran dari resolusi |
| `StereoDepth` | `StereoDepth` | preset, LR-check, subpixel, extended, confidence, median, align, ukuran keluaran |
| `NeuralNetwork`, `DetectionNetwork`, `SpatialDetectionNetwork` | `NeuralNetwork` | model `.blob`, `.superblob`, atau NN Archive (`.tar.xz`) |
| `ImageManip` | `ImageManip` | crop ternormalisasi, rotasi, resize (crop tengah / stretch), format keluaran |
| `VideoEncoder` | `VideoEncoder` | MJPEG (lossy/lossless), H.264, H.265 |
| `Imu` | `IMU` | accelerometer, gyroscope, magnetometer, rotation vector |

Preview kamera yang dihubungkan ke node NN diminta dalam format **planar** (BGR888p), karena
model blob RVC2 mengharapkan CHW. Bila preview yang sama juga dialirkan ke host, shim
menyusunnya ulang menjadi interleaved, karena `ImageFrame` hanya mengenal interleaved.

### Format payload non-piksel

- **Tensor NN**: `int32 count`, lalu per tensor `int32 nameLen, name, int32 rank,
  int32 dims[rank], float32 data[]`. Tensor 4D selalu dikirim NCHW dan sudah di-dequantize.
- **IMU**: `float32[11]` = accel xyz, gyro xyz, rotasi i j k real, penanda rotasi.

## Membangun

```powershell
pwsh native/depthai-c/build.ps1        # atau: powershell -File native/depthai-c/build.ps1
dotnet pack src/DepthAI.Net.Runtime.win-x64
```

Skrip ini tidak membangun depthai-core dari sumber. Yang dilakukannya:

1. Mengunduh rilis prebuilt `depthai-core-v3.11.0-win64.zip` dari Luxonis.
2. Mengunduh installer resmi OpenCV 4.13.0 dan mengambil `opencv_world4130.dll` saja. DLL
   prebuilt depthai-core mengimpornya tapi tidak menyertakannya. Skrip memeriksa tabel impor
   supaya versi OpenCV yang dipakai pasti cocok.
3. Mengompilasi shim dengan MSVC, CMake, dan Ninja bawaan Visual Studio.
4. Menyalin semua DLL, termasuk runtime MSVC dari folder Redist Visual Studio, ke
   `artifacts/native/win-x64`.

Prasyarat: Visual Studio 2022+ dengan workload C++, dan 7-Zip. Keduanya tersedia di runner
GitHub `windows-latest`; CI dan workflow publish menjalankan skrip ini sebelum pack.

CMake memakai target impor buatan sendiri, bukan `find_package(depthai)`: config CMake rilis
prebuilt mewajibkan OpenCV, padahal shim tidak memakai API OpenCV sama sekali.

## Batasan yang perlu diketahui

- **Hanya Windows x64.** Luxonis hanya merilis paket C++ prebuilt untuk win64. Untuk Linux,
  depthai-core perlu dibangun dari sumber; shim dan `CMakeLists.txt`-nya sudah portabel
  (`DEPTHAI_CORE_DIR` menunjuk hasil `cmake --install`), tapi paket `linux-x64` belum dibuat.
- **Posisi 3D deteksi spasial belum dihitung**, baik di hardware maupun di simulasi. Link
  kedalaman ke node spasial diterima tapi diabaikan.
- **Stop pipeline menutup perangkat** (perilaku depthai v3). Shim membukanya ulang supaya handle
  tetap bisa dipakai, dan proses ini memakan beberapa detik.
- **H.264/H.265** keluar sebagai paket encoded tanpa format piksel. Stream .NET-nya belum punya
  tipe frame khusus untuk bitstream video; MJPEG sudah didukung penuh.
- `DeviceOpenOptions.BootTimeout` diabaikan (depthai memakai bawaannya sendiri).
  `FirmwarePath` dan `MaxUsbSpeed` didukung.

## Memverifikasi

```bash
depthai-dotnet-cli info                              # baris "Runtime native" → tersedia (depthai-core 3.11.0)
depthai-dotnet-cli devices list --require-hardware   # gagal keras alih-alih jatuh ke simulasi
depthai-dotnet-cli devices info                      # sensor, kecepatan USB, suhu chip
```
