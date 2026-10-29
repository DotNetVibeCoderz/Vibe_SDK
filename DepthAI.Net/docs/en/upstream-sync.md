# Syncing with depthai-core

DepthAI.Net tracks [luxonis/depthai-core](https://github.com/luxonis/depthai-core). The SDK
was built on **v3.8.0**; release 0.2.0 syncs changes up to **v3.11.0** (upstream release,
12 October 2026).

Not every upstream change applies. Most are firmware, RVC4 or on-device nodes, which only
become usable once the native `depthai-c` shim exists (see [native runtime](native-runtime.md)).
What was ported is behaviour that runs on the host and can therefore be tested without a camera.

## Ported

| Upstream | Release | In DepthAI.Net |
| --- | --- | --- |
| `msg.save()` / `msg.load()`, `.dai` files | 3.10 | `DaiMessageFile` — `frame.Save(path)`, `Load`, `LoadImageFrame`, `LoadDepthFrame`, `LoadDetections` |
| `DetectionParserConfig` via `inputConfig` | 3.11 | `device.SendConfig("detector", new DetectionParserConfig { ... })`, `DetectionNetworkNode.IouThreshold` |
| `dai::utility::colorizeDepthFrame` | 3.9 | `DepthColorizer.Colorize` / `ColorizeToImageFrame` |
| `strides` and `background_class` in NN Archive metadata | 3.9 | `NnArchiveConfig`, `ModelMetadata.Strides`, `YoloOutputs`, `BackgroundClass` |
| YOLOv6 decoding fix (stride-based centre/size) | 3.9 | `YoloDecodingFamily.CenterSize`, grid decoder in `YoloParser` |
| DetectionParser counts only `_yolo` outputs for strides | 3.10 | `YoloParser` and `NnArchiveConfig` validation |
| SegmentationParser uses `background_class` | 3.9 | `SegmentationFrame.NoClass` (255) |

### `.dai` files

The format is upstream's `ProtoSerializableMessage` protobuf envelope, byte for byte, so it
works in both directions:

```csharp
using DepthAI.Messages;

frame.Save("capture");                         // → capture.dai
using var loaded = DaiMessageFile.LoadImageFrame("capture.dai");
```

```python
frame = dai.ImgFrame(); frame.load("capture.dai")
```

Supported: `ImgFrame` (BGR/RGB interleaved, BGR/RGB planar, which loads as interleaved,
GRAY8/RAW8, NV12, RGBA, and RAW16 depth, which loads as a `DepthFrame`) and `ImgDetections`.
Data the upstream schema has no field for is not stored: `DepthFrame` calibration,
`DetectionFrame` source size, and spatial detection coordinates.

### Runtime detection thresholds

```csharp
device.SendConfig("detector", new DetectionParserConfig { ConfidenceThreshold = 0.7f });
```

The new thresholds apply from the next frame onward, to every output stream of that node.
A `null` property keeps the value that is currently active. As upstream, IoU has no effect
on MobileNet-SSD.

### Upstream-style depth colouring

`DepthColorizer.Colorize` uses upstream's defaults: 300–12000 mm, Jet, and log scaling.
When `maxMillimeters <= minMillimeters`, the range comes from the 3rd and 95th percentiles of
valid pixels. The existing `ToBgr` is unchanged.

### NN Archive

`NeuralModel.LoadFromFileAsync` now also reads the NN Archive `config.json`, either inside a
`.tar` / `.tar.gz` archive or extracted next to the model. Extract `.tar.xz` archives first,
because .NET ships no xz decoder.

## Not ported

| Upstream | Why |
| --- | --- |
| Multi-device pipelines (`pipeline.addDevice`, cross-device relay), 3.11 | Needs the native runtime. Until then, open several `DepthAiDevice`s with `OpenAsync(backend, device)`. |
| Align, Stitching, MultiDeviceCalibration, ToFStereoFusion nodes | Device or beta nodes; simulating them without hardware would only produce fake results. |
| `Odometry` message, Basalt/RTAB-Map VIO | The SDK has no VIO nodes yet. |
| Beta-namespace parsers (YuNet, SCRFD, HRNet, ...) | Upstream may change their API without notice. |
| PoE bootloader, RVC4 IFE sharing, VideoEncoder/ImageManip fixes | Firmware or device-side only. |

## Verification

- 101 Core tests pass, including `.dai` fixtures written by depthai **3.11.0** for Python
  (`tests/DepthAI.Net.Core.Tests/Fixtures/depthai-3.11`).
- The other direction was checked too: depthai 3.11 for Python opens `.dai` files written by
  DepthAI.Net, with identical pixels, depth and detections.
- Tested with a real OAK-1 (MxId `14442C10011298CD00`). A BGR888p 512×384 passthrough frame
  and YOLOv6-nano detections captured with depthai 3.11 load in DepthAI.Net and redraw with
  correctly placed overlays.
