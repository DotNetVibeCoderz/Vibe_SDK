using DepthAI;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Streaming;

// Model placeholder: metadata saja, tanpa bobot. Cukup untuk mengembangkan
// aplikasi melawan backend simulasi. Ganti dengan berkas .blob sungguhan
// sebelum menjalankan pada hardware:
//   var model = await NeuralModel.LoadFromFileAsync("yolov8n.blob");
var model = NeuralModel.CreatePlaceholder(
    ModelFamily.MobileNetSsd,
    labels: ["person", "bicycle", "car", "motorbike", "aeroplane", "bus", "train", "truck", "boat", "traffic light", "bottle", "chair", "sofa", "laptop", "cup", "keyboard", "cell phone", "book"],
    inputWidth: 300,
    inputHeight: 300,
    confidenceThreshold: 0.5f);

await using var device = await DepthAiDevice.OpenAsync();
Console.WriteLine($"Terhubung ke {device.Info.Name}");

var pipeline = Pipeline.CreateBuilder()
    .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
    .AddObjectDetection(model, "rgb.preview", "detector")
    .StreamOut("detector.detections", "detections")
    .Build(device.Capabilities);

await device.StartAsync(pipeline);

using var subscription = device.GetStream<DetectionFrame>("detections").Subscribe(frame =>
{
    if (frame.Count == 0)
    {
        return;
    }

    Console.WriteLine($"[{frame.SequenceNumber,6}] {frame.Count} objek");
    foreach (var detection in frame.Detections)
    {
        Console.WriteLine($"         {detection.Label,-12} {detection.Confidence:P0}  {detection.Box}");
    }
});

Console.WriteLine("Tekan Enter untuk berhenti.");
Console.ReadLine();

await device.StopAsync();