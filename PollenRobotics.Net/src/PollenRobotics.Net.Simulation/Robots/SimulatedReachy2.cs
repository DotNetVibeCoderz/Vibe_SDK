using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Kinematics;

namespace PollenRobotics.Net.Simulation.Robots;

/// <summary>
/// A kinematic model of Reachy 2: both arms, the neck, the antennas and the grippers.
/// </summary>
/// <remarks>
/// <para>
/// Movements queue per part, as they do on the robot, so a sequence of gotos issued back to back
/// plays in order rather than each one replacing the last. That queueing is the single most
/// surprising thing about Reachy 2's API to someone coming from a robot that executes immediately,
/// and it is worth having the simulator reproduce it.
/// </para>
/// <para>
/// Inverse kinematics here uses <see cref="ReachyArmChains"/>, whose link lengths approximate the
/// real ones. A pose that solves here may be marginally out of reach on hardware and vice versa
/// near the edge of the workspace.
/// </para>
/// </remarks>
public sealed class SimulatedReachy2 : ISimulatedRobot
{
    /// <summary>One queued movement for one part.</summary>
    private sealed class QueuedMove
    {
        public required int[] JointIndices { get; init; }
        public required double[] From { get; init; }
        public required double[] To { get; init; }
        public required TimeSpan Duration { get; init; }
        public InterpolationMethod Method { get; init; } = InterpolationMethod.MinJerk;
        public TimeSpan Elapsed { get; set; }
        public int Id { get; init; }
    }

    private readonly Lock _gate = new();
    private readonly double[] _joints = new double[RobotCatalog.Reachy2.JointCount];
    private readonly Dictionary<string, Queue<QueuedMove>> _queues = [];
    private readonly Dictionary<string, QueuedMove?> _playing = [];
    private readonly HashSet<string> _energised = [];

    private readonly SerialChain _rightArm = ReachyArmChains.Arm(ArmSide.Right);
    private readonly SerialChain _leftArm = ReachyArmChains.Arm(ArmSide.Left);

    private TimeSpan _clock;
    private int _nextMoveId = 1;

    /// <inheritdoc />
    public RobotKind Kind => RobotKind.Reachy2;

    /// <inheritdoc />
    public RobotDescription Description => RobotCatalog.Reachy2;

    /// <summary>The part names this model exposes.</summary>
    public static IReadOnlyList<string> PartNames { get; } = ["r_arm", "l_arm", "head", "r_hand", "l_hand"];

    /// <summary>True when the named part is energised.</summary>
    public bool IsOn(string part) => _energised.Contains(part);

    /// <summary>Energises a part.</summary>
    public void TurnOn(string part)
    {
        lock (_gate)
        {
            _energised.Add(part);
        }
    }

    /// <summary>
    /// De-energises a part.
    /// </summary>
    /// <remarks>Abandons anything queued for it, which is what losing torque means in practice.</remarks>
    public void TurnOff(string part)
    {
        lock (_gate)
        {
            _energised.Remove(part);
            _queues.Remove(part);
            _playing.Remove(part);
        }
    }

    /// <summary>Queues a joint-space movement for one part.</summary>
    /// <param name="part">Part name, e.g. <c>r_arm</c>.</param>
    /// <param name="jointNames">Which joints this movement drives.</param>
    /// <param name="targets">Target angles in radians, one per named joint.</param>
    /// <param name="duration">Motion time.</param>
    /// <param name="method">Easing.</param>
    /// <returns>An id that <see cref="GetMoveStatus"/> and <see cref="CancelMove"/> accept.</returns>
    public int QueueJointMove(
        string part,
        IReadOnlyList<string> jointNames,
        IReadOnlyList<double> targets,
        TimeSpan duration,
        InterpolationMethod method = InterpolationMethod.MinJerk)
    {
        if (jointNames.Count != targets.Count)
        {
            throw new ArgumentException("One target per joint, please.", nameof(targets));
        }

        lock (_gate)
        {
            if (!_energised.Contains(part))
            {
                // A compliant part accepts the command and does nothing, exactly as on the robot.
                return 0;
            }

            int[] indices = new int[jointNames.Count];
            double[] from = new double[jointNames.Count];

            for (int i = 0; i < jointNames.Count; i++)
            {
                JointDescriptor joint = Description[jointNames[i]];
                indices[i] = joint.Index;
                from[i] = _joints[joint.Index];
            }

            double[] to = new double[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                to[i] = Description.Joints[indices[i]].Clamp(Angle.FromRadians(targets[i])).Radians;
            }

            var move = new QueuedMove
            {
                JointIndices = indices,
                From = from,
                To = to,
                Duration = duration <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : duration,
                Method = method,
                Id = _nextMoveId++,
            };

            if (!_queues.TryGetValue(part, out Queue<QueuedMove>? queue))
            {
                queue = new Queue<QueuedMove>();
                _queues[part] = queue;
            }

            queue.Enqueue(move);
            return move.Id;
        }
    }

