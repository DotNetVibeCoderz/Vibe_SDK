using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DepthAI;
using DepthAI.Imaging;
using DepthAI.Inference;
using DepthAI.Pipelines;
using DepthAI.Streaming;
using SkiaSharp;

namespace DepthAiDesktopApp;

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

            var pipeline = PipelinePresets.StereoDepth(fps: 30);

            await _device.StartAsync(pipeline);

            _subscriptions.Add(_device.GetStream<ImageFrame>("video")
                .Subscribe(frame => ShowFrame(Preview, frame)));

            _subscriptions.Add(_device.GetStream<DepthFrame>("depth")
                .Subscribe(frame =>
                {
                    // Salinan disimpan supaya pembacaan jarak di bawah kursor
                    // bisa memakai nilai milimeter asli, bukan piksel berwarna.
                    var previous = Interlocked.Exchange(ref _latestDepth, frame.Clone());
                    previous?.Dispose();

                    ShowDepth(DepthView, frame);
                }));

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

    private DepthFrame? _latestDepth;

    private void OnDepthPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        var depth = Volatile.Read(ref _latestDepth);
        if (depth is null || DepthView.Bounds.Width <= 0)
        {
            return;
        }

        var position = e.GetPosition(DepthView);
        var x = (int)(position.X / DepthView.Bounds.Width * depth.Width);
        var y = (int)(position.Y / DepthView.Bounds.Height * depth.Height);

        if (x < 0 || y < 0 || x >= depth.Width || y >= depth.Height)
        {
            return;
        }

        var distance = depth.GetDistanceMeters(x, y);
        Status.Text = distance is null
            ? $"({x}, {y}) — tidak ada pengukuran"
            : $"({x}, {y}) — {distance:F2} m";
    }
}