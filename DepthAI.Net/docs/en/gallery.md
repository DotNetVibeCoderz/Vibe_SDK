# Gallery

Screenshots and code from applications built with DepthAI.Net. Every image on this page was
captured from a running system.

---

## Jack The Code Bender

![Jack The Code Bender — dark theme](../images/wizard-dark.png)

Dark theme: the editor with re-coloured syntax highlighting, the chat panel, and the depth ribbon under the title.

![Jack The Code Bender — light theme](../images/wizard-light.png)

Light theme: the same palette with accents darkened so contrast still holds on white.

The layout: project explorer on the left, editor in the middle, chat panel on the right, and
a status bar carrying the *depth ribbon* — a gradient strip using the same Turbo colour map
the SDK uses to colourise camera depth.

```bash
dotnet run --project src/DepthAI.Net.Wizard
```

---

## Real hardware

The frame below came from a physical OAK-1 (Movidius MyriadX, IMX378 sensor) at 640×480,
29.7 fps, chip temperature 42.4 °C:

![Frame from a real OAK-1](../images/oak1-real-frame.png)

That device is detected by `UsbDeviceScanner` with no native library at all:

```
OAK / Movidius MyriadX (booted) — 03E7:F63B, Booted, MxId 14442C10011298CD00
```

Opening the device and running pipelines still needs the native shim — see
[native runtime](native-runtime.md).

---

## Simulation backend

Without hardware, the SDK produces a synthetic scene. The colour frame and the depth map come
from the same scene, so objects genuinely line up between them:

| Colour | Depth (Turbo colour map) |
| --- | --- |
| ![Simulated RGB](../images/simulated-rgb.png) | ![Simulated depth](../images/simulated-depth.png) |

The scattered dark dots in the depth map are deliberate: they are pixels with no measurement,
exactly what a real stereo matcher produces on textureless surfaces. Code that fails to handle
them gets caught early.

```bash
depthai-dotnet-cli capture -o ./out --frames 3 --streams rgb,depth
```

---

## Samples

### Object detection dashboard

Live video with detection boxes and a side list of visible objects.

```csharp
_subscriptions.Add(_device.GetStream<DetectionFrame>("detections")
    .Subscribe(frame =>
    {
        Volatile.Write(ref _latestDetections, frame.Detections);

        var lines = frame.Detections.Select(d => $"{d.Label}  {d.Confidence:P0}").ToList();
        Dispatcher.UIThread.Post(() => DetectionList.ItemsSource = lines);
    }));
```

`samples/avalonia/DepthAI.Sample.DetectionDashboard`

### Depth viewer

Colour and depth side by side; hover to read a real distance.

```csharp
var distance = depth.GetDistanceMeters(x, y);
Status.Text = distance is null
    ? $"({x}, {y}) — no measurement"
    : $"({x}, {y}) — {distance:F2} m";
```

`samples/avalonia/DepthAI.Sample.DepthViewer`

### Privacy blur

Every detected person is pixelated before the frame leaves the app. Mosaic rather than
gaussian blur, because it cannot be reversed — which is the point for privacy.

`samples/avalonia/DepthAI.Sample.FaceBlur`

### People counter

Counts crossings of a virtual line, using proximity-based tracking between frames so a
detection that blinks for one or two frames is not counted as a new person.

`samples/console/DepthAI.Sample.PeopleCounter`

### Live web dashboard

Blazor Server with a dark theme and responsive layout; frames go out as data URIs, capped at
about 12 fps because past that, JPEG encoding and SignalR traffic become the bottleneck rather
than the camera.

`samples/web/DepthAI.Sample.BlazorLive`

### Vision REST API

Exposes detections and depth readings as JSON for other systems to consume.

```bash
curl http://localhost:5090/api/detections
curl "http://localhost:5090/api/depth?x=320&y=200"
```

`samples/web/DepthAI.Sample.VisionApi`

---

## Wizard templates

Sixteen computer vision templates, grouped by category:

| Category | Templates |
| --- | --- |
| Blank | Console, Desktop, Web |
| Detection | Object detection (console), Object detection dashboard |
| Depth | Depth viewer, RGB-D recorder |
| Analytics | People counter |
| Safety | Safety zone monitor, Distance between people, Privacy blur |
| Industrial | Quality inspection, PPE compliance, Shelf stock monitor |
| Web | Live inference (Blazor), Vision REST API |

Every one of them produces a project that builds and runs immediately.
