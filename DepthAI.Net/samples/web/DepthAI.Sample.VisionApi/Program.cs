using DepthAI.Sample.VisionApi;

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