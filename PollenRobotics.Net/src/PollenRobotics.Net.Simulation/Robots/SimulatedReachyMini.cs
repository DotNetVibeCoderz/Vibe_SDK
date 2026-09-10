using PollenRobotics.Net.Core;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Realtime;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.Kinematics;
using PollenRobotics.Net.ReachyMini;

namespace PollenRobotics.Net.Simulation.Robots;

/// <summary>
/// A kinematic model of Reachy Mini.
/// </summary>
/// <remarks>
/// <para>
/// Reproduces the parts of the daemon an application actually depends on: the head/antennas/body
/// split, daemon-side goto interpolation, the motor modes, the idle wobble and the yaw coupling
/// between head and body. What it does not reproduce is the servo dynamics - a commanded pose is
/// reached exactly, on time, every time. Real servos overshoot slightly and lag under load.
/// </para>
/// <para>
/// The head-versus-body yaw constraint is enforced here as it is on the robot, because an
/// application that only ever runs against a simulator with no limits will exceed it the first time
/// it meets hardware and never find out why the head stopped turning.
/// </para>
/// </remarks>
public sealed class SimulatedReachyMini : ISimulatedRobot
{
    private const double YawDeltaLimitDegrees = 65;

    private readonly StewartPlatform _neck = new(StewartGeometry.ReachyMiniApproximation);
    private readonly Lock _gate = new();

    private Pose _headPose = Pose.Identity;
    private Pose _headStart = Pose.Identity;
    private Pose _headTarget = Pose.Identity;

    private (Angle Right, Angle Left) _antennas;
    private (Angle Right, Angle Left) _antennaStart;
    private (Angle Right, Angle Left) _antennaTarget;

    private Angle _bodyYaw;
    private Angle _bodyYawStart;
    private Angle _bodyYawTarget;

    private TimeSpan _gotoElapsed;
    private TimeSpan _gotoDuration;
    private InterpolationMethod _gotoMethod = InterpolationMethod.MinJerk;
    private bool _gotoRunning;

    private TimeSpan _clock;
    private double _wobblePhase;

    /// <inheritdoc />
    public RobotKind Kind => RobotKind.ReachyMini;

    /// <inheritdoc />
    public RobotDescription Description => RobotCatalog.ReachyMini;

    /// <summary>Current motor mode.</summary>
    public MotorMode MotorMode { get; private set; } = MotorMode.Disabled;

    /// <summary>True while the idle breathing motion is on.</summary>
    public bool Wobbling { get; private set; }

    /// <summary>True while daemon-side face tracking is on.</summary>
    public bool HeadTracking { get; private set; }

    /// <summary>Blend weight for face tracking against application motion.</summary>
    public double HeadTrackingWeight { get; private set; } = 1.0;

    /// <summary>True when the daemon may rotate the body to satisfy a large head yaw.</summary>
    public bool AutomaticBodyYaw { get; set; } = true;

    /// <summary>True while a goto is in flight.</summary>
    public bool IsMoveRunning => _gotoRunning;

    /// <summary>Where a simulated face is, for exercising the tracking path.</summary>
    public FaceTarget SimulatedFace { get; set; } = FaceTarget.None;

    /// <summary>Applies a target immediately.</summary>
    public void SetTarget(ReachyMiniTarget target)
    {
        lock (_gate)
        {
            _gotoRunning = false;

            if (target.Head is { } head)
            {
                _headPose = head;
            }

            if (target.Antennas is { } antennas)
            {
                _antennas = antennas;
            }

            if (target.BodyYaw is { } yaw)
            {
                _bodyYaw = yaw;
            }

            ApplyYawCoupling();
        }
    }

    /// <summary>Starts a daemon-side interpolation to a target.</summary>
    public void StartGoto(ReachyMiniTarget target, TimeSpan duration, InterpolationMethod method)
    {
        lock (_gate)
        {
            _headStart = _headPose;
            _antennaStart = _antennas;
            _bodyYawStart = _bodyYaw;

            _headTarget = target.Head ?? _headPose;
            _antennaTarget = target.Antennas ?? _antennas;
            _bodyYawTarget = target.BodyYaw ?? _bodyYaw;

            _gotoDuration = duration <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : duration;
            _gotoElapsed = TimeSpan.Zero;
            _gotoMethod = method;
            _gotoRunning = true;
        }
    }

    /// <summary>Cancels an in-flight goto. The robot holds where it is.</summary>
    public void CancelMove()
    {
        lock (_gate)
        {
            _gotoRunning = false;
        }
    }

