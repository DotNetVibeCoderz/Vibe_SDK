using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PollenRobotics.Net.Simulation;
using PollenRobotics.Net.Simulator.Components;

namespace PollenRobotics.Net.Simulator.Hosting;

/// <summary>
/// Runs the Blazor viewport on a loopback Kestrel inside this process.
/// </summary>
/// <remarks>
/// <para>
/// The simulator is one application with two halves: an Avalonia window for the controls and a web
/// page for the 3D. Hosting the page in-process rather than as a separate service means they share
/// one <see cref="SimulationEngine"/> instance - the viewport reads the same snapshots the status
/// panel does, with no serialisation between them and no second copy of the robot to keep in step.
/// </para>
/// <para>
/// It binds to loopback on an ephemeral port. Binding to a fixed port would collide with a second
/// copy of the simulator, and binding to anything but loopback would put a robot viewport on the
/// network without anyone asking for it.
/// </para>
/// </remarks>
public sealed class ViewportHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    /// <summary>The address the viewport is served on.</summary>
    public Uri Address { get; }

    private ViewportHost(WebApplication application, Uri address)
    {
        _application = application;
        Address = address;
    }

    /// <summary>Builds and starts the host.</summary>
    /// <param name="engine">The simulation the viewport renders.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    public static async Task<ViewportHost> StartAsync(SimulationEngine engine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);

        int port = FindFreePort();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ViewportHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        // Resolves static web assets through the build-time manifest rather than expecting every
        // file to sit under wwwroot. The framework's own assets - blazor.web.js among them - live in
        // obj/ and in the NuGet cache and are never copied into the output tree, so without this the
        // manifest names a file that is not there and the request fails with FileNotFoundException.
        //
        // ASP.NET Core calls this automatically in the Development environment. A desktop tool runs
        // in Production, so it has to ask.
        builder.WebHost.UseStaticWebAssets();

        // Kestrel's own request logging would drown the panel that matters. The viewport reports
        // its own failures through the engine's sink instead.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(engine.Log.AsLoggerProvider());
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddSingleton(engine);

        WebApplication application = builder.Build();

        // MapStaticAssets, not UseStaticFiles. The framework's own files - blazor.web.js above all -
        // are served from the static web assets endpoint manifest, not from wwwroot. With
        // UseStaticFiles alone, wwwroot serves fine, _framework/blazor.web.js 404s, and the page
        // renders as a static prerender that never becomes interactive: the markup looks right and
        // no event handler or OnAfterRenderAsync ever runs.
        application.MapStaticAssets();
        application.UseAntiforgery();
        application.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        await application.StartAsync(cancellationToken).ConfigureAwait(false);

        engine.Log.Info("viewport", $"3D viewport serving on http://127.0.0.1:{port}");
        return new ViewportHost(application, new Uri($"http://127.0.0.1:{port}"));
    }

    /// <summary>
    /// Asks the OS for a free port by binding to port zero and reading back what it assigned.
    /// </summary>
    /// <remarks>
    /// There is an unavoidable race here: the port is released before Kestrel claims it. On
    /// loopback, for a desktop tool, the window is microseconds and the alternative is a fixed port
    /// that fails whenever a second copy is open.
    /// </remarks>
    private static int FindFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _application.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The process is going away regardless; a stubborn circuit is not worth blocking exit.
        }

        await _application.DisposeAsync().ConfigureAwait(false);
    }
}
