using Microsoft.Extensions.Diagnostics.HealthChecks;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Sensors;

namespace Unitree.Net.Diagnostics;

/// <summary>
/// Reports robot health through the standard ASP.NET Core health-check pipeline.
/// </summary>
/// <remarks>
/// Distinguishes degraded from unhealthy deliberately. A robot on a low battery is still reachable and
/// still answering — flagging that as unhealthy would take a deployment out of rotation for what is
/// really an operational warning.
/// </remarks>
public sealed class RobotHealthCheck(UnitreeRobot robot, TelemetryHub telemetry) : IHealthCheck
{
    private readonly UnitreeRobot _robot = robot ?? throw new ArgumentNullException(nameof(robot));
    private readonly TelemetryHub _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ConnectionState state = _robot.RefreshConnectionState();
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model"] = _robot.Model.ToString(),
            ["connectionState"] = state.ToString(),
            ["transport"] = _robot.Participant.Transport.Name,
            ["lowStateCount"] = _telemetry.LowStateCount,
        };

        if (state is ConnectionState.Disconnected or ConnectionState.Faulted)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Robot link is {state}.",
                data: data));
        }

        if (state == ConnectionState.Stale)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Telemetry has gone stale.", data: data));
        }

        TelemetrySnapshot? snapshot = _telemetry.GetSnapshot();

        if (snapshot is null)
        {
            return Task.FromResult(HealthCheckResult.Degraded("No telemetry has been received yet.", data: data));
        }

        TelemetrySnapshot value = snapshot.Value;
        RobotSafetyOptions safety = _robot.Options.Safety;

        data["batterySoc"] = value.Battery.StateOfChargePercent;
        data["batteryVoltage"] = value.Battery.PackVoltage;
        data["cellImbalanceMv"] = value.Battery.CellImbalanceMillivolts;
        data["maxMotorTemperature"] = value.MaxMotorTemperatureCelsius;
        data["rollDegrees"] = float.RadiansToDegrees(value.Orientation.Roll);
        data["pitchDegrees"] = float.RadiansToDegrees(value.Orientation.Pitch);

        var warnings = new List<string>();

        if (_robot.LowLevel.IsEmergencyStopped)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "An emergency stop is latched on the low-level controller.",
                data: data));
        }

        if (value.MaxMotorTemperatureCelsius > safety.MaxMotorTemperatureCelsius)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Motor temperature {value.MaxMotorTemperatureCelsius} °C exceeds the {safety.MaxMotorTemperatureCelsius} °C limit.",
                data: data));
        }

        if (value.Battery.StateOfChargePercent > 0 &&
            value.Battery.StateOfChargePercent < safety.MinBatterySocPercent)
        {
            warnings.Add($"battery at {value.Battery.StateOfChargePercent}%");
        }

        if (value.MaxMotorTemperatureCelsius > safety.MaxMotorTemperatureCelsius - 10)
        {
            warnings.Add($"motors at {value.MaxMotorTemperatureCelsius} °C, approaching the limit");
        }

        if (value.Battery.HasCellImbalanceWarning)
        {
            warnings.Add($"cell imbalance {value.Battery.CellImbalanceMillivolts} mV");
        }

        return Task.FromResult(warnings.Count > 0
            ? HealthCheckResult.Degraded(string.Join("; ", warnings), data: data)
            : HealthCheckResult.Healthy("Robot is nominal.", data));
    }
}
