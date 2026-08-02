# API reference

The public surface of DepthAI.Net. For concepts see the [README](../../README.md);
for step-by-step walkthroughs see the [tutorials](tutorials.md).

---

## `DepthAi` — static entry point

```csharp
string  DepthAi.Version                     // SDK version
bool    DepthAi.IsNativeAvailable           // can real hardware be used
string? DepthAi.NativeVersion               // depthai-core version, null when absent
string? DepthAi.NativeUnavailableReason     // actionable explanation
string  DepthAi.DescribeEnvironment()       // runtime + USB summary in one sentence

IReadOnlyList<DeviceInfo>          DepthAi.ListDevices(DepthAiOptions? options = null)
IReadOnlyList<UsbDeviceDescriptor> DepthAi.ScanUsbDevices()
IDepthAiBackend                    DepthAi.CreateBackend(DepthAiOptions? options = null)
```

`ListDevices` goes through the backend; `ScanUsbDevices` reads the USB bus directly and works
without the native runtime. The difference between them is what separates "no camera" from
"a camera is plugged in but the native library is missing".

### `DepthAiOptions`

```csharp
BackendSelection  Backend      // Auto (default), NativeOnly, SimulationOnly
SimulationOptions Simulation   // DeviceCount, Seed, DeviceName
DeviceOpenOptions DeviceOpen   // MaxUsbSpeed, BootTimeout, FirmwarePath
ILoggerFactory?   LoggerFactory

DepthAiOptions.Default    // Auto
DepthAiOptions.Simulated  // force simulation
```

---

## `DepthAiDevice`

```csharp
static Task<DepthAiDevice> OpenAsync(DepthAiOptions? options = null, CancellationToken ct = default)
static Task<DepthAiDevice> OpenBySerialAsync(string serialNumber, ...)
static Task<DepthAiDevice> OpenAsync(IDepthAiBackend backend, DeviceInfo device, ...)

DeviceInfo          Info
DeviceCapabilities  Capabilities
bool                IsSimulated
bool                IsRunning
Pipeline?           RunningPipeline

Task StartAsync(Pipeline pipeline, CancellationToken ct = default)
Task StopAsync(CancellationToken ct = default)

IFrameStream<T>     GetStream<T>(string name) where T : Frame
IFrameStream<Frame> GetStream(string name)
DeviceTelemetry     ReadTelemetry()

event EventHandler<DeviceErrorEventArgs>? Error
```

`DepthAiDevice` is `IAsyncDisposable`; use `await using`.

### `DeviceCapabilities`

```csharp
int  ColorCameraCount
int  MonoCameraCount
bool SupportsStereoDepth
bool HasImu
int  ShaveCores
IReadOnlyDictionary<CameraSocket, string> Sensors
```

Pass this to `Build(...)` or `Validate(...)` so impossible configurations are rejected on the
host rather than failing silently on the device.

### `DeviceWatcher` — hotplug

```csharp
await using var watcher = new DeviceWatcher();
watcher.PollInterval = TimeSpan.FromSeconds(2);

watcher.DeviceConnected    += (_, e) => { };
watcher.DeviceDisconnected += (_, e) => { };
watcher.DeviceStateChanged += (_, e) => { };

watcher.Start();
```

Devices already attached when `Start()` runs are reported through `DeviceConnected` too, so
callers need no separate initial-enumeration path.

---

## `Pipeline`

```csharp
static Pipeline        Pipeline.Create()
static PipelineBuilder Pipeline.CreateBuilder()

T                    Add<T>(T node)
ColorCameraNode      AddColorCamera(string name = "rgb", Action<ColorCameraNode>? configure = null)
MonoCameraNode       AddMonoCamera(string name, ...)
StereoDepthNode      AddStereoDepth(string name = "stereo", ...)
NeuralNetworkNode    AddNeuralNetwork(string name = "nn", ...)
DetectionNetworkNode AddDetectionNetwork(string name = "detector", ...)
SpatialDetectionNetworkNode AddSpatialDetectionNetwork(string name = "spatialDetector", ...)
ImageManipNode       AddImageManip(string name = "manip", ...)
VideoEncoderNode     AddVideoEncoder(string name = "encoder", ...)
ImuNode              AddImu(string name = "imu", ...)

OutputStreamDefinition AddOutputStream(NodeOutput output, string? name = null,
                                       int maxSize = 4, bool blocking = false)

void       Link(string fromOutputPath, string toInputPath)
NodeOutput ResolveOutput(string path)      // "rgb.preview"
NodeInput  ResolveInput(string path)
T          GetNode<T>(string name)

PipelineValidationResult Validate(DeviceCapabilities? capabilities = null)

string          ToJson(bool indented = true)
static Pipeline FromJson(string json, PipelineLoadOptions? options = null)
Task            SaveToFileAsync(string path, CancellationToken ct = default)
static Task<Pipeline> LoadFromFileAsync(string path, ...)
```

