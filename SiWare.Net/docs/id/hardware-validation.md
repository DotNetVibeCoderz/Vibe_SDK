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
| Satuan `scan_time` dan arti `mode` | **Belum diketahui** | Harus berasal dari dokumentasi Si-Ware |
| Satuan sumbu `wvn` adalah cm⁻¹ | **Diasumsikan, dikonfirmasi pengguna** | Flag konfigurasi wajib |
| Penemuan, akuisisi, dan penutupan pada sensor nyata | **Belum divalidasi** | Tidak ada perangkat selama pengembangan |
| Akurasi pengukuran dibanding instrumen acuan | **Belum divalidasi** | |
| Akuisisi dark melalui API vendor | **Tidak didukung** | Tidak ditemukan operasinya |

Nilai enum adalah nilai enum Python, bukan opcode kabel yang terverifikasi. Impor yang berhasil membuktikan kompatibilitas biner, bukan kompatibilitas sensor.

## Daftar periksa validasi perangkat keras

Jalankan langkah-langkah berikut dengan sensor yang didukung, lalu catat hasilnya di `Progress.md` (model perangkat, firmware dari `read_fw_version`, SHA-256 driver, dan tanggal):

1. Konfirmasikan `scanTimeUnit` dan `mode` ke dokumentasi atau dukungan Si-Ware.
2. `siware discover --vendor-config …` menampilkan ID perangkat yang diharapkan. Mencabut lalu memasang ulang sensor tercermin di hasilnya.
3. Rekam **Reference** dan **Sample** untuk setiap jumlah titik (65 sampai 4096). Cocokkan rentang sumbu dengan datasheet sensor, dan pastikan sumbu tetap monoton setelah dikonversi ke nm.
4. Bandingkan dengan pengukuran yang sama di Si-Spect: pengaturan sama, serta nilai PSD dan sumbunya cocok dalam batas derau yang wajar.
5. Ulangi 100 akuisisi dan periksa kebocoran sumber daya (jumlah proses worker, handle, memori).
6. Batalkan di tengah akuisisi dan cabut kabel di tengah akuisisi. Aplikasi wajib menampilkan galat, dan worker wajib sudah berhenti.
7. Validasi absorbansi dengan material acuan yang diketahui setelah metode dark/zero yang terverifikasi tersedia.

## Gerbang rilis

Publikasi publik `Gravicode.SiWare.Sdk` di NuGet mensyaratkan:

- [ ] Daftar periksa perangkat keras selesai pada minimal satu sensor yang didukung
- [ ] Identitas paket ditinjau (nama dan hubungannya dengan merek dagang Si-Ware), dan lisensi dikonfirmasi
- [ ] Versi baru keluar dari `-preview` setelah validasi perangkat keras
- [ ] Dokumentasi diperbarui dengan model perangkat dan pengaturan yang sudah divalidasi

Sampai saat itu, buat paket secara lokal (`dotnet pack`, lihat [Memulai](getting-started.md)). Kredensial publikasi wajib berada di luar repositori.
