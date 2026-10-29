using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Highlighting;
using DepthAI.Gallery.Inference;
using DepthAI.Gallery.Showcase;
using SkiaSharp;

namespace DepthAI.Gallery.Views;

public sealed partial class MainWindow : Window
{
    private const string AllSectors = "Semua";

    private readonly HttpClient _http = GalleryPaths.CreateHttpClient();
    private readonly GalleryEngine _engine;
    private readonly Recordings _recordings = Recordings.Load(GalleryPaths.SamplesDirectory);
    private readonly Dictionary<string, SKBitmap> _images = [];
    private readonly Dictionary<string, Bitmap> _thumbnails = [];
    private readonly List<UseCaseItem> _useCases = [.. Catalog.All.Select(u => new UseCaseItem(u))];

    private UseCase _useCase = Catalog.All[0];
    private SampleItem? _sample;
    private SKBitmap? _customImage;
    private SKBitmap? _liveFrame;
    private Bitmap? _shownImage;
    private Bitmap? _shownOverlay;
    private CancellationTokenSource? _runCts;
    private bool _live;
    private bool _busy;
    private bool _suppressSelection;
    private int _liveFrames;
    private DateTime _liveWindowStart = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent();
        _engine = new GalleryEngine(new ModelZoo(_http, ModelZoo.DefaultCacheDirectory, GalleryPaths.ModelConfigDirectory));

        CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        EditorTheme.Apply(CodeEditor);
        ActualThemeVariantChanged += (_, _) =>
        {
            EditorTheme.Apply(CodeEditor);
            Stage.InvalidateVisual();
        };

        SectorList.ItemsSource = (string[])[AllSectors, .. Catalog.Sectors];
        SectorList.SelectedIndex = 0;
        SectorList.SelectionChanged += (_, _) => ApplySectorFilter();

        UseCaseList.ItemsSource = _useCases;
        UseCaseList.SelectionChanged += async (_, _) =>
        {
            if (!_suppressSelection && UseCaseList.SelectedItem is UseCaseItem item && item.UseCase != _useCase)
            {
                await SelectUseCaseAsync(item.UseCase);
            }
        };

        SampleList.SelectionChanged += async (_, _) =>
        {
            // Daftar kadang memilih ulang item yang sama saat kontainernya dibuat; tanpa
            // pemeriksaan ini, jalannya kedua membatalkan yang pertama.
            if (!_suppressSelection && SampleList.SelectedItem is SampleItem sample && sample != _sample)
            {
                _customImage = null;
                _sample = sample;
                await ShowSampleAsync();
            }
        };

        RunButton.Click += async (_, _) => await RunButtonClickedAsync();
        OpenImageButton.Click += async (_, _) => await OpenOwnImageAsync();
        CopyCodeButton.Click += async (_, _) => await CopyCodeAsync();
        ResultTabButton.IsCheckedChanged += (_, _) => ShowTab();
        LiveModeButton.IsCheckedChanged += async (_, _) => await ModeChangedAsync();

