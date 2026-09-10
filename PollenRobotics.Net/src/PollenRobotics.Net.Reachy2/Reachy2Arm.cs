using PollenRobotics.Net.Core;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Safety;
using PollenRobotics.Net.Kinematics;
using Reachy.Part;
using Reachy.Part.Arm;

namespace PollenRobotics.Net.Reachy2;

/// <summary>
/// One of Reachy 2's seven-axis arms.
/// </summary>
/// <remarks>
/// <para>
/// The arm is redundant - seven joints for a six-dimensional task - so a Cartesian goal has an
/// infinite family of joint solutions. The robot resolves that with a preferred elbow angle and a
/// continuity mode, which is why <see cref="GotoPoseAsync"/> exposes both: switching to
/// <see cref="IKContinuousMode.Discrete"/> mid-trajectory is how an arm ends up flipping its elbow
/// through the torso between two nearby waypoints.
/// </para>
/// <para>
/// Prefer the robot's own IK (<see cref="ComputeInverseKinematicsAsync"/>) over the local
/// <see cref="Chain"/>: the robot solves against its true model, the local chain against
/// approximate link lengths.
/// </para>
/// </remarks>
public sealed class Reachy2Arm
{
    private readonly ArmService.ArmServiceClient _client;
    private readonly GoToService.GoToServiceClient _goto;
    private readonly PartId _partId;
    private readonly JointLimitGuard _guard;
    private readonly string _prefix;
    private readonly TimeSpan _timeout;

    /// <summary>Which arm this is.</summary>
    public ArmSide Side { get; }

    /// <summary>The part name the robot knows this arm by, <c>r_arm</c> or <c>l_arm</c>.</summary>
    public string Name => _partId.Name;

    /// <summary>
    /// A local kinematic chain for this arm.
    /// </summary>
    /// <remarks>
    /// For previewing reachability and for the simulator. Uses approximate link geometry - see
    /// <see cref="ReachyArmChains"/>.
    /// </remarks>
    public SerialChain Chain { get; }

    /// <summary>Number of joints. Seven.</summary>
    public int JointCount => 7;

    internal Reachy2Arm(
        ArmService.ArmServiceClient client,
        GoToService.GoToServiceClient gotoClient,
        PartId partId,
        ArmSide side,
        JointLimitGuard guard,
        TimeSpan timeout)
    {
        _client = client;
        _goto = gotoClient;
        _partId = partId;
        Side = side;
        _guard = guard;
        _prefix = side == ArmSide.Right ? "r_arm" : "l_arm";
        _timeout = timeout;
        Chain = ReachyArmChains.Arm(side);
    }

    /// <summary>Energises the arm. Nothing moves until this has run.</summary>
    public async Task TurnOnAsync(CancellationToken cancellationToken = default) =>
        await _client.TurnOnAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

    /// <summary>Makes the arm compliant. It will sag under its own weight.</summary>
    public async Task TurnOffAsync(CancellationToken cancellationToken = default) =>
        await _client.TurnOffAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