    /// <summary>Queues a Cartesian movement for one arm, solving IK locally.</summary>
    /// <returns>The movement id, or 0 when the pose is unreachable.</returns>
    public int QueueArmPose(ArmSide side, Pose pose, TimeSpan duration, InterpolationMethod method = InterpolationMethod.MinJerk)
    {
        string part = side == ArmSide.Right ? "r_arm" : "l_arm";
        SerialChain chain = side == ArmSide.Right ? _rightArm : _leftArm;
        string prefix = part;

        string[] jointNames =
        [
            $"{prefix}.shoulder.pitch", $"{prefix}.shoulder.roll",
            $"{prefix}.elbow.yaw", $"{prefix}.elbow.pitch",
            $"{prefix}.wrist.roll", $"{prefix}.wrist.pitch", $"{prefix}.wrist.yaw",
        ];

        double[] seed = new double[7];
        lock (_gate)
        {
            for (int i = 0; i < 7; i++)
            {
                seed[i] = _joints[Description[jointNames[i]].Index];
            }
        }

        // Seeded from the arm's present configuration so successive waypoints stay on the same
        // branch of the solution family rather than flipping the elbow between them.
        double[]? solution = chain.SolveInverse(pose, seed);
        return solution is null ? 0 : QueueJointMove(part, jointNames, solution, duration, method);
    }

    /// <summary>Where a queued movement is: 0 unknown, 1 queued, 2 running, 3 finished.</summary>
    public int GetMoveStatus(int id)
    {
        lock (_gate)
        {
            foreach (QueuedMove? playing in _playing.Values)
            {
                if (playing?.Id == id)
                {
                    return 2;
                }
            }

            foreach (Queue<QueuedMove> queue in _queues.Values)
            {
                if (queue.Any(m => m.Id == id))
                {
                    return 1;
                }
            }

            // Unknown and finished are indistinguishable once the record is gone, and callers
            // treat both as done.
            return id > 0 && id < _nextMoveId ? 3 : 0;
        }
    }

    /// <summary>Cancels a queued or running movement.</summary>
    public bool CancelMove(int id)
    {
        lock (_gate)
        {
            foreach (string part in _playing.Keys.ToList())
            {
                if (_playing[part]?.Id == id)
                {
                    _playing[part] = null;
                    return true;
                }
            }

            foreach (string part in _queues.Keys.ToList())
            {
                Queue<QueuedMove> queue = _queues[part];
                if (!queue.Any(m => m.Id == id))
                {
                    continue;
                }

                _queues[part] = new Queue<QueuedMove>(queue.Where(m => m.Id != id));
                return true;
            }

            return false;
        }
    }

    /// <summary>Cancels everything, everywhere.</summary>
    public void CancelAll()
    {
        lock (_gate)
        {
            _queues.Clear();
            _playing.Clear();
        }
    }

    /// <summary>Sets a gripper opening as a percentage.</summary>
    public void SetGripper(ArmSide side, double percent)
    {
        lock (_gate)
        {
            JointDescriptor joint = Description[side == ArmSide.Right ? "r_arm.gripper" : "l_arm.gripper"];
            double span = joint.Upper.Radians - joint.Lower.Radians;
            _joints[joint.Index] = joint.Lower.Radians + (span * Math.Clamp(percent, 0, 100) / 100.0);
        }
    }

    /// <summary>Reads a gripper opening as a percentage.</summary>
    public double GetGripper(ArmSide side)
    {
        lock (_gate)
        {
            JointDescriptor joint = Description[side == ArmSide.Right ? "r_arm.gripper" : "l_arm.gripper"];
            double span = joint.Upper.Radians - joint.Lower.Radians;
            return span <= 0 ? 0 : (_joints[joint.Index] - joint.Lower.Radians) / span * 100.0;
        }
    }

