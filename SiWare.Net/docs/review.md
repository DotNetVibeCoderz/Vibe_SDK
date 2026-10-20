# Code review / Pemeriksaan kode

Reviewed 2026-10-20 after the request to inspect existing changes before continuing. This directory has no Git metadata, so the review covers the current files rather than a commit diff.

## Corrections

- Calibration now compares recorded acquisition settings as well as sensor identity, provenance, and wavelength grid. Changing integration duration or averaging cannot silently reuse incompatible calibration. Newly acquired spectra record settings; JSON and derived results preserve them. Legacy/CSV spectra without settings can only match other spectra without settings; callers must verify acquisition compatibility themselves.
- Managed SNV, absorbance, and smoothing use the same stable numerical approach as Rust, including extreme finite values. Smoothing is linear in point count.
- Vendor status polling tolerates process cleanup; returned sensor identity, measurement kind, point count, and wavelength units are checked.
- Cancellation returns HTTP 409. HTTP 408 can trigger browser retries and inadvertently restart an acquisition.
- Rust is optional at application runtime, but required by the complete native integration test suite. Managed-only test filtering is documented. The crate declares Rust 1.87 because it uses [`usize::is_multiple_of`](https://doc.rust-lang.org/std/primitive.usize.html#method.is_multiple_of).
- Formatting and documentation exceptions were aligned with the code. Vendor logs are excluded from version control.

## Validation

- Release build with warnings as errors.
- 90 xUnit cases and 9 Rust cases; strict Clippy and formatting checks.
- Browser: connect, dark/reference/sample, absorbance, SNV, smoothing, keyboard chart inspection, history, CSV/JSON downloads, cancellation, EN/ID, mobile width.
- CLI demo exports the full calibration pipeline.
- Local NuGet consumer uses an isolated package cache and disables repository build targets: native loading, simulator acquisition metadata, and packaged Python worker are verified from package assets.

## Remaining boundary

No physical sensor was used. Native binding metadata inspection verifies Python interfaces, not scan-time units, mode semantics, discovery reliability, or real measurements. Those remain explicit hardware validation tasks. The adapter supports sample and reference scans; dark scans and device configuration/firmware updates are not implemented. The core implementation does not depend on the optional Science/Visual/HF libraries.

## Bahasa Indonesia

Pemeriksaan dilakukan pada berkas saat ini karena folder belum memiliki metadata Git. Kalibrasi kini menolak pengaturan akuisisi yang berbeda; metadata disimpan pada JSON dan hasil pemrosesan. Perhitungan managed dan Rust konsisten, pembatalan browser tidak memulai ulang akuisisi, serta dokumentasi kebutuhan toolchain telah diperbaiki. Build, pengujian, alur UI, CLI, dan penggunaan paket NuGet lokal lulus. Kompatibilitas perangkat nyata tetap memerlukan sensor dan konfirmasi parameter vendor.
