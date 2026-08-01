using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Sensors;

namespace Unitree.Net.Ai;

/// <summary>
/// Read-only robot functions exposed to a language model.
/// </summary>
/// <remarks>
/// Safe to expose unconditionally: nothing here can move the robot. Keeping observation strictly
/// separate from actuation is what makes it possible to run a diagnostic assistant against a robot in
/// the field without any risk of it deciding to take a walk.
/// </remarks>
public sealed class RobotTelemetryPlugin(UnitreeRobot robot, TelemetryHub telemetry)
{
    private readonly UnitreeRobot _robot = robot ?? throw new ArgumentNullException(nameof(robot));
    private readonly TelemetryHub _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

    /// <summary>Reports the robot's overall status.</summary>
    [KernelFunction("get_robot_status")]
    [Description("Gets the robot's current status: connection state, battery, orientation, motor temperature and foot contact.")]
    public string GetRobotStatus()
    {
        ConnectionState state = _robot.RefreshConnectionState();
        TelemetrySnapshot? snapshot = _telemetry.GetSnapshot();

        if (snapshot is null)
        {
            return $"Model: {_robot.Model}. Connection: {state}. No telemetry has been received yet.";
        }

        TelemetrySnapshot value = snapshot.Value;
        var builder = new StringBuilder();

        builder.Append(CultureInfo.InvariantCulture, $"Model: {_robot.Model}. ");
        builder.Append(CultureInfo.InvariantCulture, $"Connection: {state}. ");
        builder.Append(CultureInfo.InvariantCulture, $"Battery: {value.Battery.StateOfChargePercent}% ");
        builder.Append(CultureInfo.InvariantCulture, $"({value.Battery.PackVoltage:0.0} V, {value.Battery.CurrentAmps:0.0} A). ");
        builder.Append(CultureInfo.InvariantCulture, $"Orientation: roll {float.RadiansToDegrees(value.Orientation.Roll):0.#}°, ");
        builder.Append(CultureInfo.InvariantCulture, $"pitch {float.RadiansToDegrees(value.Orientation.Pitch):0.#}°, ");
        builder.Append(CultureInfo.InvariantCulture, $"yaw {float.RadiansToDegrees(value.Orientation.Yaw):0.#}°. ");
        builder.Append(CultureInfo.InvariantCulture, $"Hottest motor: {value.MaxMotorTemperatureCelsius} °C. ");
        builder.Append(CultureInfo.InvariantCulture, $"Feet in contact: {value.FootContact.ContactCount} of 4. ");
        builder.Append(CultureInfo.InvariantCulture, $"Body height: {value.BodyHeight:0.00} m.");

        if (_robot.LowLevel.IsEmergencyStopped)
        {
            builder.Append(" WARNING: an emergency stop is currently latched.");
        }

        return builder.ToString();
    }

    /// <summary>Reports battery health.</summary>
    [KernelFunction("get_battery_status")]
    [Description("Gets battery state of charge, voltage, current, cycle count, cell imbalance and estimated remaining runtime.")]
    public string GetBatteryStatus()
    {
        BatteryStatus? battery = _telemetry.GetBattery();

        if (battery is null)
        {
            return "Battery telemetry is unavailable.";
        }

        BatteryStatus value = battery.Value;
        TimeSpan? remaining = value.EstimateRemaining();

        string remainingText = remaining is null
            ? value.IsCharging ? "charging" : "idle, no estimate"
            : $"about {remaining.Value.TotalMinutes:0} minutes remaining";

        string imbalance = value.HasCellImbalanceWarning
            ? $"WARNING: cell imbalance is {value.CellImbalanceMillivolts} mV, above the 50 mV healthy threshold"
            : $"cell imbalance {value.CellImbalanceMillivolts} mV (healthy)";

        return $"State of charge {value.StateOfChargePercent}%, {value.PackVoltage:0.0} V, " +
               $"{value.CurrentAmps:0.00} A, {value.CycleCount} cycles, {value.MaxTemperatureCelsius} °C; " +
               $"{remainingText}; {imbalance}.";
    }

    /// <summary>Reports the robot's odometry position.</summary>
    [KernelFunction("get_position")]
    [Description("Gets the robot's dead-reckoned position and heading in the odometry frame. This drifts over distance and resets on power cycle.")]
    public string GetPosition()
    {
        if (!_robot.TryGetSportState(out Messages.Go.SportModeState state))
        {
            return "Position telemetry is unavailable.";
        }

        System.Numerics.Vector3 position = state.GetPosition();
        float yaw = float.RadiansToDegrees(state.ImuState.ToEuler().Yaw);

        return $"Position x {position.X:0.00} m, y {position.Y:0.00} m, heading {yaw:0.#}°. " +
               "Note this is dead-reckoned odometry and accumulates drift.";
    }

