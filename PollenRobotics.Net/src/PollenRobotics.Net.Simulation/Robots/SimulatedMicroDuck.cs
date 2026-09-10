using System.Numerics;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Robots;
using PollenRobotics.Net.MicroDuck;

namespace PollenRobotics.Net.Simulation.Robots;

/// <summary>
/// A kinematic model of MicroDuck: a scripted gait, integrated odometry and the action slots.
/// </summary>
/// <remarks>
/// <para>
/// The real duck walks because a reinforcement-learning policy decides, 50 times a second, what
/// fifteen servos should do. Nothing here reproduces that. What this does instead is drive the legs
/// through a plausible periodic gait whose phase advances with commanded speed, so an application
/// sees leg motion that starts, stops and turns when it asks - which is the part application code
/// interacts with.
/// </para>
/// <para>
/// The consequence is worth stating plainly: this model will happily walk at speeds and on
/// gradients where a real duck falls over. It is for developing an application, not for tuning a
/// gait.
/// </para>
/// </remarks>
public sealed class SimulatedMicroDuck : ISimulatedRobot
{
    private readonly Lock _gate = new();
    private readonly double[] _joints = new double[RobotCatalog.MicroDuck.JointCount];

    private DuckVelocity _velocity;
    private double _gaitPhase;
    private Vector3 _position;
    private Angle _heading;
    private TimeSpan _clock;

    private DuckActionSlot? _activeSlot;
    private TimeSpan _actionRemaining;
    private bool _initialised;
    private bool _fallen;

    /// <inheritdoc />
    public RobotKind Kind => RobotKind.MicroDuck;

    /// <inheritdoc />
    public RobotDescription Description => RobotCatalog.MicroDuck;

    /// <summary>True once the servos have been powered and homed.</summary>
    public bool IsInitialised => _initialised;

    /// <summary>True while the duck is down.</summary>
    public bool IsFallen => _fallen;

    /// <summary>The action slot currently running, or null.</summary>
    public DuckActionSlot? ActiveSlot => _activeSlot;

    /// <summary>Simulated pack voltage, which drains slowly while walking.</summary>
    public double BatteryVolts { get; private set; } = 8.2;

    /// <summary>Powers the servos and ramps to the home pose.</summary>
    public void Init()
    {
        lock (_gate)
        {
            _initialised = true;
            _fallen = false;
            ApplyStandPose();
        }
    }

    /// <summary>Cuts servo power. The duck collapses.</summary>
    public void Relax()
    {
        lock (_gate)
        {
            _initialised = false;
            _velocity = DuckVelocity.Zero;
            _activeSlot = null;
            Array.Clear(_joints);
        }
    }

    /// <summary>Sets the velocity intent the gait tracks.</summary>
    public void SetVelocity(DuckVelocity velocity)
    {
        lock (_gate)
        {
            _velocity = _initialised && !_fallen ? velocity.Clamped() : DuckVelocity.Zero;
        }
    }

    /// <summary>Starts an action slot. It runs for its scripted duration.</summary>
    public void Perform(DuckActionSlot slot)
    {
        lock (_gate)
        {
            if (!_initialised)
            {
                // A relaxed duck ignores commands silently, exactly as the daemon does.
                return;
            }

            _activeSlot = slot;
            _actionRemaining = DurationOf(slot);
            _velocity = DuckVelocity.Zero;

            if (slot is DuckActionSlot.Roulade or DuckActionSlot.Stand)
            {
                _fallen = false;
            }
        }
    }

    /// <summary>Tips the duck over, for exercising fall-recovery paths.</summary>
    public void Trip()
    {
        lock (_gate)
        {
            _fallen = true;
            _velocity = DuckVelocity.Zero;
            _activeSlot = null;
        }
    }

    /// <inheritdoc />
    public void Tick(TimeSpan delta)
    {
        lock (_gate)
        {
            _clock += delta;

            if (!_initialised)
            {
                return;
            }

            if (_activeSlot is { } slot)
            {
                AdvanceAction(slot, delta);
                return;
            }

            if (_fallen)
            {
                ApplyFallenPose();
                return;
            }

            AdvanceGait(delta);
            IntegrateOdometry(delta);
            DrainBattery(delta);
        }
    }

