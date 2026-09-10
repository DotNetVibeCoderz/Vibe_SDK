using Microsoft.Extensions.Logging;
using PollenRobotics.Net.Core.Geometry;
using PollenRobotics.Net.Core.Robots;

namespace PollenRobotics.Net.Core.Safety;

/// <summary>
/// Applies <see cref="RobotSafetyOptions"/> to commanded joint vectors.
/// </summary>
/// <remarks>
/// This sits on the hot path - every <c>SetTarget</c> at up to 500 Hz goes through it - so it
/// works in place over a span and allocates nothing. The logger call is behind an
/// <see cref="ILogger.IsEnabled"/> check for the same reason.
/// </remarks>
public sealed class JointLimitGuard(RobotDescription description, RobotSafetyOptions options, ILogger? logger = null)
{
    private readonly RobotDescription _description = description;
    private readonly RobotSafetyOptions _options = options;
    private readonly ILogger? _logger = logger;

    /// <summary>The description this guard was built for.</summary>
    public RobotDescription Description => _description;

    /// <summary>The options in force.</summary>
    public RobotSafetyOptions Options => _options;

    /// <summary>
    /// Validates and, when configured to, clamps a full joint vector in place.
    /// </summary>
    /// <param name="positions">Joint positions in radians, in wire order.</param>
    /// <exception cref="RobotSafetyException">A joint is out of range and clamping is off.</exception>
    public void Apply(Span<double> positions)
    {
        if (!_options.EnforceJointLimits)
        {
            return;
        }

        if (positions.Length != _description.JointCount)
        {
            throw new ArgumentException(
                $"{_description.DisplayName} has {_description.JointCount} joints, got {positions.Length}.",
                nameof(positions));
        }

        for (int i = 0; i < positions.Length; i++)
        {
            JointDescriptor joint = _description.Joints[i];
            Angle value = Angle.FromRadians(positions[i]);
            if (joint.Contains(value))
            {
                continue;
            }

            if (!_options.ClampInsteadOfThrow)
            {
                throw new RobotSafetyException(joint.Name, positions[i], joint.Lower.Radians, joint.Upper.Radians);
            }

            positions[i] = joint.Clamp(value).Radians;

            if (_options.WarnOnClamp && _logger?.IsEnabled(LogLevel.Warning) == true)
            {
                _logger.LogWarning("Clamped {Joint} from {Requested:0.#} deg to {Applied:0.#} deg.",
                    joint.Name, value.Degrees, Angle.FromRadians(positions[i]).Degrees);
            }
        }
    }

    /// <summary>Validates and clamps a single named joint.</summary>
    public double Apply(string jointName, double radians)
    {
        if (!_options.EnforceJointLimits)
        {
            return radians;
        }

        JointDescriptor joint = _description[jointName];
        Angle value = Angle.FromRadians(radians);
        if (joint.Contains(value))
        {
            return radians;
        }

        if (!_options.ClampInsteadOfThrow)
        {
            throw new RobotSafetyException(joint.Name, radians, joint.Lower.Radians, joint.Upper.Radians);
        }

        double clamped = joint.Clamp(value).Radians;
        if (_options.WarnOnClamp && _logger?.IsEnabled(LogLevel.Warning) == true)
        {
            _logger.LogWarning("Clamped {Joint} from {Requested:0.#} deg to {Applied:0.#} deg.",
                joint.Name, value.Degrees, Angle.FromRadians(clamped).Degrees);
        }

        return clamped;
    }

    /// <summary>
    /// Rejects a commanded step larger than <see cref="RobotSafetyOptions.MaxJointStepRadians"/>.
    /// </summary>
    /// <remarks>
    /// A large step is how a servo gets commanded from one end of its range to the other in a
    /// single tick, which on real hardware is a bang rather than a movement.
    /// </remarks>
    public void CheckStep(ReadOnlySpan<double> previous, ReadOnlySpan<double> next)
    {
        if (_options.MaxJointStepRadians is not { } maxStep || previous.Length != next.Length)
        {
            return;
        }

        for (int i = 0; i < next.Length; i++)
        {
            double step = Math.Abs(next[i] - previous[i]);
            if (step > maxStep)
            {
                throw new RobotSafetyException(_description.Joints[i].Name, next[i], previous[i] - maxStep, previous[i] + maxStep);
            }
        }
    }
}
