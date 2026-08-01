using System.Numerics;
using Unitree.Net.Core;
using Unitree.Net.Messages.Go;

namespace Unitree.Net.Simulation;

/// <summary>
/// What the simulated robot is currently doing.
/// </summary>
public enum SimulatedGait
{
    /// <summary>Lying down, motors damped.</summary>
    Resting,

    /// <summary>Standing still on all contacts.</summary>
    Standing,

    /// <summary>Walking under the commanded velocity.</summary>
    Walking,
}

/// <summary>
/// The velocity a caller is asking the simulated robot to hold.
/// </summary>
/// <param name="Forward">Forward speed in metres per second.</param>
/// <param name="Lateral">Left-positive strafe speed in metres per second.</param>
/// <param name="YawRate">Counter-clockwise turn rate in radians per second.</param>
public readonly record struct SimulatedVelocity(float Forward, float Lateral, float YawRate)
{
    /// <summary>A full stop.</summary>
    public static SimulatedVelocity Zero { get; }

    /// <summary>Magnitude of the planar velocity component, in metres per second.</summary>
    public float Speed => MathF.Sqrt((Forward * Forward) + (Lateral * Lateral));

    /// <summary>Whether the command asks for any motion at all.</summary>
    public bool IsMoving => Speed > 0.01f || MathF.Abs(YawRate) > 0.01f;
}

/// <summary>
/// An immutable view of the simulation at one instant, safe to hand to a UI thread.
/// </summary>
/// <param name="Model">The platform being simulated.</param>
/// <param name="Gait">What the robot is doing.</param>
/// <param name="JointAngles">Joint angles in radians, indexed to match the rig.</param>
/// <param name="Position">World position of the body origin, in metres.</param>
/// <param name="Roll">Body roll in radians.</param>
/// <param name="Pitch">Body pitch in radians.</param>
/// <param name="Yaw">Body heading in radians.</param>
/// <param name="Height">Height of the body origin above the ground, in metres.</param>
/// <param name="Contacts">Per-contact ground force in newtons, in the rig's contact order.</param>
/// <param name="Speed">Ground speed in metres per second.</param>
/// <param name="BatterySoc">Battery state of charge, 0–100.</param>
/// <param name="PackVoltage">Pack voltage in volts.</param>
/// <param name="PackCurrent">Pack current in amperes; negative while discharging.</param>
/// <param name="MaxMotorTemperature">Hottest motor in degrees Celsius.</param>
/// <param name="ElapsedSeconds">Seconds since the simulation started.</param>
public sealed record SimulationSnapshot(
    RobotModel Model,
    SimulatedGait Gait,
    IReadOnlyList<float> JointAngles,
    Vector3 Position,
    float Roll,
    float Pitch,
    float Yaw,
    float Height,
    IReadOnlyList<float> Contacts,
    float Speed,
    float BatterySoc,
    float PackVoltage,
    float PackCurrent,
    float MaxMotorTemperature,
    double ElapsedSeconds);

/// <summary>
/// A kinematic stand-in for any supported Unitree platform.
/// </summary>
/// <remarks>
/// <para>
/// Motion is generated, not integrated: legs follow a phase-driven gait, the body rides on top of the
/// legs, and the battery discharges on a timer. The goal is telemetry with realistic <em>shape</em> —
/// values that move, correlate, and cross thresholds — so that everything downstream of the robot can
/// be developed and exercised honestly. It is not a dynamics simulator and will not tell you whether a
/// controller is stable.
/// </para>
/// <para>
/// Not thread-safe. <see cref="Advance"/> runs on the control loop; <see cref="Capture"/> produces an
/// immutable snapshot for other threads to read.
/// </para>
/// </remarks>
public sealed class SimulatedRobot
{
    private const float AmbientTemperature = 28f;

    private readonly RobotRig _rig;
    private readonly float[] _jointAngles;
    private readonly float[] _jointVelocities;
    private readonly float[] _motorTemperatures;
    private readonly float[] _contacts;

    // Joint indices the biped gait drives, resolved from the rig once. They cannot be hard-coded: H1
    // has a pitch-only ankle, so its right leg starts at index 5 where G1's starts at 6. Resolving by
    // link name also keeps the 500 Hz path free of lookups.
    private readonly int[] _hipPitch = [-1, -1];
    private readonly int[] _knee = [-1, -1];
    private readonly int[] _anklePitch = [-1, -1];
    private readonly int[] _shoulderPitch = [-1, -1];
    private readonly int[] _elbow = [-1, -1];

