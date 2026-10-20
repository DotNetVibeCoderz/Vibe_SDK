# SiWare DSP / Pemrosesan Spektrum

Dependency-free Rust spectral kernels with C ABI version 1. Created by Gravicode Studios, led by Kang Fadhil.

Kernel spektrum Rust tanpa dependensi dengan C ABI versi 1. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

## Build and validate / Build dan validasi

Run from this directory / Jalankan dari direktori ini:

```sh
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test
cargo build --release
```

Windows output: `target/release/siware_dsp.dll`; Linux: `libsiware_dsp.so`; macOS: `libsiware_dsp.dylib`. Match native architecture to the .NET process. Sesuaikan arsitektur native dengan proses .NET. C declarations: `include/siware_dsp.h`.

## Operations / Operasi

- SNV uses sample standard deviation (`N - 1`), requires at least two values, and rejects constant spectra. SNV memakai simpangan baku sampel; spektrum konstan ditolak.
- Absorbance computes `-log10((sample-dark)/(reference-dark))` using logarithm differences to avoid ratio overflow. Both corrected signals must be positive and finite. Kedua sinyal setelah koreksi dark wajib positif dan hingga.
- Moving average accepts an odd window from 1 through spectrum length. Edge windows are truncated; runtime is O(N). Rata-rata bergerak memakai jendela ganjil, dipotong pada batas spektrum.

## Memory and errors / Memori dan galat

Return codes: `0` success/sukses, `1` invalid argument or allocation failure/argumen tidak valid atau alokasi gagal, `2` nonfinite value or numerical domain error/galat domain numerik. Every input must contain finite doubles. Output is unchanged on errors. Semua input wajib berupa double hingga; output tidak berubah jika gagal.

Caller supplies aligned, valid buffers of `len` doubles. Output must be disjoint from every input; read-only inputs may overlap. Length overflow, null pointers, misalignment and overlap are rejected, but allocation validity cannot be checked. No concurrent input mutation or output access. Pemanggil wajib menjamin validitas dan masa hidup alokasi; output tidak boleh tumpang tindih dengan input.

One temporary result allocation ensures transactional output. Rust panics are caught at the ABI boundary; process-level allocation aborts and invalid-pointer faults cannot be recovered. Alokasi hasil sementara menjaga output tetap utuh jika validasi gagal. Tidak ada alokasi yang kepemilikannya berpindah melintasi ABI.
