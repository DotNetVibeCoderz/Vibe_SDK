using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Kinematics;
using Reachy.Part;
using Reachy.Part.Hand;

namespace PollenRobotics.Net.Reachy2;

/// <summary>
/// A parallel gripper on the end of one arm.
/// </summary>
/// <remarks>
/// Opening is expressed as a percentage rather than as a jaw angle because that is what the API
/// takes and what an application actually reasons about. Closing on an object stalls the motor,
/// which is normal - the gripper is meant to hold - but leaving it stalled indefinitely heats it,
/// so <see cref="GetStateAsync"/> is worth watching in anything that grips for a long time.
/// </remarks>
public sealed class Reachy2Gripper
{
    private readonly HandService.HandServiceClient _client;
    private readonly PartId _partId;
    private readonly TimeSpan _timeout;

    /// <summary>Which arm this gripper is on.</summary>
    public ArmSide Side { get; }

    /// <summary>The part name the robot knows this hand by.</summary>
    public string Name => _partId.Name;

    internal Reachy2Gripper(HandService.HandServiceClient client, PartId partId, ArmSide side, TimeSpan timeout)
    {
        _client = client;
        _partId = partId;
        Side = side;
        _timeout = timeout;
    }

    /// <summary>Energises the gripper.</summary>
    public async Task TurnOnAsync(CancellationToken cancellationToken = default) =>
        await _client.TurnOnAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

    /// <summary>Makes the gripper compliant. Anything it is holding drops.</summary>
    public async Task TurnOffAsync(CancellationToken cancellationToken = default) =>
        await _client.TurnOffAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

    /// <summary>Opens fully.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default) =>
        await _client.OpenHandAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

    /// <summary>Closes fully, gripping whatever is in the way.</summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default) =>
        await _client.CloseHandAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

    /// <summary>Sets the opening as a percentage, 0 closed and 100 fully open.</summary>
    public async Task SetOpeningAsync(double percent, CancellationToken cancellationToken = default)
    {
        if (percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Opening is a percentage.");
        }

        var request = new HandPositionRequest
        {
            Id = _partId,
            Position = new HandPosition
            {
                ParallelGripper = new ParallelGripperPosition
                {
                    OpeningPercentage = ProtoConversions.Wrap(percent),
                },
            },
        };

        await _client.SetHandPositionAsync(request, deadline: Deadline(), cancellationToken: cancellationToken);
    }

    /// <summary>Sets the jaw position directly, in radians.</summary>
    public async Task SetPositionAsync(Angle position, CancellationToken cancellationToken = default)
    {
        var request = new HandPositionRequest
        {
            Id = _partId,
            Position = new HandPosition
            {
                ParallelGripper = new ParallelGripperPosition
                {
                    Position = ProtoConversions.Wrap(position.Radians),
                },
            },
        };

        await _client.SetHandPositionAsync(request, deadline: Deadline(), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Reads opening, force and whether the gripper reports holding something.
    /// </summary>
    /// <remarks>
    /// Read through <c>GetState</c> rather than the <c>GetForce</c> RPC: that RPC exists in the API
    /// but its <c>Force</c> message is still empty upstream, so it returns nothing useful.
    /// <c>HandState</c> carries the same information and is populated.
    /// </remarks>
    public async Task<(double OpeningPercent, double Force, bool IsHolding)> GetStateAsync(CancellationToken cancellationToken = default)
    {
        HandState state = await _client.GetStateAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

        return (
            ProtoConversions.Unwrap(state.Opening),
            ProtoConversions.Unwrap(state.Force),
            state.HoldingObject ?? false);
    }

    /// <summary>True when the gripper reports holding something rather than being closed on air.</summary>
    public async Task<bool> IsHoldingAsync(CancellationToken cancellationToken = default) =>
        (await GetStateAsync(cancellationToken).ConfigureAwait(false)).IsHolding;

    private DateTime? Deadline() => DateTime.UtcNow.Add(_timeout);
}
