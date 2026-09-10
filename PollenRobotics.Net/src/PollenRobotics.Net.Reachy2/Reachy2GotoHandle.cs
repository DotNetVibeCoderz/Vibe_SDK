using PollenRobotics.Net.Core;

namespace PollenRobotics.Net.Reachy2;

/// <summary>How a queued movement ended, or where it is.</summary>
public enum GotoOutcome
{
    /// <summary>Queued but not started.</summary>
    Accepted,

    /// <summary>Running now.</summary>
    Executing,

    /// <summary>Finished normally.</summary>
    Succeeded,

    /// <summary>Cancelled before it finished.</summary>
    Canceled,

    /// <summary>The robot gave up on it.</summary>
    Aborted,

    /// <summary>The robot does not recognise the id, usually because it has been discarded.</summary>
    Unknown,
}

/// <summary>
/// A handle on one queued movement.
/// </summary>
/// <remarks>
/// <para>
/// Reachy 2 queues movements per part rather than executing them as they arrive, so a
/// <c>GoTo</c> call returns immediately with an id and the motion happens later. That is what makes
/// it possible to stack a sequence up front, and it is also why "the call returned" says nothing
/// about where the arm is.
/// </para>
/// <para>
/// Await <see cref="WaitAsync"/> to follow the motion, or hold the handle and
/// <see cref="CancelAsync"/> it. Dropping the handle does not cancel anything - the robot keeps
/// going.
/// </para>
/// </remarks>
public sealed class Reachy2GotoHandle
{
    private readonly GoToService.GoToServiceClient _client;
    private readonly GoToId _id;
    private readonly TimeSpan _timeout;

    /// <summary>The robot's id for this movement.</summary>
    public int Id => _id.Id;

    internal Reachy2GotoHandle(GoToService.GoToServiceClient client, GoToId id, TimeSpan timeout)
    {
        _client = client;
        _id = id;
        _timeout = timeout;
    }

    /// <summary>Reads where the movement currently is.</summary>
    public async Task<GotoOutcome> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        GoToGoalStatus status = await _client.GetGoToStateAsync(
            _id, deadline: DateTime.UtcNow.Add(_timeout), cancellationToken: cancellationToken);

        return status.GoalStatus switch
        {
            GoalStatus.StatusAccepted => GotoOutcome.Accepted,
            GoalStatus.StatusExecuting or GoalStatus.StatusCanceling => GotoOutcome.Executing,
            GoalStatus.StatusSucceeded => GotoOutcome.Succeeded,
            GoalStatus.StatusCanceled => GotoOutcome.Canceled,
            GoalStatus.StatusAborted => GotoOutcome.Aborted,
            _ => GotoOutcome.Unknown,
        };
    }

    /// <summary>
    /// Waits for the movement to finish.
    /// </summary>
    /// <param name="timeout">How long to wait before giving up on it.</param>
    /// <param name="cancellationToken">
    /// Stops waiting. It does not cancel the movement - call <see cref="CancelAsync"/> for that.
    /// </param>
    /// <returns>How the movement ended.</returns>
    public async Task<GotoOutcome> WaitAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));

        while (DateTimeOffset.UtcNow < deadline)
        {
            GotoOutcome outcome = await GetStatusAsync(cancellationToken).ConfigureAwait(false);

            if (outcome is GotoOutcome.Succeeded or GotoOutcome.Canceled or GotoOutcome.Aborted)
            {
                return outcome;
            }

            // A queued goto reports Unknown once the robot has discarded its record of it, which it
            // does after completion. Treating that as still-pending would hang the caller forever.
            if (outcome == GotoOutcome.Unknown)
            {
                return GotoOutcome.Succeeded;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }

        throw new RobotCommandException($"Movement {Id} did not finish within {(timeout ?? TimeSpan.FromSeconds(60)).TotalSeconds:0.#}s.");
    }

    /// <summary>Cancels the movement. The part stops where it is.</summary>
    public async Task<bool> CancelAsync(CancellationToken cancellationToken = default)
    {
        GoToAck ack = await _client.CancelGoToAsync(
            _id, deadline: DateTime.UtcNow.Add(_timeout), cancellationToken: cancellationToken);

        return ack.Ack;
    }
}