    private readonly float[] _wheelAngles = new float[4];

    private double _elapsedSeconds;
    private double _gaitPhase;
    private float _batterySoc = 92f;
    private float _standBlend;
    private Vector3 _position;
    private float _yaw;
    private float _roll;
    private float _pitch;
    private float _height;

    /// <summary>Creates a simulation of <paramref name="model"/>.</summary>
    /// <param name="model">The platform to simulate.</param>
    /// <exception cref="ArgumentOutOfRangeException">No rig exists for <paramref name="model"/>.</exception>
    public SimulatedRobot(RobotModel model)
    {
        _rig = RobotRig.For(model);
        _jointAngles = new float[_rig.JointCount];
        _jointVelocities = new float[_rig.JointCount];
        _motorTemperatures = new float[_rig.JointCount];
        _contacts = new float[_rig.ContactLinks.Count];

        Array.Fill(_motorTemperatures, AmbientTemperature);
        _height = _rig.StandingHeight;
        Gait = SimulatedGait.Resting;

        if (!_rig.IsQuadruped)
        {
            ResolveBipedJoints();
        }
    }

    private void ResolveBipedJoints()
    {
        var byLink = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (RigLink link in _rig.Links)
        {
            if (link.JointIndex >= 0)
            {
                byLink[link.Name] = link.JointIndex;
            }
        }

        for (int side = 0; side < 2; side++)
        {
            string prefix = side == 0 ? "left" : "right";

            _hipPitch[side] = Lookup(byLink, $"{prefix}_hip_pitch");
            _knee[side] = Lookup(byLink, $"{prefix}_calf");
            _anklePitch[side] = Lookup(byLink, $"{prefix}_ankle");
            _shoulderPitch[side] = Lookup(byLink, $"{prefix}_shoulder_pitch");
            _elbow[side] = Lookup(byLink, $"{prefix}_forearm");
        }

        static int Lookup(Dictionary<string, int> map, string name) =>
            map.TryGetValue(name, out int index) ? index : -1;
    }

    /// <summary>The rig driving both the kinematics and the viewport.</summary>
    public RobotRig Rig => _rig;

    /// <summary>What the robot is currently doing.</summary>
    public SimulatedGait Gait { get; private set; }

    /// <summary>The velocity the robot is being asked to hold.</summary>
    public SimulatedVelocity Command { get; set; }

    /// <summary>Battery state of charge, 0–100.</summary>
    public float BatterySoc => _batterySoc;

    /// <summary>Stands the robot up. Motion commands are ignored until this has been called.</summary>
    public void StandUp() => Gait = SimulatedGait.Standing;

    /// <summary>Lies the robot down and cancels any commanded motion.</summary>
    public void StandDown()
    {
        Gait = SimulatedGait.Resting;
        Command = SimulatedVelocity.Zero;
    }

    /// <summary>Overrides the battery state of charge, for exercising low-battery handling.</summary>
    /// <param name="stateOfCharge">The charge to set, clamped to 0–100.</param>
    public void SetBatterySoc(float stateOfCharge) => _batterySoc = Math.Clamp(stateOfCharge, 0f, 100f);

    /// <summary>
    /// Advances the simulation by <paramref name="deltaSeconds"/>.
    /// </summary>
    /// <param name="deltaSeconds">Elapsed time since the previous call, in seconds.</param>
    /// <remarks>
    /// Allocation-free: this runs on the same 500 Hz loop the real control path uses, and a garbage
    /// collection here would show up as loop jitter rather than as a memory problem.
    /// </remarks>
    public void Advance(double deltaSeconds)
    {
        if (deltaSeconds <= 0)
        {
            return;
        }

        _elapsedSeconds += deltaSeconds;
        float dt = (float)deltaSeconds;

        SimulatedVelocity command = Gait == SimulatedGait.Resting ? SimulatedVelocity.Zero : Command;
        Gait = Gait == SimulatedGait.Resting
            ? SimulatedGait.Resting
            : command.IsMoving ? SimulatedGait.Walking : SimulatedGait.Standing;

        // The stand blend is what stops the robot snapping between lying and standing. It also scales
        // the body height, so the legs and the body agree on where the ground is throughout.
        float standTarget = Gait == SimulatedGait.Resting ? 0f : 1f;
        _standBlend += Math.Clamp(standTarget - _standBlend, -dt * 1.6f, dt * 1.6f);

        AdvanceGait(dt, command);
        AdvanceOdometry(dt, command);
        AdvanceThermal(dt);
        AdvanceBattery(dt, command);
    }

