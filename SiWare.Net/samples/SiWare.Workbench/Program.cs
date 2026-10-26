using System.Text.Json.Serialization;
using SiWare.Sdk;
using SiWare.Workbench;

// A published build carries wwwroot next to the executable; use it so the app works from any working directory.
var publishedRoot = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = publishedRoot });
var vendorConfig = Environment.GetEnvironmentVariable("SIWARE_VENDOR_CONFIG");
builder.Services.AddSingleton<ISpectrometer>(_ => vendorConfig == null ? new SimulatedSpectrometer() :
    new VendorSpectrometer(System.Text.Json.JsonSerializer.Deserialize<VendorBridgeOptions>(File.ReadAllText(vendorConfig), SpectrumFile.JsonOptions)
        ?? throw new ArgumentException("Invalid vendor configuration.")));
builder.Services.AddSingleton<WorkbenchSession>();
builder.Services.AddSingleton(new InsightService(new HttpClient()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024);
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Method == "POST" && context.Request.Headers.TryGetValue("Origin", out var origin)
        && (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) || uri.Authority != context.Request.Host.Value))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { error = "Cross-origin control requests are disabled." });
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'";
    try { await next(context); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or OperationCanceledException or BadHttpRequestException or IOException or NotSupportedException or HttpRequestException)
    {
        if (context.Response.HasStarted) throw;
        // Not 408: browsers transparently resend a request answered with 408, which would rerun a cancelled acquisition.
        context.Response.StatusCode = ex switch { KeyNotFoundException => 404, InvalidOperationException or OperationCanceledException => 409, IOException or HttpRequestException => 502, _ => 400 };
        await context.Response.WriteAsJsonAsync(new { error = ex is OperationCanceledException ? "Acquisition cancelled or timed out." : ex.Message }, CancellationToken.None);
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/status", (WorkbenchSession s) => s.Status());
app.MapPost("/api/connect", (WorkbenchSession s, CancellationToken ct) => s.ConnectAsync(ct));
app.MapPost("/api/disconnect", (WorkbenchSession s, CancellationToken ct) => s.DisconnectAsync(ct));
app.MapPost("/api/acquire", (AcquireRequest r, WorkbenchSession s, CancellationToken ct) => s.AcquireAsync(r, ct));
app.MapPost("/api/series", (SeriesRequest r, WorkbenchSession s) => s.StartSeries(r));
app.MapPost("/api/cancel", (WorkbenchSession s) => { s.Cancel(); return Results.Ok(); });
app.MapGet("/api/history", (WorkbenchSession s) => s.History());
app.MapGet("/api/spectra/{id:guid}", (Guid id, WorkbenchSession s) => s.Get(id));
app.MapPost("/api/insight/{id:guid}", (Guid id, InsightRequest r, WorkbenchSession s, InsightService i, CancellationToken ct) => i.ExplainAsync(s.Get(id), r.Language, ct));
app.MapPost("/api/process/{id:guid}", (Guid id, ProcessRequest r, WorkbenchSession s) => s.Process(id, r));
app.MapGet("/api/export/{id:guid}", (Guid id, string? format, WorkbenchSession s) =>
{
    var spectrum = s.Get(id);
    return format switch
    {
        "csv" => Results.File(System.Text.Encoding.UTF8.GetBytes(SpectrumFile.ToCsv(spectrum)), "text/csv", $"siware-{id}.csv"),
        "json" or null => Results.File(System.Text.Encoding.UTF8.GetBytes(SpectrumFile.ToJson(spectrum)), "application/json", $"siware-{id}.json"),
        _ => throw new ArgumentException("Export format must be csv or json.")
    };
});
app.Run();
