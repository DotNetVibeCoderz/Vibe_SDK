# Installed driver: static evidence

Inspected 2026-10-20, read-only. Source: `C:\Program Files (x86)\Si-Spect Platform\Analyze\pyDriver.pyd` (4,645,376 bytes). SHA-256: `5ca6bd73b6d99e9875a1ab55ee741bb9b4404061129aad62c5987097fd12defa`.

## Method and limits

Python standard-library byte reads, PE machine-header inspection, and printable ASCII extraction with file offsets. No vendor code was loaded, imported, executed, or modified. Native decompiler provider was unavailable. Strings establish names and implementation hints; they do **not** establish call signatures, enum numeric values, field layouts, or executable control flow. No vendor implementation is reproduced here.

## Observations

| Evidence | File offset | Interpretation |
|---|---:|---|
| PE machine `0x8664` | PE header | x64 Windows native module |
| `pyDriver.cp310-win_amd64.pyd`; `python310.dll` | `0x398d6a`; `0x39add8` | CPython 3.10-specific build |
| `PyInit_pyDriver` | `0x399127` | Python extension entry-point name present; not a documented C SDK |
| `py_driver` | `0x309350` | Exposed driver-class name candidate |
| `set_on_connected`, `set_on_disconnected` | `0x309eb8`, `0x309ed0` | Connection callback API names |
| `run_psd`, `run_absorbance`, `run_background`, `read_background` | `0x309f50`–`0x309ff8` | Measurement/background API names |
| `RunPSdResponseElements`; PSD/WVN format labels | `0x309390`; `0x309188` | Spectrum-response type and intensity/wavenumber concepts |
| `get_sensor_id`, `read_fw_version`, `read_temperature` | `0x30a008`, `0x30a018`, `0x30a410` | Identification and telemetry APIs |
| `SocketsLowerLayer::start_discovery` | `0x322338` | Network discovery implementation |
| TCP and UDP ASIO type names | `0x3aa7c0`, `0x3b5120` | Both socket families occur in the build |
| `SocketsPackageAggregator::discover_packets` | `0x322498` | Stream/packet aggregation exists |
| compatibility/default contract source-path labels | `0x320b10`, `0x320d48` | Multiple protocol implementations exist |

Binding-related strings also name `scan_time`, `common_wave_num`, `optical_gain_selection`, `apodization_selection`, and `zero_padding`. Common-wave-number choices include disabled, 65, 129, 257, 513, 1024, 2048, and 4096 points. Apodization names include boxcar, Gaussian, Happ-Genzel, and Lorentz. Padding choices describe none, 8192, 16384, and 32768 points. These descriptions do not reveal encoded enum values.

Generated signature fragments describe integer device-ID sequences, floating-point parameters, result dictionaries keyed by integer IDs, and asynchronous callbacks. Their correspondence to individual methods remains unverified.

`interfacesParams.json` enables sockets and disables MQTT. `MQTT_Configuration.json` contains local broker configuration (port 1883) and credentials; credential values are deliberately omitted. The MQTT port must not be reused as a sensor TCP/discovery port without evidence.

## Implementation boundary

No sensor TCP/UDP ports, discovery bytes, operation numbers, headers, checksums, endianness, spectrum numeric encoding, or wavelength conversion rules were established. Native Rust interoperability must use a separately specified local ABI; calling this extension as if it exported a C sensor API is unsupported. A hardware transport must remain explicitly unavailable until protocol captures or authorized documentation establish these fields. A simulator can implement the SDK's own documented contracts without claiming hardware compatibility.

Firmware, calibration-write, reset, and Wi-Fi provisioning API names also occur. They are not necessary for passive inspection and must not be probed merely to discover signatures.

## Bahasa Indonesia

Pemeriksaan statis menemukan nama API pengukuran, callback koneksi, dan implementasi soket TCP/UDP. Format paket, nomor operasi, port sensor, dan tata letak data spektrum belum terbukti. Simulasi dapat digunakan untuk pengembangan; kompatibilitas perangkat nyata memerlukan dokumentasi protokol atau rekaman komunikasi yang diizinkan. Kredensial konfigurasi tidak disalin.
