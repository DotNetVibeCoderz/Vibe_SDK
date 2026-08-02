using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DepthAI;
using DepthAI.Imaging;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Streaming;
using SkiaSharp;

namespace DepthAI.Sample.DetectionDashboard;

public partial class MainWindow : Window
{
    private readonly List<IDisposable> _subscriptions = [];
    private DepthAiDevice? _device;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            _device = await DepthAiDevice.OpenAsync();

            Subtitle.Text = _device.IsSimulated
                ? $"{_device.Info.Name} — data simulasi, tidak ada hardware terdeteksi"
                : $"{_device.Info.Name} · {_device.Info.SerialNumber}";

            var pipeline = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
            .AddObjectDetection(_model, "rgb.preview", "detector")
            .StreamOut("rgb.preview", "video")
            .StreamOut("detector.detections", "detections")
            .Build(_device.Capabilities);

            await _device.StartAsync(pipeline);

            _subscriptions.Add(_device.GetStream<DetectionFrame>("detections")
                .Subscribe(frame =>
                {
                    // Deteksi disimpan supaya frame video berikutnya bisa
                    // menggambarnya; keduanya datang di stream terpisah.
                    Volatile.Write(ref _latestDetections, frame.Detections);

                    var lines = frame.Detections
                        .Select(d => $"{d.Label}  {d.Confidence:P0}")
                        .ToList();

                    Dispatcher.UIThread.Post(() => DetectionList.ItemsSource = lines);
                }));

            _subscriptions.Add(_device.GetStream<ImageFrame>("video")
                .Subscribe(frame => ShowAnnotated(frame)));

            Status.Text = "Berjalan";
        }
        catch (Exception ex)
        {
            Status.Text = $"Gagal memulai: {ex.Message}";
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();

        if (_device is not null)
        {
            await _device.DisposeAsync();
            _device = null;
        }
    }

    /// <summary>
    /// Menampilkan frame pada sebuah Image. Konversi dilakukan di thread pemanggil
    /// lalu hanya penugasan bitmap yang dipindahkan ke UI thread, supaya render
    /// tidak menahan antrean dispatcher.
    /// </summary>
    private static void ShowFrame(Image target, ImageFrame frame)
    {
        using var skia = frame.ToBitmap();
        var bitmap = ToAvaloniaBitmap(skia);
        Dispatcher.UIThread.Post(() => target.Source = bitmap);
    }

    private static void ShowDepth(Image target, DepthFrame frame)
    {
        using var skia = frame.ToBitmap(DepthColorMap.Turbo, 0.3f, 4.5f);
        var bitmap = ToAvaloniaBitmap(skia);
        Dispatcher.UIThread.Post(() => target.Source = bitmap);
    }

    private static Bitmap ToAvaloniaBitmap(SKBitmap source)
    {
        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        return new Bitmap(stream);
    }

    private IReadOnlyList<Detection> _latestDetections = [];

    private NeuralModel _model = NeuralModel.CreatePlaceholder(
        ModelFamily.MobileNetSsd,
        labels: ["person", "bicycle", "car", "motorbike", "aeroplane", "bus", "train", "truck", "boat", "traffic light", "bottle", "chair", "sofa", "laptop", "cup", "keyboard", "cell phone", "book"],
        inputWidth: 300,
        inputHeight: 300);

    /// <summary>Menggambar deteksi terakhir di atas frame video terbaru.</summary>
    private void ShowAnnotated(ImageFrame frame)
    {
        var detections = Volatile.Read(ref _latestDetections);
        var pixels = PixelConverter.ToBgr888(frame);
        FrameOverlay.DrawDetections(pixels, frame.Width, frame.Height, detections);

        using var annotated = ImageFrame.Wrap(
            pixels, frame.Width, frame.Height, PixelFormat.Bgr888);

        ShowFrame(Preview, annotated);
    }
}