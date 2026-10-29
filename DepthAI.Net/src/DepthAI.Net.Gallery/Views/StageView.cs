using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DepthAI.Gallery.Inference;

namespace DepthAI.Gallery.Views;

/// <summary>
/// Panggung: gambar, lapisan mask, dan anotasi vektor hasil inferensi.
/// </summary>
/// <remarks>
/// Elemen penanda galeri ada di sini. Saat gambar sedang diproses kamera, depth ribbon yang
/// biasanya tipis di status bar berubah menjadi garis pindai yang menyapu gambar; hasilnya
/// muncul di belakang garis itu. Saat hasil datang, anotasi memudar masuk.
/// </remarks>
internal sealed class StageView : Control
{
    private static readonly Typeface LabelFace = new("Inter", FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface CaptionFace = new("Inter");

    private readonly DispatcherTimer _timer;
    private Bitmap? _image;
    private Bitmap? _overlay;
    private GalleryResult? _result;
    private DateTime _scanStarted;
    private DateTime _revealStarted;
    private bool _scanning;

    public StageView()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
    }

    /// <summary>Teks di tengah panggung bila tidak ada gambar, atau di bawah gambar bila ada.</summary>
    public string? Message { get; set; }

    public bool Scanning
    {
        get => _scanning;
        set
        {
            if (_scanning == value)
            {
                return;
            }

            _scanning = value;
            if (value)
            {
                _scanStarted = DateTime.UtcNow;
            }

            UpdateTimer();
            InvalidateVisual();
        }
    }

    /// <summary>Mengganti gambar dan hasilnya sekaligus. Panggung tidak membuang bitmap lama milik pemanggil.</summary>
    public void Show(Bitmap? image, GalleryResult? result, Bitmap? overlay, bool animate)
    {
        _image = image;
        _result = result;
        _overlay = overlay;
        _revealStarted = animate ? DateTime.UtcNow : DateTime.MinValue;
        UpdateTimer();
        InvalidateVisual();
    }

    private void UpdateTimer()
    {
        var revealing = (DateTime.UtcNow - _revealStarted).TotalMilliseconds < 400;
        if (_scanning || revealing)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Tick()
    {
        UpdateTimer();
        InvalidateVisual();
    }

    /// <summary>Kotak tempat gambar digambar: pas di panggung dengan rasio aspek terjaga.</summary>
    private Rect ImageRect()
    {
        if (_image is null)
        {
            return default;
        }

        var (w, h) = (_image.Size.Width, _image.Size.Height);
        var scale = Math.Min(Bounds.Width / w, Bounds.Height / h);
        var size = new Size(w * scale, h * scale);
        return new Rect(new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2), size);
    }

    public override void Render(DrawingContext context)
    {
        var ink = App.Resource("InkMuted");
        if (_image is null)
        {
            DrawCentered(context, Message ?? string.Empty, ink, Bounds.Center);
            return;
        }

        var rect = ImageRect();
        context.DrawImage(_image, rect);

        var reveal = _revealStarted == DateTime.MinValue
            ? 1.0
            : Math.Clamp((DateTime.UtcNow - _revealStarted).TotalMilliseconds / 400.0, 0, 1);

        if (_result is not null && !_scanning)
        {
            using (context.PushOpacity(reveal))
            {
                if (_overlay is not null)
                {
                    using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    {
                        context.DrawImage(_overlay, rect);
                    }
                }

                DrawSkeletons(context, rect, _result);

                // Kotak dulu semuanya, label belakangan: label yang lebih yakin dapat tempat lebih dulu.
                var quiet = _result.Effect == ImageEffect.PixelateBoxes;
                foreach (var box in _result.Boxes)
                {
                    DrawFrame(context, rect, box, quiet);
                }

                // Puluhan label kembar ("teks", "teks", ...) hanya menutupi gambar; jumlahnya
                // sudah ada di panel hasil, jadi kotaknya cukup tanpa label.
                var repetitive = _result.Boxes.Count >= 8 && _result.Boxes.Select(b => b.Label).Distinct().Count() == 1;
                if (!quiet && !repetitive)
                {
                    var placed = new List<Rect>();
                    foreach (var box in _result.Boxes.OrderByDescending(b => b.Score))
                    {
                        DrawLabel(context, rect, box, placed);
                    }
                }
            }
        }

        if (_scanning)
        {
            DrawScan(context, rect);
        }
    }