        Opened += async (_, _) => await StartupAsync();
        Closing += (_, _) => _ = _engine.DisposeAsync().AsTask();
    }

    private async Task StartupAsync()
    {
        var initial = Catalog.All.FirstOrDefault(u => u.Id == Program.Options.InitialUseCase) ?? Catalog.All[0];
        SetDeviceChip(connecting: true);
        await SelectUseCaseAsync(initial, runOnDevice: false);

        await _engine.ConnectAsync();
        SetDeviceChip(connecting: false);

        if (Program.Options.ScreenshotDirectory is { } folder)
        {
            await Screenshots.CaptureAsync(this, folder);
            return;
        }

        if (_engine.IsConnected)
        {
            await ShowSampleAsync();
        }
    }

    // ---------------------------------------------------------------------------------
    // Pilihan

    internal async Task SelectUseCaseAsync(UseCase useCase, bool runOnDevice = true)
    {
        _useCase = useCase;
        _suppressSelection = true;
        UseCaseList.SelectedItem = _useCases.FirstOrDefault(i => i.UseCase == useCase);

        SectorEyebrow.Text = string.Join("  ·  ", useCase.Sectors).ToUpperInvariant();
        TitleText.Text = useCase.Title;
        ApplicationText.Text = useCase.Application;
        NoteText.Text = useCase.Note;
        NoteBox.IsVisible = useCase.Note is not null;

        // Gambar milik use case ini di depan; sisanya tetap bisa dicoba dengan model yang sama.
        var files = useCase.Samples
            .Concat(Directory.EnumerateFiles(GalleryPaths.SamplesDirectory, "*.jpg").Select(Path.GetFileName).OfType<string>().Order())
            .Distinct()
            .ToList();
        var samples = files.Select(f => new SampleItem(
            f,
            Thumbnail(f),
            _recordings.Find(useCase.Id, f) is not null,
            useCase.Samples.Contains(f))).ToList();
        SampleList.ItemsSource = samples;
        _sample = samples[0];
        _customImage = null;
        SampleList.SelectedItem = _sample;
        _suppressSelection = false;

        UpdateCode();
        if (_live)
        {
            await StartLiveAsync();
        }
        else if (runOnDevice)
        {
            await ShowSampleAsync();
        }
        else
        {
            ShowRecordedOrEmpty();
        }
    }

    private void ApplySectorFilter()
    {
        var sector = SectorList.SelectedItem as string ?? AllSectors;
        var visible = _useCases.Where(i => sector == AllSectors || i.UseCase.Sectors.Contains(sector)).ToList();
        _suppressSelection = true;
        UseCaseList.ItemsSource = visible;
        UseCaseList.SelectedItem = visible.FirstOrDefault(i => i.UseCase == _useCase);
        _suppressSelection = false;
    }

    private Bitmap Thumbnail(string file)
    {
        if (!_thumbnails.TryGetValue(file, out var thumbnail))
        {
            using var stream = File.OpenRead(Path.Combine(GalleryPaths.SamplesDirectory, file));
            _thumbnails[file] = thumbnail = Bitmap.DecodeToWidth(stream, 240);
        }

        return thumbnail;
    }

    private SKBitmap SampleImage(string file)
    {
        if (!_images.TryGetValue(file, out var image))
        {
            _images[file] = image = ImageIo.Load(Path.Combine(GalleryPaths.SamplesDirectory, file));
        }

        return image;
    }

    internal bool IsBusy => _busy;

    private SKBitmap? CurrentImage => _customImage ?? (_sample is null ? null : SampleImage(_sample.File));

    // ---------------------------------------------------------------------------------
    // Gambar statis

    private async Task ShowSampleAsync()
    {
        if (_engine.IsConnected && !_live)
        {
            await RunOnDeviceAsync();
        }
        else
        {
            ShowRecordedOrEmpty();
        }
    }

    private void ShowRecordedOrEmpty()
    {
        if (CurrentImage is not { } image)
        {
            return;
        }

        var recorded = _customImage is null && _sample is not null ? _recordings.Find(_useCase.Id, _sample.File) : null;
        if (recorded is not null)
        {
            Display(image, recorded, ResultSource.Recorded, animate: false);
            return;
        }

        Display(image, null, ResultSource.None, animate: false);
        HeadlineText.Text = _engine.IsConnected
            ? "Tekan Jalankan di kamera untuk menganalisis gambar ini."
            : "Gambar ini belum direkam untuk use case ini. Sambungkan kamera OAK untuk menjalankannya.";
    }

    private async Task RunOnDeviceAsync()
    {
        if (CurrentImage is not { } image || !_engine.IsConnected)
        {
            ShowRecordedOrEmpty();
            return;
        }

        _runCts?.Cancel();
        var cts = _runCts = new CancellationTokenSource();
        var useCase = _useCase;
        SetBusy(true);
        Display(image, null, ResultSource.None, animate: false);
        HeadlineText.Text = "Kamera sedang menganalisis…";
        Stage.Scanning = true;

        try
        {
            var progress = new Progress<string>(SetStatus);
            var result = await _engine.AnalyzeImageAsync(useCase, image, progress, cts.Token);
            if (cts.IsCancellationRequested || useCase != _useCase || image != CurrentImage)
            {
                return;
            }

            Stage.Scanning = false;
            Display(image, result, ResultSource.Device, animate: true);
            SetStatus($"Selesai di {result.Run!.DeviceName} dalam {result.Run.RoundTripMilliseconds:0} ms.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Stage.Scanning = false;
            Console.Error.WriteLine($"[galeri] {useCase.Id}: {ex}");
            SetStatus($"Gagal menjalankan di kamera: {ex.Message}");
            ShowRecordedOrEmpty();
        }
        finally
        {
            if (_runCts == cts)
            {
                Stage.Scanning = false;
                SetBusy(false);
            }
        }
    }

    private async Task RunButtonClickedAsync()
    {
        if (_live)
        {
            LiveModeButton.IsChecked = false;
            ImageModeButton.IsChecked = true;
            return;
        }

        await RunOnDeviceAsync();
    }

    private async Task OpenOwnImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pilih gambar untuk dianalisis kamera",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            _customImage = ImageIo.Load(path);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            return;
        }

        _suppressSelection = true;
        SampleList.SelectedItem = null;
        _suppressSelection = false;
        await ShowSampleAsync();
    }

    // ---------------------------------------------------------------------------------
    // Kamera live

    private async Task ModeChangedAsync()
    {
        var wantLive = LiveModeButton.IsChecked == true;
        if (wantLive == _live)
        {
            return;
        }

        if (wantLive && !_engine.IsConnected)
        {
            LiveModeButton.IsChecked = false;
            ImageModeButton.IsChecked = true;
            SetStatus(_engine.ConnectionProblem ?? "Kamera belum tersambung.");
            return;
        }

        _live = wantLive;
        SampleList.IsVisible = !_live;
        OpenImageButton.IsVisible = !_live;
        RunButton.Content = _live ? "Hentikan kamera" : "Jalankan di kamera";
        LiveBadge.IsVisible = _live;
        UpdateCode();

        if (_live)
        {
            await StartLiveAsync();
        }
        else
        {
            _runCts?.Cancel();
            await _engine.StopLiveAsync();
            SetStatus("Kamera live dihentikan.");
            await ShowSampleAsync();
        }
    }

    private async Task StartLiveAsync()
    {
        _runCts?.Cancel();
        var cts = _runCts = new CancellationTokenSource();
        SetBusy(true);
        Stage.Show(null, null, null, false);
        Stage.Message = "Menyiapkan kamera…";
        HeadlineText.Text = "Menunggu frame pertama…";
        LiveBadgeText.Text = "LIVE";
        _liveFrames = 0;
        _liveWindowStart = DateTime.UtcNow;

        try
        {
            await _engine.StartLiveAsync(
                _useCase,
                update => Dispatcher.UIThread.Post(() => ShowLive(update, cts.Token)),
                new Progress<string>(SetStatus),
                cts.Token);
            SetStatus($"Kamera live: {_useCase.Primary} berjalan di {_engine.DeviceName}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus($"Kamera live gagal: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            RunButton.IsEnabled = true;
        }
    }

    private void ShowLive(LiveUpdate update, CancellationToken token)
    {
        if (token.IsCancellationRequested || !_live)
        {
            update.Frame.Dispose();
            return;
        }

        var previous = _liveFrame;
        _liveFrame = update.Frame;
        Display(update.Frame, update.Result, ResultSource.Live, animate: false);
        previous?.Dispose();

        _liveFrames++;
        var elapsed = (DateTime.UtcNow - _liveWindowStart).TotalSeconds;
        if (elapsed >= 1)
        {
            LiveBadgeText.Text = string.Create(CultureInfo.CurrentCulture, $"LIVE · {_liveFrames / elapsed:0.0} hasil/detik");
            _liveFrames = 0;
            _liveWindowStart = DateTime.UtcNow;
        }
    }

    // ---------------------------------------------------------------------------------
    // Tampilan hasil

    private enum ResultSource
    {
        None,
        Recorded,
        Device,
        Live,
    }

    private void Display(SKBitmap image, GalleryResult? result, ResultSource source, bool animate)
    {
        var oldImage = _shownImage;
        var oldOverlay = _shownOverlay;

        using (var composed = result is null ? null : ResultImaging.Compose(image, result))
        {
            _shownImage = ResultImaging.ToAvalonia(composed ?? image);
        }

        _shownOverlay = result is null ? null : ResultImaging.BuildOverlay(result);
        Stage.Message = null;
        Stage.Show(_shownImage, result, _shownOverlay, animate);
        oldImage?.Dispose();
        oldOverlay?.Dispose();

        HeadlineText.Text = result?.Headline ?? string.Empty;
        MetricList.ItemsSource = result?.Metrics.Select(m => new MetricItem(m)).ToList();
        ScoreList.ItemsSource = result?.Scores.Select(s => new ScoreItem(s)).ToList();
        ScoreList.IsVisible = result?.Scores.Count > 0;
        UpdateSpecimen(image, result, source);
    }

    private void UpdateSpecimen(SKBitmap image, GalleryResult? result, ResultSource source)
    {
        var metadata = _engine.Metadata(_useCase.Primary);
        SpecModel.Text = string.Join(Environment.NewLine, _useCase.Stages.Select(s => s.Model.ToString()).Prepend(_useCase.Primary.ToString()));
        SpecInput.Text = source == ResultSource.Live
            ? $"{metadata.InputWidth}×{metadata.InputHeight} preview kamera, {_useCase.LiveFps} fps"
            : $"{metadata.InputWidth}×{metadata.InputHeight} BGR, letterbox dari {image.Width}×{image.Height}";

        if (result?.Run is not { } run)
        {
            SpecDevice.Text = _engine.IsConnected ? $"{_engine.DeviceName} · {_engine.DeviceId}" : "–";
            SpecTime.Text = "–";
            SpecSource.Text = source == ResultSource.None && _engine.IsConnected ? "Menunggu kamera" : "Belum dijalankan";
            return;
        }

        SpecDevice.Text = $"{run.DeviceName} · {run.DeviceId}";
        var inferences = run.InferenceCount == 1 ? "1 inferensi" : $"{run.InferenceCount} inferensi";
        SpecTime.Text = source == ResultSource.Live
            ? $"{run.RoundTripMilliseconds:0} ms antar-hasil · {inferences}"
            : $"{run.RoundTripMilliseconds:0} ms kirim→hasil · {inferences}";
        SpecSource.Text = source switch
        {
            ResultSource.Recorded => $"Rekaman, {run.Timestamp.ToLocalTime():d MMM yyyy HH:mm}",
            ResultSource.Live => "Kamera live",
            _ => "Langsung dari kamera",
        };
    }

    // ---------------------------------------------------------------------------------
    // Kode

    internal void ShowCode(bool code)
    {
        CodeTabButton.IsChecked = code;
        ResultTabButton.IsChecked = !code;
        ShowTab();
    }

    private void ShowTab()
    {
        var code = CodeTabButton.IsChecked == true;
        CodePanel.IsVisible = code;
        Stage.IsVisible = !code;
        LiveBadge.IsVisible = _live && !code;
    }

    private void UpdateCode()
    {
        CodeEditor.Text = Snippets.Build(_useCase, _engine.Zoo, _live);
        CodeCaption.Text = _live
            ? "Mode kamera: model berjalan pada preview, hasil datang lewat langganan stream."
            : "Mode gambar: kirim satu gambar ke kamera, tunggu satu hasil.";
    }

    private async Task CopyCodeAsync()
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(CodeEditor.Text);
            SetStatus("Kode disalin ke papan klip.");
        }
    }

    // ---------------------------------------------------------------------------------
    // Status

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RunButton.IsEnabled = (!busy && _engine.IsConnected) || _live;
        OpenImageButton.IsEnabled = !busy && _engine.IsConnected;
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void SetDeviceChip(bool connecting)
    {
        if (connecting)
        {
            DeviceText.Text = "Mencari kamera…";
            DeviceDot.Fill = App.Resource("InkFaint");
            return;
        }

        if (_engine.IsConnected)
        {
            DeviceText.Text = $"{_engine.DeviceName} · {_engine.DeviceId}";
            DeviceDot.Fill = App.Resource("AccentMid");
            SetStatus($"Kamera tersambung. Gambar dikirim ke {_engine.DeviceName} dan dianalisis di sana.");
        }
        else
        {
            DeviceText.Text = "Tanpa kamera · hasil rekaman";
            DeviceDot.Fill = App.Resource("InkFaint");
            ToolTip.SetTip(LiveModeButton, _engine.ConnectionProblem);
            LiveModeButton.IsEnabled = false;
            SetStatus($"{_engine.ConnectionProblem} Menampilkan hasil yang direkam dari OAK-1.");
        }

        SetBusy(_busy);
    }

    // ---------------------------------------------------------------------------------
    // Item daftar

    private sealed class UseCaseItem(UseCase useCase)
    {
        public UseCase UseCase { get; } = useCase;

        public string Title => UseCase.Title;

        public string Summary => UseCase.Summary;

        public string SectorText => string.Join(" · ", UseCase.Sectors).ToUpperInvariant();
    }

    private sealed class SampleItem(string file, Bitmap thumbnail, bool isRecorded, bool isOwn)
    {
        public string File { get; } = file;

        public Bitmap Thumbnail { get; } = thumbnail;

        public bool IsRecorded { get; } = isRecorded;

        public string Tip => (isOwn ? "Gambar contoh use case ini" : "Coba model ini pada gambar lain")
            + (IsRecorded ? " · ada rekaman hasil" : string.Empty);
    }

    private sealed class MetricItem(ResultMetric metric)
    {
        public string Label { get; } = metric.Label;

        public string Value { get; } = metric.Value;

        public IBrush Brush { get; } = metric.Tone == ResultTone.Neutral
            ? App.Resource("InkBright")
            : new SolidColorBrush(Palette.ForTone(metric.Tone, Colors.White));
    }

    private sealed class ScoreItem(ResultScore score)
    {
        public string Label { get; } = score.Label;

        public string Percent { get; } = $"{score.Value:P0}";

        public double BarWidth { get; } = Math.Max(2, score.Value * 296);
    }
}