### `PipelineBuilder`

```csharp
PipelineBuilder AddColorCamera(string name = "rgb", Action<ColorCameraNode>? configure = null)
PipelineBuilder AddMonoCamera(string name, ...)
PipelineBuilder AddStereoDepth(string name = "stereo", Action<StereoDepthNode>? configure = null,
                               MonoResolution resolution = MonoResolution.The400P, int fps = 30)
PipelineBuilder AddObjectDetection(NeuralModel model, string sourceOutputPath = "rgb.preview",
                                   string name = "detector", ...)
PipelineBuilder AddSpatialObjectDetection(NeuralModel model, ...)
PipelineBuilder Link(string from, string to)
PipelineBuilder StreamOut(string outputPath, string? name = null, int maxSize = 4, bool blocking = false)
PipelineBuilder Configure(Action<Pipeline> configure)

Pipeline Build(DeviceCapabilities? capabilities = null)   // validate, then return
Pipeline BuildUnvalidated()                               // for half-finished pipelines
```

`AddStereoDepth` also creates and wires a left/right mono camera pair.
`AddObjectDetection` inserts an `ImageManipNode` when the source size differs from the model input.

### Nodes and ports

| Node | Inputs | Outputs |
| --- | --- | --- |
| `ColorCameraNode` | `inputControl` | `preview`, `video`, `still`, `isp` |
| `MonoCameraNode` | `inputControl` | `out` |
| `StereoDepthNode` | `left`, `right` | `depth`, `disparity`, `rectifiedLeft`, `rectifiedRight`, `syncedLeft`, `syncedRight` |
| `NeuralNetworkNode` | `input` | `out`, `passthrough` |
| `DetectionNetworkNode` | `input` | `out`, `passthrough`, `detections` |
| `SpatialDetectionNetworkNode` | `input`, `depth` | `detections`, `boundingBoxMapping`, `passthroughDepth` |
| `ImageManipNode` | `input` | `out` |
| `VideoEncoderNode` | `input` | `bitstream` |
| `ImuNode` | — | `out` |

Use `passthrough` — not the camera preview — when drawing overlays, so boxes line up with the
frame that was actually inferred on.

### `PipelinePresets`

```csharp
Pipeline PipelinePresets.Create(string preset, NeuralModel? model = null, int fps = 30)

// rgb-preview, stereo-depth, object-detection, spatial-detection, record-rgbd, imu-stream
```

---

## Frames and streams

```csharp
abstract class Frame : IDisposable
{
    long           SequenceNumber
    TimeSpan       DeviceTimestamp
    DateTimeOffset HostTimestamp
    string         StreamName
    bool           IsDisposed
}
```

### `ImageFrame`

```csharp
int              Width, Height, Stride, ByteLength, BytesPerPixel
PixelFormat      Format
ReadOnlySpan<byte> Pixels

ImageFrame Clone()
byte[]     ToArray()
ReadOnlySpan<byte> GetRow(int y)

static ImageFrame Wrap(byte[] buffer, int width, int height, PixelFormat format, ...)
static ImageFrame CopyFrom(ReadOnlySpan<byte> source, int width, int height, PixelFormat format, ...)
```

### `DepthFrame`

```csharp
int   Width, Height
float MinDepthMeters, MaxDepthMeters, FocalLengthPixels, BaselineCentimeters
ReadOnlySpan<ushort> Millimeters

ushort  GetMillimeters(int x, int y)          // 0 = no measurement
float?  GetDistanceMeters(int x, int y)       // null = no measurement
(float X, float Y, float Z)? GetPoint3D(int x, int y)
float[,] ToMeterMatrix()                      // NaN for empty pixels
DepthFrame Clone()
```

### Stream operators

