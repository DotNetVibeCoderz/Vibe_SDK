using Microsoft.Extensions.Logging;
using PollenRobotics.Net.Core;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Core.Safety;

namespace PollenRobotics.Net.AmazingHand;

/// <summary>
/// What a transport has to provide for <see cref="AmazingHandClient"/> to drive a hand.
/// </summary>
/// <remarks>
/// As with the other robots, the client never touches a wire. The simulated transport and any
/// future hardware transport both implement this, and the client cannot tell them apart.
/// </remarks>
public interface IAmazingHandTransport : IAsyncDisposable
{
    /// <summary>A human-readable endpoint, for logs and status panels.</summary>
    string Endpoint { get; }

    /// <summary>Opens the connection.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes it.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Commands all eight motors, in catalogue order, in radians.</summary>
    Task SetMotorAnglesAsync(IReadOnlyList<double> radians, CancellationToken cancellationToken = default);

    /// <summary>Reads all eight measured motor angles, in catalogue order, in radians.</summary>
    Task<IReadOnlyList<double>> GetMotorAnglesAsync(CancellationToken cancellationToken = default);

    /// <summary>Powers the servos on or off. A relaxed hand backdrives freely.</summary>
    Task SetTorqueAsync(bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
/// Drives an Amazing Hand.
/// </summary>
/// <remarks>
/// <para>
/// The hand is four parallel-linkage fingers and nothing else: no thumb, no wrist, no palm joint.
/// Work in <see cref="AmazingHandPose"/> rather than in motor angles - the two servos on a finger
/// are not a knuckle and a tip, and treating them that way produces postures nobody meant.
/// </para>
/// <para>
/// <b>There is no hardware transport in this SDK.</b> Pollen drives the hand over a Feetech serial
/// bus from their own Rust tool, and they publish no network protocol for it; inventing one here
/// would be a wire format nothing could check. What ships is the client, the posture model and the
/// simulation. See <c>docs/amazing-hand.md</c>.
/// </para>
/// </remarks>
public sealed class AmazingHandClient : IAsyncDisposable
{
    private readonly IAmazingHandTransport _transport;
    private readonly bool _ownsTransport;
    private readonly JointLimitGuard _guard;
    private readonly ILogger<AmazingHandClient>? _logger;

    /// <summary>Creates a client over a transport.</summary>
    public AmazingHandClient(
        IAmazingHandTransport transport,
        HandSide side = HandSide.Right,
        RobotSafetyOptions? safety = null,
        bool ownsTransport = false,
        ILogger<AmazingHandClient>? logger = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ownsTransport = ownsTransport;
        _logger = logger;

        Side = side;
        Description = RobotCatalog.AmazingHand;
        _guard = new JointLimitGuard(Description, safety ?? RobotSafetyOptions.Default);
    }

    /// <summary>Which hand this is.</summary>
    public HandSide Side { get; }

    /// <summary>The joint table this client validates against.</summary>
    public RobotDescription Description { get; }

    /// <summary>Where the transport is pointed.</summary>
    public string Endpoint => _transport.Endpoint;

    /// <summary>Opens the connection.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Amazing Hand ({Side}) connected on {Endpoint}.", Side, Endpoint);
    }

    /// <summary>Powers the servos.</summary>
    public Task TurnOnAsync(CancellationToken cancellationToken = default) =>
        _transport.SetTorqueAsync(true, cancellationToken);

    /// <summary>Cuts power. The fingers go limp and can be backdriven by hand.</summary>
    public Task RelaxAsync(CancellationToken cancellationToken = default) =>
        _transport.SetTorqueAsync(false, cancellationToken);

    /// <summary>Commands a whole-hand posture.</summary>
    public Task SetPoseAsync(AmazingHandPose pose, CancellationToken cancellationToken = default)
    {
        double[] angles = pose.ToJointAngles(Side);
        _guard.Apply(angles);

        return _transport.SetMotorAnglesAsync(angles, cancellationToken);
    }

    /// <summary>Commands one finger and leaves the others where they are.</summary>
    /// <param name="finger">1 to 4, index to little, matching the servo numbering.</param>
    /// <param name="pose">The posture to put that finger in.</param>
    /// <param name="cancellationToken">Cancels the read and the command.</param>
    public async Task SetFingerAsync(int finger, FingerPose pose, CancellationToken cancellationToken = default)
    {
        if (finger is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(finger), finger, "The hand has fingers 1 to 4.");
        }

        AmazingHandPose current = await GetPoseAsync(cancellationToken).ConfigureAwait(false);
        FingerPose[] fingers = current.Fingers;
        fingers[finger - 1] = pose;

        await SetPoseAsync(new AmazingHandPose(fingers[0], fingers[1], fingers[2], fingers[3]), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the measured posture.</summary>
    public async Task<AmazingHandPose> GetPoseAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<double> angles = await _transport.GetMotorAnglesAsync(cancellationToken).ConfigureAwait(false);
        return AmazingHandPose.FromJointAngles(angles, Side);
    }

    /// <summary>Reads the measured motor angles, in catalogue order.</summary>
    public Task<IReadOnlyList<double>> GetMotorAnglesAsync(CancellationToken cancellationToken = default) =>
        _transport.GetMotorAnglesAsync(cancellationToken);

    /// <summary>
    /// Moves to a posture over a duration, instead of jumping to it.
    /// </summary>
    /// <remarks>
    /// The servos have no on-board interpolation, so a single position command is a step input and
    /// the finger arrives with a snap. Interpolating here is the difference between a hand that
    /// closes and a hand that slams.
    /// </remarks>
    public async Task GotoPoseAsync(
        AmazingHandPose target,
        TimeSpan duration,
        InterpolationMethod method = InterpolationMethod.MinJerk,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            await SetPoseAsync(target, cancellationToken).ConfigureAwait(false);
            return;
        }

        double[] from = (await GetPoseAsync(cancellationToken).ConfigureAwait(false)).ToJointAngles(Side);
        double[] to = target.ToJointAngles(Side);
        double[] frame = new double[from.Length];

        var loop = new RealtimeLoop(100);
        DateTimeOffset started = DateTimeOffset.UtcNow;

        // RealtimeLoop runs until its token is cancelled, so finishing the move means cancelling a
        // token of our own. The linked source keeps the caller's cancellation working too.
        using var finished = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await loop.RunAsync(async (_, token) =>
            {
                double t = (DateTimeOffset.UtcNow - started) / duration;

                if (t >= 1)
                {
                    await _transport.SetMotorAnglesAsync(to, token).ConfigureAwait(false);
                    await finished.CancelAsync().ConfigureAwait(false);
                    return;
                }

                double eased = Interpolation.Evaluate(method, Math.Clamp(t, 0, 1));

                for (int i = 0; i < frame.Length; i++)
                {
                    frame[i] = from[i] + ((to[i] - from[i]) * eased);
                }

                _guard.Apply(frame);
                await _transport.SetMotorAnglesAsync(frame, token).ConfigureAwait(false);
            }, finished.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own token: the move reached its target. The caller's cancellation still throws.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _transport.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Disconnecting the Amazing Hand failed.");
        }

        if (_ownsTransport)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