    private void AdvanceGait(TimeSpan delta)
    {
        double speed = Math.Sqrt(
            (_velocity.ForwardMetersPerSecond * _velocity.ForwardMetersPerSecond) +
            (_velocity.LateralMetersPerSecond * _velocity.LateralMetersPerSecond));

        double turning = Math.Abs(_velocity.YawRadiansPerSecond);

        if (speed < 1e-4 && turning < 1e-4)
        {
            // Standing: ease the legs back to the home pose rather than freezing mid-stride.
            BlendToward(StandPose(), Math.Min(1, delta.TotalSeconds * 4));
            _gaitPhase = 0;
            return;
        }

        // Step frequency rises with speed, with a floor so that a slow walk still steps rather
        // than sliding. 2.6 Hz at full speed is roughly what a 25 cm biped does.
        double frequency = 1.4 + (speed * 5.0) + (turning * 0.4);
        _gaitPhase += delta.TotalSeconds * frequency * 2 * Math.PI;

        if (_gaitPhase > 2 * Math.PI)
        {
            _gaitPhase -= 2 * Math.PI;
        }

        double amplitude = Math.Clamp(0.25 + (speed * 2.0), 0.25, 0.8);
        double lean = _velocity.ForwardMetersPerSecond * 0.6;

        // The two legs run half a cycle apart. Hip and knee are in quadrature so the foot traces a
        // rough ellipse rather than swinging as a rigid pendulum.
        SetLeg(isLeft: true, _gaitPhase, amplitude, lean);
        SetLeg(isLeft: false, _gaitPhase + Math.PI, amplitude, lean);

        // The head counter-rotates a little against the turn, which reads as the duck looking
        // where it is going.
        Set("neck.yaw", -_velocity.YawRadiansPerSecond * 0.35);
        Set("neck.pitch", lean * 0.4);
        Set("head.roll", Math.Sin(_gaitPhase) * 0.05);
    }

    private void SetLeg(bool isLeft, double phase, double amplitude, double lean)
    {
        string prefix = isLeft ? "left" : "right";
        double swing = Math.Sin(phase);
        double lift = Math.Max(0, Math.Cos(phase));

        Set($"{prefix}.hip_pitch", (swing * amplitude * 0.5) - lean);
        Set($"{prefix}.knee", lift * amplitude);
        Set($"{prefix}.ankle_pitch", (-swing * amplitude * 0.3) + (lean * 0.5));
        Set($"{prefix}.hip_roll", (isLeft ? 1 : -1) * _velocity.LateralMetersPerSecond * 1.5);
        Set($"{prefix}.hip_yaw", (isLeft ? 1 : -1) * _velocity.YawRadiansPerSecond * 0.15);
    }

    private void IntegrateOdometry(TimeSpan delta)
    {
        double dt = delta.TotalSeconds;
        _heading += Angle.FromRadians(_velocity.YawRadiansPerSecond * dt);

        double cos = Math.Cos(_heading.Radians);
        double sin = Math.Sin(_heading.Radians);

        _position += new Vector3(
            (float)(((_velocity.ForwardMetersPerSecond * cos) - (_velocity.LateralMetersPerSecond * sin)) * dt),
            (float)(((_velocity.ForwardMetersPerSecond * sin) + (_velocity.LateralMetersPerSecond * cos)) * dt),
            0);
    }

    private void DrainBattery(TimeSpan delta)
    {
        double load = 0.4 + (Math.Abs(_velocity.ForwardMetersPerSecond) * 3);
        BatteryVolts = Math.Max(6.0, BatteryVolts - (load * delta.TotalSeconds * 0.0004));
    }

    private void AdvanceAction(DuckActionSlot slot, TimeSpan delta)
    {
        _actionRemaining -= delta;
        double progress = 1 - Math.Clamp(_actionRemaining.TotalSeconds / DurationOf(slot).TotalSeconds, 0, 1);

        switch (slot)
        {
            case DuckActionSlot.SitStand:
                double crouch = Math.Sin(progress * Math.PI) * 1.1;
                Set("left.knee", crouch);
                Set("right.knee", crouch);
                Set("left.hip_pitch", -crouch * 0.6);
                Set("right.hip_pitch", -crouch * 0.6);
                break;

            case DuckActionSlot.GroundPick:
                double reach = Math.Sin(progress * Math.PI);
                Set("neck.pitch", reach * 0.7);
                Set("beak", reach > 0.5 ? 0 : reach * 0.7);
                Set("left.hip_pitch", -reach * 0.5);
                Set("right.hip_pitch", -reach * 0.5);
                break;

            case DuckActionSlot.KickLeft:
            case DuckActionSlot.KickRight:
                string leg = slot == DuckActionSlot.KickLeft ? "left" : "right";
                double kick = Math.Sin(progress * Math.PI);
                Set($"{leg}.hip_pitch", -kick * 1.2);
                Set($"{leg}.knee", (1 - kick) * 0.8);
                break;

            case DuckActionSlot.Roulade:
                double roll = Math.Sin(progress * Math.PI);
                Set("head.roll", roll * 0.4);
                Set("left.hip_roll", roll * 0.5);
                Set("right.hip_roll", -roll * 0.5);
                break;

            case DuckActionSlot.Stand:
            case DuckActionSlot.Walk:
            default:
                BlendToward(StandPose(), Math.Min(1, delta.TotalSeconds * 5));
                break;
        }

        if (_actionRemaining <= TimeSpan.Zero)
        {
            _activeSlot = null;
            ApplyStandPose();
        }
    }

