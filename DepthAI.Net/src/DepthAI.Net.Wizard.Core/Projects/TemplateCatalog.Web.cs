namespace DepthAI.Wizard.Projects;

public static partial class TemplateCatalog
{
    private static ProjectTemplate BlazorLiveInference() => new()
    {
        Id = "blazor-live-inference",
        Title = "Inferensi Live (Blazor)",
        TitleEnglish = "Live Inference (Blazor)",
        Description = "Halaman Blazor Server yang menampilkan video kamera dan deteksi secara real-time.",
        DescriptionEnglish = "A Blazor Server page streaming live camera video and detections.",
        Kind = ProjectKind.Web,
        Category = TemplateCategory.Web,
        Icon = "⚡",
        Requires = ["Kamera RGB"],
        Files =
        [
            new("{{ProjectName}}.csproj", TemplateFragments.WebCsproj(
                """<PackageReference Include="DepthAI.Net.Imaging.ImageSharp" Version="0.1.0" />""")),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(
                "Blazor Server dengan render interaktif: frame dikirim sebagai data URI, deteksi diperbarui lewat SignalR.",
                "Blazor Server with interactive rendering: frames are sent as data URIs, detections update over SignalR.")),
            new("Program.cs", """
                using {{ProjectNamespace}};
                using {{ProjectNamespace}}.Components;

                var builder = WebApplication.CreateBuilder(args);

                builder.Services.AddRazorComponents().AddInteractiveServerComponents();
                builder.Services.AddSingleton<VisionService>();
                builder.Services.AddHostedService(sp => sp.GetRequiredService<VisionService>());

                var app = builder.Build();

                app.UseStaticFiles();
                app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

                app.Run();
                """),
            new("VisionService.cs", """
                using DepthAI;
                using DepthAI.Imaging;
                using DepthAI.Inference;
                using DepthAI.Pipelines;
                using DepthAI.Streaming;

                namespace {{ProjectNamespace}};

                /// <summary>
                /// Menjaga satu perangkat tetap terbuka untuk seluruh aplikasi dan menyiarkan
                /// frame terbaru ke komponen yang berlangganan.
                /// </summary>
                /// <remarks>
                /// Perangkat hanya bisa dibuka satu proses, jadi ini singleton — bukan
                /// scoped per koneksi. Semua sirkuit Blazor melihat frame yang sama.
                /// </remarks>
                public sealed class VisionService : BackgroundService
                {
                    private ImageFrame? _latestFrame;

                    public IReadOnlyList<Detection> LatestDetections { get; private set; } = [];

                    public string DeviceName { get; private set; } = "menghubungkan…";

                    public bool IsSimulated { get; private set; }

                    /// <summary>Dipicu tiap kali ada frame baru; komponen memakai ini untuk merender ulang.</summary>
                    public event Action? FrameReady;

                    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                    {
                        var model = NeuralModel.CreatePlaceholder(
                            ModelFamily.MobileNetSsd,
                            labels: ["person", "bottle", "chair"],
                            inputWidth: 300,
                            inputHeight: 300);

                        await using var device = await DepthAiDevice.OpenAsync(cancellationToken: stoppingToken);

                        DeviceName = device.Info.Name;
                        IsSimulated = device.IsSimulated;

                        var pipeline = Pipeline.CreateBuilder()
                            .AddColorCamera("rgb", camera => camera.WithPreview(640, 480))
                            .AddObjectDetection(model, "rgb.preview", "detector")
                            .StreamOut("rgb.preview", "video")
                            .StreamOut("detector.detections", "detections")
                            .Build(device.Capabilities);

                        await device.StartAsync(pipeline, stoppingToken);

                        using var detectionSubscription = device.GetStream<DetectionFrame>("detections")
                            .Subscribe(frame => LatestDetections = frame.Detections);

                        // Dibatasi ~12 fps: melebihi itu, encoding JPEG dan lalu lintas
                        // SignalR menjadi hambatan, bukan kameranya.
                        using var videoSubscription = device.GetStream<ImageFrame>("video")
                            .Throttle(TimeSpan.FromMilliseconds(80))
                            .Subscribe(frame =>
                            {
                                var previous = Interlocked.Exchange(ref _latestFrame, frame.Clone());
                                previous?.Dispose();
                                FrameReady?.Invoke();
                            });

                        try
                        {
                            await Task.Delay(Timeout.Infinite, stoppingToken);
                        }
                        catch (OperationCanceledException)
                        {
                            // Penghentian aplikasi yang normal.
                        }

                        await device.StopAsync(CancellationToken.None);
                    }

                    /// <summary>Frame terbaru sebagai data URI yang siap dipasang ke tag img.</summary>
                    public async Task<string?> GetFrameDataUriAsync()
                    {
                        var frame = Volatile.Read(ref _latestFrame);
                        if (frame is null)
                        {
                            return null;
                        }

                        var jpeg = await frame.ToJpegAsync(quality: 70);
                        return "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
                    }
                }
                """),
            new("Components/App.razor", """
                <!DOCTYPE html>
                <html lang="id" data-bs-theme="dark">
                <head>
                    <meta charset="utf-8" />
                    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                    <title>{{ProjectName}}</title>
                    <base href="/" />
                    <link rel="stylesheet" href="app.css" />
                    <HeadOutlet />
                </head>
                <body>
                    <Routes />
                    <script src="_framework/blazor.web.js"></script>
                </body>
                </html>
                """),
            new("Components/Routes.razor", """
                <Router AppAssembly="typeof(Program).Assembly">
                    <Found Context="routeData">
                        <RouteView RouteData="routeData" />
                    </Found>
                    <NotFound>
                        <p>Halaman tidak ditemukan.</p>
                    </NotFound>
                </Router>
                """),
            new("Components/_Imports.razor", """
                @using Microsoft.AspNetCore.Components.Web
                @using Microsoft.AspNetCore.Components.Routing
                @* Membuat @rendermode InteractiveServer bisa ditulis tanpa awalan RenderMode. *@
                @using static Microsoft.AspNetCore.Components.Web.RenderMode
                @using {{ProjectNamespace}}
                @using {{ProjectNamespace}}.Components
                """),
            new("Components/Live.razor", """
                @page "/"
                @rendermode InteractiveServer
                @inject VisionService Vision
                @implements IDisposable

                <PageTitle>{{ProjectName}}</PageTitle>

                <header>
                    <h1>{{ProjectName}}</h1>
                    <p class="device">
                        @Vision.DeviceName
                        @if (Vision.IsSimulated)
                        {
                            <span class="badge">simulasi</span>
                        }
                    </p>
                </header>

                <main>
                    <div class="viewer">
                        @if (_frame is null)
                        {
                            <div class="placeholder">Menunggu frame pertama…</div>
                        }
                        else
                        {
                            <img src="@_frame" alt="Tampilan kamera langsung" />
                        }
                    </div>

                    <aside>
                        <h2>Objek terdeteksi</h2>
                        @if (Vision.LatestDetections.Count == 0)
                        {
                            <p class="empty">Belum ada objek dalam pandangan.</p>
                        }
                        else
                        {
                            <ul>
                                @foreach (var detection in Vision.LatestDetections)
                                {
                                    <li>
                                        <span class="label">@detection.Label</span>
                                        <span class="score">@detection.Confidence.ToString("P0")</span>
                                    </li>
                                }
                            </ul>
                        }
                    </aside>
                </main>

                @code {
                    private string? _frame;

                    protected override void OnInitialized() => Vision.FrameReady += OnFrameReady;

                    private async void OnFrameReady()
                    {
                        _frame = await Vision.GetFrameDataUriAsync();
                        await InvokeAsync(StateHasChanged);
                    }

                    public void Dispose() => Vision.FrameReady -= OnFrameReady;
                }
                """),
            new("wwwroot/app.css", """
                :root {
                    --abyss: #0e1417;
                    --deep: #141c21;
                    --mid: #1b252b;
                    --ink: #e8f1f2;
                    --muted: #8ca3ad;
                    --near: #f2a03d;
                }

                * { box-sizing: border-box; }

                body {
                    margin: 0;
                    padding: 24px;
                    background: var(--abyss);
                    color: var(--ink);
                    font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
                }

                header h1 { margin: 0 0 4px; font-size: 20px; font-weight: 600; }

                .device { margin: 0 0 20px; color: var(--muted); font-size: 13px; }

                .badge {
                    margin-left: 8px;
                    padding: 2px 8px;
                    border-radius: 10px;
                    background: var(--mid);
                    font-size: 11px;
                    letter-spacing: 0.06em;
                    text-transform: uppercase;
                }

                main { display: grid; grid-template-columns: 1fr 260px; gap: 16px; }

                .viewer, aside { background: var(--deep); border-radius: 10px; overflow: hidden; }

                .viewer img { display: block; width: 100%; }

                .placeholder {
                    display: grid;
                    place-items: center;
                    aspect-ratio: 4 / 3;
                    color: var(--muted);
                }

                aside { padding: 16px; }

                aside h2 {
                    margin: 0 0 12px;
                    font-size: 11px;
                    font-weight: 600;
                    letter-spacing: 0.12em;
                    text-transform: uppercase;
                    color: var(--muted);
                }

                aside ul { list-style: none; margin: 0; padding: 0; }

                aside li {
                    display: flex;
                    justify-content: space-between;
                    padding: 8px 10px;
                    margin-bottom: 6px;
                    border-radius: 6px;
                    background: var(--mid);
                    font-size: 13px;
                }

                .score { color: var(--near); font-variant-numeric: tabular-nums; }

                .empty { color: var(--muted); font-size: 13px; }

                @media (max-width: 720px) {
                    main { grid-template-columns: 1fr; }
                }
                """),
            new("Properties/launchSettings.json", """
                {
                  "profiles": {
                    "{{ProjectName}}": {
                      "commandName": "Project",
                      "launchBrowser": true,
                      "applicationUrl": "http://localhost:5080",
                      "environmentVariables": {
                        "ASPNETCORE_ENVIRONMENT": "Development"
                      }
                    }
                  }
                }
                """),
        ],
        NextSteps = ["Jalankan `dotnet run`", "Buka http://localhost:5080"],
    };

    private static ProjectTemplate VisionRestApi() => new()
    {
        Id = "vision-rest-api",
        Title = "REST API Vision",
        TitleEnglish = "Vision REST API",
        Description = "Memaparkan deteksi dan pembacaan kedalaman sebagai JSON untuk dipakai sistem lain.",
        DescriptionEnglish = "Exposes detections and depth readings as JSON for other systems to consume.",
        Kind = ProjectKind.Web,
        Category = TemplateCategory.Web,
        Icon = "🔌",
        Requires = ["Kamera RGB", "Stereo depth"],
        Files =
        [
            new("{{ProjectName}}.csproj", TemplateFragments.WebCsproj(
                """<PackageReference Include="DepthAI.Net.Imaging.ImageSharp" Version="0.1.0" />""")),
            new(".gitignore", TemplateFragments.GitIgnore),
            new("README.md", TemplateFragments.Readme(
                "Endpoint: `/api/detections`, `/api/depth?x=&y=`, `/api/frame.jpg`, `/api/health`.",
                "Endpoints: `/api/detections`, `/api/depth?x=&y=`, `/api/frame.jpg`, `/api/health`.")),
            new("Program.cs", """
                using {{ProjectNamespace}};

                var builder = WebApplication.CreateBuilder(args);

                builder.Services.AddSingleton<VisionService>();
                builder.Services.AddHostedService(sp => sp.GetRequiredService<VisionService>());
                builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
                    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

                var app = builder.Build();
                app.UseCors();

                app.MapGet("/api/health", (VisionService vision) => Results.Ok(new
                {
                    device = vision.DeviceName,
                    simulated = vision.IsSimulated,
                    running = vision.IsRunning,
                    framesReceived = vision.FrameCount,
                }));

                app.MapGet("/api/detections", (VisionService vision) => Results.Ok(
                    vision.LatestDetections.Select(d => new
                    {
                        label = d.Label,
                        confidence = d.Confidence,
                        box = new { d.Box.XMin, d.Box.YMin, d.Box.XMax, d.Box.YMax },
                        distanceMeters = d.Spatial?.Z,
                    })));

                app.MapGet("/api/depth", (VisionService vision, int x, int y) =>
                {
                    var distance = vision.ReadDepthMeters(x, y);

                    // Piksel tanpa pengukuran adalah kondisi normal, bukan error —
                    // dikembalikan sebagai 200 dengan nilai null supaya klien bisa
                    // membedakannya dari koordinat di luar frame.
                    return distance is null
                        ? Results.Ok(new { x, y, distanceMeters = (float?)null, measured = false })
                        : Results.Ok(new { x, y, distanceMeters = distance, measured = true });
                });

                app.MapGet("/api/frame.jpg", async (VisionService vision) =>
                {
                    var jpeg = await vision.GetLatestJpegAsync();
                    return jpeg is null ? Results.NotFound() : Results.File(jpeg, "image/jpeg");
                });

                app.Run();
                """),
            new("VisionService.cs", """
                using DepthAI;
                using DepthAI.Imaging;
                using DepthAI.Inference;
                using DepthAI.Pipelines;
                using DepthAI.Streaming;

                namespace {{ProjectNamespace}};

                /// <summary>Menjaga perangkat tetap terbuka dan menyimpan hasil terbaru untuk endpoint API.</summary>
                public sealed class VisionService : BackgroundService
                {
                    private ImageFrame? _latestFrame;
                    private DepthFrame? _latestDepth;
                    private long _frameCount;

                    public IReadOnlyList<Detection> LatestDetections { get; private set; } = [];

                    public string DeviceName { get; private set; } = "menghubungkan…";

                    public bool IsSimulated { get; private set; }

                    public bool IsRunning { get; private set; }

                    public long FrameCount => Interlocked.Read(ref _frameCount);

                    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                    {
                        var model = NeuralModel.CreatePlaceholder(
                            ModelFamily.MobileNetSsd,
                            labels: ["person", "bottle", "chair"],
                            inputWidth: 300,
                            inputHeight: 300);

                        await using var device = await DepthAiDevice.OpenAsync(cancellationToken: stoppingToken);

                        DeviceName = device.Info.Name;
                        IsSimulated = device.IsSimulated;

                        var pipeline = Pipeline.CreateBuilder()
                            .AddColorCamera("rgb", camera => camera.WithPreview(640, 400))
                            .AddSpatialObjectDetection(model, "rgb.preview", "detector")
                            .StreamOut("rgb.preview", "video")
                            .StreamOut("detector.detections", "detections")
                            .StreamOut("detector_stereo.depth", "depth")
                            .Build(device.Capabilities);

                        await device.StartAsync(pipeline, stoppingToken);
                        IsRunning = true;

                        using var detections = device.GetStream<DetectionFrame>("detections")
                            .Subscribe(frame => LatestDetections = frame.Detections);

                        using var video = device.GetStream<ImageFrame>("video").Subscribe(frame =>
                        {
                            Interlocked.Increment(ref _frameCount);
                            var previous = Interlocked.Exchange(ref _latestFrame, frame.Clone());
                            previous?.Dispose();
                        });

                        using var depth = device.GetStream<DepthFrame>("depth").Subscribe(frame =>
                        {
                            var previous = Interlocked.Exchange(ref _latestDepth, frame.Clone());
                            previous?.Dispose();
                        });

                        try
                        {
                            await Task.Delay(Timeout.Infinite, stoppingToken);
                        }
                        catch (OperationCanceledException)
                        {
                            // Penghentian aplikasi yang normal.
                        }

                        IsRunning = false;
                        await device.StopAsync(CancellationToken.None);
                    }

                    public float? ReadDepthMeters(int x, int y)
                    {
                        var depth = Volatile.Read(ref _latestDepth);
                        if (depth is null || x < 0 || y < 0 || x >= depth.Width || y >= depth.Height)
                        {
                            return null;
                        }

                        return depth.GetDistanceMeters(x, y);
                    }

                    public async Task<byte[]?> GetLatestJpegAsync()
                    {
                        var frame = Volatile.Read(ref _latestFrame);
                        return frame is null ? null : await frame.ToJpegAsync();
                    }
                }
                """),
            new("Properties/launchSettings.json", """
                {
                  "profiles": {
                    "{{ProjectName}}": {
                      "commandName": "Project",
                      "launchBrowser": false,
                      "applicationUrl": "http://localhost:5090",
                      "environmentVariables": {
                        "ASPNETCORE_ENVIRONMENT": "Development"
                      }
                    }
                  }
                }
                """),
        ],
        NextSteps = ["Jalankan `dotnet run`", "Coba `curl http://localhost:5090/api/detections`"],
    };
}
