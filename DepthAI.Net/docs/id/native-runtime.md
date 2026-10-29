# Runtime native / Native runtime

Dokumen ini menjelaskan bagaimana DepthAI.Net berbicara dengan hardware OAK sungguhan:
shim `depthai-c`, cara memasangnya, cara membangunnya, dan batasannya.

## Keadaan sekarang

| Kemampuan | Windows x64 | Linux x64 | Linux ARM64 | macOS |
| --- | --- | --- | --- | --- |
| Deteksi perangkat USB (ada/tidak, tahap boot, MxId) | ✅ tanpa pustaka native | ✅ sysfs | ✅ sysfs | ❌ |
| Runtime native termuat (`dai_get_version`, ABI cocok) | ✅ | ✅ diuji di CI | ✅ diuji di CI | ❌ belum ada paket |
| Membuka perangkat dan streaming | ✅ diuji dengan OAK-1 | ⚠️ belum diuji dengan kamera | ⚠️ belum diuji dengan kamera | ❌ |
| Seluruh SDK di atas backend simulasi | ✅ | ✅ | ✅ | ✅ |

Linux ARM64 mencakup Raspberry Pi 4/5 dengan OS 64-bit (Raspberry Pi OS Bookworm) dan
Jetson Orin dengan JetPack 6. JetPack 5 (Ubuntu 20.04, glibc 2.31) terlalu lama.

Runtime Linux berasal dari kode shim yang sama dengan Windows dan depthai-core versi yang
sama, dan CI membuktikan pustakanya termuat dari paket NuGet maupun tool CLI. Tapi belum
pernah ada kamera yang dibuka lewat runtime Linux, karena mesin pengembangan berjalan di
Windows. Anggap Linux siap dicoba, belum terverifikasi end-to-end.

Diverifikasi pada perangkat sungguhan di Windows (OAK-1, MxId `14442C10011298CD00`, IMX378, USB 3):

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
<PackageReference Include="DepthAI.Net" Version="0.6.0" />
<PackageReference Include="DepthAI.Net.Runtime.win-x64" Version="0.6.0"
                  Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
<PackageReference Include="DepthAI.Net.Runtime.linux-x64" Version="0.6.0"
                  Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
<PackageReference Include="DepthAI.Net.Runtime.linux-arm64" Version="0.6.0"
                  Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
```

Di Linux, rujuk paket x64 dan arm64 sekaligus. Aplikasi .NET portabel membawa keduanya di
`runtimes/<rid>/native`, dan host memilih yang cocok dengan arsitektur mesin. Aplikasi yang
dipublikasikan untuk satu RID (`dotnet publish -r linux-arm64`) hanya membawa satu.

Tidak ada konfigurasi lain. `DepthAiDevice.OpenAsync()` memakai hardware bila runtime
native termuat dan ada perangkat, selain itu jatuh ke simulasi. Untuk memaksa hardware:

```csharp
await using var device = await DepthAiDevice.OpenAsync(
    new DepthAiOptions { Backend = BackendSelection.NativeOnly });