    /// <summary>Reports whether the robot is safe to command into motion.</summary>
    [KernelFunction("check_ready_to_move")]
    [Description("Checks whether the robot is currently in a state where it is safe to command motion. Call this before any movement.")]
    public string CheckReadyToMove()
    {
        ConnectionState state = _robot.RefreshConnectionState();

        if (state != ConnectionState.Connected)
        {
            return $"NOT READY: the robot link is {state}.";
        }

        if (_robot.LowLevel.IsEmergencyStopped)
        {
            return "NOT READY: an emergency stop is latched and must be cleared by an operator.";
        }

        TelemetrySnapshot? snapshot = _telemetry.GetSnapshot();

        if (snapshot is null)
        {
            return "NOT READY: no telemetry has been received.";
        }

        TelemetrySnapshot value = snapshot.Value;
        RobotSafetyOptions safety = _robot.Options.Safety;

        if (value.Battery.StateOfChargePercent > 0 &&
            value.Battery.StateOfChargePercent < safety.MinBatterySocPercent)
        {
            return $"NOT READY: battery at {value.Battery.StateOfChargePercent}%, below the " +
                   $"{safety.MinBatterySocPercent}% minimum.";
        }

        if (value.MaxMotorTemperatureCelsius > safety.MaxMotorTemperatureCelsius)
        {
            return $"NOT READY: motors at {value.MaxMotorTemperatureCelsius} °C, above the " +
                   $"{safety.MaxMotorTemperatureCelsius} °C limit.";
        }

        if (MathF.Abs(value.Orientation.Roll) > safety.FallDetectionAngle ||
            MathF.Abs(value.Orientation.Pitch) > safety.FallDetectionAngle)
        {
            return "NOT READY: the robot appears to have fallen. Use recover_stand first.";
        }

        return "READY: the robot is connected, upright and within its safety limits.";
    }
}

