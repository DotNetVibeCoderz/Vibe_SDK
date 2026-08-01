using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Messages;
using Unitree.Net.Messages.Go;

namespace Unitree.Net.Control;

/// <summary>
/// High-level locomotion control: postures, gaits and velocity commands.
/// </summary>
/// <remarks>
/// <para>
/// Every velocity command is clamped to the configured <see cref="RobotSafetyOptions.Velocity"/>
/// envelope before it leaves the process. Clamping happens here rather than on the robot because the
/// firmware accepts its full factory envelope regardless of context.
/// </para>
/// <para>
/// <see cref="MoveAsync"/> is a one-shot command with a short lifetime on the robot. To keep moving,
/// either call it repeatedly at 10 Hz or better, or use <see cref="StartVelocityStream"/>, which owns
/// the cadence and the watchdog for you.
/// </para>
/// </remarks>
public sealed class SportClient : IDisposable
{
    private readonly ServiceClient _service;
    private readonly RobotSafetyOptions _safety;
    private readonly ILogger _logger;
    private VelocityStream? _activeStream;
    private bool _disposed;

    /// <summary>Creates a sport client over <paramref name="participant"/>.</summary>
    /// <param name="participant">The DDS participant.</param>
    /// <param name="safety">Safety envelope applied to velocity commands.</param>
    /// <param name="requestTimeout">Default service call timeout.</param>
    /// <param name="logger">Logger.</param>
    public SportClient(
        IDdsParticipant participant,
        RobotSafetyOptions safety,
        TimeSpan requestTimeout,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(safety);

        _safety = safety;
        _logger = logger ?? NullLogger.Instance;
        _service = new ServiceClient(participant, Services.Sport, requestTimeout, _logger);
    }

    /// <summary>Stands the robot up to its nominal height.</summary>
    public Task StandUpAsync(CancellationToken cancellationToken = default) =>
        _service.CallAsync(SportApi.StandUp, cancellationToken: cancellationToken);

    /// <summary>Lowers the robot to the crouched posture.</summary>
    public Task StandDownAsync(CancellationToken cancellationToken = default) =>
        _service.CallAsync(SportApi.StandDown, cancellationToken: cancellationToken);

    /// <summary>
    /// Enters balanced standing, the mode in which velocity commands are accepted.
    /// </summary>
    /// <remarks>
    /// <see cref="MoveAsync"/> is silently ignored outside this mode, so call it once after standing up
    /// rather than wondering why the robot will not walk.
    /// </remarks>
    public Task BalanceStandAsync(CancellationToken cancellationToken = default) =>
        _service.CallAsync(SportApi.BalanceStand, cancellationToken: cancellationToken);

    /// <summary>Stops motion while remaining standing.</summary>
    public Task StopMoveAsync(CancellationToken cancellationToken = default) =>
        _service.CallAsync(SportApi.StopMove, cancellationToken: cancellationToken);

    /// <summary>
    /// Enters damping mode: the joints resist motion but do not hold a posture.
    /// </summary>
    /// <remarks>
    /// This is the safe way to end a session. The robot settles under gravity rather than dropping, and
    /// it is also the correct response to a detected fall.
    /// </remarks>
    public Task DampAsync(CancellationToken cancellationToken = default) =>
        _service.CallAsync(SportApi.Damp, cancellationToken: cancellationToken);

    /// <summary>Recovers to standing from a fallen or lying posture.</summary>
    public Task RecoveryStandAsync(CancellationToken cancellationToken = default) =>
        _service.CallAsync(SportApi.RecoveryStand, cancellationToken: cancellationToken);

    /// <summary>Sits the robot down.</summary>
    public Task SitAsync(CancellationToken cancellationToken = default) =>
        _service.CallAsync(SportApi.Sit, cancellationToken: cancellationToken);

    /// <summary>Rises from sitting.</summary>
    public Task RiseSitAsync(CancellationToken cancellationToken = default) =>
        _service.CallAsync(SportApi.RiseSit, cancellationToken: cancellationToken);

    /// <summary>Waves a front leg.</summary>
    public Task HelloAsync(CancellationToken cancellationToken = default) =>
        _service.CallAsync(SportApi.Hello, cancellationToken: cancellationToken);

    /// <summary>Performs the stretch routine.</summary>
    public Task StretchAsync(CancellationToken cancellationToken = default) =>
        _service.CallAsync(SportApi.Stretch, cancellationToken: cancellationToken);