```

| Paket | Isi | Ukuran |
| --- | --- | --- |
| `DepthAI.Net.Runtime.win-x64` | `depthai-c.dll`, `depthai-core.dll` 3.11.0, `opencv_world4130.dll`, `dynamic_calibration.dll`, `libusb-1.0.dll`, runtime MSVC app-local (tidak butuh VC++ Redistributable) | ±54 MB |
| `DepthAI.Net.Runtime.linux-x64` | `libdepthai-c.so`, `libdepthai-core.so` 3.11.0 (tanpa OpenCV), `libusb-1.0.so`, semuanya dengan RUNPATH `$ORIGIN` | ±22 MB |
| `DepthAI.Net.Runtime.linux-arm64` | isi yang sama, dibangun native di ARM64 | ±22 MB |

Linux x64 dan ARM64 butuh **glibc 2.35 dan libstdc++ GLIBCXX_3.4.30** atau lebih baru (Ubuntu 22.04,
Debian 12, Fedora 36, dan sesudahnya). Selain itu hanya `libc`, `libm`, `libstdc++`, dan
`libgcc_s` dari sistem; libudev tidak dibutuhkan.

Di Linux, akses USB tanpa root butuh aturan udev untuk vendor Movidius (03e7):

```bash
echo 'SUBSYSTEM=="usb", ATTRS{idVendor}=="03e7", MODE="0666"' | sudo tee /etc/udev/rules.d/80-movidius.rules
sudo udevadm control --reload-rules && sudo udevadm trigger
```

Tanpa aturan ini perangkat tetap terlihat, tapi dilaporkan `Faulty`, dan membukanya gagal
dengan pesan yang berisi perintah di atas.

Template `dotnet new` dan proyek buatan Jack The Code Bender sudah menyertakan referensi ini.
Di dalam repo, `DepthAI.Net.Core.csproj` menyalin pustaka dari `artifacts/native/<rid>` (bila
sudah dibangun) ke setiap proyek yang mereferensikannya: CLI, Wizard, sample, dan tes. Windows
disalin rata ke folder output; Linux disalin ke `runtimes/<rid>/native`, karena x64 dan arm64
memakai nama berkas yang sama, dan `NativeLibraryResolver` memuatnya dari sana. Tool CLI
membawa ketiga platform, jadi `depthai-dotnet-cli` membaca kamera di Windows x64, Linux x64,
dan Linux ARM64 (paketnya ±100 MB).

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
| `HostInput` | `Input::createInputQueue` pada masukan tujuannya | gambar dari `dai_device_send_frame`; ke NN dikirim planar |

Preview kamera yang dihubungkan ke node NN diminta dalam format **planar** (BGR888p), karena
model blob RVC2 mengharapkan CHW. Bila preview yang sama juga dialirkan ke host, shim
menyusunnya ulang menjadi interleaved, karena `ImageFrame` hanya mengenal interleaved.

`HostInput` tidak punya padanan node: shim membuat antrean masukan (4 slot, non-blocking) pada
port yang disambungkan ke sana. `dai_device_send_frame` menyalin gambar interleaved dari host,
mengubahnya ke planar bila tujuannya neural network, lalu mengirimnya dengan nomor urut dari
pemanggil. NN menyalin nomor urut itu ke tensornya, dan dari situ `InferAsync` memasangkan hasil
dengan gambarnya. Diverifikasi di OAK-1: YOLOv6-nano pada foto 1280×714 menjawab dalam ±40 ms.

### Format payload non-piksel

- **Tensor NN**: `int32 count`, lalu per tensor `int32 nameLen, name, int32 rank,
  int32 dims[rank], float32 data[]`. Tensor 4D selalu dikirim NCHW dan sudah di-dequantize.
- **IMU**: `float32[11]` = accel xyz, gyro xyz, rotasi i j k real, penanda rotasi.

## Membangun

### Windows x64

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

### Linux x64 dan ARM64

```bash
sudo apt-get install ninja-build g++-12 pkg-config libudev-dev patchelf zip unzip \
  autoconf automake autoconf-archive libtool
CC=gcc-12 CXX=g++-12 native/depthai-c/build.sh
dotnet pack src/DepthAI.Net.Runtime.linux-x64     # atau linux-arm64, sesuai mesin
```

`build.sh` mengikuti arsitektur mesin tempatnya dijalankan (`uname -m`) dan tidak
melakukan cross-compile. Di ARM64, vcpkg diminta memakai cmake dan ninja dari sistem
(`VCPKG_FORCE_SYSTEM_BINARIES`), karena vcpkg tidak menyediakan binari prebuilt untuk ARM Linux.

Luxonis tidak merilis paket C++ prebuilt untuk Linux, jadi skrip ini membangun depthai-core
dari sumber dengan vcpkg bawaannya. Fitur yang tidak dipakai shim dimatikan: OpenCV (beserta
ffmpeg, bagian build paling lama), dynamic calibration, protobuf, curl, dan AprilTag.
Firmware perangkat tetap di-embed. Di runner GitHub build pertama memakan waktu ±9–13 menit;
hasil install-nya di-cache, jadi build berikutnya hanya mengompilasi shim.

Skrip lalu menelusuri `ldd` dari `libdepthai-c.so`, menyalin setiap pustaka yang berasal
dari hasil build dengan nama SONAME-nya (NuGet tidak menyimpan symlink), mengarahkan RUNPATH
ke `$ORIGIN` dengan `patchelf`, dan gagal bila ada dependensi yang tidak ditemukan tanpa
`LD_LIBRARY_PATH`. Di Linux, CMake memakai `find_package(depthai)`, karena tanpa OpenCV
config resminya bisa dipakai dan definisi kompilasinya dijamin cocok.

CI membangun runtime Linux di Ubuntu 22.04, x64 di runner biasa dan arm64 di runner ARM64
GitHub (`ubuntu-22.04-arm`), lalu menguji paket hasil pack di aplikasi baru dan tool CLI yang
di-install, di Ubuntu x64, Ubuntu ARM64, dan Windows.

## Batasan yang perlu diketahui

- **Linux (x64 dan ARM64) belum diuji dengan kamera.** Runtime-nya termuat dan ABI-nya cocok
  (diuji di CI), tapi membuka perangkat dan streaming di Linux belum pernah dicoba.
- **Belum ada macOS**, dan belum ada ARM 32-bit (Raspberry Pi OS 32-bit).
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
