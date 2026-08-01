namespace Unitree.Net.Core;

/// <summary>
/// Per-joint actuation ceilings enforced before any low-level command is published.
/// </summary>
/// <remarks>
/// These are the last line of defence between application code and the motors. The defaults are
/// deliberately below the hardware maxima; raise them only with the robot on a stand.
/// </remarks>
public sealed class JointSafetyLimits
{
    /// <summary>Maximum absolute commanded position, radians.</summary>
    public float MaxPosition { get; init; } = MathF.PI;

    /// <summary>Minimum commanded position, radians.</summary>
    public float MinPosition { get; init; } = -MathF.PI;

    /// <summary>Maximum absolute commanded velocity, rad/s.</summary>
    public float MaxVelocity { get; init; } = 20f;

    /// <summary>Maximum absolute commanded feed-forward torque, N·m.</summary>
    public float MaxTorque { get; init; } = 23.7f;

    /// <summary>Maximum position gain.</summary>
    public float MaxKp { get; init; } = 150f;

    /// <summary>Maximum damping gain.</summary>
    public float MaxKd { get; init; } = 10f;

    /// <summary>
    /// Maximum change in commanded position per control tick, radians.
    /// </summary>
    /// <remarks>
    /// At the default 500 Hz this permits roughly 5 rad/s of setpoint slew, which is fast enough for
    /// normal gaits but slow enough that a bad setpoint cannot become a step input.
    /// </remarks>
    public float MaxPositionDeltaPerTick { get; init; } = 0.01f;

    /// <summary>Conservative limits for the Go2 leg motors.</summary>
    public static JointSafetyLimits Go2Default { get; } = new()
    {
        MaxPosition = MathF.PI,
        MinPosition = -MathF.PI,
        MaxVelocity = 20f,
        MaxTorque = 23.7f,
        MaxKp = 150f,
        MaxKd = 10f,
        MaxPositionDeltaPerTick = 0.01f,
    };

    /// <summary>Conservative limits for G1 humanoid joints.</summary>
    public static JointSafetyLimits G1Default { get; } = new()
    {
        MaxPosition = MathF.PI,
        MinPosition = -MathF.PI,
        MaxVelocity = 15f,
        MaxTorque = 60f,
        MaxKp = 200f,
        MaxKd = 15f,
        MaxPositionDeltaPerTick = 0.008f,
    };

    /// <summary>Gets the default limits for <paramref name="model"/>.</summary>
    public static JointSafetyLimits ForModel(RobotModel model) =>
        RobotModelInfo.IsHumanoid(model) ? G1Default : Go2Default;

    /// <summary>
    /// Validates a joint command, throwing if any component exceeds a limit.
    /// </summary>
    /// <param name="jointIndex">The joint the command targets, used for error reporting.</param>
    /// <param name="position">Target position, radians.</param>
    /// <param name="velocity">Target velocity, rad/s.</param>
    /// <param name="torque">Feed-forward torque, N·m.</param>
    /// <param name="kp">Position gain.</param>
    /// <param name="kd">Damping gain.</param>
    /// <exception cref="SafetyViolationException">A limit was exceeded.</exception>
    public void Validate(int jointIndex, float position, float velocity, float torque, float kp, float kd)
    {
        if (!float.IsFinite(position) || !float.IsFinite(velocity) ||
            !float.IsFinite(torque) || !float.IsFinite(kp) || !float.IsFinite(kd))
        {
            throw new SafetyViolationException($"Joint[{jointIndex}].Finite", double.NaN, 0);
        }

        if (position > MaxPosition || position < MinPosition)
        {
            throw new SafetyViolationException(
                $"Joint[{jointIndex}].Position", position, position > MaxPosition ? MaxPosition : MinPosition);
        }

        if (MathF.Abs(velocity) > MaxVelocity)
        {
            throw new SafetyViolationException($"Joint[{jointIndex}].Velocity", velocity, MaxVelocity);
        }

        if (MathF.Abs(torque) > MaxTorque)
        {
            throw new SafetyViolationException($"Joint[{jointIndex}].Torque", torque, MaxTorque);
        }

        if (kp < 0f || kp > MaxKp)
        {
            throw new SafetyViolationException($"Joint[{jointIndex}].Kp", kp, MaxKp);
        }

        if (kd < 0f || kd > MaxKd)
        {
            throw new SafetyViolationException($"Joint[{jointIndex}].Kd", kd, MaxKd);
        }
    }

