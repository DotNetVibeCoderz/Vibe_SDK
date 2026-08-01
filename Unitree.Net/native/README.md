# `unitree_net_native` — Cyclone DDS shim

This directory holds the native library that gives Unitree.Net a **wire-compatible** link to real robot
firmware. It is only needed when `Unitree:Transport` is set to `CycloneNative`. Everything else in the
solution — including the full test suite — runs without it.

## Why a native shim exists at all

Unitree robots speak RTPS using the `unitree_go`, `unitree_hg` and `unitree_api` IDL types. DDS requires
a registered type descriptor for every topic, and there is no supported way to publish opaque bytes onto
a typed topic from managed code. The shim registers the generated descriptors and exchanges
**pre-serialised CDR** with them.

That split is deliberate: all message encoding stays in C# (`Unitree.Net.Messages`), where it is unit
tested against the struct layouts, and the native side stays small enough to audit in one sitting. The
CDR bodies produced by the managed codec are verified byte-for-byte against the C struct layout, so what
the shim puts on the wire is exactly what the firmware expects.

## Prerequisites

| Requirement | Notes |
|---|---|
| CMake ≥ 3.20 | |
| A C++17 compiler | GCC 9+, Clang 10+, or MSVC 2019+ |
| Cyclone DDS ≥ 0.10 | The `dds_writecdr` / `dds_takecdr` serdata API is used |
| Unitree IDL files | From an `unitree_sdk2` checkout |

Cyclone DDS is already vendored inside `unitree_sdk2`. If you have that checked out, point
`CMAKE_PREFIX_PATH` at its install tree rather than building Cyclone separately.

## Build

```bash
git clone https://github.com/unitreerobotics/unitree_sdk2.git

cmake -S native/unitree_net_native -B native/build \
      -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_PREFIX_PATH=/path/to/cyclonedds/install \
      -DUNITREE_IDL_DIR=/path/to/unitree_sdk2/idl

cmake --build native/build --config Release
```

The build produces `libunitree_net_native.so` (Linux), `unitree_net_native.dll` (Windows) or
`libunitree_net_native.dylib` (macOS).

### Making .NET find it

The default probing rules search the application directory and the platform library path. Either copy
the binary next to your executable, or:

```bash
export LD_LIBRARY_PATH=/path/to/native/build:$LD_LIBRARY_PATH   # Linux
```

Check it is visible from managed code:

```csharp
Console.WriteLine(CycloneDdsTransport.IsNativeLibraryAvailable());
Console.WriteLine(CycloneDdsTransport.GetNativeVersion());
```

Or from the CLI: `unitree diagnose`.

## Cross-compiling for ARM (Jetson, Raspberry Pi)

The robot-side hosts are ARM64. Build natively on the target if you can — it is far less trouble than a
cross toolchain. If you must cross-compile:

```bash
cmake -S native/unitree_net_native -B native/build-arm64 \
      -DCMAKE_TOOLCHAIN_FILE=/path/to/aarch64-toolchain.cmake \
      -DCMAKE_PREFIX_PATH=/path/to/cyclonedds-arm64 \
      -DUNITREE_IDL_DIR=/path/to/unitree_sdk2/idl
```

Cyclone DDS must itself be built for the target architecture; a host-architecture Cyclone will configure
cleanly and then fail at link time.

## Adding a message type

Three places must agree, and a mismatch shows up as `UN_UNKNOWN_TYPE` at endpoint creation:

1. `src/unitree_net_native.cpp` — add the generated header include and one row in `find_descriptor()`.
2. `Unitree.Net.Interop/CycloneDdsTransport.ResolveTypeName` — map the topic to the same type name.
3. `Unitree.Net.Messages` — implement `ICdrSerializable<T>` for the message.

## QoS

The shim applies different QoS by topic class, matching what the firmware advertises:

- **Streaming topics** (`rt/lowcmd`, `rt/lowstate`, `rt/sportmodestate`): best-effort, keep-last-1,
  volatile. Requesting *reliable* here leaves the reader unmatched — the link looks connected and never
  delivers anything, which is a genuinely confusing failure.
- **Service topics** (`.../request`, `.../response`): reliable, keep-last-16, volatile.

## Troubleshooting

**Nothing is received, no error.** Almost always the network interface. Set `Unitree:NetworkInterface`
explicitly — with several NICs up, Cyclone picks one on its own and it is frequently the corporate LAN
where multicast is filtered. See `docs/dds-networking.md`.

**`un_init` returns `UN_DDS_ERROR`.** Another process already created a domain with a conflicting
configuration, or the interface name does not exist. `dds_strretcode` detail is in `un_last_error()`,
which the managed exception message includes.

**Commands are accepted but the robot does not move.** The sport service still owns the motors. Release
it via the motion-switcher API before publishing on `rt/lowcmd`; `LowLevelController.ConnectAsync`
does this for you.
