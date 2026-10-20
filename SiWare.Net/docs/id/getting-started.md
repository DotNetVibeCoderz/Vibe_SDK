# Memulai

*SiWare.NET, dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/getting-started.md)

## Kebutuhan

| Perangkat | Versi | Dibutuhkan untuk |
|---|---|---|
| .NET SDK | 10.0.100 atau lebih baru (`global.json` mengikuti feature band terbaru) | Semua komponen |
| Toolchain Rust | 1.87 atau lebih baru (edition 2024) | Kernel DSP native (opsional) |
| Windows x64 | n/a | Hanya jembatan vendor; SDK, simulator, dan aplikasi bersifat lintas platform |
| CPython 3.10 x64 | 3.10.x | Hanya jembatan vendor (modul vendor dibuat untuk `cp310-win_amd64`) |
| Node.js + Playwright | Node 20+ | Hanya smoke test UI dan skrip tangkapan layar |

## Build dan pengujian

```sh
# 1. Opsional: kernel native. Build mode release; build .NET otomatis menyalin library-nya.
cd native/siware-dsp
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test
cargo build --release
cd ../..

# 2. Solusi .NET (peringatan dianggap galat)
dotnet build SiWare.Net.slnx
dotnet test SiWare.Net.slnx
dotnet test tests/SiWare.Sdk.Tests --filter "FullyQualifiedName~ProcessingTests"
```

Rust opsional untuk menjalankan aplikasi, tetapi wajib untuk seluruh suite pengujian. Pengujian managed saja dapat dijalankan dengan `dotnet test --filter "FullyQualifiedName!~NativeDspTests"`. Tanpa langkah 1, `NativeDsp.IsAvailable` bernilai `false`, dan aplikasi memakai implementasi managed. Build ulang proyek .NET setelah membangun library Rust agar library tersebut tersalin ke samping assembly.

## Menjalankan Workbench

```sh
dotnet run --project samples/SiWare.Workbench
```

Buka `http://localhost:5069`, lalu:

1. Klik **Hubungkan instrumen**. Simulator dipakai kecuali `SIWARE_VENDOR_CONFIG` diisi (lihat [Integrasi vendor](vendor-integration.md)).
2. Pada **Kalibrasi**, rekam **Sinyal dark** dan **Referensi**.
3. Klik **Rekam sampel**, pilih hasilnya, lalu tekan **Absorbansi**. **SNV** dan **Perhalus** berlaku untuk spektrum apa pun yang dipilih.
4. Arahkan kursor ke grafik, atau fokuskan grafik lalu tekan ←/→/Home/End untuk membaca nilai.
5. Ekspor spektrum terpilih sebagai **CSV** atau **JSON**. Sesi menyimpan 100 spektrum terakhir di memori.

Tombol **EN / ID** mengganti bahasa, dan pilihan itu diingat oleh browser. Akuisisi dapat dibatalkan saat berjalan dan otomatis berhenti setelah 3 menit.

![Workbench](../screenshots/workbench-desktop-id.png)

## Menggunakan CLI

```sh
dotnet run --project samples/SiWare.Cli -- help
dotnet run --project samples/SiWare.Cli -- info                       # versi .NET dan ketersediaan ABI Rust
dotnet run --project samples/SiWare.Cli -- demo --output artifacts/demo
dotnet run --project samples/SiWare.Cli -- scan --output spectrum.json
dotnet run --project samples/SiWare.Cli -- process --input spectrum.json --operation snv --output snv.json
dotnet run --project samples/SiWare.Cli -- discover --vendor-config vendor.json
```

`demo` hanya memakai simulasi dan menulis `dark.json`, `reference.json`, `sample.json`, `absorbance.json`, serta `absorbance.csv`. `scan` dan `discover` memakai driver vendor bila `--vendor-config` diberikan. Kode keluar: `0` sukses, `1` input tidak valid atau galat perangkat, `2` dibatalkan (Ctrl+C) atau melewati batas waktu.

![CLI](../screenshots/cli.png)

## Membuat paket NuGet

```sh
cargo build --release --manifest-path native/siware-dsp/Cargo.toml
dotnet pack src/SiWare.Sdk -c Release -o artifacts/packages
```

Hasilnya adalah `Gravicode.SiWare.Sdk.<versi>.nupkg`, yang berisi:

- `lib/net10.0/SiWare.Sdk.dll` beserta dokumentasi XML-nya
- `runtimes/win-x64/native/siware_dsp.dll`, hanya jika library release sudah ada saat pack
- `contentFiles/any/any/SiWare/vendor_bridge.py`, yang disalin ke folder output pemakai sebagai `SiWare/vendor_bridge.py`
- Ikon (`docs/assets/siware-net-icon.svg`, dirender dengan `node scripts/render-icon.mjs`), README, dan lisensi MIT

Untuk memakainya secara lokal, tambahkan `artifacts/packages` sebagai sumber paket di `nuget.config`, lalu referensikan `Gravicode.SiWare.Sdk` dengan opsi prerelease. Jika Anda mem-pack ulang versi yang sama, hapus dulu `~/.nuget/packages/gravicode.siware.sdk`. Publikasi publik dibatasi; lihat [Validasi perangkat keras](hardware-validation.md).

## Smoke test UI dan tangkapan layar

```sh
dotnet run --project samples/SiWare.Workbench --urls http://127.0.0.1:5088
node scripts/ui-smoke.mjs        # di terminal kedua; isi SIWARE_TEST_URL untuk alamat lain
node scripts/cli-screenshot.mjs  # membuat ulang docs/screenshots/cli.png dari output CLI asli
```

Smoke test menjalankan seluruh alur di Chrome headless (hubungkan, kalibrasi, akuisisi, proses, inspeksi, ekspor, batal, ganti bahasa, tata letak mobile). Setelah itu, tangkapan layar Workbench di `docs/screenshots/` diperbarui. Pasang Playwright dengan `npm install` di `.tools/ui`, dan isi `SIWARE_BROWSER_CHANNEL` jika Chrome tidak terpasang.