    /// <summary>Switches motor mode.</summary>
    public void SetMotorMode(MotorMode mode)
    {
        lock (_gate)
        {
            MotorMode = mode;

            // Cutting torque abandons whatever was in flight, exactly as it does on the robot.
            if (mode == MotorMode.Disabled)
            {
                _gotoRunning = false;
            }
        }
    }

    /// <summary>Turns the idle breathing motion on or off.</summary>
    public void SetWobbling(bool enabled)
    {
        lock (_gate)
        {
            Wobbling = enabled;
            if (!enabled)
            {
                _wobblePhase = 0;
            }
        }
    }

    /// <summary>Turns face tracking on or off.</summary>
    public void SetHeadTracking(bool enabled, double weight)
    {
        lock (_gate)
        {
            HeadTracking = enabled;
            HeadTrackingWeight = Math.Clamp(weight, 0, 1);
        }
    }

    /// <inheritdoc />
    public void Tick(TimeSpan delta)
    {
        lock (_gate)
        {
            _clock += delta;

            if (MotorMode == MotorMode.Disabled)
            {
                // No torque: the head sags back to rest rather than holding its pose.
                _headPose = Pose.Lerp(_headPose, Pose.Identity, Math.Min(1, delta.TotalSeconds * 2));
                return;
            }

            AdvanceGoto(delta);
            AdvanceHeadTracking(delta);
            AdvanceWobble(delta);
            ApplyYawCoupling();
        }
    }

    private void AdvanceGoto(TimeSpan delta)
    {
        if (!_gotoRunning)
        {
            return;
        }

        _gotoElapsed += delta;
        double t = Math.Clamp(_gotoElapsed.TotalSeconds / _gotoDuration.TotalSeconds, 0, 1);
        double alpha = Interpolation.Evaluate(_gotoMethod, t);

        _headPose = Pose.Lerp(_headStart, _headTarget, alpha);
        _antennas = (
            Lerp(_antennaStart.Right, _antennaTarget.Right, alpha),
            Lerp(_antennaStart.Left, _antennaTarget.Left, alpha));
        _bodyYaw = Lerp(_bodyYawStart, _bodyYawTarget, alpha);

        if (t >= 1)
        {
            // Land exactly on the target. Leaving the eased value in place accumulates a small
            // error over a sequence of gotos, and it shows up as drift in a long behaviour.
            _headPose = _headTarget;
            _antennas = _antennaTarget;
            _bodyYaw = _bodyYawTarget;
            _gotoRunning = false;
        }

        static Angle Lerp(Angle from, Angle to, double alpha) =>
            Angle.FromRadians(from.Radians + ((to.Radians - from.Radians) * alpha));
    }

    private void AdvanceHeadTracking(TimeSpan delta)
    {
        if (!HeadTracking || HeadTrackingWeight <= 0 || !SimulatedFace.Detected)
        {
            return;
        }

        // The tracker aims at the nose: x and y are normalised image coordinates, so they map onto
        // yaw and pitch offsets rather than onto world angles.
        Angle targetYaw = Angle.FromDegrees(-SimulatedFace.X * 45);
        Angle targetPitch = Angle.FromDegrees(SimulatedFace.Y * 30);

        (Angle roll, Angle pitch, Angle yaw) = _headPose.Rpy;
        double blend = Math.Min(1, delta.TotalSeconds * 6 * HeadTrackingWeight);

        _headPose = Pose.FromRpy(
            _headPose.Position.X, _headPose.Position.Y, _headPose.Position.Z,
            roll,
            Angle.FromRadians(pitch.Radians + ((targetPitch.Radians - pitch.Radians) * blend)),
            Angle.FromRadians(yaw.Radians + ((targetYaw.Radians - yaw.Radians) * blend)));
    }

    private void AdvanceWobble(TimeSpan delta)
    {
        if (!Wobbling || _gotoRunning)
        {
            return;
        }

        _wobblePhase += delta.TotalSeconds * 1.1;
    }

    /// <summary>
    /// The pose actually shown: what the application commanded, plus the idle breath.
    /// </summary>
    /// <remarks>
    /// The breath is applied here as an offset rather than written back into
    /// <see cref="_headPose"/>. Folding it into the commanded pose each tick integrates it: the
    /// head climbs a few millimetres per second until the pose leaves the neck's workspace, the
    /// solver starts throwing, and every branch angle reads zero while the status panel cheerfully
    /// says "moving".
    /// </remarks>
    private Pose DisplayPose()
    {
        if (!Wobbling || _gotoRunning)
        {
            return _headPose;
        }

        // A slow breath: a few millimetres of travel and about a degree of roll. Enough to read as
        // alive, small enough not to fight an application that is also commanding.
        double lift = Math.Sin(_wobblePhase) * 0.0035;
        double roll = Math.Sin(_wobblePhase * 0.6) * Angle.FromDegrees(1.2).Radians;

        (Angle currentRoll, Angle pitch, Angle yaw) = _headPose.Rpy;

        return Pose.FromRpy(
            _headPose.Position.X,
            _headPose.Position.Y,
            _headPose.Position.Z + lift,
            Angle.FromRadians(currentRoll.Radians + roll),
            pitch,
            yaw);
    }

