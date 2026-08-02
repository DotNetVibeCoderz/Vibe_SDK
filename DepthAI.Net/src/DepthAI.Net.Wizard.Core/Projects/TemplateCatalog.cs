namespace DepthAI.Wizard.Projects;

/// <summary>
/// Galeri template computer vision yang ditawarkan wizard saat membuat proyek baru.
/// </summary>
public static partial class TemplateCatalog
{
    private static readonly Lazy<IReadOnlyList<ProjectTemplate>> Templates = new(Build);

    /// <summary>Semua template, terurut menurut kategori lalu judul.</summary>
    public static IReadOnlyList<ProjectTemplate> All => Templates.Value;

    /// <summary>Mencari template menurut id; melempar bila tidak ada.</summary>
    public static ProjectTemplate Get(string id)
        => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Template '{id}' tidak ditemukan. Yang tersedia: {string.Join(", ", All.Select(t => t.Id))}.");

    /// <summary>Template dalam satu kategori.</summary>
    public static IEnumerable<ProjectTemplate> ByCategory(TemplateCategory category)
        => All.Where(t => t.Category == category);

    /// <summary>Template yang menghasilkan bentuk aplikasi tertentu.</summary>
    public static IEnumerable<ProjectTemplate> ByKind(ProjectKind kind)
        => All.Where(t => t.Kind == kind);