    private void AdvanceGait(float dt, SimulatedVelocity command)
    {
        // Step frequency rises with speed, which is what keeps stride length roughly constant instead
        // of letting the legs windmill at low speed.
        float frequency = _rig.IsQuadruped
            ? 1.4f + (1.1f * MathF.Min(command.Speed, 1.5f))
            : 0.9f + (0.7f * MathF.Min(command.Speed, 1.2f));

        if (Gait == SimulatedGait.Walking)
        {
            _gaitPhase = (_gaitPhase + (frequency * dt)) % 1.0;
        }

        float amplitude = Gait == SimulatedGait.Walking
            ? MathF.Min(1f, (MathF.Abs(command.Forward) * 1.4f) + (MathF.Abs(command.YawRate) * 0.5f))
            : 0f;

        if (_rig.IsQuadruped)
        {
            AdvanceQuadrupedGait(dt, command, amplitude);
        }
        else
        {
            AdvanceBipedGait(dt, command, amplitude);
        }

        float bodyPhase = (float)_gaitPhase * MathF.Tau;
        _roll = 0.02f * amplitude * MathF.Sin(bodyPhase);
        _pitch = 0.03f * amplitude * MathF.Sin(bodyPhase * 2f);
        _height = _rig.StandingHeight * (0.30f + (0.70f * _standBlend));
    }

    private void AdvanceQuadrupedGait(float dt, SimulatedVelocity command, float amplitude)
    {
        // The W variants roll rather than step: their legs act as suspension and hold the standing
        // pose while the wheels do the work. Driving a trot into them would look like a robot
        // pedalling.
        if (_rig.IsWheeled)
        {
            AdvanceWheeledGait(dt, command);
            return;
        }

        // A trot pairs diagonal legs — FR with RL, FL with RR. Offsetting the two pairs by half a cycle
        // is the whole difference between a trot and a hop.
        ReadOnlySpan<float> phaseOffset = [0f, 0.5f, 0.5f, 0f];
        IReadOnlyList<float> neutral = _rig.NeutralPose;

        for (int leg = 0; leg < 4; leg++)
        {
            float phase = ((float)_gaitPhase + phaseOffset[leg]) % 1f;
            float swing = MathF.Sin(phase * MathF.Tau);

            int hip = leg * 3;
            int thigh = hip + 1;
            int calf = hip + 2;

            // Yaw is produced by lengthening the stride on the outside of the turn, which is how a real
            // trot steers — not by twisting the hips.
            float side = leg is 1 or 3 ? 1f : -1f;
            float turnBias = command.YawRate * side * 0.25f;

            float hipTarget = neutral[hip] + (command.Lateral * side * 0.20f);
            float thighTarget = neutral[thigh] + (0.30f * swing * (amplitude + turnBias));
            float calfTarget = neutral[calf] - (0.42f * swing * (amplitude + turnBias));

            SetJoint(hip, Blend(hipTarget), dt);
            SetJoint(thigh, Blend(thighTarget), dt);
            SetJoint(calf, Blend(calfTarget), dt);

            // Stance is the loaded half of the cycle. Standing still loads every foot evenly, which is
            // what makes a stationary robot read as supported rather than mid-stride.
            bool stance = phase < 0.5f;
            _contacts[leg] = Gait switch
            {
                SimulatedGait.Resting => 0f,
                SimulatedGait.Standing => 62f * _standBlend,
                _ => stance ? 120f + (60f * MathF.Sin(phase * 2f * MathF.PI)) : 0f,
            };
        }
    }