/// <summary>
/// Motion functions exposed to a language model.
/// </summary>
/// <remarks>
/// <para>
/// These move a physical robot. Registration is gated behind
/// <see cref="AiOptions.ExposeMotionFunctions"/>, and actual invocation behind
/// <see cref="AiOptions.AllowAutomaticFunctionCalling"/>, so exposing them is a two-step decision.
/// </para>
/// <para>
/// Every function re-checks readiness before acting. A model may call functions in any order it likes,
/// including calling <c>move_forward</c> without ever having checked whether the robot is upright —
/// so the check cannot live only in the prompt.
/// </para>
/// </remarks>
public sealed class RobotMotionPlugin(
    UnitreeRobot robot,
    RobotTelemetryPlugin telemetryPlugin,
    ILogger<RobotMotionPlugin>? logger = null)
{
    private readonly UnitreeRobot _robot = robot ?? throw new ArgumentNullException(nameof(robot));

    private readonly RobotTelemetryPlugin _telemetryPlugin =
        telemetryPlugin ?? throw new ArgumentNullException(nameof(telemetryPlugin));

    private readonly ILogger<RobotMotionPlugin> _logger = logger ?? NullLogger<RobotMotionPlugin>.Instance;

    /// <summary>Stands the robot up.</summary>
    [KernelFunction("stand_up")]
    [Description("Makes the robot stand up to its normal height. Must be called before the robot can walk.")]
    public async Task<string> StandUpAsync(CancellationToken cancellationToken = default)
    {
        string readiness = _telemetryPlugin.CheckReadyToMove();

        if (!readiness.StartsWith("READY", StringComparison.Ordinal))
        {
            return $"Refused: {readiness}";
        }

        _logger.LogInformation("AI workflow requested stand up.");
        await _robot.Sport.StandUpAsync(cancellationToken).ConfigureAwait(false);
        await _robot.Sport.BalanceStandAsync(cancellationToken).ConfigureAwait(false);
        return "The robot is standing and ready to accept movement commands.";
    }

    /// <summary>Lowers the robot.</summary>
    [KernelFunction("stand_down")]
    [Description("Lowers the robot into its resting crouch.")]
    public async Task<string> StandDownAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("AI workflow requested stand down.");
        await _robot.Sport.StandDownAsync(cancellationToken).ConfigureAwait(false);
        return "The robot has lowered itself.";
    }

    /// <summary>Walks the robot a set distance.</summary>
    [KernelFunction("move_forward")]
    [Description("Walks the robot forward or backward a given distance in metres. Negative values walk backwards. Maximum 10 metres per call.")]
    public async Task<string> MoveForwardAsync(
        [Description("Distance in metres. Negative walks backwards.")] double distanceMetres,
        CancellationToken cancellationToken = default)
    {
        string readiness = _telemetryPlugin.CheckReadyToMove();

        if (!readiness.StartsWith("READY", StringComparison.Ordinal))
        {
            return $"Refused: {readiness}";
        }

        if (Math.Abs(distanceMetres) > 10)
        {
            return "Refused: a single move is limited to 10 metres. Break the motion into shorter legs.";
        }

        if (Math.Abs(distanceMetres) < 0.01)
        {
            return "Nothing to do: the requested distance is effectively zero.";
        }

        const float Speed = 0.4f;
        float direction = distanceMetres > 0 ? 1f : -1f;
        var duration = TimeSpan.FromSeconds(Math.Abs(distanceMetres) / Speed);

        _logger.LogInformation(
            "AI workflow requested a {Distance:0.##} m move, executing for {Duration:0.#} s.",
            distanceMetres,
            duration.TotalSeconds);

        using VelocityStream stream = _robot.Sport.StartVelocityStream();
        stream.Command = new VelocityCommand(Speed * direction, 0f, 0f);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stream.Stop();
        }

        return $"Moved approximately {distanceMetres:0.##} m. Distance is open-loop and approximate.";
    }

    /// <summary>Turns the robot in place.</summary>
    [KernelFunction("turn")]
    [Description("Turns the robot in place by a given angle in degrees. Positive turns left (counter-clockwise). Maximum 360 degrees per call.")]
    public async Task<string> TurnAsync(
        [Description("Angle in degrees. Positive turns left.")] double degrees,
        CancellationToken cancellationToken = default)
    {
        string readiness = _telemetryPlugin.CheckReadyToMove();

        if (!readiness.StartsWith("READY", StringComparison.Ordinal))
        {
            return $"Refused: {readiness}";
        }

        if (Math.Abs(degrees) > 360)
        {
            return "Refused: a single turn is limited to 360 degrees.";
        }

        const float YawRate = 0.6f;
        float direction = degrees > 0 ? 1f : -1f;
        var duration = TimeSpan.FromSeconds(Math.Abs(float.DegreesToRadians((float)degrees)) / YawRate);

        _logger.LogInformation("AI workflow requested a {Degrees:0.#}° turn.", degrees);

        using VelocityStream stream = _robot.Sport.StartVelocityStream();
        stream.Command = new VelocityCommand(0f, 0f, YawRate * direction);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stream.Stop();
        }

        return $"Turned approximately {degrees:0.#}°. Rotation is open-loop and approximate.";
    }

    /// <summary>Stops the robot immediately.</summary>
    [KernelFunction("stop")]
    [Description("Immediately stops all robot motion. Always available, even when other commands are refused.")]
    public async Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("AI workflow requested an immediate stop.");
        await _robot.Sport.StopMoveAsync(cancellationToken).ConfigureAwait(false);
        return "The robot has stopped.";
    }

    /// <summary>Recovers the robot to standing after a fall.</summary>
    [KernelFunction("recover_stand")]
    [Description("Recovers the robot to a standing posture after it has fallen or been lying down.")]
    public async Task<string> RecoverStandAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("AI workflow requested recovery stand.");
        await _robot.Sport.RecoveryStandAsync(cancellationToken).ConfigureAwait(false);
        return "Recovery stand executed. Check the robot's status to confirm it is upright.";
    }

    /// <summary>Waves a front leg.</summary>
    [KernelFunction("greet")]
    [Description("Makes the robot wave a front leg in greeting. Requires clear space around the robot.")]
    public async Task<string> GreetAsync(CancellationToken cancellationToken = default)
    {
        string readiness = _telemetryPlugin.CheckReadyToMove();

        if (!readiness.StartsWith("READY", StringComparison.Ordinal))
        {
            return $"Refused: {readiness}";
        }

        await _robot.Sport.HelloAsync(cancellationToken).ConfigureAwait(false);
        return "The robot waved.";
    }
}
