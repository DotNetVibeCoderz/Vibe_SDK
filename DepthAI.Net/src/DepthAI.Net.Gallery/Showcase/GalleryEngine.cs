using System.Diagnostics;
using DepthAI.Gallery.Inference;
using DepthAI.Inference;
using DepthAI.Interop;
using DepthAI.Pipelines;
using DepthAI.Streaming;
using SkiaSharp;

namespace DepthAI.Gallery.Showcase;

/// <summary>Satu frame live beserta hasil analisisnya.</summary>
public sealed record LiveUpdate(SKBitmap Frame, GalleryResult Result);

/// <summary>
/// Menghubungkan galeri dengan kamera: mengunduh model, menyusun pipeline per use case, dan
/// menjalankan analisis pada gambar statis atau aliran kamera.
/// </summary>
/// <remarks>
/// Semua model use case dimuat dalam satu pipeline. Model utama diumpani HostInput (gambar
/// statis) atau kamera (live); model tahap kedua selalu diumpani HostInput-nya sendiri, karena
/// masukannya potongan buatan host. Pipeline hanya disusun ulang saat use case atau mode
/// berganti: depthai v3 menutup dan mem-boot ulang perangkat setiap pipeline berhenti.
/// </remarks>
public sealed class GalleryEngine(ModelZoo zoo) : IAsyncDisposable
{
    private const string PrimaryInput = "image";
    private const string PrimaryStream = "result";
    private const string PassthroughStream = "frame";
    private static readonly TimeSpan InferTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ZooModel, NeuralModel> _models = [];
    private DepthAiDevice? _device;
    private string? _pipelineKey;
    private LiveSession? _live;

    public ModelZoo Zoo { get; } = zoo;

    public bool IsConnected => _device is not null;

    /// <summary>Nama produk, misalnya "OAK-1".</summary>
    public string DeviceName { get; private set; } = string.Empty;

    public string DeviceId { get; private set; } = string.Empty;