    /// <summary>
    /// Advances a wheeled quadruped: the legs hold a stance and the four wheels drive.
    /// </summary>
    private void AdvanceWheeledGait(float dt, SimulatedVelocity command)
    {
        IReadOnlyList<float> neutral = _rig.NeutralPose;

        for (int leg = 0; leg < 4; leg++)
        {
            int hip = leg * 3;

            SetJoint(hip, Blend(neutral[hip]), dt);
            SetJoint(hip + 1, Blend(neutral[hip + 1]), dt);
            SetJoint(hip + 2, Blend(neutral[hip + 2]), dt);

            _contacts[leg] = Gait == SimulatedGait.Resting ? 0f : 95f * _standBlend;
        }

        // Rotation follows from ground speed and wheel radius, so the wheels turn at a rate that
        // matches the distance covered rather than an arbitrary one. Steering is skid-steer: the
        // wheels on the outside of a turn run faster than those on the inside.
        const float HalfTrackMetres = 0.15f;

        float radius = MathF.Max(_rig.WheelRadius, 0.01f);
        float baseRate = command.Forward / radius;

        for (int leg = 0; leg < 4; leg++)
        {
            float leftward = leg is 1 or 3 ? 1f : -1f;
            float rate = baseRate - (command.YawRate * leftward * HalfTrackMetres / radius);

            // The angle wraps but the velocity is supplied directly. Deriving it from the wrapped
            // angle would produce an enormous spike each time it crossed pi, and that spike feeds
            // straight into the thermal model.
            _wheelAngles[leg] = RobotMath.WrapAngle(_wheelAngles[leg] + (rate * dt));
            SetJointWithVelocity(GoJoint.Count + leg, _wheelAngles[leg], rate);
        }
    }

    private void AdvanceBipedGait(float dt, SimulatedVelocity command, float amplitude)
    {
        float phase = (float)_gaitPhase * MathF.Tau;

        for (int leg = 0; leg < 2; leg++)
        {
            float legPhase = phase + (leg * MathF.PI);
            float swing = MathF.Sin(legPhase);

            // The knee only ever flexes, so the lift is rectified rather than sinusoidal — a knee that
            // bent backwards on the other half of the cycle is the classic tell of a fake walk cycle.
            float lift = MathF.Max(0f, MathF.Sin(legPhase));

            int hipPitch = _hipPitch[leg];
            int knee = _knee[leg];
            int ankle = _anklePitch[leg];

            // Turning is produced by shortening the stride on the inside of the turn, the same way the
            // quadruped trot steers.
            float sideSign = leg == 0 ? 1f : -1f;
            float hipTarget = -0.10f + (0.42f * swing * amplitude) + (command.YawRate * sideSign * 0.12f);
            float kneeTarget = 0.18f + (0.75f * lift * amplitude);

            // The ankle holds the foot flat against the combined hip and knee rotation, so the sole
            // stays parallel to the floor instead of pointing wherever the leg happens to face.
            float ankleTarget = -(hipTarget + kneeTarget) * 0.55f;

            SetJoint(hipPitch, Blend(hipTarget), dt);
            SetJoint(knee, Blend(kneeTarget), dt);
            SetJoint(ankle, Blend(ankleTarget), dt);

            _contacts[leg] = Gait switch
            {
                SimulatedGait.Resting => 0f,
                SimulatedGait.Standing => 180f * _standBlend,
                _ => lift < 0.2f ? 300f * (1f - lift) : 0f,
            };
        }

        ApplyArmSwing(dt, phase, amplitude);
    }

    private void ApplyArmSwing(float dt, float phase, float amplitude)
    {
        float swing = MathF.Sin(phase);

        for (int side = 0; side < 2; side++)
        {
            // The arms swing against the legs. Without this a walking humanoid looks like it is being
            // carried rather than walking.
            float direction = side == 0 ? -1f : 1f;

            SetJoint(_shoulderPitch[side], Blend(0.35f * direction * swing * amplitude), dt);

            // A small permanent elbow bend keeps the hands off the thighs even when standing still.
            SetJoint(_elbow[side], Blend(0.35f + (0.12f * direction * swing * amplitude)), dt);
        }
    }

    /// <summary>Scales a standing joint target by how far through standing up the robot is.</summary>
    private float Blend(float standingTarget) => standingTarget * _standBlend;

    private void SetJoint(int index, float target, float dt)
    {
        if ((uint)index >= (uint)_jointAngles.Length)
        {
            return;
        }

        float previous = _jointAngles[index];
        _jointAngles[index] = target;
        _jointVelocities[index] = dt > 0 ? (target - previous) / dt : 0f;
    }