    /// <summary>
    /// Commands a body-frame velocity.
    /// </summary>
    /// <param name="command">The requested velocity; clamped to the safety envelope.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// The robot applies the command for a few hundred milliseconds and then stops. Repeat the call to
    /// keep moving.
    /// </remarks>
    public Task MoveAsync(VelocityCommand command, CancellationToken cancellationToken = default)
    {
        VelocityCommand clamped = command.Clamp(_safety.Velocity);
        LogClampIfChanged(command, clamped);

        return _service.CallAsync(SportApi.Move, FormatMove(clamped), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Sends a velocity command without waiting for a response.
    /// </summary>
    /// <remarks>Use on a periodic control path where round-trip latency would cap the update rate.</remarks>
    public void Move(VelocityCommand command)
    {
        VelocityCommand clamped = command.Clamp(_safety.Velocity);
        LogClampIfChanged(command, clamped);
        _service.Send(SportApi.Move, FormatMove(clamped));
    }

    /// <summary>Sets the standing body height offset.</summary>
    /// <param name="heightOffsetMetres">Offset from nominal, in the range −0.18 to 0.03 m for a Go2.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task SetBodyHeightAsync(float heightOffsetMetres, CancellationToken cancellationToken = default) =>
        _service.CallAsync(
            SportApi.BodyHeight,
            $"{{\"data\":{ServiceClient.Json(heightOffsetMetres)}}}",
            cancellationToken: cancellationToken);

    /// <summary>Sets the swing height.</summary>
    /// <param name="heightMetres">Swing height, in the range 0.05 to 0.16 m for a Go2.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task SetFootRaiseHeightAsync(float heightMetres, CancellationToken cancellationToken = default) =>
        _service.CallAsync(
            SportApi.FootRaiseHeight,
            $"{{\"data\":{ServiceClient.Json(heightMetres)}}}",
            cancellationToken: cancellationToken);

    /// <summary>Sets body orientation while standing.</summary>
    public Task SetEulerAsync(EulerAngles orientation, CancellationToken cancellationToken = default) =>
        _service.CallAsync(
            SportApi.Euler,
            $"{{\"x\":{ServiceClient.Json(orientation.Roll)},\"y\":{ServiceClient.Json(orientation.Pitch)},\"z\":{ServiceClient.Json(orientation.Yaw)}}}",
            cancellationToken: cancellationToken);

    /// <summary>Switches the active gait.</summary>
    public Task SwitchGaitAsync(GaitType gait, CancellationToken cancellationToken = default) =>
        _service.CallAsync(
            SportApi.SwitchGait,
            $"{{\"data\":{(int)gait}}}",
            cancellationToken: cancellationToken);

    /// <summary>Enables or disables continuous gait, in which the robot keeps stepping while stationary.</summary>
    public Task SetContinuousGaitAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _service.CallAsync(
            SportApi.ContinuousGait,
            $"{{\"data\":{(enabled ? "true" : "false")}}}",
            cancellationToken: cancellationToken);

    /// <summary>Sets the speed level.</summary>
    /// <param name="level">−1 for slow, 0 for normal, 1 for fast.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task SetSpeedLevelAsync(int level, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 1);

        return _service.CallAsync(
            SportApi.SpeedLevel,
            $"{{\"data\":{level.ToString(CultureInfo.InvariantCulture)}}}",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Starts a self-refreshing velocity stream.
    /// </summary>
    /// <returns>
    /// A handle whose <see cref="VelocityStream.Command"/> can be updated at any time, and which stops
    /// the robot when disposed.
    /// </returns>
    /// <param name="updateRateHz">How often the held command is resent to the robot.</param>
    /// <param name="commandTimeout">
    /// How long a command may go without being reassigned before the stream stops the robot.
    /// <see langword="null"/>, the default, means never — holding one velocity is ordinary.
    /// </param>
    /// <remarks>
    /// <para>
    /// Solves what makes raw <see cref="MoveAsync"/> awkward for continuous motion: the robot expires
    /// a velocity command about half a second after receiving it, so continuous motion needs the
    /// command resent. The pump does that at <paramref name="updateRateHz"/>.
    /// </para>
    /// <para>
    /// That is also the safety property. Because the pump is what keeps the requests flowing, the
    /// robot stops on its own if this process dies or the stream is disposed — no request arrives,
    /// the robot's own expiry fires. Set <paramref name="commandTimeout"/> only if you additionally
    /// want a still-running application that stops <em>reassigning</em> to be treated as a fault.
    /// </para>
    /// <para>Only one stream may be active at a time.</para>
    /// </remarks>
    public VelocityStream StartVelocityStream(int updateRateHz = 20, TimeSpan? commandTimeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeStream is { IsRunning: true })
        {
            throw new InvalidOperationException(
                "A velocity stream is already running. Dispose it before starting another.");
        }

        _activeStream = new VelocityStream(this, updateRateHz, commandTimeout, _logger);
        return _activeStream;
    }

    private static string FormatMove(VelocityCommand command) =>
        $"{{\"x\":{ServiceClient.Json(command.Forward)},\"y\":{ServiceClient.Json(command.Lateral)},\"z\":{ServiceClient.Json(command.YawRate)}}}";

    private void LogClampIfChanged(VelocityCommand requested, VelocityCommand clamped)
    {
        if (requested != clamped)
        {
            _logger.LogWarning(
                "Velocity command clamped from ({Fx:0.##}, {Fy:0.##}, {Fz:0.##}) to ({Cx:0.##}, {Cy:0.##}, {Cz:0.##}) by the safety envelope.",
                requested.Forward,
                requested.Lateral,
                requested.YawRate,
                clamped.Forward,
                clamped.Lateral,
                clamped.YawRate);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activeStream?.Dispose();
        _service.Dispose();
    }
}

/// <summary>
/// A self-refreshing velocity command with a watchdog.
/// </summary>
/// <remarks>
/// Set <see cref="Command"/> from anywhere; a background timer resends it at the configured rate. If
/// the command is not refreshed within the watchdog interval, the stream sends a stop — an application
/// that hangs must not leave a robot walking.
/// </remarks>
public sealed class VelocityStream : IDisposable
{
    private readonly SportClient _client;
    private readonly TimeSpan? _commandTimeout;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _pumpTask;
    private readonly Lock _commandLock = new();

    private VelocityCommand _command;
    private DateTimeOffset _lastUpdate = DateTimeOffset.UtcNow;
    private bool _watchdogTripped;
    private bool _disposed;

    internal VelocityStream(SportClient client, int updateRateHz, TimeSpan? commandTimeout, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(updateRateHz, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(updateRateHz, 200);

        _client = client;
        _commandTimeout = commandTimeout;
        _logger = logger;
        UpdateRateHz = updateRateHz;

        _pumpTask = Task.Run(() => PumpAsync(_cancellation.Token), CancellationToken.None);
    }

    /// <summary>How often the current command is resent.</summary>
    public int UpdateRateHz { get; }

    /// <summary>Whether the stream is still pumping.</summary>
    public bool IsRunning => !_disposed;

    /// <summary>
    /// Whether a configured command timeout has elapsed and the stream is holding the robot stopped.
    /// </summary>
    /// <remarks>Always false when no timeout was configured, which is the default.</remarks>
    public bool IsWatchdogTripped
    {
        get
        {
            lock (_commandLock)
            {
                return _watchdogTripped;
            }
        }
    }

    /// <summary>
    /// The velocity being maintained. Assigning refreshes the watchdog.
    /// </summary>
    public VelocityCommand Command
    {
        get
        {
            lock (_commandLock)
            {
                return _command;
            }
        }

        set
        {
            lock (_commandLock)
            {
                _command = value;
                _lastUpdate = DateTimeOffset.UtcNow;
                _watchdogTripped = false;
            }
        }
    }

    /// <summary>Commands an immediate stop without ending the stream.</summary>
    public void Stop() => Command = VelocityCommand.Stop;

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / UpdateRateHz));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                VelocityCommand toSend;

                lock (_commandLock)
                {
                    // Only when the caller asked for an expiry. Holding one velocity for a few
                    // seconds is ordinary — a dance step, a leg of a patrol — and expiring it by
                    // default made the stream fight its own pump: the robot stopped half a second
                    // after every command and the log filled with watchdog warnings.
                    //
                    // The guarantee that matters does not depend on this. The pump is what satisfies
                    // the robot's own command expiry, so if this process dies or the stream is
                    // disposed, the requests stop arriving and the robot stops on its own.
                    bool expired = _commandTimeout is { } timeout
                        && DateTimeOffset.UtcNow - _lastUpdate > timeout;

                    if (expired && !_command.IsStop)
                    {
                        if (!_watchdogTripped)
                        {
                            _logger.LogWarning(
                                "Command not refreshed for {Timeout:0} ms; stopping the robot.",
                                _commandTimeout!.Value.TotalMilliseconds);
                            _watchdogTripped = true;
                        }

                        _command = VelocityCommand.Stop;
                    }

                    toSend = _command;
                }

                _client.Move(toSend);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Velocity stream pump failed; the robot may continue on its last command.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();

        try
        {
            _pumpTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutdown race; the stop below is what matters.
        }

        // Leaving a robot in motion because the stream object went out of scope is not acceptable,
        // so a stop is sent unconditionally on the way out.
        try
        {
            _client.Move(VelocityCommand.Stop);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send the final stop command while disposing the velocity stream.");
        }

        _cancellation.Dispose();
    }
}