    private void DrawScan(DrawingContext context, Rect rect)
    {
        // Satu sapuan 1,1 detik, berulang sampai kamera menjawab.
        var t = (DateTime.UtcNow - _scanStarted).TotalMilliseconds % 1100 / 1100.0;
        var x = rect.X + (rect.Width * t);

        var trail = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0x1B, 0xCF, 0xD4), 0),
                new GradientStop(Color.FromArgb(70, 0x1B, 0xCF, 0xD4), 1),
            },
        };
        var trailWidth = Math.Min(rect.Width * 0.25, x - rect.X);
        context.FillRectangle(trail, new Rect(x - trailWidth, rect.Y, trailWidth, rect.Height));

        var ribbon = (IBrush?)Application.Current?.FindResource("DepthRibbonVertical") ?? Brushes.Orange;
        context.FillRectangle(ribbon, new Rect(x - 1.5, rect.Y, 3, rect.Height));
    }

    private static void DrawSkeletons(DrawingContext context, Rect rect, GalleryResult result)
    {
        foreach (var box in result.Boxes)
        {
            if (box.Keypoints is not { Count: > 0 } points)
            {
                continue;
            }

            var color = Palette.ForBox(box);
            var line = new Pen(new SolidColorBrush(color, 0.9), 2.5, lineCap: PenLineCap.Round);
            foreach (var edge in result.Skeleton)
            {
                if (edge[0] < points.Count && edge[1] < points.Count && points[edge[0]].Score >= 0.5f && points[edge[1]].Score >= 0.5f)
                {
                    context.DrawLine(line, Map(rect, points[edge[0]]), Map(rect, points[edge[1]]));
                }
            }

            var dot = new SolidColorBrush(color);
            var rim = new Pen(new SolidColorBrush(Color.Parse("#10191E"), 0.8), 1.2);
            foreach (var point in points.Where(p => p.Score >= 0.5f))
            {
                context.DrawEllipse(dot, rim, Map(rect, point), 3.2, 3.2);
            }
        }
    }

    private static Point Map(Rect rect, ResultPoint p) => new(rect.X + (p.X * rect.Width), rect.Y + (p.Y * rect.Height));

    private static Rect Area(Rect rect, ResultBox box) => new(
        rect.X + (box.X0 * rect.Width),
        rect.Y + (box.Y0 * rect.Height),
        Math.Max(1, (box.X1 - box.X0) * rect.Width),
        Math.Max(1, (box.Y1 - box.Y0) * rect.Height));

    /// <remarks>
    /// Wajah yang sudah disamarkan cukup diberi garis tipis tanpa label: labelnya justru
    /// menarik perhatian ke wajah yang sengaja disembunyikan.
    /// </remarks>
    private static void DrawFrame(DrawingContext context, Rect rect, ResultBox box, bool quiet)
        => context.DrawRectangle(null, new Pen(new SolidColorBrush(Palette.ForBox(box)), quiet ? 1.2 : 2), Area(rect, box), 3, 3);

    /// <summary>
    /// Label di atas kotak, di dalam tepi atasnya, atau di bawahnya, mana yang pertama tidak
    /// menutupi label lain. Bila semuanya bertabrakan, kotak dibiarkan tanpa label: warnanya
    /// sudah membawa makna, dan daftar hasil di panel kanan memuat semuanya.
    /// </summary>
    private static void DrawLabel(DrawingContext context, Rect rect, ResultBox box, List<Rect> placed)
    {
        var color = Palette.ForBox(box);
        var area = Area(rect, box);

        var label = box.Score > 0 && box.Score <= 1 ? $"{box.Label} {box.Score * 100:0}%" : box.Label;
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11.5, new SolidColorBrush(Palette.InkOn(color)));
        FormattedText? caption = box.Caption is { Length: > 0 }
            ? new FormattedText(box.Caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CaptionFace, 10.5, new SolidColorBrush(Palette.InkOn(color)))
            : null;

        var chipWidth = Math.Max(text.Width, caption?.Width ?? 0) + 10;
        var chipHeight = text.Height + (caption?.Height ?? 0) + 4;

        var chipX = Math.Max(rect.X, Math.Min(area.X, rect.Right - chipWidth));
        Rect? free = null;
        foreach (var y in new[] { area.Y - chipHeight, area.Y + 1, area.Bottom + 1 })
        {
            var candidate = new Rect(chipX, y, chipWidth, chipHeight);
            if (y >= rect.Y && candidate.Bottom <= rect.Bottom && !placed.Any(p => p.Intersects(candidate)))
            {
                free = candidate;
                break;
            }
        }

        if (free is not { } chip)
        {
            return;
        }

        placed.Add(chip);
        context.DrawRectangle(new SolidColorBrush(color, 0.92), null, chip, 3, 3);
        context.DrawText(text, new Point(chip.X + 5, chip.Y + 2));
        if (caption is not null)
        {
            context.DrawText(caption, new Point(chip.X + 5, chip.Y + 2 + text.Height));
        }
    }

    private static void DrawCentered(DrawingContext context, string message, IBrush brush, Point center)
    {
        if (message.Length == 0)
        {
            return;
        }

        var text = new FormattedText(message, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CaptionFace, 13, brush)
        {
            MaxTextWidth = 420,
            TextAlignment = TextAlignment.Center,
        };
        context.DrawText(text, new Point(center.X - 210, center.Y - (text.Height / 2)));
    }
}
