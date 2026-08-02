namespace DepthAI.Wizard.Ai.Plugins;

/// <summary>
/// Cuplikan API DepthAI.Net yang diberikan kepada asisten saat diminta.
/// </summary>
/// <remarks>
/// Ditulis manual, bukan direfleksi dari assembly: yang dibutuhkan model bukan daftar
/// lengkap anggota, melainkan pola pemakaian yang benar beserta jebakannya (siklus hidup
/// frame, ketidakcocokan ukuran, piksel depth kosong).
/// </remarks>
internal static class ApiReference
{
    public const string Device = """
        ## Perangkat

        ```csharp
        // Membuka perangkat pertama yang tersedia. Otomatis memakai simulasi bila
        // runtime native tidak terpasang.
        await using var device = await DepthAiDevice.OpenAsync();

        // Memaksa simulasi, walau ada hardware:
        await using var sim = await DepthAiDevice.OpenAsync(DepthAiOptions.Simulated);

        // Membuka perangkat tertentu:
        await using var byId = await DepthAiDevice.OpenBySerialAsync("14442C10D1");

        device.Info;          // DeviceInfo: Name, SerialNumber, Protocol, FirmwareVersion
        device.Capabilities;  // DeviceCapabilities: ColorCameraCount, SupportsStereoDepth, HasImu, ShaveCores
        device.IsSimulated;   // bool
        device.ReadTelemetry();

        // Enumerasi tanpa membuka:
        IReadOnlyList<DeviceInfo> devices = DepthAi.ListDevices();

        // Hotplug:
        await using var watcher = new DeviceWatcher();
        watcher.DeviceConnected += (_, e) => Console.WriteLine(e.Device);
        watcher.Start();
        ```
        """;

    public const string Pipeline = """
        ## Pipeline

        ```csharp
        // Gaya fluent untuk susunan yang lazim:
        var pipeline = Pipeline.CreateBuilder()
            .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
            .AddStereoDepth("stereo")                       // sekaligus menambah kamera mono kiri/kanan
            .AddObjectDetection(model, "rgb.preview", "detector")
            .StreamOut("rgb.preview", "video")
            .StreamOut("stereo.depth", "depth")
            .StreamOut("detector.detections", "detections")
            .Build(device.Capabilities);

        // Gaya graf untuk susunan tidak biasa:
        var p = Pipeline.Create();
        var camera = p.AddColorCamera("rgb", c => { c.Fps = 30; c.WithPreview(640, 480); });
        var encoder = p.AddVideoEncoder("enc", e => e.Profile = VideoProfile.H265Main);
        camera.Video.LinkTo(encoder.Input);
        p.AddOutputStream(encoder.Bitstream, "video");

        // Preset siap pakai:
        var preset = PipelinePresets.StereoDepth(fps: 30);
        // rgb-preview, stereo-depth, object-detection, spatial-detection, record-rgbd, imu-stream

        // JSON:
        string json = pipeline.ToJson();
        var loaded = Pipeline.FromJson(json, new PipelineLoadOptions { ModelResolver = name => model });
        pipeline.Validate(device.Capabilities).ThrowIfInvalid();
        ```

        Node yang tersedia: ColorCameraNode, MonoCameraNode, StereoDepthNode, NeuralNetworkNode,
        DetectionNetworkNode, SpatialDetectionNetworkNode, ImageManipNode, VideoEncoderNode, ImuNode.

        `AddObjectDetection` otomatis menyisipkan ImageManip bila ukuran preview kamera
        berbeda dari ukuran input model.
        """;

