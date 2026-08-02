using DepthAiWebApp;
using DepthAiWebApp.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<VisionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VisionService>());

var app = builder.Build();

app.UseStaticFiles();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();