# Vendor driver: Ghidra analysis and installation evidence

Investigated 2026-10-20, read-only, with REA 6.7.0 and Ghidra 12.1.4 (default analyzers). The vendor code was not executed, and no vendor code is reproduced here. Target: `C:\Program Files (x86)\Si-Spect Platform\Analyze\pyDriver.pyd`, SHA-256 `5ca6bd73b6d99e9875a1ab55ee741bb9b4404061129aad62c5987097fd12defa`. The same installation's UI files, sample templates, database schema, and driver INF files were read as supporting evidence.

Labels: **Observed** means seen directly in the artifact. **Inferred** is a conclusion from observed facts. **Unknown** means not established.

## Answers

| Question | Answer | Confidence |
|---|---|---|
| Unit of `scan_time` | **Seconds.** The vendor's Calibrate UI shows `device_settings.scan_time` as `scanTime + " sec"` (`Calibrate/gui/qml/components/ModelDetailsDialog.qml:673`). The user settings table stores `"Scan Time" REAL`. All 138 vendor sample spectra (`Calibrate/gui/assets/templates/*_sample_data.csv`) use `Scan Time = 2`. | Strong inference. The UI label refers to the same setting, but the value was not traced through the binding into the wire request. |
| Wavenumber axis unit | **cm⁻¹.** Every vendor sample has a 257-point axis from 3921.57 to 7407.41, which equals 2550–1350 nm. | Observed in vendor data. |
| Meaning of `mode` | **Unknown.** The parameter is named `mode` (observed binding argument string at `0x18030aae4`). Leads: the driver has two request "contracts" (`default_format` with `DefaultFormatSensorOperationIds`, and `compatibility` with `CompatibilityModeRequest`); the strings mention "default mode" measurements and "super mode ports"; the vendor app stores a separate `"Run Mode" TEXT`. None of these establishes the integer values. | Unresolved. Decompiling the 65 KB nanobind initialiser crashed the Ghidra session. |
| Dark acquisition | **None exists.** The binding exposes `run_background` (reference), `read_background`, `run_psd`, `run_absorbance`, `run_interferogram`, and `*_with_accessory` variants, but no dark operation and no "dark" string. This fits a Fourier-transform instrument, where the detector offset sits at the interferogram's DC term rather than in the PSD. | Observed absence in the binding, plus an inference about the physics. |
| Discovery and transport | **TCP/IP over USB network adapters.** The driver broadcasts over UDP on every interface (`SocketBroadCastManager`, "Got Broadcast data from {} [{}] with if idx {}"), then opens TCP connections to the ports each module reports ("Module {} connected on ports {}"). It uses asio IOCP, a 30 s reconnect wait, and a `connect_to_wifi_op`. | Observed in strings and RTTI. |

## Hardware interfaces (driver folder)

| Driver | USB ID | Function |
|---|---|---|
| `driver/x64/Si-NIR.inf` | VID `2D15`, PID `F317`, MI_00 | RNDIS (network over USB). `install.bat` then runs `setIP.ps1`, which gives the host adapter (MAC `48:6F:73:74:50:43`) the static address **192.168.137.1/24**. |
| `driver/p10_x64/siwarenirusbdriver.inf` | VID `303A` (Espressif), PID `4000`, MI_00 | USB NCM (network over USB) for ESP-based "P10" devices. |
| `driver/x64/CP210x_…` | Silicon Labs CP210x | USB-UART bridge (service/serial). |
| `driver/bluegiga/*` | Bluegiga BLED112 | Bluetooth LE dongle (serial plus DFU). |

The binding's `DeviceType` enum documents the families: `esp` = "ESP Based inline Device", `rpi` = "Raspberry Pi Based inline Device", `pi_zero` = "Raspberry Pi Zero Skin Sense device".

## Additional binding surface (observed names)

`run_psd_with_accessory`, `run_absorbance_with_accessory`, `run_background_with_accessory` (with `accessory_idx`), `check_board`, `run_self_corr`, `run_wvl_corr_background`, `run_wvl_corr_sample`, `material_peaks`, `restore_default_correction`, source-timing parameters `t1`, `delta_t`, `t2_c1`, `t2_c2`, `t2_max`, and **`burn_wln`, `burn_gain`, `burn_self`**.

The `burn_*`, `restore_default_correction`, and `write_*` operations change calibration stored on the device. **SiWare.NET must never call them.** The bridge allow-lists only discovery, `run_psd`, `run_background`, and `read_background`.

## Typical vendor settings (from the shipped sample data)

Scan time 2 s · 257 points · optical gain `internal` (0) · apodization BOXCAR · zero padding shown as "3N" (how this maps to `ZeroPaddingSelection` is unknown) · device temperature about 32.5 °C.

## Impact on SiWare.NET

- `scanTimeUnit: "seconds"` is now the documented recommendation. Configuration still requires it to be set explicitly.
- The cm⁻¹ confirmation flag is backed by vendor data.
- Absorbance for vendor sessions uses the reference only (`SpectralProcessing.Absorbance(sample, reference)`), because no dark scan exists.
- `mode` stays an explicit, user-confirmed setting.

## Method and limits

The REA `search_strings`, `xrefs`, `resolve_containing_procedure`, and `read_bytes` calls ran on the Ghidra analysis. Initial analysis takes about 330 s, so `REA_GHIDRA_STARTUP_TIMEOUT_MS=1800000` was needed. Symbols are stripped, so no procedure names were available. Decompiling `FUN_1801007e4` (the 65 KB module initialiser holding all 46 `scan_time` references) failed in the provider. Nothing here was validated on a physical sensor.

## Bahasa Indonesia

- **`scan_time` dalam detik.** UI Calibrate milik vendor menampilkannya dengan satuan "sec", dan semua 138 data contoh memakai nilai 2.
- **Sumbu spektrum dalam cm⁻¹** (3921,57–7407,41 cm⁻¹ = 2550–1350 nm).
- **Arti `mode` belum diketahui.**
- **Tidak ada operasi dark.** Absorbansi untuk sesi vendor memakai referensi saja.
- **Penemuan perangkat:** broadcast UDP lalu koneksi TCP, melalui adaptor jaringan USB: RNDIS dengan IP host 192.168.137.1/24, atau NCM pada perangkat berbasis ESP.
- **Operasi `burn_*`/`write_*` mengubah kalibrasi perangkat** dan tidak boleh dipanggil oleh SiWare.NET.

## Update check: driver `be10d2e3…` (2026-10-26)

The same REA/Ghidra string search was repeated on the updated driver and diffed against the 2026-10-20 results (170 vs 177 entries, compared in full).

- **Connection sequence revised.** A third port group was added ("prod ports", next to the default and compatibility ports). New messages: "Module {} failed to connect because we there was no valid contracts…" and `SocketsLowerLayer::disconnect_impl_sync`.
- **New types:** `CalibrationPSDResponseElements`, and the simdjson JSON parser was added.
- **Unchanged:** UDP broadcast discovery, TCP connections, the `scan_time` argument, the default/compatibility request formats, and the absence of a dark operation.
- **Installation evidence still holds:** the USB IDs (RNDIS `2D15:F317`, NCM `303A:4000`), the host IP `192.168.137.1/24`, the vendor UI's "sec" label for `scan_time`, and the sample templates (2 s, 257 points).

`mode` remains unknown. **Bahasa Indonesia:** pembaruan driver hanya mengubah urutan koneksi (grup port "prod" baru) dan menambah tipe baru. Transport, argumen `scan_time`, dan ketiadaan operasi dark tidak berubah.
