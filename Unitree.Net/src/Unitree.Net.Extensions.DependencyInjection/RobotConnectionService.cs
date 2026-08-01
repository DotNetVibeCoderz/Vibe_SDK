using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Unitree.Net.Control;
using Unitree.Net.Core;

namespace Unitree.Net.Extensions.DependencyInjection;

/// <summary>
/// Connects the robot when the host starts and disconnects it on shutdown.
/// </summary>
/// <remarks>
/// <para>
/// A failed connection does not abort host startup. A dashboard or telemetry service should still come
/// up and report that the robot is unreachable — taking the whole process down means the operator has
/// no interface to diagnose from, which is exactly backwards.
/// </para>
/// <para>
/// Reconnection is attempted with a bounded backoff for as long as the host runs.
/// </para>
/// </remarks>
public sealed partial class RobotConnectionService(
    UnitreeRobot robot,
    ILogger<RobotConnectionService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan delay = InitialRetryDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (robot.State is ConnectionState.Connected)
            {
                robot.RefreshConnectionState();
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await robot.ConnectAsync(stoppingToken).ConfigureAwait(false);
                delay = InitialRetryDelay;
                LogConnected(logger, robot.Model);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (UnitreeException ex)
            {
                LogConnectFailed(logger, ex, delay.TotalSeconds);

                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);

                // Exponential backoff with a ceiling: a robot that is simply powered off should not
                // generate a connection attempt every two seconds for the rest of the day.
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await robot.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDisconnectFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Robot connection established to {Model}.")]
    private static partial void LogConnected(ILogger logger, RobotModel model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Robot connection failed; retrying in {DelaySeconds:0} s.")]
    private static partial void LogConnectFailed(ILogger logger, Exception exception, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Robot disconnect did not complete cleanly.")]
    private static partial void LogDisconnectFailed(ILogger logger, Exception exception);
}
