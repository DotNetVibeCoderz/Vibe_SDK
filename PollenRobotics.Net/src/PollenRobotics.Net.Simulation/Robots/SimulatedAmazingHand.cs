using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Robots;

namespace PollenRobotics.Net.Simulation.Robots;

/// <summary>
/// A kinematic Amazing Hand.
/// </summary>
/// <remarks>
/// <para>
/// Eight servos, four parallel-linkage fingers, nothing else. The model tracks commanded motor
/// angles with a first-order lag so a commanded step looks like a servo moving rather than like a
/// finger teleporting, and that lag is the only dynamics here: no contact, no grasp force, no
/// object.
/// </para>
/// <para>
/// <b>It will happily close through anything.</b> A simulated grasp proves the posture is
/// reachable and nothing more - whether a real finger would hold the object, slip off it or stall
/// against it is exactly what a kinematic model cannot tell you.
/// </para>
/// </remarks>
public sealed class SimulatedAmazingHand : ISimulatedRobot
{
    // The servos are SCS0009s on a short linkage. 8 rad/s is a little slower than they manage
    // unloaded, which is the honest end of the range for a finger that is pushing anything.
    private const double TrackingRate = 8.0;

    private readonly double[] _measured;
    private readonly double[] _commanded;
    private readonly Lock _gate = new();

    private TimeSpan _clock;

    // Off at power-on, like the contract requires and like the real servos are. Starting powered
    // would let an application skip its own bring-up here and then do nothing on hardware.
    private bool _torque;

    /// <summary>Creates the model.</summary>
    public SimulatedAmazingHand()
    {
        Description = RobotCatalog.AmazingHand;
        _measured = new double[Description.JointCount];
        _commanded = new double[Description.JointCount];
    }

    /// <inheritdoc />
    public RobotKind Kind => RobotKind.AmazingHand;

    /// <inheritdoc />
    public RobotDescription Description { get; }

    /// <summary>Whether the servos are powered. A relaxed hand drifts open.</summary>
    public bool TorqueEnabled
    {
        get { lock (_gate) { return _torque; } }
        set { lock (_gate) { _torque = value; } }
    }

    /// <summary>Commands the eight motors, in catalogue order.</summary>
    public void SetMotorAngles(IReadOnlyList<double> radians)
    {
        ArgumentNullException.ThrowIfNull(radians);

        lock (_gate)
        {
            for (int i = 0; i < _commanded.Length && i < radians.Count; i++)
            {
                JointDescriptor joint = Description.Joints[i];
                _commanded[i] = joint.Clamp(Angle.FromRadians(radians[i])).Radians;
            }
        }
    }

    /// <summary>Reads the measured motor angles.</summary>
    public double[] MotorAngles
    {
        get { lock (_gate) { return [.. _measured]; } }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_measured);
            Array.Clear(_commanded);
            _clock = TimeSpan.Zero;
            _torque = false;
        }
    }

    /// <inheritdoc />
    public void Tick(TimeSpan delta)
    {
        lock (_gate)
        {
            _clock += delta;

            double alpha = Math.Clamp(delta.TotalSeconds * TrackingRate, 0, 1);

            for (int i = 0; i < _measured.Length; i++)
            {
                // Unpowered, the fingers fall back toward straight rather than holding: the
                // linkage has no detent and the servo no brake.
                double target = _torque ? _commanded[i] : 0;
                _measured[i] += (target - _measured[i]) * alpha;
            }
        }
    }

    /// <inheritdoc />
    public SimulationSnapshot Snapshot()
    {
        lock (_gate)
        {
            bool moving = false;

            for (int i = 0; i < _measured.Length && !moving; i++)
            {
                moving = Math.Abs(_measured[i] - (_torque ? _commanded[i] : 0)) > 1e-3;
            }

            // The hand does not travel: it is bolted to a wrist, so its body pose is the identity
            // and the viewport leaves it at the origin.
            return new SimulationSnapshot(Kind, [.. _measured], Pose.Identity, _clock, moving);
        }
    }
}
