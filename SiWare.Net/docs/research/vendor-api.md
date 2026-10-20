# Verified installed Python binding

On 2026-10-20, the installed `Analyze/pyDriver.pyd` was imported in an isolated CPython 3.10.11 x64 process. The SHA-256 is `5ca6bd73b6d99e9875a1ab55ee741bb9b4404061129aad62c5987097fd12defa`. Public binding metadata was read; no driver instance was created and no sensor operation was invoked. This extends the earlier static-only investigation.

The interpreter was downloaded from the official Python distribution: `https://www.python.org/ftp/python/3.10.11/python-3.10.11-embed-amd64.zip`, extracted only into the ignored local `.tools/python310/` directory. No vendor installation files were modified.

Reproduce with:

```powershell
.tools/python310/python.exe scripts/vendor_probe.py --inspect
```

## Usable contract

`pyDriver.py_driver()` takes no constructor arguments. Both connection callbacks accept an integer device identifier: `set_on_connected(callback)` and `set_on_disconnected(callback)`; `None` is accepted to clear them. No public start, stop, connect, close, or explicit discovery method was present. Constructor discovery behavior and shutdown behavior require separate runtime validation.

The synchronous `run_psd`, `run_absorbance`, and `run_interferogram` methods accept these positional arguments, in order:

1. `ids`: sequence of integer device IDs.
2. `scan_time`: float; units and valid range are not documented by the binding.
3. `common_wave_num`: `py_driver.CommonWaveNumber`.
4. `optical_gain_selection`: `py_driver.OpticalGainSelection`.
5. `apodization_selection`: `py_driver.ApodizationSelection`.
6. `zero_padding`: `py_driver.ZeroPaddingSelection`.
7. `mode`: integer; meaning is not documented by the binding.

They return a dictionary from device ID to `(Error, RunPSdResponseElements | None)`. The response exposes writable `psd` and `wvn` list-of-float properties. These are not callable methods. Axis units still require confirmation before converting values.

`run_background` takes the same arguments but returns ID-to-`Error`. Retrieve its data separately with `read_background(ids, commonWaveNumber)`, which returns the spectrum result dictionary. Async overloads append a callback receiving the corresponding result mapping.

`get_sensor_id(ids)` returns ID-to-`(Error, string | None)`; `read_fw_version(ids)` returns ID-to-`(Error, (int, int) | None)`; `read_temperature(ids)` returns ID-to-`(Error, float | None)`. `Error.value()` is a method returning an integer. A separately constructed data-only `Error()` returned value `0`, boolean `False`, and representation `NoError`, establishing the zero-success convention without constructing a driver.

## Enum values

All these enums are nested in `py_driver`:

| Enum | Members and values |
|---|---|
| `CommonWaveNumber` | `disabled=0`, `points_65=1`, `points_129=2`, `points_257=3`, `points_513=4`, `points_1024=5`, `points_2048=6`, `points_4096=7` |
| `OpticalGainSelection` | `internal=0`, `calculated=1`, `external=2` |
| `ApodizationSelection` | `boxcar=0`, `gaussian=1`, `happ_genzel=2`, `lorentz=3` |
| `ZeroPaddingSelection` | `points_zero=0`, `points_8k=1`, `points_16k=2`, `points_32k=3` |
| `DeviceType` | `esp=0`, `rpi=1`, `pi_zero=2`, `invalid=3` |

These are Python enum values, not independently verified wire opcodes. The proprietary module remains an external prerequisite; it is not redistributed. An adapter can call this actual interface while isolating its lifetime in a worker process. A successful import validates binary compatibility, not sensor compatibility or measurement correctness.

## Worker adapter

`scripts/vendor_bridge.py` implements JSON-lines IPC. Startup requires explicit `--wavenumber-unit cm-1`, `--scan-time-unit milliseconds|seconds`, and `--mode N`; these are user-confirmed device settings, not findings from introspection. Starting the worker constructs the driver and may initiate vendor discovery. `--self-test` only exercises local protocol validation with a fake implementation and imports no vendor code.

Each request supplies integer `id` and `op` (`discover` or `acquire`). Acquisition supports `Sample` through `run_psd`, and `Reference` through `run_background` followed by `read_background`. Optional `deviceId` selects a discovered device; otherwise the first discovered ID is selected. Parameters are `points`, `integrationMilliseconds`, and `averages`. Defaults are 257 points, 100 milliseconds, and one scan; these are adapter defaults, not verified hardware limits. Repeated scans are averaged only when their axes match. Internal gain, boxcar apodization, and no zero padding are selected explicitly.

Replies contain matching `id`, `ok`, and either `result` or `error`. Spectra contain `deviceId`, `wavenumbers`, `values`, `kind`, and `wavenumberUnit`. Dark acquisition is unsupported. Requests are limited to 1 MiB. Native/Python diagnostics are redirected to stderr. The host must enforce a command timeout and terminate the worker when a native call hangs; the binding exposes no explicit shutdown method. Constructor-to-callback registration has an unavoidable small window because callbacks cannot be passed to the constructor.

## Bahasa Indonesia

Impor modul asli berhasil menggunakan CPython 3.10 x64. Kontrak metode dan nilai enum sudah diperiksa langsung tanpa membuat driver atau mengirim perintah perangkat. Adapter dapat memakai modul vendor yang terpasang; satuan waktu pemindaian, arti mode, satuan sumbu, perilaku penemuan perangkat, dan hasil pengukuran masih perlu validasi perangkat nyata.