    public const string Streaming = """
        ## Streaming

        ```csharp
        await device.StartAsync(pipeline);

        using var subscription = device.GetStream<ImageFrame>("video")
            .Subscribe(frame => { /* ... */ });

        // Operator yang tersedia tanpa System.Reactive:
        device.GetStream<ImageFrame>("video").Throttle(TimeSpan.FromMilliseconds(80));
        device.GetStream<ImageFrame>("video").Where(f => f.Width > 320);
        await device.GetStream<ImageFrame>("video").FirstAsync();

        await foreach (var frame in device.GetStream<DepthFrame>("depth").ToAsyncEnumerable())
        {
            // ...
        }

        await device.StopAsync();
        ```

        PENTING — siklus hidup frame: buffer frame dipinjam dari pool dan dibuang segera
        setelah callback `Subscribe` kembali. Bila frame perlu disimpan (untuk UI, penulisan
        berkas asinkron, atau dipakai frame berikutnya), panggil `frame.Clone()` dan buang
        salinannya sendiri. Memakai frame yang sudah dibuang melempar ObjectDisposedException.
        """;

    public const string Detection = """
        ## Inferensi

        ```csharp
        // Model sungguhan:
        var model = await NeuralModel.LoadFromFileAsync("yolov8n.blob");

        // Model metadata-saja untuk dikembangkan melawan simulasi:
        var placeholder = NeuralModel.CreatePlaceholder(
            ModelFamily.MobileNetSsd,
            labels: ["person", "bottle", "chair"],
            inputWidth: 300, inputHeight: 300,
            confidenceThreshold: 0.5f);

        using var s = device.GetStream<DetectionFrame>("detections").Subscribe(frame =>
        {
            foreach (var d in frame.Detections)
            {
                d.Label;                        // string
                d.Confidence;                   // float 0..1
                d.Box;                          // BoundingBox ternormalisasi 0..1
                d.Box.ToPixels(width, height);  // (x, y, w, h)
                d.Spatial?.Z;                   // float? meter — hanya untuk spatial detection
            }

            frame.Best;   // Detection? dengan keyakinan tertinggi
            frame.Count;
        });
        ```

        Keluarga model: Yolo, MobileNetSsd, Classification, Segmentation, Raw.
        Tipe frame hasil: DetectionFrame, ClassificationFrame, SegmentationFrame, NeuralTensorFrame.
        """;

    public const string Depth = """
        ## Kedalaman

        ```csharp
        using var s = device.GetStream<DepthFrame>("depth").Subscribe(frame =>
        {
            frame.Width; frame.Height;
            ushort mm = frame.GetMillimeters(x, y);        // 0 = tidak ada pengukuran
            float? meters = frame.GetDistanceMeters(x, y); // null = tidak ada pengukuran
            var point = frame.GetPoint3D(x, y);            // (X, Y, Z)? dalam meter
            float[,] matrix = frame.ToMeterMatrix();       // NaN untuk piksel kosong
        });
        ```

        Nilai 0 berarti "tidak ada pengukuran" — oklusi, permukaan tanpa tekstur, atau di
        luar jangkauan. Jangan diperlakukan sebagai jarak nol; `GetDistanceMeters`
        mengembalikan null justru supaya kasus ini tidak lolos diam-diam.

        Deteksi spasial membutuhkan stereo depth yang `AlignTo`-nya diarahkan ke kamera warna.
        """;

    public const string Imaging = """
        ## Imaging

        ```csharp
        // ImageSharp — paket DepthAI.Net.Imaging.ImageSharp
        using var image = frame.ToImage();                    // Image<Rgb24>
        await frame.SaveAsync("frame.png");
        await depthFrame.SaveAsync("depth.png", DepthColorMap.Turbo);
        await depthFrame.SaveRawDepthAsync("depth16.png");    // PNG 16-bit, milimeter asli
        byte[] jpeg = await frame.ToJpegAsync(quality: 85);

        // SkiaSharp — paket DepthAI.Net.Imaging.SkiaSharp
        using var bitmap = frame.ToBitmap();                  // SKBitmap
        canvas.DrawDetections(detections, width, height);

        // Tanpa dependensi imaging (ada di Core):
        byte[] bgr = PixelConverter.ToBgr888(frame);
        FrameOverlay.DrawDetections(bgr, frame.Width, frame.Height, detections);
        var colorized = DepthColorizer.ToImageFrame(depthFrame, DepthColorMap.Turbo);
        ```
        """;
}