    /// <summary>
    /// Clamps a joint command into the safe envelope instead of throwing.
    /// </summary>
    /// <remarks>
    /// Use this on paths where a rejected command is worse than a reduced one — teleoperation, for
    /// example, where throwing would drop the operator's control authority mid-motion.
    /// </remarks>
    public void Clamp(ref float position, ref float velocity, ref float torque, ref float kp, ref float kd)
    {
        position = float.IsFinite(position) ? Math.Clamp(position, MinPosition, MaxPosition) : 0f;
        velocity = float.IsFinite(velocity) ? Math.Clamp(velocity, -MaxVelocity, MaxVelocity) : 0f;
        torque = float.IsFinite(torque) ? Math.Clamp(torque, -MaxTorque, MaxTorque) : 0f;
        kp = float.IsFinite(kp) ? Math.Clamp(kp, 0f, MaxKp) : 0f;
        kd = float.IsFinite(kd) ? Math.Clamp(kd, 0f, MaxKd) : 0f;
    }
}

/// <summary>
/// Whole-robot operating limits applied by the high-level controllers.
/// </summary>
public sealed class RobotSafetyOptions
{
    /// <summary>Configuration section name for binding from <c>appsettings.json</c>.</summary>
    public const string SectionName = "Unitree:Safety";

    /// <summary>Planar velocity ceilings.</summary>
    public VelocityLimits Velocity { get; set; } = VelocityLimits.Conservative;

    /// <summary>Per-joint actuation ceilings.</summary>
    public JointSafetyLimits Joints { get; set; } = JointSafetyLimits.Go2Default;

    /// <summary>
    /// How long the robot treats a locomotion command as valid after receiving it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This describes the <em>robot's</em> behaviour, not a client-side timer: firmware stops if no
    /// further request arrives within roughly this window. It is why continuous motion needs the
    /// command resent, and why <c>VelocityStream</c> pumps at 20 Hz by default.
    /// </para>
    /// <para>
    /// It is deliberately not used to expire a command the caller is deliberately holding. Doing that
    /// made the stream fight its own pump — the robot stopped half a second after every command, and
    /// holding a velocity for a second, which is what any dance step or patrol leg does, looked like a
    /// fault. Pass <c>commandTimeout</c> to <c>StartVelocityStream</c> if you want that behaviour.
    /// </para>
    /// </remarks>
    public TimeSpan CommandWatchdog { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long low-level state may be stale before the controller treats the link as lost.
    /// </summary>
    public TimeSpan StateTimeout { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Battery state-of-charge percentage below which motion commands are refused.</summary>
    public int MinBatterySocPercent { get; set; } = 15;

    /// <summary>Maximum motor temperature in °C before motion commands are refused.</summary>
    public int MaxMotorTemperatureCelsius { get; set; } = 80;

    /// <summary>
    /// Absolute roll or pitch beyond which the robot is considered to have fallen, radians.
    /// </summary>
    public float FallDetectionAngle { get; set; } = float.DegreesToRadians(50f);

    /// <summary>
    /// Whether a violated limit clamps the command (<see langword="true"/>) or throws
    /// <see cref="SafetyViolationException"/> (<see langword="false"/>).
    /// </summary>
    public bool ClampInsteadOfThrow { get; set; }

    /// <summary>Produces the default options for <paramref name="model"/>.</summary>
    public static RobotSafetyOptions ForModel(RobotModel model) => new()
    {
        Velocity = VelocityLimits.ForModel(model),
        Joints = JointSafetyLimits.ForModel(model),
    };
}
