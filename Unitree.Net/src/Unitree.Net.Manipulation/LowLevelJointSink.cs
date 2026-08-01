using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Messages.Go;

namespace Unitree.Net.Manipulation;

/// <summary>
/// Routes arm commands through a <see cref="LowLevelController"/>.
/// </summary>
/// <remarks>
/// Applies to platforms whose arm joints live in the same motor-slot space as the rest of the robot.
/// The controller's safety envelope still applies to every command that passes through.
/// </remarks>
public sealed class LowLevelJointSink(LowLevelController controller) : IJointCommandSink
{
    private readonly LowLevelController _controller =
        controller ?? throw new ArgumentNullException(nameof(controller));

    /// <inheritdoc />
    public int JointCount => RobotModelInfo.GoMotorSlots;

    /// <inheritdoc />
    public void SetJointPosition(int jointIndex, float position, float kp, float kd, float feedForwardTorque) =>
        _controller.SetJointPosition(jointIndex, position, kp, kd, feedForwardTorque);

    /// <inheritdoc />
    public bool TryGetJointPosition(int jointIndex, out float position)
    {
        if ((uint)jointIndex >= JointCount || !_controller.TryGetState(out LowState state))
        {
            position = 0f;
            return false;
        }

        position = state.MotorState[jointIndex].Q;
        return true;
    }
}

/// <summary>
/// An in-memory joint sink for tests and dry runs.
/// </summary>
/// <remarks>
/// Records every command and reports back the last commanded position as the measured one, so a
/// trajectory can be executed end to end with no robot and no transport present.
/// </remarks>
public sealed class RecordingJointSink(int jointCount) : IJointCommandSink
{
    private readonly float[] _positions = new float[jointCount];
    private readonly bool[] _commanded = new bool[jointCount];
    private readonly List<JointCommandRecord> _history = [];
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public int JointCount { get; } = jointCount > 0
        ? jointCount
        : throw new ArgumentOutOfRangeException(nameof(jointCount), jointCount, "Joint count must be positive.");

    /// <summary>Every command received, in order.</summary>
    public IReadOnlyList<JointCommandRecord> History
    {
        get
        {
            lock (_lock)
            {
                return [.. _history];
            }
        }
    }

    /// <summary>Seeds a joint's measured position, as if the robot were reporting it.</summary>
    public void SeedPosition(int jointIndex, float position)
    {
        lock (_lock)
        {
            _positions[jointIndex] = position;
            _commanded[jointIndex] = true;
        }
    }

    /// <inheritdoc />
    public void SetJointPosition(int jointIndex, float position, float kp, float kd, float feedForwardTorque)
    {
        lock (_lock)
        {
            _positions[jointIndex] = position;
            _commanded[jointIndex] = true;
            _history.Add(new JointCommandRecord(jointIndex, position, kp, kd, feedForwardTorque));
        }
    }

    /// <inheritdoc />
    public bool TryGetJointPosition(int jointIndex, out float position)
    {
        lock (_lock)
        {
            position = _positions[jointIndex];
            return _commanded[jointIndex];
        }
    }
}

/// <summary>
/// One recorded joint command.
/// </summary>
/// <param name="JointIndex">Which joint was commanded.</param>
/// <param name="Position">Commanded position, radians.</param>
/// <param name="Kp">Position gain.</param>
/// <param name="Kd">Damping gain.</param>
/// <param name="FeedForwardTorque">Feed-forward torque, N·m.</param>
public readonly record struct JointCommandRecord(
    int JointIndex,
    float Position,
    float Kp,
    float Kd,
    float FeedForwardTorque);
