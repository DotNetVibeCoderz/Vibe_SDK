# Referensi API

Ringkasan permukaan publik DepthAI.Net. Untuk penjelasan konsep, lihat
[README](../../README.md); untuk latihan langkah demi langkah, lihat [tutorial](tutorials.md).

---

## `DepthAi` — titik masuk statis

```csharp
string  DepthAi.Version                     // versi SDK
bool    DepthAi.IsNativeAvailable           // apakah hardware bisa dipakai
string? DepthAi.NativeVersion               // versi depthai-core, null bila tidak ada
string? DepthAi.NativeUnavailableReason     // penjelasan yang bisa ditindaklanjuti
string  DepthAi.DescribeEnvironment()       // ringkasan runtime + USB dalam satu kalimat

IReadOnlyList<DeviceInfo>          DepthAi.ListDevices(DepthAiOptions? options = null)
IReadOnlyList<UsbDeviceDescriptor> DepthAi.ScanUsbDevices()
IDepthAiBackend                    DepthAi.CreateBackend(DepthAiOptions? options = null)
```

`ListDevices` melewati backend; `ScanUsbDevices` membaca bus USB langsung dan tetap bekerja
tanpa runtime native. Perbedaan keduanya yang membedakan "tidak ada kamera" dari
"ada kamera tapi pustaka native belum terpasang".

### `DepthAiOptions`

```csharp
BackendSelection  Backend      // Auto (bawaan), NativeOnly, SimulationOnly
SimulationOptions Simulation   // DeviceCount, Seed, DeviceName
DeviceOpenOptions DeviceOpen   // MaxUsbSpeed, BootTimeout, FirmwarePath
ILoggerFactory?   LoggerFactory

DepthAiOptions.Default    // Auto
DepthAiOptions.Simulated  // memaksa simulasi
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

`DepthAiDevice` adalah `IAsyncDisposable`; pakai `await using`.

### `DeviceCapabilities`

```csharp
int  ColorCameraCount
int  MonoCameraCount
bool SupportsStereoDepth
bool HasImu
int  ShaveCores
IReadOnlyDictionary<CameraSocket, string> Sensors
```

Kirimkan ke `Build(...)` atau `Validate(...)` agar konfigurasi yang mustahil ditolak di host,
bukan gagal diam-diam di perangkat.

### `DeviceWatcher` — hotplug

```csharp
await using var watcher = new DeviceWatcher();
watcher.PollInterval = TimeSpan.FromSeconds(2);

watcher.DeviceConnected    += (_, e) => { };
watcher.DeviceDisconnected += (_, e) => { };
watcher.DeviceStateChanged += (_, e) => { };

watcher.Start();
```

Perangkat yang sudah terpasang saat `Start()` juga dilaporkan lewat `DeviceConnected`, jadi
tidak perlu menangani enumerasi awal secara terpisah.

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

Pipeline Build(DeviceCapabilities? capabilities = null)   // validasi lalu kembalikan
Pipeline BuildUnvalidated()                               // untuk pipeline setengah jadi
```

`AddStereoDepth` sekaligus membuat dan menyambungkan sepasang kamera mono.
`AddObjectDetection` menyisipkan `ImageManipNode` bila ukuran sumber berbeda dari input model.

### Node dan port

| Node | Masukan | Keluaran |
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

Pakai `passthrough` — bukan preview kamera — saat menggambar overlay, supaya kotak sejajar
dengan frame yang benar-benar diinferensi.

### `PipelinePresets`

```csharp
Pipeline PipelinePresets.Create(string preset, NeuralModel? model = null, int fps = 30)

// rgb-preview, stereo-depth, object-detection, spatial-detection, record-rgbd, imu-stream
```

---

## Frame dan stream

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

ushort  GetMillimeters(int x, int y)          // 0 = tidak ada pengukuran
float?  GetDistanceMeters(int x, int y)       // null = tidak ada pengukuran
(float X, float Y, float Z)? GetPoint3D(int x, int y)
float[,] ToMeterMatrix()                      // NaN untuk piksel kosong
DepthFrame Clone()
```

### Operator stream

```csharp
IDisposable Subscribe<T>(Action<T> onNext, Action<Exception>? onError = null, Action? onCompleted = null)
IObservable<T> Where<T>(Func<T, bool> predicate)
IObservable<TResult> Select<T, TResult>(Func<T, TResult> selector)
IObservable<T> Throttle<T>(TimeSpan interval)
Task<T> FirstAsync<T>(CancellationToken ct = default)
IAsyncEnumerable<T> ToAsyncEnumerable<T>(int capacity = 2, CancellationToken ct = default)
```

`ToAsyncEnumerable` memakai channel bounded yang membuang frame terlama saat penuh: lebih
baik menampilkan frame terkini daripada menumpuk antrean yang makin tertinggal.

---

## Inferensi

```csharp
static Task<NeuralModel> NeuralModel.LoadFromFileAsync(string path, ModelMetadata? metadata = null, ...)
static Task<NeuralModel> NeuralModel.LoadFromStreamAsync(Stream stream, ModelFormat format, ...)
static NeuralModel       NeuralModel.FromBytes(ReadOnlyMemory<byte> payload, ModelFormat format, ...)
static NeuralModel       NeuralModel.CreatePlaceholder(ModelFamily family, IReadOnlyList<string> labels,
                                                       int inputWidth = 640, int inputHeight = 640, ...)
```

`LoadFromFileAsync` otomatis membaca berkas `.json` pendamping bergaya Luxonis bila ada.
`CreatePlaceholder` membuat model metadata-saja untuk dikembangkan melawan simulasi.

### Hasil

```csharp
sealed record Detection
{
    int    LabelIndex
    string Label
    float  Confidence
    BoundingBox    Box       // ternormalisasi 0..1
    SpatialPoint?  Spatial   // meter; hanya untuk spatial detection network
}

sealed class DetectionFrame : Frame
{
    IReadOnlyList<Detection> Detections
    int Count
    Detection? Best
}
```

Tipe frame lain: `ClassificationFrame`, `SegmentationFrame`, `NeuralTensorFrame`.

### Parser

`YoloParser`, `MobileNetSsdParser`, `ClassificationParser`, `SegmentationParser`, `RawTensorParser`.
Implementasikan `IInferenceParser` untuk arsitektur yang belum didukung.

---

## Imaging

Di dalam Core, tanpa dependensi:

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

Lewat paket adapter:

```csharp
// DepthAI.Net.Imaging.ImageSharp
Image<Rgb24> frame.ToImage()
Image<Rgb24> depthFrame.ToImage(DepthColorMap map = DepthColorMap.Turbo, ...)
Image<Rgb24> frame.ToImageWithDetections(IEnumerable<Detection> detections, int thickness = 2)
Task frame.SaveAsync(string path, CancellationToken ct = default)
Task depthFrame.SaveRawDepthAsync(string path, ...)      // PNG 16-bit, milimeter asli
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

## Exception

| Tipe | Kapan |
| --- | --- |
| `DepthAiException` | Kesalahan dari perangkat atau lapisan native |
| `DeviceNotFoundException` | Tidak ada perangkat yang cocok |
| `ObjectDisposedException` | Memakai frame setelah dibuang — panggil `Clone()` |
| `InvalidOperationException` | Pipeline tidak valid; pesannya memuat seluruh masalah |
| `KeyNotFoundException` | Nama stream, node, atau port tidak dikenal; pesannya memuat yang tersedia |
