using Unitree.Net.Dashboard.Components;
using Unitree.Net.Dashboard.Services;
using Unitree.Net.Extensions.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The robot, its transport and telemetry are singletons: a connection is an exclusive resource, and
// every browser circuit observes the same one rather than opening its own link to the hardware.
builder.Services.AddUnitreeRobot(builder.Configuration);
builder.Services.AddUnitreeRobotHostedConnection();
builder.Services.AddUnitreeDiagnostics();

builder.Services.AddSingleton<TelemetryRecorder>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<TelemetryRecorder>());

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHealthChecks("/health");

app.Run();
