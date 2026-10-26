# Validasi perangkat keras

*SiWare.NET, dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/hardware-validation.md)

Halaman ini mencatat apa yang sudah dibuktikan, apa yang belum, dan apa yang harus dilakukan sebelum SiWare.NET mengklaim kompatibel dengan perangkat keras Si-Ware atau dipublikasikan secara umum.

## Tingkat bukti

| Klaim | Status | Bukti |
|---|---|---|
| Driver vendor adalah ekstensi CPython 3.10 x64 | **Terverifikasi** | Header PE dan string ([driver-static.md](../research/driver-static.md)) |
| Nama metode, urutan argumen, bentuk nilai kembali, nilai enum | **Terverifikasi** (level Python) | Introspeksi saat impor tanpa membuat driver ([vendor-api.md](../research/vendor-api.md)) |
| `Error.value() == 0` berarti sukses | **Terverifikasi** | Objek `Error()` berisi data saja |
| Protokol jembatan, validasi, batas waktu, pembersihan proses | **Diuji** | `vendor_bridge.py --self-test`, `VendorValidationTests` |
| Simulator, model, pemrosesan, format berkas, ABI Rust | **Diuji** | 86 tes xUnit, 9 tes Rust, smoke test UI |
| `scan_time` dalam detik | **Inferensi kuat** | UI vendor memberi label "sec"; semua pemindaian contoh vendor memakai 2 ([driver-ghidra.md](../research/driver-ghidra.md)) |
| Arti `mode` | **Belum diketahui** | Hanya terkait dengan format permintaan "default" dan "compatibility" pada driver |
| Transport: penemuan lewat UDP, lalu TCP melalui USB RNDIS/NCM atau Wi-Fi | **Teramati** | String driver dan berkas INF yang disertakan |
| Satuan sumbu `wvn` adalah cm⁻¹ | **Teramati pada data vendor** | 138 spektrum contoh vendor mencakup 3921,57–7407,41 cm⁻¹ (= 2550–1350 nm) |
| Penemuan perangkat dan akuisisi pada sensor nyata | **Tervalidasi** | Si-NIR Matrix DVK (USB NCM `303A:4000`) di Windows 10, 2026-10-26, dengan konfigurasi vendor kit. Sensor USB baru muncul sekitar 6 detik setelah driver mulai, sehingga jendela penemuan dinaikkan menjadi 20 detik |
| Driver USB di Windows 11 build 26200 | **Gagal** | `siwarenirusbdriver.inf` 1.0.0.2 dari vendor tidak bertanda tangan Microsoft dan mengembalikan `STATUS_NOT_SUPPORTED`; driver NCM bawaan melaporkan galat perangkat keras. Gunakan Windows 10 |
| Akurasi pengukuran dibanding instrumen acuan | **Belum divalidasi** | |
| Akuisisi dark melalui API vendor | **Tidak ada** | Binding tidak memiliki operasi dark; absorbansi memakai referensi saja |

Nilai enum adalah nilai enum Python, bukan opcode kabel yang terverifikasi. Impor yang berhasil membuktikan kompatibilitas biner, bukan kompatibilitas sensor.

## Daftar periksa validasi perangkat keras

Jalankan langkah-langkah berikut dengan sensor yang didukung, lalu catat hasilnya di `Progress.md` (model perangkat, firmware dari `read_fw_version`, SHA-256 driver, dan tanggal):

1. Konfirmasikan `mode` ke dokumentasi atau dukungan Si-Ware, dan periksa bahwa `scan_time` 2 benar-benar berlaku sebagai 2 detik.
2. `siware discover --vendor-config …` menampilkan ID perangkat yang diharapkan. Mencabut lalu memasang ulang sensor tercermin di hasilnya.
3. Rekam **Reference** dan **Sample** untuk setiap jumlah titik (65 sampai 4096). Cocokkan rentang sumbu dengan datasheet sensor, dan pastikan sumbu tetap monoton setelah dikonversi ke nm.
4. Bandingkan dengan pengukuran yang sama di Si-Spect: pengaturan sama, serta nilai PSD dan sumbunya cocok dalam batas derau yang wajar.
5. Ulangi 100 akuisisi dan periksa kebocoran sumber daya (jumlah proses worker, handle, memori).
6. Batalkan di tengah akuisisi dan cabut kabel di tengah akuisisi. Aplikasi wajib menampilkan galat, dan worker wajib sudah berhenti.
7. Validasi absorbansi dengan material acuan yang diketahui setelah metode dark/zero yang terverifikasi tersedia.

## Gerbang rilis

Publikasi publik `Gravicode.SiWare.Sdk` di NuGet mensyaratkan:

- [x] Penemuan perangkat dan akuisisi tervalidasi pada satu sensor yang didukung (Si-NIR Matrix DVK, Windows 10). Butir 3–7 daftar periksa tetap disarankan sebagai tindak lanjut.
- [x] Identitas paket (`Gravicode.SiWare.*`) dan lisensi MIT disetujui pemilik
- [x] Versi keluar dari `-preview`: 0.1.0
- [x] Dokumentasi diperbarui dengan perangkat dan pengaturan yang sudah divalidasi

Publikasi berjalan lewat `.github/workflows/siware-net-publish.yml`: push tag `siware-net-v<versi>` (atau jalankan manual dari tab Actions). Workflow ini membangun DSP Rust, menjalankan semua tes, membuat empat paket, lalu mendorongnya dengan secret repository `NUGET_API_KEY`.

Sampai saat itu, buat paket secara lokal (`dotnet pack`, lihat [Memulai](getting-started.md)). Kredensial publikasi wajib berada di luar repositori.
