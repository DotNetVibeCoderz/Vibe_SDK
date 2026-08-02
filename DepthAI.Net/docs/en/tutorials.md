# Tutorials

Three walkthroughs that build on each other. All of them run without an OAK camera — the SDK
falls back to the simulation backend when the native runtime is absent.

---

## 1. Object detection

**Result:** a console app that prints visible objects and their confidence.

### Create the project

```bash
dotnet new depthai-console -n ObjectDetection
cd ObjectDetection
```

### Prepare a model

For real hardware, load a `.blob`:

```csharp
var model = await NeuralModel.LoadFromFileAsync("mobilenet-ssd.blob");
```

If a Luxonis-style `mobilenet-ssd.json` sits next to it, labels and input size are read
automatically. To start right now without a model file:

```csharp
var model = NeuralModel.CreatePlaceholder(
    ModelFamily.MobileNetSsd,
    labels: ["person", "bottle", "chair"],
    inputWidth: 300,
    inputHeight: 300,
    confidenceThreshold: 0.5f);
```

### Build the pipeline

```csharp
await using var device = await DepthAiDevice.OpenAsync();

var pipeline = Pipeline.CreateBuilder()
    .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
    .AddObjectDetection(model, "rgb.preview", "detector")
    .StreamOut("detector.detections", "detections")
    .Build(device.Capabilities);
```

The 640×480 preview does not match the model's 300×300 input, so the builder inserts a resize
node. Size mismatch is the single most common reason detection runs but finds nothing — do not
skip it if you wire the graph by hand.

### Read results

```csharp
await device.StartAsync(pipeline);

using var subscription = device.GetStream<DetectionFrame>("detections").Subscribe(frame =>
{
    foreach (var detection in frame.Detections)
    {
        Console.WriteLine($"{detection.Label,-12} {detection.Confidence:P0}  {detection.Box}");
    }
});

Console.ReadLine();
await device.StopAsync();
```

`Box` is normalised 0..1, so it stays correct even when the frame is resized on the host.
For pixels:

```csharp
var (x, y, width, height) = detection.Box.ToPixels(frameWidth, frameHeight);
```

### Tune sensitivity

The confidence threshold can be changed at runtime without reloading the model:

```csharp
.AddObjectDetection(model, "rgb.preview", "detector", node => node.ConfidenceThreshold = 0.65f)
```

---

## 2. Stereo depth

**Result:** reading real distances to objects, in metres.

### Minimal setup

```csharp
await using var device = await DepthAiDevice.OpenAsync();

var pipeline = Pipeline.CreateBuilder()
    .AddColorCamera("rgb", camera => camera.WithPreview(640, 400))
    .AddStereoDepth("stereo", depth =>
    {
        depth.Preset = DepthPreset.HighDensity;
        depth.LeftRightCheck = true;
        depth.AlignTo = CameraSocket.Rgb;
    })
    .StreamOut("rgb.preview", "video")
    .StreamOut("stereo.depth", "depth")
    .Build(device.Capabilities);
```

`AddStereoDepth` creates and wires the left/right mono camera pair for you.

`AlignTo = CameraSocket.Rgb` matters: without it, depth pixels and colour pixels refer to
different world points, and overlays will be off.

### Read distances

```csharp
using var subscription = device.GetStream<DepthFrame>("depth").Subscribe(frame =>
{
    var distance = frame.GetDistanceMeters(frame.Width / 2, frame.Height / 2);

    Console.WriteLine(distance is null
        ? "frame centre: no measurement"
        : $"frame centre: {distance:F2} m");
});
```

**Empty values are normal.** Textureless surfaces, occluded areas, and out-of-range objects
produce no measurement. `GetDistanceMeters` returns `null` precisely so that case cannot slip
through as "zero distance".

### Choosing a preset

| Preset | Use when |
| --- | --- |
| `HighDensity` | You want as few holes as possible; rough object edges are acceptable |
| `HighAccuracy` | Discard doubtful measurements; holier but more trustworthy |
| `FastAccuracy` | Low latency for robot control |
| `Default` | A balanced starting point |

`Subpixel` improves precision for distant objects; `ExtendedDisparity` extends close range.
Both use the same hardware block, so they cannot be enabled together — validation rejects it.

### Detection with 3D coordinates

Combining detection and depth gives each object a 3D position:

```csharp
var pipeline = Pipeline.CreateBuilder()
    .AddColorCamera("rgb", camera => camera.WithPreview(640, 400))
    .AddSpatialObjectDetection(model, "rgb.preview", "detector")
    .StreamOut("detector.detections", "detections")
    .Build(device.Capabilities);

using var subscription = device.GetStream<DetectionFrame>("detections").Subscribe(frame =>
{
    foreach (var detection in frame.Detections)
    {
        if (detection.Spatial is { } spatial)
        {
            Console.WriteLine($"{detection.Label} at {spatial.Z:F2} m");
        }
    }
});
```

Distance is sampled from the middle of the box rather than all of it —
`BoundingBoxScaleFactor` controls how much, so background pixels at the box edges do not
contaminate the estimate.

---

## 3. Custom models

**Result:** running your own model and handling its output.

### Supported formats

| Format | Notes |
| --- | --- |
| `.blob` | OpenVINO compiled for MyriadX; the most direct path |
| `.superblob` | One file with several SHAVE-count variants |
| `.onnx` | Compiled on the fly |

### Metadata

A bare `.blob` carries neither labels nor input size, so both must be supplied — via a
Luxonis-style `.json` sidecar, or directly in code:

```csharp
var model = await NeuralModel.LoadFromFileAsync("yolov8n.blob", new ModelMetadata
{
    Family = ModelFamily.Yolo,
    InputWidth = 640,
    InputHeight = 640,
    Labels = File.ReadAllLines("coco.names"),
    ConfidenceThreshold = 0.5f,
    IouThreshold = 0.5f,
});
```

### YOLO layouts

The parser recognises two layouts and picks between them from the **tensor shape**, not the
model name:

- anchor-free (v8/v10/v11): `[1, 4 + nc, anchors]`, no objectness score
- anchor-based (v5/v6/v7): `[1, anchors, 5 + nc]`, column 5 is objectness

So there is usually nothing to configure.

### Unusual outputs

If your architecture does not match a built-in parser, leave the family as `Raw` and process
the tensors yourself:

```csharp
using var subscription = device.GetStream<NeuralTensorFrame>("nn").Subscribe(frame =>
{
    var tensor = frame.First;
    ReadOnlySpan<float> values = tensor.Span;
    // post-process with ML.NET, TorchSharp, or your own code
});
```

Or implement `IInferenceParser` and return your own frame type.

### Verifying a model

```bash
depthai-dotnet-cli model info yolov8n.blob
depthai-dotnet-cli model upload yolov8n.blob --verify
```

`--verify` runs a short pipeline and confirms the model actually emits results, not merely
that it loaded.

---

## Common mistakes

**Blank frames or exceptions after use.** Frames are disposed once the callback returns.
Call `Clone()` if you need to keep one.

**Detection runs but finds nothing.** Almost always a model input size that does not match
the source. Use `AddObjectDetection`, which handles it.

**Overlays are offset from objects.** Draw over the neural network's `passthrough` output,
not the camera preview — the two are not synchronised.

**Depth reads zero everywhere.** Zero means no measurement. Use `GetDistanceMeters`, which
returns `null`, and check lighting and surface texture.

**The app stutters at high fps.** Reduce the render rate with `Throttle`, and keep pixel
conversion off the UI thread.