    private static IReadOnlyList<ProjectTemplate> Build()
    {
        List<ProjectTemplate> templates =
        [
            BlankConsole(),
            BlankDesktop(),
            BlankWeb(),
            ObjectDetectionConsole(),
            ObjectDetectionDesktop(),
            DepthViewerDesktop(),
            PeopleCounter(),
            SafetyZoneMonitor(),
            SocialDistanceMonitor(),
            QualityInspection(),
            PrivacyBlur(),
            RgbdRecorder(),
            ShelfMonitor(),
            PpeCompliance(),
            BlazorLiveInference(),
            VisionRestApi(),
        ];

        return [.. templates.OrderBy(t => t.Category).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)];
    }

    // ---------------------------------------------------------------- Blank

    private static ProjectTemplate BlankConsole() => new()
    {
        Id = "blank-console",
        Title = "Konsol Kosong",
        TitleEnglish = "Blank Console",
        Description = "Aplikasi konsol minimal yang membuka perangkat dan menampilkan satu stream preview.",
        DescriptionEnglish = "Minimal console app that opens a device and prints one preview stream.",
        Kind = ProjectKind.Console,
        Category = TemplateCategory.Blank,
        Icon = "⌨️",
        Files =
        [
            new("{{ProjectName}}.csproj", TemplateFragments.ConsoleCsproj()),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(
                "Titik awal paling sederhana: membuka perangkat, menjalankan pipeline kamera, mencetak info frame.",
                "The simplest starting point: open a device, run a camera pipeline, print frame info.")),
            new("Program.cs", """
                using DepthAI;
                using DepthAI.Pipelines;
                using DepthAI.Streaming;

                // Membuka perangkat OAK pertama yang tersedia. Bila runtime native tidak
                // terpasang, SDK memakai perangkat simulasi supaya kode ini tetap jalan.
                await using var device = await DepthAiDevice.OpenAsync();
                Console.WriteLine($"Terhubung ke {device.Info.Name} ({device.Info.SerialNumber})");

                var pipeline = Pipeline.CreateBuilder()
                    .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
                    .StreamOut("rgb.preview", "video")
                    .Build(device.Capabilities);

                await device.StartAsync(pipeline);

                using var subscription = device.GetStream<ImageFrame>("video").Subscribe(frame =>
                    Console.WriteLine($"frame #{frame.SequenceNumber}: {frame.Width}x{frame.Height} {frame.Format}"));

                Console.WriteLine("Tekan Enter untuk berhenti.");
                Console.ReadLine();

                await device.StopAsync();
                """),
        ],
        NextSteps = ["Jalankan `dotnet run`", "Ubah resolusi preview di Program.cs"],
    };

    private static ProjectTemplate BlankDesktop() => new()
    {
        Id = "blank-desktop",
        Title = "Desktop Kosong",
        TitleEnglish = "Blank Desktop",
        Description = "Jendela Avalonia dengan satu tampilan kamera live.",
        DescriptionEnglish = "An Avalonia window with a single live camera view.",
        Kind = ProjectKind.Desktop,
        Category = TemplateCategory.Blank,
        Icon = "🖥️",
        Files = DesktopShell(
            title: "{{ProjectName}}",
            body: """
                  <Image x:Name="Preview" Stretch="Uniform" />
                  """,
            codeBehind: DesktopCodeBehind(
                pipeline: """
                    Pipeline.CreateBuilder()
                                .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
                                .StreamOut("rgb.preview", "video")
                                .Build(_device.Capabilities)
                    """,
                subscribe: """
                    _subscriptions.Add(_device.GetStream<ImageFrame>("video")
                                .Subscribe(frame => ShowFrame(Preview, frame)));
                    """),
            readmeId: "Jendela Avalonia yang menampilkan preview kamera secara langsung.",
            readmeEn: "An Avalonia window showing a live camera preview."),
        NextSteps = ["Jalankan `dotnet run`", "Tambahkan node deteksi ke pipeline"],
    };

    private static ProjectTemplate BlankWeb() => new()
    {
        Id = "blank-web",
        Title = "Web Kosong",
        TitleEnglish = "Blank Web",
        Description = "ASP.NET Core minimal API yang memaparkan satu frame JPEG.",
        DescriptionEnglish = "ASP.NET Core minimal API exposing a single JPEG frame.",
        Kind = ProjectKind.Web,
        Category = TemplateCategory.Blank,
        Icon = "🌐",
        Files =
        [
            new("{{ProjectName}}.csproj", TemplateFragments.WebCsproj(
                """<PackageReference Include="DepthAI.Net.Imaging.ImageSharp" Version="0.1.0" />""")),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(
                "Web API minimal: `GET /frame.jpg` mengembalikan frame kamera terbaru.",
                "Minimal web API: `GET /frame.jpg` returns the latest camera frame.")),
            new("Program.cs", """
                using DepthAI;
                using DepthAI.Imaging;
                using DepthAI.Pipelines;
                using DepthAI.Streaming;

                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddSingleton<CameraService>();
                builder.Services.AddHostedService(sp => sp.GetRequiredService<CameraService>());

                var app = builder.Build();

                app.MapGet("/", () => Results.Content(
                    "<html><body style='margin:0;background:#111'>"
                    + "<img src='/frame.jpg' style='width:100%' "
                    + "onload=\"setTimeout(()=>this.src='/frame.jpg?'+Date.now(),100)\" /></body></html>",
                    "text/html"));

                app.MapGet("/frame.jpg", async (CameraService camera) =>
                {
                    var jpeg = await camera.GetLatestJpegAsync();
                    return jpeg is null
                        ? Results.NotFound("Belum ada frame yang diterima.")
                        : Results.File(jpeg, "image/jpeg");
                });

                app.Run();

                /// <summary>Menjaga satu perangkat tetap terbuka selama aplikasi hidup.</summary>
                internal sealed class CameraService : BackgroundService
                {
                    private readonly SemaphoreSlim _lock = new(1, 1);
                    private ImageFrame? _latest;

                    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                    {
                        await using var device = await DepthAiDevice.OpenAsync(cancellationToken: stoppingToken);

                        var pipeline = Pipeline.CreateBuilder()
                            .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
                            .StreamOut("rgb.preview", "video")
                            .Build(device.Capabilities);

                        await device.StartAsync(pipeline, stoppingToken);

                        using var subscription = device.GetStream<ImageFrame>("video").Subscribe(frame =>
                        {
                            // Frame milik stream dibuang setelah callback, jadi simpan salinannya.
                            var copy = frame.Clone();
                            var previous = Interlocked.Exchange(ref _latest, copy);
                            previous?.Dispose();
                        });

                        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                        await device.StopAsync(CancellationToken.None);
                    }

                    public async Task<byte[]?> GetLatestJpegAsync()
                    {
                        await _lock.WaitAsync();
                        try
                        {
                            var frame = Volatile.Read(ref _latest);
                            return frame is null ? null : await frame.ToJpegAsync();
                        }
                        finally
                        {
                            _lock.Release();
                        }
                    }
                }
                """),
        ],
        NextSteps = ["Jalankan `dotnet run`", "Buka http://localhost:5000"],
    };
}
