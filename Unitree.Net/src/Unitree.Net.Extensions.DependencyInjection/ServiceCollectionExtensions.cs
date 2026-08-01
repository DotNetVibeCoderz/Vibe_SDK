using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unitree.Net.Ai;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Diagnostics;
using Unitree.Net.Interop;
using Unitree.Net.Ros2;
using Unitree.Net.Sensors;

namespace Unitree.Net.Extensions.DependencyInjection;

/// <summary>
/// Registers Unitree.Net services in a dependency-injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the transport, participant, robot and telemetry hub from configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration root, read from section <c>Unitree</c>.</param>
    /// <remarks>
    /// Everything is registered as a singleton. A robot connection is an exclusive resource — Unitree
    /// firmware does not arbitrate between controlling hosts — so a scoped or transient lifetime would
    /// create a second owner of the same hardware.
    /// </remarks>
    public static IServiceCollection AddUnitreeRobot(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<UnitreeOptions>(configuration.GetSection(UnitreeOptions.SectionName));
        services.AddSingleton<IValidateOptions<UnitreeOptions>, UnitreeOptionsValidator>();

        return services.AddUnitreeRobotCore();
    }

    /// <summary>
    /// Registers the transport, participant, robot and telemetry hub from an inline configuration delegate.
    /// </summary>
    public static IServiceCollection AddUnitreeRobot(
        this IServiceCollection services,
        Action<UnitreeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddSingleton<IValidateOptions<UnitreeOptions>, UnitreeOptionsValidator>();

        return services.AddUnitreeRobotCore();
    }

    private static IServiceCollection AddUnitreeRobotCore(this IServiceCollection services)
    {
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<UnitreeOptions>>().Value);

        services.AddSingleton<IDdsTransport>(CreateTransport);

        services.AddSingleton<IDdsParticipant>(serviceProvider => new DdsParticipant(
            serviceProvider.GetRequiredService<IDdsTransport>(),
            serviceProvider.GetService<ILogger<DdsParticipant>>()));

        services.AddSingleton(serviceProvider => new UnitreeRobot(
            serviceProvider.GetRequiredService<IDdsParticipant>(),
            serviceProvider.GetRequiredService<UnitreeOptions>(),
            serviceProvider.GetService<ILoggerFactory>()));

        services.AddSingleton(serviceProvider => new TelemetryHub(
            serviceProvider.GetRequiredService<IDdsParticipant>(),
            serviceProvider.GetRequiredService<UnitreeOptions>().TelemetryQueueCapacity,
            serviceProvider.GetService<ILogger<TelemetryHub>>()));

        return services;
    }

    /// <summary>
    /// Adds a hosted service that connects on startup and disconnects on shutdown.
    /// </summary>
    public static IServiceCollection AddUnitreeRobotHostedConnection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<RobotConnectionService>();
        return services;
    }

    /// <summary>Adds metrics and the robot health check.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="healthCheckName">Name the health check is registered under.</param>
    public static IServiceCollection AddUnitreeDiagnostics(
        this IServiceCollection services,
        string healthCheckName = "unitree-robot")
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(serviceProvider => new RobotMetrics(
            serviceProvider.GetRequiredService<UnitreeRobot>(),
            serviceProvider.GetRequiredService<TelemetryHub>()));

        services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckName,
            serviceProvider => new RobotHealthCheck(
                serviceProvider.GetRequiredService<UnitreeRobot>(),
                serviceProvider.GetRequiredService<TelemetryHub>()),
            failureStatus: HealthStatus.Unhealthy,
            tags: ["robot", "hardware"]));

        return services;
    }

    /// <summary>Adds the AI workflow engine, reading configuration section <c>Unitree:Ai</c>.</summary>
    public static IServiceCollection AddUnitreeAi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        return services.AddUnitreeAiCore();
    }

    /// <summary>Adds the AI workflow engine from an inline configuration delegate.</summary>
    public static IServiceCollection AddUnitreeAi(this IServiceCollection services, Action<AiOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        return services.AddUnitreeAiCore();
    }

    private static IServiceCollection AddUnitreeAiCore(this IServiceCollection services)
    {
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<AiOptions>>().Value);

        services.AddSingleton(serviceProvider => new AiWorkflowEngine(
            serviceProvider.GetRequiredService<UnitreeRobot>(),
            serviceProvider.GetRequiredService<TelemetryHub>(),
            serviceProvider.GetRequiredService<AiOptions>(),
            serviceProvider.GetService<ILoggerFactory>()));

        return services;
    }

    /// <summary>Adds the ROS 2 bridge as a hosted service.</summary>
    public static IServiceCollection AddUnitreeRos2Bridge(
        this IServiceCollection services,
        Action<Ros2BridgeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new Ros2BridgeOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<Ros2Bridge>(serviceProvider => new Ros2Bridge(
            serviceProvider.GetRequiredService<UnitreeRobot>(),
            serviceProvider.GetRequiredService<Ros2BridgeOptions>(),
            serviceProvider.GetService<ILogger<Ros2Bridge>>()));

        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<Ros2Bridge>());
        return services;
    }

    /// <summary>
    /// Builds the transport the configuration selects.
    /// </summary>
    /// <remarks>
    /// This is the only place the three transports meet. Everything above it depends on
    /// <see cref="IDdsTransport"/>, which is what lets the same application run against real hardware,
    /// a host-only multicast link, or an in-process loopback with nothing but a configuration change.
    /// </remarks>
    private static IDdsTransport CreateTransport(IServiceProvider serviceProvider)
    {
        UnitreeOptions options = serviceProvider.GetRequiredService<UnitreeOptions>();

        return options.Transport switch
        {
            DdsTransportKind.CycloneNative => new CycloneDdsTransport(
                options,
                serviceProvider.GetService<ILogger<CycloneDdsTransport>>()),

            DdsTransportKind.ManagedMulticast => new ManagedMulticastTransport(
                options,
                serviceProvider.GetService<ILogger<ManagedMulticastTransport>>()),

            DdsTransportKind.Loopback => new LoopbackTransport(),

            _ => throw new ArgumentOutOfRangeException(
                nameof(serviceProvider),
                options.Transport,
                "Unknown transport kind."),
        };
    }
}

/// <summary>
/// Runs <see cref="UnitreeOptions.Validate"/> through the options validation pipeline.
/// </summary>
/// <remarks>
/// Validating at resolution time rather than at first use means a misconfigured host fails at startup
/// with a clear message, instead of throwing from deep inside the transport minutes later.
/// </remarks>
internal sealed class UnitreeOptionsValidator : IValidateOptions<UnitreeOptions>
{
    public ValidateOptionsResult Validate(string? name, UnitreeOptions options)
    {
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (OptionsValidationFailure ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