    private static TimeSpan DurationOf(DuckActionSlot slot) => slot switch
    {
        DuckActionSlot.SitStand => TimeSpan.FromSeconds(2.0),
        DuckActionSlot.GroundPick => TimeSpan.FromSeconds(3.5),
        DuckActionSlot.KickLeft or DuckActionSlot.KickRight => TimeSpan.FromSeconds(1.2),
        DuckActionSlot.Roulade => TimeSpan.FromSeconds(4.0),
        _ => TimeSpan.FromSeconds(1.5),
    };

    private void ApplyStandPose() => StandPose().CopyTo(_joints, 0);

    private void ApplyFallenPose()
    {
        // Sprawled: hips out, knees loose. Distinct enough on screen that "it has fallen over" is
        // obvious without reading the status panel.
        Set("left.hip_roll", 0.5);
        Set("right.hip_roll", -0.5);
        Set("left.knee", 0.3);
        Set("right.knee", 0.3);
        Set("head.roll", 0.4);
    }

    private double[] StandPose()
    {
        double[] pose = new double[_joints.Length];

        // A slight crouch: knees bent, hips and ankles compensating so the body stays level.
        pose[Description["left.hip_pitch"].Index] = -0.25;
        pose[Description["right.hip_pitch"].Index] = -0.25;
        pose[Description["left.knee"].Index] = 0.5;
        pose[Description["right.knee"].Index] = 0.5;
        pose[Description["left.ankle_pitch"].Index] = -0.25;
        pose[Description["right.ankle_pitch"].Index] = -0.25;

        return pose;
    }

    private void BlendToward(double[] target, double alpha)
    {
        for (int i = 0; i < _joints.Length; i++)
        {
            _joints[i] += (target[i] - _joints[i]) * alpha;
        }
    }

    private void Set(string joint, double radians)
    {
        JointDescriptor descriptor = Description[joint];
        _joints[descriptor.Index] = descriptor.Clamp(Angle.FromRadians(radians)).Radians;
    }

    /// <summary>The current state, in the shape the SDK reports it.</summary>
    public MicroDuckState State()
    {
        lock (_gate)
        {
            System.Numerics.Quaternion orientation = Rotation.FromRpy(
                Angle.Zero,
                Angle.FromRadians(_fallen ? Math.PI / 2 : _velocity.ForwardMetersPerSecond * 0.3),
                _heading);

            return new MicroDuckState(
                [.. _joints],
                Array.Empty<double>(),
                (orientation.W, orientation.X, orientation.Y, orientation.Z),
                (0, 0, _velocity.YawRadiansPerSecond),
                (0, 0, 9.81),
                _activeSlot,
                _fallen,
                BatteryVolts,
                50,
                DateTimeOffset.UtcNow);
        }
    }

    /// <summary>An 8x8 depth frame, faked as a wall a fixed distance ahead.</summary>
    public TofFrame ReadTimeOfFlight()
    {
        double[] distances = new double[TofFrame.Size * TofFrame.Size];

        for (int row = 0; row < TofFrame.Size; row++)
        {
            for (int column = 0; column < TofFrame.Size; column++)
            {
                // A flat wall 60 cm out, further toward the edges of the field of view because the
                // ray is longer off-axis. Enough structure for an obstacle check to do something.
                double dx = (column - 3.5) / 3.5;
                double dy = (row - 3.5) / 3.5;
                distances[(row * TofFrame.Size) + column] = 0.6 * (1 + (0.15 * ((dx * dx) + (dy * dy))));
            }
        }

        return new TofFrame(distances, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public SimulationSnapshot Snapshot()
    {
        lock (_gate)
        {
            var body = new Pose(_position with { Z = _fallen ? 0.06f : 0.14f }, Rotation.FromRpy(Angle.Zero, Angle.Zero, _heading));
            bool moving = _activeSlot is not null || _velocity != DuckVelocity.Zero;
            return new SimulationSnapshot(Kind, [.. _joints], body, _clock, moving);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_joints);
            _velocity = DuckVelocity.Zero;
            _gaitPhase = 0;
            _position = Vector3.Zero;
            _heading = Angle.Zero;
            _clock = TimeSpan.Zero;
            _activeSlot = null;
            _initialised = false;
            _fallen = false;
            BatteryVolts = 8.2;
        }
    }
}