    /// <summary>Alasan kamera tidak bisa dipakai, untuk ditampilkan apa adanya.</summary>
    public string? ConnectionProblem { get; private set; }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_device is not null)
        {
            return true;
        }

        if (!DepthAi.IsNativeAvailable)
        {
            ConnectionProblem = $"Runtime native tidak tersedia: {DepthAi.NativeUnavailableReason}";
            return false;
        }

        try
        {
            _device = await DepthAiDevice.OpenAsync(
                new DepthAiOptions { Backend = BackendSelection.NativeOnly },
                cancellationToken);
            DeviceName = _device.Info.Name;
            DeviceId = _device.Info.SerialNumber;
            ConnectionProblem = null;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConnectionProblem = ex is DeviceNotFoundException ? "Tidak ada kamera OAK yang tersambung." : ex.Message;
            return false;
        }
    }

    public ModelMetadata Metadata(ZooModel model) => Zoo.ReadMetadata(model);

    /// <summary>Menjalankan use case pada satu gambar di kamera.</summary>
    public async Task<GalleryResult> AnalyzeImageAsync(UseCase useCase, SKBitmap image, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var device = _device ?? throw new InvalidOperationException("Kamera belum tersambung.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopLiveCoreAsync();
            await EnsurePipelineAsync(useCase, live: false, progress, cancellationToken);

            var metadata = Metadata(useCase.Primary);
            var letterbox = new Letterbox(image.Width, image.Height, metadata.InputWidth, metadata.InputHeight);
            using var input = ImageIo.ToModelInput(image, letterbox);

            progress?.Report("Mengirim gambar ke kamera…");
            var clock = Stopwatch.StartNew();
            using var output = await device.InferAsync<NeuralTensorFrame>(PrimaryInput, input, PrimaryStream, InferTimeout, cancellationToken);
            var context = CreateContext(useCase, output.Tensors, metadata, letterbox, image, cancellationToken);
            var result = await useCase.Analyze(context);
            result.Run = Stamp(clock.Elapsed, 1 + context.StageInferences);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Memulai aliran kamera; <paramref name="onUpdate"/> dipanggil dari thread latar.</summary>
    public async Task StartLiveAsync(UseCase useCase, Action<LiveUpdate> onUpdate, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var device = _device ?? throw new InvalidOperationException("Kamera belum tersambung.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopLiveCoreAsync();
            await EnsurePipelineAsync(useCase, live: true, progress, cancellationToken);
            var metadata = Metadata(useCase.Primary);
            _live = new LiveSession(this, device, useCase, metadata, onUpdate);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopLiveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopLiveCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopLiveAsync();
        if (_device is not null)
        {
            await _device.DisposeAsync();
            _device = null;
        }
    }

    private async Task StopLiveCoreAsync()
    {
        if (_live is { } live)
        {
            _live = null;
            await live.DisposeAsync();
        }
    }

    private RunInfo Stamp(TimeSpan elapsed, int inferences) => new()
    {
        DeviceName = DeviceName,
        DeviceId = DeviceId,
        Timestamp = DateTimeOffset.Now,
        RoundTripMilliseconds = Math.Round(elapsed.TotalMilliseconds, 1),
        InferenceCount = inferences,
    };

    private AnalysisContext CreateContext(
        UseCase useCase,
        IReadOnlyDictionary<string, Tensor> tensors,
        ModelMetadata metadata,
        Letterbox letterbox,
        SKBitmap image,
        CancellationToken cancellationToken)
    {
        var stages = useCase.Stages.ToDictionary(s => s.Key, s => s.Model);
        return new AnalysisContext
        {
            Tensors = tensors,
            Metadata = metadata,
            Letterbox = letterbox,
            Image = image,
            StageMetadata = key => Metadata(stages[key]),
            InferStage = async (key, frame) =>
            {
                using var output = await _device!.InferAsync<NeuralTensorFrame>(key + "-in", frame, key, InferTimeout, cancellationToken);
                return output.Tensors;
            },
        };
    }

    private async Task<NeuralModel> LoadModelAsync(ZooModel model, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (_models.TryGetValue(model, out var cached))
        {
            return cached;
        }

        var loaded = await Zoo.LoadAsync(model, progress, cancellationToken);

        // Galeri selalu memakai tensor mentah: dekodernya sendiri yang menafsirkan, jadi
        // jalur gambar statis dan live melewati kode yang sama persis.
        var raw = NeuralModel.FromBytes(loaded.Payload, loaded.Format, loaded.Metadata with { Family = ModelFamily.Raw }, model.Slug);
        _models[model] = raw;
        return raw;
    }

    private async Task EnsurePipelineAsync(UseCase useCase, bool live, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var key = $"{useCase.Id}:{(live ? "live" : "image")}";
        if (key == _pipelineKey && _device!.IsRunning)
        {
            return;
        }

        var primary = await LoadModelAsync(useCase.Primary, progress, cancellationToken);
        var stages = new List<(StageModel Stage, NeuralModel Model)>();
        foreach (var stage in useCase.Stages)
        {
            stages.Add((stage, await LoadModelAsync(stage.Model, progress, cancellationToken)));
        }

        var pipeline = Pipeline.Create();
        var nn = pipeline.AddNeuralNetwork("nn", n => n.Model = primary);
        if (live)
        {
            var camera = pipeline.AddColorCamera("rgb", c =>
            {
                c.PreviewWidth = primary.Metadata.InputWidth;
                c.PreviewHeight = primary.Metadata.InputHeight;
                c.Fps = useCase.LiveFps;
            });
            camera.Preview.LinkTo(nn.Input);
            pipeline.AddOutputStream(nn.Passthrough, PassthroughStream);
        }
        else
        {
            pipeline.AddHostInput(PrimaryInput).Out.LinkTo(nn.Input);
        }

        pipeline.AddOutputStream(nn.Out, PrimaryStream);

        foreach (var (stage, model) in stages)
        {
            var stageNn = pipeline.AddNeuralNetwork(stage.Key, n => n.Model = model);
            pipeline.AddHostInput(stage.Key + "-in").Out.LinkTo(stageNn.Input);
            pipeline.AddOutputStream(stageNn.Out, stage.Key);
        }

        var device = _device!;
        if (device.IsRunning)
        {
            progress?.Report("Menghentikan pipeline sebelumnya…");
            await device.StopAsync(cancellationToken);
        }

        _pipelineKey = null;
        progress?.Report($"Memuat {useCase.Primary} ke kamera…");
        await device.StartAsync(pipeline, cancellationToken);
        _pipelineKey = key;
    }

    /// <summary>
    /// Memasangkan frame passthrough dengan tensornya lewat nomor urut, lalu menganalisis
    /// pasangan terbaru di thread sendiri. Callback stream tidak boleh menunggu: tahap kedua
    /// memakai InferAsync, yang jawabannya datang lewat thread polling yang sama.
    /// </summary>
    private sealed class LiveSession : IAsyncDisposable
    {
        private readonly GalleryEngine _engine;
        private readonly UseCase _useCase;
        private readonly ModelMetadata _metadata;
        private readonly Action<LiveUpdate> _onUpdate;
        private readonly IDisposable _frames;
        private readonly IDisposable _results;
        private readonly Lock _gate = new();
        private readonly Dictionary<long, SKBitmap> _pendingFrames = [];
        private readonly Dictionary<long, IReadOnlyDictionary<string, Tensor>> _pendingTensors = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly Task _worker;
        private (long Sequence, SKBitmap Frame, IReadOnlyDictionary<string, Tensor> Tensors)? _latest;
        private DateTime _lastFrame = DateTime.UtcNow;

        public LiveSession(GalleryEngine engine, DepthAiDevice device, UseCase useCase, ModelMetadata metadata, Action<LiveUpdate> onUpdate)
        {
            _engine = engine;
            _useCase = useCase;
            _metadata = metadata;
            _onUpdate = onUpdate;
            _frames = device.GetStream<ImageFrame>(PassthroughStream).Subscribe(frame => Offer(frame.SequenceNumber, ImageIo.FromFrame(frame), null));
            _results = device.GetStream<NeuralTensorFrame>(PrimaryStream).Subscribe(frame => Offer(frame.SequenceNumber, null, frame.Tensors));
            _worker = Task.Run(RunAsync);
        }

        private void Offer(long sequence, SKBitmap? frame, IReadOnlyDictionary<string, Tensor>? tensors)
        {
            lock (_gate)
            {
                if (frame is not null) _pendingFrames[sequence] = frame;
                if (tensors is not null) _pendingTensors[sequence] = tensors;

                if (_pendingFrames.TryGetValue(sequence, out var f) && _pendingTensors.TryGetValue(sequence, out var t))
                {
                    _pendingFrames.Remove(sequence);
                    _pendingTensors.Remove(sequence);
                    if (_latest is { } stale)
                    {
                        stale.Frame.Dispose();
                    }

                    _latest = (sequence, f, t);
                    _signal.Release();
                }

                // Pasangan yang tidak pernah lengkap (frame dibuang perangkat) jangan menumpuk.
                foreach (var old in _pendingFrames.Keys.Where(k => k < sequence - 8).ToList())
                {
                    _pendingFrames[old].Dispose();
                    _pendingFrames.Remove(old);
                }

                foreach (var old in _pendingTensors.Keys.Where(k => k < sequence - 8).ToList())
                {
                    _pendingTensors.Remove(old);
                }
            }
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                (long Sequence, SKBitmap Frame, IReadOnlyDictionary<string, Tensor> Tensors) item;
                lock (_gate)
                {
                    if (_latest is not { } latest)
                    {
                        continue;
                    }

                    item = latest;
                    _latest = null;
                }

                try
                {
                    var now = DateTime.UtcNow;
                    var context = _engine.CreateContext(
                        _useCase, item.Tensors, _metadata, Letterbox.Identity(item.Frame.Width, item.Frame.Height), item.Frame, _cts.Token);
                    var result = await _useCase.Analyze(context);

                    // Pada mode live, angka yang berguna adalah selang antar-hasil: itulah laju
                    // yang dirasakan pengguna, termasuk inferensi tahap kedua.
                    result.Run = _engine.Stamp(now - _lastFrame, 1 + context.StageInferences);
                    _lastFrame = now;
                    _onUpdate(new LiveUpdate(item.Frame, result));
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    item.Frame.Dispose();
                    return;
                }
                catch (Exception ex)
                {
                    item.Frame.Dispose();
                    Trace.TraceWarning($"Analisis live gagal: {ex.Message}");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _frames.Dispose();
            _results.Dispose();
            await _cts.CancelAsync();
            try
            {
                await _worker;
            }
            catch (OperationCanceledException)
            {
            }

            lock (_gate)
            {
                foreach (var frame in _pendingFrames.Values) frame.Dispose();
                _pendingFrames.Clear();
                _latest?.Frame.Dispose();
                _latest = null;
            }

            _cts.Dispose();
        }
    }
}
