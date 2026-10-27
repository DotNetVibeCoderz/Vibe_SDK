using PollenRobotics.Net.AmazingHand;
using PollenRobotics.Net.Simulation.Robots;

namespace PollenRobotics.Net.Simulation.Transports;

/// <summary>
/// Drives a <see cref="SimulatedAmazingHand"/> through the transport interface the real hand would
/// use.
/// </summary>
/// <remarks>
/// The ~2 ms of latency is deliberate, as in the other simulated transports: everything here could
/// complete synchronously, and then code that works against the simulator would deadlock the first
/// time it met a real round trip.
/// </remarks>
public sealed class SimulatedAmazingHandTransport(SimulatedAmazingHand robot, SimulationEngine engine)
    : IAmazingHandTransport
{
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(2);

    private readonly SimulatedAmazingHand _robot = robot ?? throw new ArgumentNullException(nameof(robot));
    private readonly SimulationEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <inheritdoc />
    public string Endpoint => "simulation://amazing-hand";

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);

        if (!_engine.IsRunning)
        {
            await _engine.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        Task.Delay(Latency, cancellationToken);

    /// <inheritdoc />
    public async Task SetMotorAnglesAsync(IReadOnlyList<double> radians, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
        _robot.SetMotorAngles(radians);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<double>> GetMotorAnglesAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
        return _robot.MotorAngles;
    }

    /// <inheritdoc />
    public async Task SetTorqueAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
        _robot.TorqueEnabled = enabled;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
