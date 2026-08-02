# Runtime native / Native runtime

Dokumen ini menjelaskan apa yang masih kurang untuk menjalankan hardware OAK sungguhan,
dan bagaimana melengkapinya.

## Keadaan sekarang

| Kemampuan | Status |
| --- | --- |
| Deteksi perangkat USB (ada/tidak, tahap boot, MxId) | ✅ Berfungsi tanpa depthai-core |
| Membuka perangkat, mengunggah pipeline, streaming | ❌ Butuh `depthai-c` |
| Seluruh SDK di atas backend simulasi | ✅ Berfungsi penuh |

Verifikasi pada perangkat sungguhan selama pengembangan:

```
Perangkat  : OAK-1 (Movidius MyriadX, RVC2)
MxId       : 14442C10011298CD00
Sensor     : CAM_A / IMX378, autofocus, sampai 4056x3040
USB        : SuperSpeed
Terdeteksi : ya, oleh UsbDeviceScanner tanpa pustaka native apa pun
Dibuka     : belum — butuh depthai-c
```

## Kenapa butuh lapisan shim

depthai-core adalah pustaka C++ yang memaparkan template, `std::shared_ptr`, `std::vector`,
dan tipe STL lain pada permukaan API-nya. Tipe-tipe itu tidak punya ABI stabil: tata letaknya
berbeda antar kompiler, antar versi kompiler yang sama, bahkan antar mode debug dan release.

P/Invoke membutuhkan ABI yang stabil. Jadi jalurnya bukan .NET → C++ langsung, melainkan:

```
DepthAI.Net  →  P/Invoke  →  depthai-c (shim C, tipe POD)  →  depthai-core (C++)
```

Shim-nya tipis: ia hanya meratakan tipe C++ menjadi struct dan pointer polos.

## ABI yang harus dipenuhi

Binding sudah ditulis dan menunggu implementasi. Lihat
[`NativeMethods.cs`](../../src/DepthAI.Net.Core/Interop/NativeMethods.cs) untuk
tanda tangan yang tepat. Ringkasnya:

```c
// Siklus hidup dan info
int  dai_get_version(char* buffer, int length);
int  dai_last_error(char* buffer, int length);
int  dai_device_list(dai_device_info_t* buffer, int capacity, int* count);
int  dai_device_open(const char* mxid, const dai_open_options_t* options, void** handle);
int  dai_device_close(void* handle);
int  dai_device_capabilities(void* handle, dai_capabilities_t* out);

// Pipeline
int  dai_device_upload_model(void* handle, const char* name, const uint8_t* data, int64_t length);
int  dai_device_start_pipeline(void* handle, const char* pipeline_json);
int  dai_device_stop_pipeline(void* handle);

// Aliran data — model tarik, bukan callback
int  dai_device_poll(void* handle, dai_packet_t* out, int timeout_ms);
int  dai_packet_release(void* handle, void* native_handle);

// Telemetri
int  dai_device_telemetry(void* handle, dai_telemetry_t* out);
```

Dua keputusan desain yang perlu dipertahankan implementor:

**Model tarik, bukan callback.** `dai_device_poll` menarik satu paket dan mengembalikan
`DAI_TIMEOUT` bila tidak ada. Callback dari native ke managed pada jalur panas mengharuskan
delegate tetap hidup dan bisa bermasalah saat GC memindahkan thread; polling menghindari
seluruh kelas masalah itu.

**Pipeline dikirim sebagai JSON.** Shim tidak perlu memaparkan API graf; ia cukup mem-parsing
satu string dan membangun `dai::Pipeline` di sisi C++. Skemanya ada di
[`pipeline.schema.json`](../../tools/vscode-depthai/schemas/pipeline.schema.json).

## Membangun shim

Prasyarat yang **tidak ada** di mesin pengembangan saat ini:

- CMake 3.20+
- Kompiler C++ (MSVC Build Tools, GCC, atau Clang)
- depthai-core beserta dependensinya

Langkah kasarnya:

```bash
git clone --recursive https://github.com/luxonis/depthai-core
cd depthai-core
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON
cmake --build build --config Release

# Lalu kompilasi shim terhadap depthai-core dan hasilkan:
#   Windows : depthai-c.dll
#   Linux   : libdepthai-c.so
#   macOS   : libdepthai-c.dylib
```

Letakkan pustaka hasilnya di samping aplikasi, atau di direktori mana pun pada `PATH`
(`LD_LIBRARY_PATH` pada Linux). SDK memeriksanya saat dipakai pertama kali dan otomatis
memakainya begitu bisa dimuat — tidak ada konfigurasi yang perlu diubah.

Untuk distribusi, kemas per RID sebagai paket `DepthAI.Net.Runtime.<rid>` dengan aset native
di `runtimes/<rid>/native/`.

## Memverifikasi

Setelah pustaka terpasang:

```bash
depthai-dotnet-cli info
```

Baris `Runtime native` akan berubah menjadi `tersedia`, dan `devices list` akan menampilkan
perangkat fisik sebagai perangkat yang bisa dibuka, bukan lagi di bagian terpisah.

Untuk memaksa kegagalan alih-alih jatuh ke simulasi selama pengujian:

```bash
depthai-dotnet-cli devices list --require-hardware
```

## Sementara itu

Backend simulasi bukan stub: ia menghasilkan frame warna, peta kedalaman yang konsisten
secara geometris dengan frame warna itu, dan tensor neural network dalam tata letak asli
MobileNet-SSD maupun YOLO. Tensor itu diurai oleh parser yang sama persis dengan yang dipakai
hardware. Artinya kode aplikasi, pipeline, parser, dan UI semuanya bisa dikembangkan dan
diuji sekarang; yang berubah nanti hanya sumber datanya.
