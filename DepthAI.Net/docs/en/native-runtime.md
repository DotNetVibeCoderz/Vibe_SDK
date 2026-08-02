# Native runtime

What is still missing before real OAK hardware works, and how to complete it.

## Where things stand

| Capability | Status |
| --- | --- |
| USB device detection (presence, boot stage, MxId) | ✅ Works without depthai-core |
| Opening a device, uploading pipelines, streaming | ❌ Needs `depthai-c` |
| The whole SDK on the simulation backend | ✅ Fully working |

Verified against real hardware during development:

```
Device    : OAK-1 (Movidius MyriadX, RVC2)
MxId      : 14442C10011298CD00
Sensor    : CAM_A / IMX378, autofocus, up to 4056x3040
USB       : SuperSpeed
Detected  : yes, by UsbDeviceScanner with no native library at all
Opened    : not yet — requires depthai-c
```

## Why a shim layer is needed

depthai-core is a C++ library whose public API exposes templates, `std::shared_ptr`,
`std::vector`, and other STL types. Those types have no stable ABI: their layout differs
between compilers, between versions of the same compiler, and even between debug and
release builds.

P/Invoke needs a stable ABI. So the path is not .NET → C++ directly, but:

```
DepthAI.Net  →  P/Invoke  →  depthai-c (C shim, POD types)  →  depthai-core (C++)
```

The shim is thin: it only flattens C++ types into plain structs and pointers.

## The ABI to implement

The bindings are already written and waiting for an implementation. See
[`NativeMethods.cs`](../../src/DepthAI.Net.Core/Interop/NativeMethods.cs) for exact
signatures. In summary:

```c
// Lifecycle and info
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

// Data flow — pull model, not callbacks
int  dai_device_poll(void* handle, dai_packet_t* out, int timeout_ms);
int  dai_packet_release(void* handle, void* native_handle);

// Telemetry
int  dai_device_telemetry(void* handle, dai_telemetry_t* out);
```

Two design decisions implementors should preserve:

**Pull model, not callbacks.** `dai_device_poll` pulls one packet and returns `DAI_TIMEOUT`
when none is ready. Native-to-managed callbacks on the hot path require keeping delegates
alive and can misbehave when the GC moves threads; polling avoids that whole class of problem.

**Pipelines travel as JSON.** The shim does not need to expose a graph API; it parses one
string and builds a `dai::Pipeline` on the C++ side. The schema lives in
[`pipeline.schema.json`](../../tools/vscode-depthai/schemas/pipeline.schema.json).

## Building the shim

Prerequisites that are **not** present on the current development machine:

- CMake 3.20+
- A C++ compiler (MSVC Build Tools, GCC, or Clang)
- depthai-core and its dependencies

Rough steps:

```bash
git clone --recursive https://github.com/luxonis/depthai-core
cd depthai-core
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON
cmake --build build --config Release

# Then compile the shim against depthai-core and produce:
#   Windows : depthai-c.dll
#   Linux   : libdepthai-c.so
#   macOS   : libdepthai-c.dylib
```

Drop the resulting library next to your application, or anywhere on `PATH`
(`LD_LIBRARY_PATH` on Linux). The SDK probes for it on first use and switches over
automatically — no configuration change needed.

For distribution, package it per RID as `DepthAI.Net.Runtime.<rid>` with the native asset
under `runtimes/<rid>/native/`.

## Verifying

Once the library is in place:

```bash
depthai-dotnet-cli info
```

The `Runtime native` row flips to available, and `devices list` shows physical devices as
openable rather than in a separate section.

To force a hard failure instead of falling back to simulation during testing:

```bash
depthai-dotnet-cli devices list --require-hardware
```

## In the meantime

The simulation backend is not a stub: it produces colour frames, a depth map that is
geometrically consistent with those frames, and neural network tensors in genuine
MobileNet-SSD and YOLO layouts. Those tensors are decoded by exactly the same parsers the
hardware path uses. So application code, pipelines, parsers, and UI can all be built and
tested today; only the data source changes later.
