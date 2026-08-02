using DepthAI.Devices;
using DepthAI.Inference;
using DepthAI.Pipelines.Nodes;

namespace DepthAI.Pipelines;

/// <summary>
/// Fasad fluent di atas <see cref="Pipeline"/>. Setiap metode mengembalikan builder
/// supaya pipeline lengkap bisa ditulis sebagai satu ekspresi. Untuk graf tidak biasa,
/// pakai <see cref="Pipeline"/> langsung — builder ini sengaja mengoptimalkan kasus lazim.
/// </summary>
public sealed class PipelineBuilder(Pipeline pipeline)
{
    private readonly Pipeline _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));

    /// <summary>Pipeline yang sedang dibangun; membolehkan operasi lanjutan di tengah rantai.</summary>
    public Pipeline Pipeline => _pipeline;

    public PipelineBuilder AddColorCamera(string name = "rgb", Action<ColorCameraNode>? configure = null)
    {
        _pipeline.AddColorCamera(name, configure);
        return this;
    }

    public PipelineBuilder AddMonoCamera(string name, Action<MonoCameraNode>? configure = null)
    {
        _pipeline.AddMonoCamera(name, configure);
        return this;
    }

    /// <summary>
    /// Menambahkan stereo depth beserta sepasang kamera mono kiri/kanan yang sudah
    /// tersambung — susunan yang praktis selalu dipakai, jadi tidak perlu diketik ulang.
    /// </summary>
    public PipelineBuilder AddStereoDepth(
        string name = "stereo",
        Action<StereoDepthNode>? configure = null,
        MonoResolution resolution = MonoResolution.The400P,
        int fps = 30)
    {
        var left = _pipeline.AddMonoCamera($"{name}_left", c =>
        {
            c.Socket = CameraSocket.Left;
            c.Resolution = resolution;
            c.Fps = fps;
        });

        var right = _pipeline.AddMonoCamera($"{name}_right", c =>
        {
            c.Socket = CameraSocket.Right;
            c.Resolution = resolution;
            c.Fps = fps;
        });

        var stereo = _pipeline.AddStereoDepth(name, configure);
        left.Out.LinkTo(stereo.Left);
        right.Out.LinkTo(stereo.Right);

        return this;
    }

    /// <summary>
    /// Menambahkan detection network dan menyambungkannya ke sumber gambar. Bila ukuran
    /// masukan model berbeda dari sumber, node <see cref="ImageManipNode"/> penyesuai
    /// disisipkan otomatis — ketidakcocokan ukuran adalah penyebab paling sering
    /// deteksi "berjalan tapi tidak menemukan apa-apa".
    /// </summary>
    public PipelineBuilder AddObjectDetection(
        NeuralModel model,
        string sourceOutputPath = "rgb.preview",
        string name = "detector",
        Action<DetectionNetworkNode>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        var source = _pipeline.ResolveOutput(sourceOutputPath);
        var detector = _pipeline.AddDetectionNetwork(name, node =>
        {
            node.Model = model;
            configure?.Invoke(node);
        });

        LinkWithResize(source, detector.Input, model, $"{name}_resize");
        return this;
    }

    /// <summary>
    /// Menambahkan detection network yang memberi koordinat 3D per objek. Bila belum ada
    /// stereo depth di pipeline, satu susunan stereo ditambahkan dan diselaraskan ke kamera sumber.
    /// </summary>
    public PipelineBuilder AddSpatialObjectDetection(
        NeuralModel model,
        string sourceOutputPath = "rgb.preview",
        string name = "spatialDetector",
        Action<SpatialDetectionNetworkNode>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        var source = _pipeline.ResolveOutput(sourceOutputPath);

        var stereo = _pipeline.Nodes.OfType<StereoDepthNode>().FirstOrDefault();
        if (stereo is null)
        {
            AddStereoDepth($"{name}_stereo", node => node.AlignTo = CameraSocket.Rgb);
            stereo = _pipeline.GetNode<StereoDepthNode>($"{name}_stereo");
        }

        var detector = _pipeline.AddSpatialDetectionNetwork(name, node =>
        {
            node.Model = model;
            configure?.Invoke(node);
        });

        LinkWithResize(source, detector.Input, model, $"{name}_resize");
        stereo.Depth.LinkTo(detector.DepthInput);

        return this;
    }

    /// <summary>Menyambungkan dua port lewat path string.</summary>
    public PipelineBuilder Link(string fromOutputPath, string toInputPath)
    {
        _pipeline.Link(fromOutputPath, toInputPath);
        return this;
    }

    /// <summary>Memaparkan port keluaran sebagai stream bernama untuk host.</summary>
    public PipelineBuilder StreamOut(string outputPath, string? name = null, int maxSize = 4, bool blocking = false)
    {
        _pipeline.AddOutputStream(_pipeline.ResolveOutput(outputPath), name, maxSize, blocking);
        return this;
    }

    /// <summary>Menerapkan konfigurasi kustom sambil tetap di dalam rantai fluent.</summary>
    public PipelineBuilder Configure(Action<Pipeline> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_pipeline);
        return this;
    }

    /// <summary>
    /// Menyelesaikan pembangunan. Validasi struktural dijalankan di sini supaya kesalahan
    /// muncul di titik pembangunan, bukan di tengah stream.
    /// </summary>
    public Pipeline Build(DeviceCapabilities? capabilities = null)
    {
        // Tanpa kemampuan perangkat, hanya cek yang tidak bergantung hardware yang berlaku;
        // pemeriksaan penuh terjadi lagi saat pipeline di-start pada perangkat nyata.
        _pipeline.Validate(capabilities).ThrowIfInvalid();
        return _pipeline;
    }

    /// <summary>Menyelesaikan tanpa validasi — untuk pipeline setengah jadi di tooling/editor.</summary>
    public Pipeline BuildUnvalidated() => _pipeline;

    private void LinkWithResize(NodeOutput source, NodeInput target, NeuralModel model, string manipName)
    {
        var (sourceWidth, sourceHeight) = SourceSizeOf(source);
        var needsResize = sourceWidth > 0
            && (sourceWidth != model.Metadata.InputWidth || sourceHeight != model.Metadata.InputHeight);

        if (!needsResize)
        {
            source.LinkTo(target);
            return;
        }

        var manip = _pipeline.AddImageManip(manipName, node =>
        {
            node.ResizeWidth = model.Metadata.InputWidth;
            node.ResizeHeight = model.Metadata.InputHeight;
            node.KeepAspectRatio = false;
            node.Interleaved = false;
        });

        source.LinkTo(manip.Input);
        manip.Out.LinkTo(target);
    }

    /// <summary>Ukuran frame yang dipancarkan sebuah port, atau (0,0) bila tidak bisa disimpulkan.</summary>
    private static (int Width, int Height) SourceSizeOf(NodeOutput output) => output.Node switch
    {
        ColorCameraNode camera when output.Name == "preview" => (camera.PreviewWidth, camera.PreviewHeight),
        ColorCameraNode camera => camera.GetSensorSize(),
        MonoCameraNode mono => mono.GetSensorSize(),
        ImageManipNode manip when manip.ResizeWidth > 0 => (manip.ResizeWidth, manip.ResizeHeight),
        _ => (0, 0),
    };
}