    /// <summary>Sets a joint whose velocity is known independently of its angle.</summary>
    /// <remarks>
    /// Used for continuously rotating joints. Their angle wraps, so differencing it would report a
    /// huge velocity on the tick the wrap happens.
    /// </remarks>
    private void SetJointWithVelocity(int index, float angle, float velocity)
    {
        if ((uint)index >= (uint)_jointAngles.Length)
        {
            return;
        }

        _jointAngles[index] = angle;
        _jointVelocities[index] = velocity;
    }

    private void AdvanceOdometry(float dt, SimulatedVelocity command)
    {
        _yaw = RobotMath.WrapAngle(_yaw + (command.YawRate * dt));

        (float sin, float cos) = MathF.SinCos(_yaw);
        _position.X += ((command.Forward * cos) - (command.Lateral * sin)) * dt;
        _position.Y += ((command.Forward * sin) + (command.Lateral * cos)) * dt;
        _position.Z = _height;
    }

    private void AdvanceThermal(float dt)
    {
        // Cooling is proportional to the excess over ambient, so the temperature settles at an
        // equilibrium of roughly ambient + heating*|velocity|/cooling. These values put sustained
        // walking near 60 °C after a minute or two, which is where a real robot lives — a model that
        // pegs the thermal limit during normal walking teaches the wrong thing about a hot motor.
        const float HeatingCoefficient = 0.09f;
        const float CoolingCoefficient = 0.011f;

        for (int i = 0; i < _motorTemperatures.Length; i++)
        {
            // Heating tracks joint work; cooling is proportional to the excess over ambient, so the
            // temperature settles at an equilibrium instead of climbing without bound.
            float heating = HeatingCoefficient * MathF.Abs(_jointVelocities[i]) * dt;
            float cooling = CoolingCoefficient * (_motorTemperatures[i] - AmbientTemperature) * dt;

            _motorTemperatures[i] = Math.Clamp(
                _motorTemperatures[i] + heating - cooling, AmbientTemperature, 95f);
        }
    }

    private void AdvanceBattery(float dt, SimulatedVelocity command)
    {
        // Roughly a 40-minute walk or a four-hour idle — fast enough that a watching dashboard sees the
        // number move within a session, slow enough that it is not a distraction.
        float drainPerHour = Gait == SimulatedGait.Resting
            ? 25f
            : 100f * (0.35f + (0.65f * MathF.Min(1f, command.Speed)));

        _batterySoc = Math.Max(0f, _batterySoc - (drainPerHour * dt / 3600f * 1.5f));
    }

    /// <summary>Captures an immutable snapshot for another thread to read.</summary>
    public SimulationSnapshot Capture()
    {
        float maxTemperature = 0f;

        for (int i = 0; i < _motorTemperatures.Length; i++)
        {
            maxTemperature = MathF.Max(maxTemperature, _motorTemperatures[i]);
        }

        int cells = RobotModelInfo.GetBatteryCellCount(_rig.Model);
        float cellVolts = 3.60f + (0.45f * (_batterySoc / 100f));
        float current = Gait == SimulatedGait.Resting ? -1.2f : -4.2f - (2.4f * Command.Speed);

        return new SimulationSnapshot(
            _rig.Model,
            Gait,
            _jointAngles.AsSpan().ToArray(),
            _position,
            _roll,
            _pitch,
            _yaw,
            _height,
            _contacts.AsSpan().ToArray(),
            Command.Speed,
            _batterySoc,
            cellVolts * cells,
            current,
            maxTemperature,
            _elapsedSeconds);
    }