    /// <summary>Reads the present joint positions in radians, shoulder to wrist.</summary>
    public async Task<double[]> GetJointPositionsAsync(CancellationToken cancellationToken = default)
    {
        ArmPosition position = await _client.GetJointPositionAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);
        return ProtoConversions.FromArmPosition(position);
    }

    /// <summary>Reads the end-effector pose in the robot frame.</summary>
    public async Task<Pose> GetPoseAsync(CancellationToken cancellationToken = default)
    {
        Reachy.Kinematics.Matrix4x4 matrix = await _client.GetCartesianPositionAsync(
            _partId, deadline: Deadline(), cancellationToken: cancellationToken);
        return ProtoConversions.ToPose(matrix);
    }

    /// <summary>
    /// Moves the joints to the given angles over <paramref name="duration"/>.
    /// </summary>
    /// <param name="joints">Seven angles in radians, shoulder pitch through wrist yaw.</param>
    /// <param name="duration">Motion time. Null asks the robot for its default.</param>
    /// <param name="interpolation">Easing. Minimum jerk unless you have a reason.</param>
    /// <param name="cancellationToken">Cancels the call, not the motion.</param>
    /// <returns>A handle on the queued movement.</returns>
    public async Task<Reachy2GotoHandle> GotoJointsAsync(
        double[] joints,
        TimeSpan? duration = null,
        InterpolationMode interpolation = InterpolationMode.MinimumJerk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(joints);

        if (joints.Length != 7)
        {
            throw new ArgumentException($"A Reachy 2 arm has 7 joints, got {joints.Length}.", nameof(joints));
        }

        double[] guarded = [.. joints];
        ApplyLimits(guarded);

        var request = new GoToRequest
        {
            JointsGoal = new JointsGoal
            {
                ArmJointGoal = new ArmJointGoal
                {
                    Id = _partId,
                    JointsGoal = ProtoConversions.ToArmPosition(guarded),
                    Duration = ProtoConversions.Wrap(duration?.TotalSeconds),
                },
            },
            InterpolationMode = new GoToInterpolation { InterpolationType = interpolation },
        };

        GoToId id = await _goto.GoToJointsAsync(request, deadline: Deadline(), cancellationToken: cancellationToken);
        return new Reachy2GotoHandle(_goto, id, _timeout);
    }

    /// <summary>
    /// Moves the end effector to a pose, letting the robot solve the joint angles.
    /// </summary>
    /// <param name="pose">Target pose in the robot frame.</param>
    /// <param name="duration">Motion time.</param>
    /// <param name="constrainedMode">
    /// <see cref="IKConstrainedMode.LowElbow"/> keeps the elbow down, which is what you want for
    /// anything happening on a table in front of the robot.
    /// </param>
    /// <param name="continuousMode">
    /// Keep this <see cref="IKContinuousMode.Continuous"/> along a trajectory so successive
    /// solutions stay near one another.
    /// </param>
    /// <param name="interpolation">Easing along the path.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A handle on the queued movement.</returns>
    public async Task<Reachy2GotoHandle> GotoPoseAsync(
        Pose pose,
        TimeSpan? duration = null,
        IKConstrainedMode constrainedMode = IKConstrainedMode.LowElbow,
        IKContinuousMode continuousMode = IKContinuousMode.Continuous,
        InterpolationMode interpolation = InterpolationMode.MinimumJerk,
        CancellationToken cancellationToken = default)
    {
        var request = new GoToRequest
        {
            CartesianGoal = new CartesianGoal
            {
                ArmCartesianGoal = BuildCartesianGoal(pose, duration, constrainedMode, continuousMode),
            },
            InterpolationMode = new GoToInterpolation { InterpolationType = interpolation },
        };

        GoToId id = await _goto.GoToCartesianAsync(request, deadline: Deadline(), cancellationToken: cancellationToken);
        return new Reachy2GotoHandle(_goto, id, _timeout);
    }

    /// <summary>
    /// Sends a Cartesian target straight to the arm, bypassing the movement queue.
    /// </summary>
    /// <remarks>
    /// This is the path for teleoperation and for streaming a trajectory computed elsewhere: it
    /// does not queue, so the newest target simply supersedes the last. Mixing it with
    /// <see cref="GotoPoseAsync"/> means two things are commanding one arm, and the result depends
    /// on which message happens to arrive last.
    /// </remarks>
    public async Task SetPoseAsync(
        Pose pose,
        IKConstrainedMode constrainedMode = IKConstrainedMode.LowElbow,
        IKContinuousMode continuousMode = IKContinuousMode.Continuous,
        CancellationToken cancellationToken = default)
    {
        ArmCartesianGoal goal = BuildCartesianGoal(pose, duration: null, constrainedMode, continuousMode);
        await _client.SendArmCartesianGoalAsync(goal, deadline: Deadline(), cancellationToken: cancellationToken);
    }

    private ArmCartesianGoal BuildCartesianGoal(
        Pose pose,
        TimeSpan? duration,
        IKConstrainedMode constrainedMode,
        IKContinuousMode continuousMode) => new()
        {
            Id = _partId,
            GoalPose = ProtoConversions.ToMatrix(pose),
            ConstrainedMode = constrainedMode,
            ContinuousMode = continuousMode,
            Duration = ProtoConversions.Wrap(duration?.TotalSeconds),
        };

    /// <summary>Moves the end effector by an offset in the robot frame, keeping its orientation.</summary>
    public async Task<Reachy2GotoHandle> TranslateByAsync(
        double x,
        double y,
        double z,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        Pose current = await GetPoseAsync(cancellationToken).ConfigureAwait(false);
        var target = new Pose(
            current.Position + new System.Numerics.Vector3((float)x, (float)y, (float)z),
            current.Orientation);

        return await GotoPoseAsync(target, duration, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rotates the end effector in place by roll/pitch/yaw.</summary>
    public async Task<Reachy2GotoHandle> RotateByAsync(
        Angle roll,
        Angle pitch,
        Angle yaw,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        Pose current = await GetPoseAsync(cancellationToken).ConfigureAwait(false);
        var delta = new Pose(System.Numerics.Vector3.Zero, Rotation.FromRpy(roll, pitch, yaw));

        return await GotoPoseAsync(current.Compose(delta), duration, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks the robot to solve forward kinematics for a joint vector.</summary>
    public async Task<Pose> ComputeForwardKinematicsAsync(double[] joints, CancellationToken cancellationToken = default)
    {
        var request = new ArmFKRequest
        {
            Id = _partId,
            Position = ProtoConversions.ToArmPosition(joints),
        };

        ArmFKSolution solution = await _client.ComputeArmFKAsync(request, deadline: Deadline(), cancellationToken: cancellationToken);

        return solution.Success
            ? ProtoConversions.ToPose(solution.EndEffector?.Pose)
            : throw new RobotCommandException($"{Name}: forward kinematics failed for the given joint vector.", "ComputeArmFK");
    }

    /// <summary>
    /// Asks the robot to solve inverse kinematics for a target pose.
    /// </summary>
    /// <param name="pose">Target end-effector pose.</param>
    /// <param name="seed">
    /// Starting configuration. Pass the arm's present joints - seeding from zero gives a
    /// geometrically valid solution that gets there by an unacceptable route.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Seven joint angles, or null when the pose is unreachable.</returns>
    public async Task<double[]?> ComputeInverseKinematicsAsync(Pose pose, double[]? seed = null, CancellationToken cancellationToken = default)
    {
        seed ??= await GetJointPositionsAsync(cancellationToken).ConfigureAwait(false);

        var request = new ArmIKRequest
        {
            Id = _partId,
            Target = new ArmEndEffector { Pose = ProtoConversions.ToMatrix(pose) },
            Q0 = ProtoConversions.ToArmPosition(seed),
        };

        ArmIKSolution solution = await _client.ComputeArmIKAsync(request, deadline: Deadline(), cancellationToken: cancellationToken);
        return solution.Success ? ProtoConversions.FromArmPosition(solution.ArmPosition) : null;
    }

    /// <summary>True when the arm can reach the pose, per the robot's own solver.</summary>
    public async Task<bool> IsReachableAsync(Pose pose, CancellationToken cancellationToken = default) =>
        await ComputeInverseKinematicsAsync(pose, cancellationToken: cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Caps joint speed as a percentage of maximum.</summary>
    public async Task SetSpeedLimitAsync(int percent, CancellationToken cancellationToken = default)
    {
        if (percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Speed limit is a percentage.");
        }

        await _client.SetSpeedLimitAsync(
            new SpeedLimitRequest { Id = _partId, Limit = (uint)percent }, deadline: Deadline(), cancellationToken: cancellationToken);
    }

    /// <summary>Caps joint torque as a percentage of maximum.</summary>
    public async Task SetTorqueLimitAsync(int percent, CancellationToken cancellationToken = default)
    {
        if (percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Torque limit is a percentage.");
        }

        await _client.SetTorqueLimitAsync(
            new TorqueLimitRequest { Id = _partId, Limit = (uint)percent }, deadline: Deadline(), cancellationToken: cancellationToken);
    }

    /// <summary>Reads the actuator temperatures, shoulder then elbow then wrist.</summary>
    public async Task<IReadOnlyList<double>> GetTemperaturesAsync(CancellationToken cancellationToken = default)
    {
        ArmTemperatures temperatures = await _client.GetTemperaturesAsync(_partId, deadline: Deadline(), cancellationToken: cancellationToken);

        return
        [
            ProtoConversions.Unwrap(temperatures.ShoulderTemperature?.Motor1),
            ProtoConversions.Unwrap(temperatures.ShoulderTemperature?.Motor2),
            ProtoConversions.Unwrap(temperatures.ElbowTemperature?.Motor1),
            ProtoConversions.Unwrap(temperatures.ElbowTemperature?.Motor2),
            ProtoConversions.Unwrap(temperatures.WristTemperature?.Motor1),
            ProtoConversions.Unwrap(temperatures.WristTemperature?.Motor2),
            ProtoConversions.Unwrap(temperatures.WristTemperature?.Motor3),
        ];
    }

    private void ApplyLimits(Span<double> joints)
    {
        for (int i = 0; i < joints.Length; i++)
        {
            joints[i] = _guard.Apply($"{_prefix}.{JointName(i)}", joints[i]);
        }
    }

    private static string JointName(int index) => index switch
    {
        0 => "shoulder.pitch",
        1 => "shoulder.roll",
        2 => "elbow.yaw",
        3 => "elbow.pitch",
        4 => "wrist.roll",
        5 => "wrist.pitch",
        6 => "wrist.yaw",
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "An arm has seven joints."),
    };

    private DateTime? Deadline() => DateTime.UtcNow.Add(_timeout);
}