    /// <summary>The end-effector pose of one arm.</summary>
    public Pose GetArmPose(ArmSide side)
    {
        SerialChain chain = side == ArmSide.Right ? _rightArm : _leftArm;
        string prefix = side == ArmSide.Right ? "r_arm" : "l_arm";

        Span<double> joints = stackalloc double[7];
        lock (_gate)
        {
            joints[0] = _joints[Description[$"{prefix}.shoulder.pitch"].Index];
            joints[1] = _joints[Description[$"{prefix}.shoulder.roll"].Index];
            joints[2] = _joints[Description[$"{prefix}.elbow.yaw"].Index];
            joints[3] = _joints[Description[$"{prefix}.elbow.pitch"].Index];
            joints[4] = _joints[Description[$"{prefix}.wrist.roll"].Index];
            joints[5] = _joints[Description[$"{prefix}.wrist.pitch"].Index];
            joints[6] = _joints[Description[$"{prefix}.wrist.yaw"].Index];
        }

        return chain.ForwardKinematics(joints);
    }

    /// <summary>Reads the joint angles for one arm.</summary>
    public double[] GetArmJoints(ArmSide side)
    {
        string prefix = side == ArmSide.Right ? "r_arm" : "l_arm";
        lock (_gate)
        {
            return
            [
                _joints[Description[$"{prefix}.shoulder.pitch"].Index],
                _joints[Description[$"{prefix}.shoulder.roll"].Index],
                _joints[Description[$"{prefix}.elbow.yaw"].Index],
                _joints[Description[$"{prefix}.elbow.pitch"].Index],
                _joints[Description[$"{prefix}.wrist.roll"].Index],
                _joints[Description[$"{prefix}.wrist.pitch"].Index],
                _joints[Description[$"{prefix}.wrist.yaw"].Index],
            ];
        }
    }

    /// <summary>Reads the neck orientation.</summary>
    public (Angle Roll, Angle Pitch, Angle Yaw) GetNeck()
    {
        lock (_gate)
        {
            return (
                Angle.FromRadians(_joints[Description["head.neck.roll"].Index]),
                Angle.FromRadians(_joints[Description["head.neck.pitch"].Index]),
                Angle.FromRadians(_joints[Description["head.neck.yaw"].Index]));
        }
    }

    /// <inheritdoc />
    public void Tick(TimeSpan delta)
    {
        lock (_gate)
        {
            _clock += delta;

            foreach (string part in PartNames)
            {
                AdvancePart(part, delta);
            }
        }
    }

    private void AdvancePart(string part, TimeSpan delta)
    {
        _playing.TryGetValue(part, out QueuedMove? current);

        if (current is null)
        {
            if (!_queues.TryGetValue(part, out Queue<QueuedMove>? queue) || queue.Count == 0)
            {
                return;
            }

            current = queue.Dequeue();

            // The queued From was captured when the move was enqueued, which may be several moves
            // ago. Re-read it now so a queued sequence chains from where the part actually is.
            for (int i = 0; i < current.JointIndices.Length; i++)
            {
                current.From[i] = _joints[current.JointIndices[i]];
            }

            _playing[part] = current;
        }

        current.Elapsed += delta;
        double t = Math.Clamp(current.Elapsed.TotalSeconds / current.Duration.TotalSeconds, 0, 1);
        double alpha = Interpolation.Evaluate(current.Method, t);

        for (int i = 0; i < current.JointIndices.Length; i++)
        {
            _joints[current.JointIndices[i]] = current.From[i] + ((current.To[i] - current.From[i]) * alpha);
        }

        if (t < 1)
        {
            return;
        }

        for (int i = 0; i < current.JointIndices.Length; i++)
        {
            _joints[current.JointIndices[i]] = current.To[i];
        }

        _playing[part] = null;
    }

    /// <inheritdoc />
    public SimulationSnapshot Snapshot()
    {
        lock (_gate)
        {
            bool moving = _playing.Values.Any(m => m is not null) || _queues.Values.Any(q => q.Count > 0);
            return new SimulationSnapshot(Kind, [.. _joints], Pose.Identity, _clock, moving);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_joints);
            _queues.Clear();
            _playing.Clear();
            _energised.Clear();
            _clock = TimeSpan.Zero;
            _nextMoveId = 1;
        }
    }
}
