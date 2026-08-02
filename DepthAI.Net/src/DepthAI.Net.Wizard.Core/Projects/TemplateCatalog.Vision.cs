namespace DepthAI.Wizard.Projects;

public static partial class TemplateCatalog
{
    /// <summary>Kelas COCO yang lazim dipakai; disematkan agar template langsung jalan.</summary>
    private const string CocoLabels =
        """["person", "bicycle", "car", "motorbike", "aeroplane", "bus", "train", "truck", "boat", "traffic light", "bottle", "chair", "sofa", "laptop", "cup", "keyboard", "cell phone", "book"]""";

    /// <summary>Blok yang menjelaskan cara mengganti model placeholder dengan model asli.</summary>
    private const string ModelNote = """
        // Model placeholder: metadata saja, tanpa bobot. Cukup untuk mengembangkan
        // aplikasi melawan backend simulasi. Ganti dengan berkas .blob sungguhan
        // sebelum menjalankan pada hardware:
        //   var model = await NeuralModel.LoadFromFileAsync("yolov8n.blob");
        """;

    // ------------------------------------------------------------- Detection

    private static ProjectTemplate ObjectDetectionConsole() => new()
    {
        Id = "object-detection-console",
        Title = "Deteksi Objek (Konsol)",
        TitleEnglish = "Object Detection (Console)",
        Description = "Menjalankan detection network dan mencetak objek yang terlihat beserta keyakinannya.",
        DescriptionEnglish = "Runs a detection network and prints visible objects with confidence.",
        Kind = ProjectKind.Console,
        Category = TemplateCategory.Detection,
        Icon = "🎯",
        Requires = ["Kamera RGB"],
        Files =
        [
            new("{{ProjectName}}.csproj", TemplateFragments.ConsoleCsproj()),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(
                "Mencetak daftar objek yang terdeteksi tiap frame, lengkap dengan skor keyakinan.",
                "Prints the list of detected objects each frame, with confidence scores.")),
            new("Program.cs", """
                using DepthAI;
                using DepthAI.Inference;
                using DepthAI.Pipelines;
                using DepthAI.Streaming;

                __MODEL_NOTE__
                var model = NeuralModel.CreatePlaceholder(
                    ModelFamily.MobileNetSsd,
                    labels: __LABELS__,
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
                """
                .Replace("__MODEL_NOTE__", ModelNote, StringComparison.Ordinal)
                .Replace("__LABELS__", CocoLabels, StringComparison.Ordinal)),
        ],
        NextSteps = ["Jalankan `dotnet run`", "Ganti model placeholder dengan .blob asli"],
    };

    private static ProjectTemplate ObjectDetectionDesktop() => new()
    {
        Id = "object-detection-desktop",
        Title = "Dashboard Deteksi Objek",
        TitleEnglish = "Object Detection Dashboard",
        Description = "Video live dengan kotak deteksi tergambar di atasnya dan daftar objek di samping.",
        DescriptionEnglish = "Live video with detection boxes drawn on top and a side list of objects.",
        Kind = ProjectKind.Desktop,
        Category = TemplateCategory.Detection,
        Icon = "📊",
        Requires = ["Kamera RGB"],
        Files = DesktopShell(
            title: "Dashboard Deteksi Objek",
            body: """
                <Grid Grid.Row="1" ColumnDefinitions="*,260">
                  <Border Background="#141C21" CornerRadius="8" ClipToBounds="True">
                    <Image x:Name="Preview" Stretch="Uniform" />
                  </Border>
                  <Border Grid.Column="1" Margin="12,0,0,0" Padding="12"
                          Background="#141C21" CornerRadius="8">
                    <DockPanel>
                      <TextBlock DockPanel.Dock="Top" Text="OBJEK TERDETEKSI"
                                 Foreground="#8CA3AD" FontSize="11" FontWeight="SemiBold"
                                 Margin="0,0,0,10" />
                      <ItemsControl x:Name="DetectionList">
                        <ItemsControl.ItemTemplate>
                          <DataTemplate>
                            <Border Background="#1B252B" CornerRadius="4" Padding="8,6" Margin="0,0,0,6">
                              <TextBlock Text="{Binding}" Foreground="#E8F1F2" FontSize="12" />
                            </Border>
                          </DataTemplate>
                        </ItemsControl.ItemTemplate>
                      </ItemsControl>
                    </DockPanel>
                  </Border>
                </Grid>
                """,
            codeBehind: DesktopCodeBehind(
                pipeline: """
                    Pipeline.CreateBuilder()
                                .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
                                .AddObjectDetection(_model, "rgb.preview", "detector")
                                .StreamOut("rgb.preview", "video")
                                .StreamOut("detector.detections", "detections")
                                .Build(_device.Capabilities)
                    """,
                subscribe: """
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
                    """,
                extraMembers: """
                    private IReadOnlyList<Detection> _latestDetections = [];

                    private NeuralModel _model = NeuralModel.CreatePlaceholder(
                        ModelFamily.MobileNetSsd,
                        labels: __LABELS__,
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
                    """
                    .Replace("__LABELS__", CocoLabels, StringComparison.Ordinal)),
            readmeId: "Menampilkan video kamera dengan kotak deteksi dan daftar objek yang sedang terlihat.",
            readmeEn: "Shows the camera feed with detection boxes and a list of currently visible objects."),
        NextSteps = ["Jalankan `dotnet run`", "Ganti model placeholder dengan .blob asli"],
    };

    private static ProjectTemplate PrivacyBlur() => new()
    {
        Id = "privacy-blur",
        Title = "Sensor Privasi",
        TitleEnglish = "Privacy Blur",
        Description = "Mengaburkan setiap orang yang terdeteksi sebelum frame ditampilkan atau disimpan.",
        DescriptionEnglish = "Blurs every detected person before the frame is shown or saved.",
        Kind = ProjectKind.Desktop,
        Category = TemplateCategory.Safety,
        Icon = "🫥",
        Requires = ["Kamera RGB"],
        Files = DesktopShell(
            title: "Sensor Privasi",
            body: """
                <Border Grid.Row="1" Background="#141C21" CornerRadius="8" ClipToBounds="True">
                  <Image x:Name="Preview" Stretch="Uniform" />
                </Border>
                """,
            codeBehind: DesktopCodeBehind(
                pipeline: """
                    Pipeline.CreateBuilder()
                                .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
                                .AddObjectDetection(_model, "rgb.preview", "detector")
                                .StreamOut("rgb.preview", "video")
                                .StreamOut("detector.detections", "detections")
                                .Build(_device.Capabilities)
                    """,
                subscribe: """
                    _subscriptions.Add(_device.GetStream<DetectionFrame>("detections")
                        .Subscribe(frame => Volatile.Write(ref _people,
                            [.. frame.Detections.Where(d => d.Label == "person")])));

                    _subscriptions.Add(_device.GetStream<ImageFrame>("video")
                        .Subscribe(frame => ShowBlurred(frame)));
                    """,
                extraMembers: """
                    private IReadOnlyList<Detection> _people = [];

                    private NeuralModel _model = NeuralModel.CreatePlaceholder(
                        ModelFamily.MobileNetSsd,
                        labels: ["person", "bottle", "chair"],
                        inputWidth: 300,
                        inputHeight: 300);

                    private void ShowBlurred(ImageFrame frame)
                    {
                        var pixels = PixelConverter.ToBgr888(frame);

                        foreach (var person in Volatile.Read(ref _people))
                        {
                            var (x, y, w, h) = person.Box.ToPixels(frame.Width, frame.Height);
                            Pixelate(pixels, frame.Width, frame.Height, x, y, w, h, blockSize: 16);
                        }

                        using var masked = ImageFrame.Wrap(
                            pixels, frame.Width, frame.Height, PixelFormat.Bgr888);

                        ShowFrame(Preview, masked);
                    }

                    /// <summary>
                    /// Mosaik alih-alih blur gaussian: satu pass, tidak butuh buffer kedua,
                    /// dan tetap tidak bisa dibalik — yang justru penting untuk privasi.
                    /// </summary>
                    private static void Pixelate(
                        Span<byte> bgr, int width, int height, int x, int y, int w, int h, int blockSize)
                    {
                        for (var by = Math.Max(0, y); by < Math.Min(height, y + h); by += blockSize)
                        {
                            for (var bx = Math.Max(0, x); bx < Math.Min(width, x + w); bx += blockSize)
                            {
                                int sumB = 0, sumG = 0, sumR = 0, count = 0;

                                for (var py = by; py < Math.Min(height, by + blockSize); py++)
                                {
                                    for (var px = bx; px < Math.Min(width, bx + blockSize); px++)
                                    {
                                        var offset = ((py * width) + px) * 3;
                                        sumB += bgr[offset];
                                        sumG += bgr[offset + 1];
                                        sumR += bgr[offset + 2];
                                        count++;
                                    }
                                }

                                if (count == 0)
                                {
                                    continue;
                                }

                                byte avgB = (byte)(sumB / count), avgG = (byte)(sumG / count), avgR = (byte)(sumR / count);

                                for (var py = by; py < Math.Min(height, by + blockSize); py++)
                                {
                                    for (var px = bx; px < Math.Min(width, bx + blockSize); px++)
                                    {
                                        var offset = ((py * width) + px) * 3;
                                        bgr[offset] = avgB;
                                        bgr[offset + 1] = avgG;
                                        bgr[offset + 2] = avgR;
                                    }
                                }
                            }
                        }
                    }
                    """),
            readmeId: "Setiap orang yang terdeteksi dimosaik sebelum frame keluar dari aplikasi.",
            readmeEn: "Every detected person is pixelated before the frame leaves the app."),
        NextSteps = ["Jalankan `dotnet run`", "Tambahkan perekaman frame yang sudah disensor"],
    };

    // ----------------------------------------------------------------- Depth

    private static ProjectTemplate DepthViewerDesktop() => new()
    {
        Id = "depth-viewer",
        Title = "Penampil Kedalaman",
        TitleEnglish = "Depth Viewer",
        Description = "Menampilkan warna dan peta kedalaman berdampingan, dengan pembacaan jarak di titik kursor.",
        DescriptionEnglish = "Shows color and depth side by side, with a distance readout under the cursor.",
        Kind = ProjectKind.Desktop,
        Category = TemplateCategory.Depth,
        Icon = "🌊",
        Requires = ["Stereo depth"],
        Files = DesktopShell(
            title: "Penampil Kedalaman",
            body: """
                <Grid Grid.Row="1" ColumnDefinitions="*,*">
                  <Border Background="#141C21" CornerRadius="8" ClipToBounds="True" Margin="0,0,6,0">
                    <Image x:Name="Preview" Stretch="Uniform" />
                  </Border>
                  <Border Grid.Column="1" Background="#141C21" CornerRadius="8" ClipToBounds="True" Margin="6,0,0,0">
                    <Image x:Name="DepthView" Stretch="Uniform"
                           PointerMoved="OnDepthPointerMoved" />
                  </Border>
                </Grid>
                """,
            codeBehind: DesktopCodeBehind(
                pipeline: """
                    PipelinePresets.StereoDepth(fps: 30)
                    """,
                subscribe: """
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
                    """,
                extraMembers: """
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
                    """),
            readmeId: "Warna dan kedalaman berdampingan. Arahkan kursor ke peta kedalaman untuk membaca jarak.",
            readmeEn: "Color and depth side by side. Hover the depth map to read the distance."),
        NextSteps = ["Jalankan `dotnet run`", "Coba preset DepthPreset.HighAccuracy"],
    };

    private static ProjectTemplate RgbdRecorder() => new()
    {
        Id = "rgbd-recorder",
        Title = "Perekam RGB-D",
        TitleEnglish = "RGB-D Recorder",
        Description = "Merekam pasangan frame warna dan kedalaman ke disk untuk membangun dataset.",
        DescriptionEnglish = "Records paired color and depth frames to disk to build a dataset.",
        Kind = ProjectKind.Console,
        Category = TemplateCategory.Depth,
        Icon = "⏺️",
        Requires = ["Stereo depth"],
        Files =
        [
            new("{{ProjectName}}.csproj", TemplateFragments.ConsoleCsproj(
                """<PackageReference Include="DepthAI.Net.Imaging.ImageSharp" Version="0.1.0" />""")),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(
                "Merekam frame RGB (PNG) dan kedalaman (PNG 16-bit, milimeter asli) berpasangan.",
                "Records RGB frames (PNG) and depth frames (16-bit PNG, true millimetres) as pairs.")),
            new("Program.cs", """
                using DepthAI;
                using DepthAI.Imaging;
                using DepthAI.Pipelines;
                using DepthAI.Streaming;

                var outputDirectory = args.Length > 0 ? args[0] : "./capture";
                var frameLimit = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 100;

                Directory.CreateDirectory(outputDirectory);
                Console.WriteLine($"Merekam {frameLimit} pasang frame ke {Path.GetFullPath(outputDirectory)}");

                await using var device = await DepthAiDevice.OpenAsync();
                var pipeline = PipelinePresets.StereoDepth(fps: 30);
                pipeline.Validate(device.Capabilities).ThrowIfInvalid();

                await device.StartAsync(pipeline);

                var pending = new List<Task>();
                var colorCount = 0;
                var depthCount = 0;

                using var colorSubscription = device.GetStream<ImageFrame>("video").Subscribe(frame =>
                {
                    if (colorCount >= frameLimit)
                    {
                        return;
                    }

                    var index = Interlocked.Increment(ref colorCount);
                    var copy = frame.Clone();
                    lock (pending)
                    {
                        pending.Add(SaveColorAsync(copy, outputDirectory, index));
                    }
                });

                using var depthSubscription = device.GetStream<DepthFrame>("depth").Subscribe(frame =>
                {
                    if (depthCount >= frameLimit)
                    {
                        return;
                    }

                    var index = Interlocked.Increment(ref depthCount);
                    var copy = frame.Clone();
                    lock (pending)
                    {
                        pending.Add(SaveDepthAsync(copy, outputDirectory, index));
                    }
                });

                while (colorCount < frameLimit || depthCount < frameLimit)
                {
                    await Task.Delay(100);
                    Console.Write($"\rwarna {colorCount}/{frameLimit}  kedalaman {depthCount}/{frameLimit}");
                }

                Task[] snapshot;
                lock (pending)
                {
                    snapshot = [.. pending];
                }

                await Task.WhenAll(snapshot);
                await device.StopAsync();

                Console.WriteLine();
                Console.WriteLine("Selesai.");

                static async Task SaveColorAsync(ImageFrame frame, string directory, int index)
                {
                    try
                    {
                        await frame.SaveAsync(Path.Combine(directory, $"color_{index:D5}.png"));
                    }
                    finally
                    {
                        frame.Dispose();
                    }
                }

                static async Task SaveDepthAsync(DepthFrame frame, string directory, int index)
                {
                    try
                    {
                        // PNG 16-bit menyimpan milimeter apa adanya, sehingga rekaman
                        // tetap bisa dipakai untuk pengukuran, bukan sekadar dilihat.
                        await frame.SaveRawDepthAsync(Path.Combine(directory, $"depth_{index:D5}.png"));
                    }
                    finally
                    {
                        frame.Dispose();
                    }
                }
                """),
        ],
        NextSteps = ["Jalankan `dotnet run -- ./capture 200`"],
    };

    // -------------------------------------------------------------- Analytics

    private static ProjectTemplate PeopleCounter() => new()
    {
        Id = "people-counter",
        Title = "Penghitung Orang",
        TitleEnglish = "People Counter",
        Description = "Menghitung orang yang melintasi garis virtual dan melaporkan arah lintasannya.",
        DescriptionEnglish = "Counts people crossing a virtual line and reports the crossing direction.",
        Kind = ProjectKind.Console,
        Category = TemplateCategory.Analytics,
        Icon = "🚶",
        Requires = ["Kamera RGB"],
        Files =
        [
            new("{{ProjectName}}.csproj", TemplateFragments.ConsoleCsproj()),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(
                "Menghitung lintasan orang pada garis vertikal di tengah frame, terpisah masuk dan keluar.",
                "Counts people crossing a vertical line at the frame centre, split into in and out.")),
            new("Program.cs", """
                using DepthAI;
                using DepthAI.Inference;
                using DepthAI.Pipelines;
                using DepthAI.Streaming;

                __MODEL_NOTE__
                var model = NeuralModel.CreatePlaceholder(
                    ModelFamily.MobileNetSsd,
                    labels: ["person", "bottle", "chair"],
                    inputWidth: 300,
                    inputHeight: 300,
                    confidenceThreshold: 0.55f);

                // Garis hitung di tengah frame, dalam koordinat ternormalisasi.
                const float LineX = 0.5f;

                var tracker = new CrossingTracker(LineX);

                await using var device = await DepthAiDevice.OpenAsync();

                var pipeline = Pipeline.CreateBuilder()
                    .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
                    .AddObjectDetection(model, "rgb.preview", "detector")
                    .StreamOut("detector.detections", "detections")
                    .Build(device.Capabilities);

                await device.StartAsync(pipeline);

                using var subscription = device.GetStream<DetectionFrame>("detections").Subscribe(frame =>
                {
                    var people = frame.Detections.Where(d => d.Label == "person").ToList();
                    if (tracker.Update(people))
                    {
                        Console.WriteLine($"masuk: {tracker.In,4}   keluar: {tracker.Out,4}   di dalam: {tracker.Occupancy,4}");
                    }
                });

                Console.WriteLine("Menghitung lintasan. Tekan Enter untuk berhenti.");
                Console.ReadLine();

                await device.StopAsync();
                Console.WriteLine($"Total — masuk {tracker.In}, keluar {tracker.Out}.");

                /// <summary>
                /// Pelacak lintasan sederhana berbasis kedekatan posisi antar frame.
                /// </summary>
                /// <remarks>
                /// Deteksi tidak membawa identitas, jadi objek dicocokkan antar frame lewat
                /// jarak titik pusat. Cukup untuk adegan lalu lintas ringan; untuk kerumunan
                /// padat pakailah pelacak ber-ID seperti SORT atau ByteTrack.
                /// </remarks>
                internal sealed class CrossingTracker(float lineX)
                {
                    private const float MatchRadius = 0.12f;

                    private readonly List<(float X, float Y, int Age)> _tracks = [];

                    public int In { get; private set; }

                    public int Out { get; private set; }

                    public int Occupancy => Math.Max(0, In - Out);

                    /// <summary>Memperbarui pelacak; true bila ada lintasan baru terhitung.</summary>
                    public bool Update(IReadOnlyList<Detection> people)
                    {
                        var counted = false;
                        var unmatched = new List<(float X, float Y, int Age)>(_tracks);
                        var next = new List<(float X, float Y, int Age)>();

                        foreach (var person in people)
                        {
                            var x = person.Box.CenterX;
                            var y = person.Box.CenterY;

                            var bestIndex = -1;
                            var bestDistance = MatchRadius;

                            for (var i = 0; i < unmatched.Count; i++)
                            {
                                var distance = MathF.Sqrt(
                                    MathF.Pow(unmatched[i].X - x, 2) + MathF.Pow(unmatched[i].Y - y, 2));

                                if (distance < bestDistance)
                                {
                                    bestDistance = distance;
                                    bestIndex = i;
                                }
                            }

                            if (bestIndex >= 0)
                            {
                                var previous = unmatched[bestIndex];
                                unmatched.RemoveAt(bestIndex);

                                // Lintasan dihitung saat titik pusat berpindah sisi garis.
                                if (previous.X < lineX && x >= lineX)
                                {
                                    In++;
                                    counted = true;
                                }
                                else if (previous.X >= lineX && x < lineX)
                                {
                                    Out++;
                                    counted = true;
                                }
                            }

                            next.Add((x, y, 0));
                        }

                        _tracks.Clear();
                        _tracks.AddRange(next);

                        // Jejak yang tidak tercocokkan dipertahankan sebentar supaya deteksi
                        // yang berkedip satu-dua frame tidak dihitung sebagai orang baru.
                        foreach (var stale in unmatched.Where(t => t.Age < 3))
                        {
                            _tracks.Add((stale.X, stale.Y, stale.Age + 1));
                        }

                        return counted;
                    }
                }
                """
                .Replace("__MODEL_NOTE__", ModelNote, StringComparison.Ordinal)),
        ],
        NextSteps = ["Jalankan `dotnet run`", "Geser posisi garis lewat konstanta LineX"],
    };

    private static ProjectTemplate ShelfMonitor() => new()
    {
        Id = "shelf-monitor",
        Title = "Monitor Stok Rak",
        TitleEnglish = "Shelf Stock Monitor",
        Description = "Memantau jumlah produk pada rak dan memberi peringatan saat stok menipis.",
        DescriptionEnglish = "Watches product counts on a shelf and warns when stock runs low.",
        Kind = ProjectKind.Console,
        Category = TemplateCategory.Industri,
        Icon = "🏪",
        Requires = ["Kamera RGB"],
        Files =
        [
            new("{{ProjectName}}.csproj", TemplateFragments.ConsoleCsproj()),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(
                "Menghitung produk pada rak dan memberi peringatan bila jumlahnya turun di bawah ambang.",
                "Counts products on a shelf and warns when the count drops below a threshold.")),
            new("Program.cs", """
                using DepthAI;
                using DepthAI.Inference;
                using DepthAI.Pipelines;
                using DepthAI.Streaming;

                __MODEL_NOTE__
                var model = NeuralModel.CreatePlaceholder(
                    ModelFamily.MobileNetSsd,
                    labels: ["person", "bottle", "chair"],
                    inputWidth: 300,
                    inputHeight: 300);

                const string WatchedProduct = "bottle";
                const int LowStockThreshold = 3;

                await using var device = await DepthAiDevice.OpenAsync();

                var pipeline = Pipeline.CreateBuilder()
                    .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
                    .AddObjectDetection(model, "rgb.preview", "detector")
                    .StreamOut("detector.detections", "detections")
                    .Build(device.Capabilities);

                await device.StartAsync(pipeline);

                // Jumlah dihaluskan lewat median jendela geser: satu frame yang meleset
                // tidak boleh memicu peringatan stok.
                var window = new Queue<int>();
                var lastReported = -1;

                using var subscription = device.GetStream<DetectionFrame>("detections").Subscribe(frame =>
                {
                    var count = frame.Detections.Count(d => d.Label == WatchedProduct);

                    window.Enqueue(count);
                    if (window.Count > 15)
                    {
                        window.Dequeue();
                    }

                    var sorted = window.OrderBy(v => v).ToList();
                    var median = sorted[sorted.Count / 2];

                    if (median == lastReported)
                    {
                        return;
                    }

                    lastReported = median;
                    var state = median <= LowStockThreshold ? "STOK MENIPIS" : "aman";
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {WatchedProduct}: {median,2}  [{state}]");
                });

                Console.WriteLine("Memantau rak. Tekan Enter untuk berhenti.");
                Console.ReadLine();

                await device.StopAsync();
                """
                .Replace("__MODEL_NOTE__", ModelNote, StringComparison.Ordinal)),
        ],
        NextSteps = ["Jalankan `dotnet run`", "Sesuaikan WatchedProduct dan ambang stok"],
    };

    // ----------------------------------------------------------------- Safety

    private static ProjectTemplate SafetyZoneMonitor() => new()
    {
        Id = "safety-zone",
        Title = "Monitor Zona Aman",
        TitleEnglish = "Safety Zone Monitor",
        Description = "Memakai kedalaman untuk memberi alarm saat orang masuk terlalu dekat ke mesin.",
        DescriptionEnglish = "Uses depth to raise an alarm when a person comes too close to machinery.",
        Kind = ProjectKind.Console,
        Category = TemplateCategory.Safety,
        Icon = "⚠️",
        Requires = ["Stereo depth", "Kamera RGB"],
        Files =
        [
            new("{{ProjectName}}.csproj", TemplateFragments.ConsoleCsproj()),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(
                "Deteksi spasial memberi jarak tiap orang; alarm menyala saat ada yang melewati batas aman.",
                "Spatial detection gives each person a distance; the alarm fires when anyone crosses the safe limit.")),
            new("Program.cs", """
                using DepthAI;
                using DepthAI.Inference;
                using DepthAI.Pipelines;
                using DepthAI.Streaming;

                __MODEL_NOTE__
                var model = NeuralModel.CreatePlaceholder(
                    ModelFamily.MobileNetSsd,
                    labels: ["person", "bottle", "chair"],
                    inputWidth: 300,
                    inputHeight: 300);

                // Jarak minimum yang dianggap aman dari mesin, meter.
                const float SafeDistanceMeters = 1.5f;

                await using var device = await DepthAiDevice.OpenAsync();

                var pipeline = Pipeline.CreateBuilder()
                    .AddColorCamera("rgb", camera => camera.WithPreview(640, 400))
                    .AddSpatialObjectDetection(model, "rgb.preview", "detector")
                    .StreamOut("detector.detections", "detections")
                    .StreamOut("detector_stereo.depth", "depth")
                    .Build(device.Capabilities);

                await device.StartAsync(pipeline);

                var alarmActive = false;

                using var subscription = device.GetStream<DetectionFrame>("detections").Subscribe(frame =>
                {
                    var people = frame.Detections.Where(d => d.Label == "person").ToList();

                    var nearest = people
                        .Select(d => d.Spatial?.Z)
                        .Where(z => z is not null)
                        .DefaultIfEmpty(null)
                        .Min();

                    var breach = nearest is not null && nearest < SafeDistanceMeters;

                    // Hanya lapor saat status berubah, supaya konsol tidak dibanjiri.
                    if (breach == alarmActive)
                    {
                        return;
                    }

                    alarmActive = breach;
                    Console.WriteLine(breach
                        ? $"{DateTime.Now:HH:mm:ss}  ⚠  PELANGGARAN ZONA — orang pada {nearest:F2} m"
                        : $"{DateTime.Now:HH:mm:ss}  ✓  zona aman kembali");
                });

                Console.WriteLine($"Memantau zona aman ({SafeDistanceMeters:F1} m). Tekan Enter untuk berhenti.");
                Console.ReadLine();

                await device.StopAsync();
                """
                .Replace("__MODEL_NOTE__", ModelNote, StringComparison.Ordinal)),
        ],
        NextSteps = ["Jalankan `dotnet run`", "Sambungkan alarm ke relai lewat GPIO atau MQTT"],
    };

    private static ProjectTemplate SocialDistanceMonitor() => new()
    {
        Id = "social-distance",
        Title = "Pemantau Jarak Antar Orang",
        TitleEnglish = "Distance Between People",
        Description = "Mengukur jarak 3D antar orang dan menandai pasangan yang terlalu berdekatan.",
        DescriptionEnglish = "Measures 3D distance between people and flags pairs that stand too close.",
        Kind = ProjectKind.Console,
        Category = TemplateCategory.Safety,
        Icon = "📏",
        Requires = ["Stereo depth", "Kamera RGB"],
        Files =
        [
            new("{{ProjectName}}.csproj", TemplateFragments.ConsoleCsproj()),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(
                "Jarak dihitung di ruang 3D, bukan piksel, sehingga tidak terpengaruh perspektif kamera.",
                "Distance is computed in 3D space rather than pixels, so camera perspective does not skew it.")),
            new("Program.cs", """
                using DepthAI;
                using DepthAI.Inference;
                using DepthAI.Pipelines;
                using DepthAI.Streaming;

                __MODEL_NOTE__
                var model = NeuralModel.CreatePlaceholder(
                    ModelFamily.MobileNetSsd,
                    labels: ["person", "bottle", "chair"],
                    inputWidth: 300,
                    inputHeight: 300);

                const float MinimumSeparationMeters = 1.0f;

                await using var device = await DepthAiDevice.OpenAsync();

                var pipeline = Pipeline.CreateBuilder()
                    .AddColorCamera("rgb", camera => camera.WithPreview(640, 400))
                    .AddSpatialObjectDetection(model, "rgb.preview", "detector")
                    .StreamOut("detector.detections", "detections")
                    .Build(device.Capabilities);

                await device.StartAsync(pipeline);

                using var subscription = device.GetStream<DetectionFrame>("detections").Subscribe(frame =>
                {
                    var people = frame.Detections
                        .Where(d => d.Label == "person" && d.Spatial is not null)
                        .Select(d => d.Spatial!.Value)
                        .ToList();

                    for (var i = 0; i < people.Count; i++)
                    {
                        for (var j = i + 1; j < people.Count; j++)
                        {
                            var a = people[i];
                            var b = people[j];

                            var separation = MathF.Sqrt(
                                MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));

                            if (separation < MinimumSeparationMeters)
                            {
                                Console.WriteLine(
                                    $"{DateTime.Now:HH:mm:ss}  dua orang berjarak {separation:F2} m "
                                    + $"(minimum {MinimumSeparationMeters:F1} m)");
                            }
                        }
                    }
                });

                Console.WriteLine("Memantau jarak antar orang. Tekan Enter untuk berhenti.");
                Console.ReadLine();

                await device.StopAsync();
                """
                .Replace("__MODEL_NOTE__", ModelNote, StringComparison.Ordinal)),
        ],
        NextSteps = ["Jalankan `dotnet run`"],
    };

    private static ProjectTemplate PpeCompliance() => new()
    {
        Id = "ppe-compliance",
        Title = "Kepatuhan APD",
        TitleEnglish = "PPE Compliance",
        Description = "Memeriksa apakah setiap orang memakai helm dan rompi di area kerja.",
        DescriptionEnglish = "Checks whether every person in the work area wears a helmet and vest.",
        Kind = ProjectKind.Console,
        Category = TemplateCategory.Industri,
        Icon = "🦺",
        Requires = ["Kamera RGB", "Model APD"],
        Files =
        [
            new("{{ProjectName}}.csproj", TemplateFragments.ConsoleCsproj()),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(
                "Mencocokkan deteksi helm dan rompi ke tiap orang lewat tumpang tindih kotak.",
                "Associates helmet and vest detections with each person via box overlap.")),
            new("Program.cs", """
                using DepthAI;
                using DepthAI.Inference;
                using DepthAI.Pipelines;
                using DepthAI.Streaming;

                __MODEL_NOTE__
                // Model APD nyata biasanya dilatih dengan kelas person/helmet/vest.
                var model = NeuralModel.CreatePlaceholder(
                    ModelFamily.Yolo,
                    labels: ["person", "helmet", "vest"],
                    inputWidth: 640,
                    inputHeight: 640,
                    confidenceThreshold: 0.45f);

                await using var device = await DepthAiDevice.OpenAsync();

                var pipeline = Pipeline.CreateBuilder()
                    .AddColorCamera("rgb", camera => camera.WithPreview(640, 640))
                    .AddObjectDetection(model, "rgb.preview", "detector")
                    .StreamOut("detector.detections", "detections")
                    .Build(device.Capabilities);

                await device.StartAsync(pipeline);

                using var subscription = device.GetStream<DetectionFrame>("detections").Subscribe(frame =>
                {
                    var people = frame.Detections.Where(d => d.Label == "person").ToList();
                    var helmets = frame.Detections.Where(d => d.Label == "helmet").ToList();
                    var vests = frame.Detections.Where(d => d.Label == "vest").ToList();

                    foreach (var person in people)
                    {
                        var hasHelmet = helmets.Any(h => Overlaps(person, h));
                        var hasVest = vests.Any(v => Overlaps(person, v));

                        if (hasHelmet && hasVest)
                        {
                            continue;
                        }

                        var missing = new List<string>();
                        if (!hasHelmet) missing.Add("helm");
                        if (!hasVest) missing.Add("rompi");

                        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  APD tidak lengkap: {string.Join(" dan ", missing)}");
                    }
                });

                Console.WriteLine("Memeriksa kepatuhan APD. Tekan Enter untuk berhenti.");
                Console.ReadLine();

                await device.StopAsync();

                /// <summary>
                /// True bila perlengkapan berada di dalam kotak orang. IoU tidak dipakai:
                /// helm jauh lebih kecil dari orang, jadi IoU-nya selalu rendah walau
                /// posisinya benar. Yang relevan adalah seberapa besar bagian perlengkapan
                /// yang tertutup kotak orang.
                /// </summary>
                static bool Overlaps(Detection person, Detection gear)
                {
                    var box = person.Box;
                    var g = gear.Box;

                    var interXMin = Math.Max(box.XMin, g.XMin);
                    var interYMin = Math.Max(box.YMin, g.YMin);
                    var interXMax = Math.Min(box.XMax, g.XMax);
                    var interYMax = Math.Min(box.YMax, g.YMax);

                    var interArea = Math.Max(0, interXMax - interXMin) * Math.Max(0, interYMax - interYMin);
                    return g.Area > 0 && interArea / g.Area > 0.5f;
                }
                """
                .Replace("__MODEL_NOTE__", ModelNote, StringComparison.Ordinal)),
        ],
        NextSteps = ["Latih atau unduh model APD", "Ganti model placeholder dengan .blob asli"],
    };

    private static ProjectTemplate QualityInspection() => new()
    {
        Id = "quality-inspection",
        Title = "Inspeksi Kualitas",
        TitleEnglish = "Quality Inspection",
        Description = "Mengklasifikasi produk lolos atau cacat dan mencatat hasilnya ke CSV.",
        DescriptionEnglish = "Classifies products as pass or defect and logs results to CSV.",
        Kind = ProjectKind.Console,
        Category = TemplateCategory.Industri,
        Icon = "🔬",
        Requires = ["Kamera RGB", "Model klasifikasi"],
        Files =
        [
            new("{{ProjectName}}.csproj", TemplateFragments.ConsoleCsproj()),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(
                "Setiap hasil klasifikasi dicatat ke CSV agar bisa diaudit belakangan.",
                "Every classification result is logged to CSV so it can be audited later.")),
            new("Program.cs", """
                using System.Globalization;
                using DepthAI;
                using DepthAI.Inference;
                using DepthAI.Pipelines;
                using DepthAI.Streaming;

                __MODEL_NOTE__
                var model = NeuralModel.CreatePlaceholder(
                    ModelFamily.Classification,
                    labels: ["lolos", "gores", "penyok", "warna menyimpang"],
                    inputWidth: 224,
                    inputHeight: 224);

                // Di bawah ambang ini, hasil dianggap tidak meyakinkan dan diteruskan
                // ke pemeriksaan manual alih-alih dinyatakan lolos.
                const float ReviewThreshold = 0.7f;

                var logPath = Path.Combine(AppContext.BaseDirectory, "inspeksi.csv");
                await using var log = new StreamWriter(logPath, append: true);

                if (new FileInfo(logPath).Length == 0)
                {
                    await log.WriteLineAsync("waktu,frame,hasil,keyakinan,tindakan");
                }

                await using var device = await DepthAiDevice.OpenAsync();

                var pipeline = Pipeline.CreateBuilder()
                    .AddColorCamera("rgb", camera => camera.WithPreview(224, 224))
                    .Configure(p =>
                    {
                        var nn = p.AddNeuralNetwork("classifier", n => n.Model = model);
                        p.ResolveOutput("rgb.preview").LinkTo(nn.Input);
                        p.AddOutputStream(nn.Out, "classification");
                    })
                    .Build(device.Capabilities);

                await device.StartAsync(pipeline);

                var inspected = 0;
                var rejected = 0;

                using var subscription = device.GetStream<ClassificationFrame>("classification").Subscribe(frame =>
                {
                    if (frame.Top is not { } top)
                    {
                        return;
                    }

                    inspected++;

                    var action = top.Confidence < ReviewThreshold
                        ? "periksa manual"
                        : top.Label == "lolos" ? "lolos" : "tolak";

                    if (action == "tolak")
                    {
                        rejected++;
                    }

                    var line = string.Create(CultureInfo.InvariantCulture,
                        $"{DateTime.Now:o},{frame.SequenceNumber},{top.Label},{top.Confidence:F4},{action}");

                    log.WriteLine(line);
                    Console.WriteLine($"#{frame.SequenceNumber,6}  {top.Label,-18} {top.Confidence:P0}  → {action}");
                });

                Console.WriteLine($"Mencatat ke {logPath}. Tekan Enter untuk berhenti.");
                Console.ReadLine();

                await device.StopAsync();
                await log.FlushAsync();

                Console.WriteLine($"Diperiksa {inspected}, ditolak {rejected}.");
                """
                .Replace("__MODEL_NOTE__", ModelNote, StringComparison.Ordinal)),
        ],
        NextSteps = ["Jalankan `dotnet run`", "Ganti label dengan kelas cacat produk Anda"],
    };
}