    /// <summary>
    /// Builds a <c>unitree_go</c> low-level state message reflecting the current simulation state.
    /// </summary>
    /// <param name="tickMilliseconds">Value for the message's tick field.</param>
    /// <remarks>
    /// Only meaningful for quadrupeds. Humanoids speak <c>unitree_hg</c>, which this SDK does not yet
    /// implement — see <c>PROGRESS.md</c>. <see cref="SimulationHost"/> is what decides not to publish
    /// this for a humanoid; the method itself will happily fill the quadruped-shaped message.
    /// </remarks>
    public LowState BuildLowState(uint tickMilliseconds)
    {
        LowState state = default;

        state.Head[0] = 0xFE;
        state.Head[1] = 0xEF;
        state.LevelFlag = 0xFF;
        state.Tick = tickMilliseconds;

        state.ImuState.Rpy[0] = _roll;
        state.ImuState.Rpy[1] = _pitch;
        state.ImuState.Rpy[2] = _yaw;

        Quaternion orientation = RobotMath.ToQuaternion(new EulerAngles(_roll, _pitch, _yaw));
        state.ImuState.Quaternion[0] = orientation.W;
        state.ImuState.Quaternion[1] = orientation.X;
        state.ImuState.Quaternion[2] = orientation.Y;
        state.ImuState.Quaternion[3] = orientation.Z;

        state.ImuState.Accelerometer[2] = 9.81f;
        state.ImuState.Temperature = 41;

        int motorCount = Math.Min(_jointAngles.Length, GoJoint.Count);

        for (int i = 0; i < motorCount; i++)
        {
            state.MotorState[i].Mode = MotorMode.Servo;
            state.MotorState[i].Q = _jointAngles[i];
            state.MotorState[i].Dq = _jointVelocities[i];
            state.MotorState[i].TauEst = _jointVelocities[i] * 1.5f;
            state.MotorState[i].Temperature = (sbyte)_motorTemperatures[i];
        }

        for (int i = 0; i < 4 && i < _contacts.Length; i++)
        {
            state.FootForce[i] = (short)_contacts[i];
            state.FootForceEst[i] = (short)_contacts[i];
        }

        int cells = RobotModelInfo.GetBatteryCellCount(_rig.Model);
        float cellVolts = 3.60f + (0.45f * (_batterySoc / 100f));

        // The cell spread widens as the pack drains, which is the shape a real pack has and is enough
        // to trip the imbalance warning near the end of a long run.
        float spread = 0.004f + (0.02f * (1f - (_batterySoc / 100f)));

        state.BmsState.Soc = (byte)_batterySoc;
        state.BmsState.Cycle = 143;
        state.BmsState.Current = (int)((-4200f) - (600f * Command.Speed));
        state.BmsState.VersionHigh = 1;
        state.BmsState.VersionLow = 4;

        for (int i = 0; i < cells && i < 15; i++)
        {
            state.BmsState.CellVoltage[i] = (ushort)((cellVolts + (spread * MathF.Sin(i * 1.7f))) * 1000f);
            state.BmsState.BqNtc[Math.Min(i, 1)] = 34;
            state.BmsState.McuNtc[Math.Min(i, 1)] = 36;
        }

        state.PowerV = cellVolts * cells;
        state.PowerA = MathF.Abs(state.BmsState.Current / 1000f);
        state.TemperatureNtc1 = 33;
        state.TemperatureNtc2 = 35;

        state.UpdateCrc();
        return state;
    }

    /// <summary>Builds a locomotion state message reflecting the current simulation state.</summary>
    public SportModeState BuildSportModeState()
    {
        SportModeState state = default;

        state.Stamp.Seconds = (int)_elapsedSeconds;
        state.Stamp.Nanoseconds = (uint)((_elapsedSeconds - (int)_elapsedSeconds) * 1_000_000_000);

        state.Mode = (byte)(Gait switch
        {
            SimulatedGait.Resting => SportMode.Damping,
            SimulatedGait.Standing => SportMode.BalanceStand,
            _ => SportMode.Locomotion,
        });

        state.GaitType = (byte)(Gait == SimulatedGait.Walking ? GaitType.Trot : GaitType.Idle);
        state.BodyHeight = _height;
        state.FootRaiseHeight = 0.09f;

        state.Position[0] = _position.X;
        state.Position[1] = _position.Y;
        state.Position[2] = _height;

        (float sin, float cos) = MathF.SinCos(_yaw);
        state.Velocity[0] = (Command.Forward * cos) - (Command.Lateral * sin);
        state.Velocity[1] = (Command.Forward * sin) + (Command.Lateral * cos);
        state.YawSpeed = Command.YawRate;

        state.ImuState.Rpy[0] = _roll;
        state.ImuState.Rpy[1] = _pitch;
        state.ImuState.Rpy[2] = _yaw;

        Quaternion orientation = RobotMath.ToQuaternion(new EulerAngles(_roll, _pitch, _yaw));
        state.ImuState.Quaternion[0] = orientation.W;
        state.ImuState.Quaternion[1] = orientation.X;
        state.ImuState.Quaternion[2] = orientation.Y;
        state.ImuState.Quaternion[3] = orientation.Z;

        for (int i = 0; i < 4; i++)
        {
            state.FootForce[i] = i < _contacts.Length ? (short)_contacts[i] : (short)0;
            state.RangeObstacle[i] = 2.5f;
        }

        return state;
    }
}