    /// <summary>
    /// Enforces the 65-degree limit between head yaw and body yaw.
    /// </summary>
    /// <remarks>
    /// With automatic body yaw on, the body follows so the head can reach where it was asked to
    /// look. With it off, the head is clamped - which is what the robot does, and which looks from
    /// the outside like a head that has decided to stop turning.
    /// </remarks>
    private void ApplyYawCoupling()
    {
        (Angle roll, Angle pitch, Angle yaw) = _headPose.Rpy;
        double deltaDegrees = (yaw - _bodyYaw).Normalized().Degrees;

        if (Math.Abs(deltaDegrees) <= YawDeltaLimitDegrees)
        {
            return;
        }

        double excess = Math.Abs(deltaDegrees) - YawDeltaLimitDegrees;
        double direction = Math.Sign(deltaDegrees);

        if (AutomaticBodyYaw)
        {
            Angle desired = _bodyYaw + Angle.FromDegrees(direction * excess);
            JointDescriptor bodyJoint = Description["body.yaw"];
            _bodyYaw = desired.Clamp(bodyJoint.Lower, bodyJoint.Upper);

            // The body may have run into its own limit before absorbing all of the excess, so
            // re-check and clamp the head with whatever is left over.
            deltaDegrees = (yaw - _bodyYaw).Normalized().Degrees;
            if (Math.Abs(deltaDegrees) <= YawDeltaLimitDegrees)
            {
                return;
            }
        }

        Angle clamped = _bodyYaw + Angle.FromDegrees(Math.Sign(deltaDegrees) * YawDeltaLimitDegrees);
        _headPose = Pose.FromRpy(_headPose.Position.X, _headPose.Position.Y, _headPose.Position.Z, roll, pitch, clamped);
    }

    /// <summary>The current state, in the shape the SDK reports it.</summary>
    public ReachyMiniState State()
    {
        lock (_gate)
        {
            return new ReachyMiniState(
                DisplayPose(),
                _antennas,
                _bodyYaw,
                SolveJointPositions(),
                MotorMode,
                _gotoRunning,
                DateTimeOffset.UtcNow);
        }
    }

    /// <inheritdoc />
    public SimulationSnapshot Snapshot()
    {
        lock (_gate)
        {
            double[] joints = new double[Description.JointCount];
            IReadOnlyList<double> head = SolveJointPositions();

            for (int i = 0; i < Math.Min(head.Count, 7); i++)
            {
                joints[i] = head[i];
            }

            joints[7] = _antennas.Right.Radians;
            joints[8] = _antennas.Left.Radians;

            return new SimulationSnapshot(Kind, joints, Pose.Identity, _clock, _gotoRunning || Wobbling);
        }
    }

    /// <summary>
    /// Solves the seven head-chain motor angles: body yaw then the six Stewart branches.
    /// </summary>
    /// <remarks>
    /// A pose outside the mechanism's reach throws from the solver. The simulation reports the
    /// branches as zero in that case rather than propagating the exception, because the pose has
    /// already been accepted by then and a viewport that throws mid-frame is worse than one that
    /// draws a neutral neck. The pose-level limits are what stop this happening in practice.
    /// </remarks>
    private IReadOnlyList<double> SolveJointPositions()
    {
        double[] joints = new double[7];
        joints[0] = _bodyYaw.Radians;

        try
        {
            Span<double> branches = stackalloc double[StewartGeometry.BranchCount];
            _neck.SolveInverse(DisplayPose(), branches);

            for (int i = 0; i < branches.Length; i++)
            {
                joints[i + 1] = branches[i];
            }
        }
        catch (PollenRoboticsException)
        {
            // Outside the workspace: leave the branches at zero.
        }

        return joints;
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_gate)
        {
            _headPose = _headStart = _headTarget = Pose.Identity;
            _antennas = _antennaStart = _antennaTarget = (Angle.Zero, Angle.Zero);
            _bodyYaw = _bodyYawStart = _bodyYawTarget = Angle.Zero;
            _gotoRunning = false;
            _clock = TimeSpan.Zero;
            _wobblePhase = 0;
            MotorMode = MotorMode.Disabled;
            Wobbling = false;
            HeadTracking = false;
        }
    }
}