```csharp
IDisposable Subscribe<T>(Action<T> onNext, Action<Exception>? onError = null, Action? onCompleted = null)
IObservable<T> Where<T>(Func<T, bool> predicate)
IObservable<TResult> Select<T, TResult>(Func<T, TResult> selector)
IObservable<T> Throttle<T>(TimeSpan interval)
Task<T> FirstAsync<T>(CancellationToken ct = default)
IAsyncEnumerable<T> ToAsyncEnumerable<T>(int capacity = 2, CancellationToken ct = default)
```

`ToAsyncEnumerable` uses a bounded channel that drops the oldest frame when full: showing the
most recent frame beats building a queue that falls further behind.

---

## Inference

```csharp
static Task<NeuralModel> NeuralModel.LoadFromFileAsync(string path, ModelMetadata? metadata = null, ...)
static Task<NeuralModel> NeuralModel.LoadFromStreamAsync(Stream stream, ModelFormat format, ...)
static NeuralModel       NeuralModel.FromBytes(ReadOnlyMemory<byte> payload, ModelFormat format, ...)
static NeuralModel       NeuralModel.CreatePlaceholder(ModelFamily family, IReadOnlyList<string> labels,
                                                       int inputWidth = 640, int inputHeight = 640, ...)
```

`LoadFromFileAsync` picks up a Luxonis-style `.json` sidecar automatically when present.
`CreatePlaceholder` builds a metadata-only model for developing against the simulator.

### Results

```csharp
sealed record Detection
{
    int    LabelIndex
    string Label
    float  Confidence
    BoundingBox    Box       // normalised 0..1
    SpatialPoint?  Spatial   // metres; spatial detection networks only
}

sealed class DetectionFrame : Frame
{
    IReadOnlyList<Detection> Detections
    int Count
    Detection? Best
}
```

Other result frames: `ClassificationFrame`, `SegmentationFrame`, `NeuralTensorFrame`.

### Parsers

`YoloParser`, `MobileNetSsdParser`, `ClassificationParser`, `SegmentationParser`, `RawTensorParser`.
Implement `IInferenceParser` for architectures not covered.

---

## Imaging

In Core, dependency-free:

```csharp
byte[] PixelConverter.ToBgr888(ImageFrame frame)
byte[] PixelConverter.PlanarToInterleaved(ReadOnlySpan<byte> planar, int width, int height)

byte[]     DepthColorizer.ToBgr(DepthFrame frame, DepthColorMap map = DepthColorMap.Turbo, ...)
ImageFrame DepthColorizer.ToImageFrame(DepthFrame frame, ...)

void FrameOverlay.DrawDetections(Span<byte> bgr, int width, int height, IEnumerable<Detection> detections, int thickness = 2)
void FrameOverlay.DrawRectangle(...)
void FrameOverlay.FillRectangle(...)
(byte R, byte G, byte B) FrameOverlay.ColorFor(int labelIndex)
```

Through the adapter packages:

```csharp
// DepthAI.Net.Imaging.ImageSharp
Image<Rgb24> frame.ToImage()
Image<Rgb24> depthFrame.ToImage(DepthColorMap map = DepthColorMap.Turbo, ...)
Image<Rgb24> frame.ToImageWithDetections(IEnumerable<Detection> detections, int thickness = 2)
Task frame.SaveAsync(string path, CancellationToken ct = default)
Task depthFrame.SaveRawDepthAsync(string path, ...)      // 16-bit PNG, true millimetres
Task<byte[]> frame.ToJpegAsync(int quality = 85, ...)

// DepthAI.Net.Imaging.SkiaSharp
SKBitmap frame.ToBitmap()
SKImage  frame.ToSKImage()
void     canvas.DrawDetections(IEnumerable<Detection> detections, float width, float height, ...)
byte[]   frame.Encode(SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 90)

// DepthAI.Net.Imaging.SystemDrawing (Windows)
Bitmap frame.ToBitmap()
void   frame.Save(string path)
```

---

## Exceptions

| Type | When |
| --- | --- |
| `DepthAiException` | Errors from the device or the native layer |
| `DeviceNotFoundException` | No device matched |
| `ObjectDisposedException` | Using a frame after disposal — call `Clone()` |
| `InvalidOperationException` | Invalid pipeline; the message lists every problem |
| `KeyNotFoundException` | Unknown stream, node, or port; the message lists what exists |
