using System.Numerics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unitree.Net.Control;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Messages.Go;

namespace Unitree.Net.Ros2;

/// <summary>
/// Topic names and frames the bridge uses.
/// </summary>
public sealed class Ros2BridgeOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Unitree:Ros2";

    /// <summary>Topic carrying incoming velocity commands.</summary>
    public string CommandVelocityTopic { get; set; } = "rt/cmd_vel";

    /// <summary>Topic to publish IMU data on.</summary>
    public string ImuTopic { get; set; } = "rt/imu";

    /// <summary>Topic to publish odometry on.</summary>
    public string OdometryTopic { get; set; } = "rt/odom";

    /// <summary>Frame identifier for IMU messages.</summary>
    public string ImuFrameId { get; set; } = "imu_link";

    /// <summary>Frame identifier for the odometry parent frame.</summary>
    public string OdometryFrameId { get; set; } = "odom";

    /// <summary>Frame identifier for the robot body.</summary>
    public string BaseFrameId { get; set; } = "base_link";

    /// <summary>How often telemetry is republished.</summary>
    public int PublishRateHz { get; set; } = 50;

    /// <summary>Whether incoming <c>cmd_vel</c> messages actually drive the robot.</summary>
    /// <remarks>
    /// Defaults to disabled. Bridging telemetry outward is read-only and harmless; accepting motion
    /// commands from any ROS 2 node on the domain is not, and should be a deliberate choice.
    /// </remarks>
    public bool AcceptVelocityCommands { get; set; }
}

/// <summary>
/// Bridges a Unitree robot into a ROS 2 graph.
/// </summary>
/// <remarks>
/// <para>
/// Unitree's DDS traffic is already RTPS with ROS 2's <c>rt/</c> topic mangling, so no protocol
/// translation is needed — only message-type translation. This bridge republishes the robot's IMU and
/// odometry as <c>sensor_msgs/Imu</c> and <c>nav_msgs/Odometry</c>, and optionally subscribes to
/// <c>geometry_msgs/Twist</c> for velocity commands.
/// </para>
/// <para>
/// The practical payoff is that Nav2, RViz, rosbag and the rest of the ROS 2 ecosystem work against the
/// robot without any of them knowing that Unitree's own API exists.
/// </para>
/// </remarks>
public sealed class Ros2Bridge : BackgroundService
{
    private readonly UnitreeRobot _robot;
    private readonly Ros2BridgeOptions _options;
    private readonly ILogger<Ros2Bridge> _logger;

    private IDdsPublisher<Ros2Imu>? _imuPublisher;
    private IDdsPublisher<Ros2Odometry>? _odometryPublisher;
    private IDdsSubscriber<Ros2Twist>? _twistSubscriber;
    private VelocityStream? _velocityStream;
    private long _imuPublishCount;
    private long _odometryPublishCount;
    private long _commandCount;

    /// <summary>Creates a bridge for <paramref name="robot"/>.</summary>
    public Ros2Bridge(UnitreeRobot robot, Ros2BridgeOptions? options = null, ILogger<Ros2Bridge>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(robot);
        _robot = robot;
        _options = options ?? new Ros2BridgeOptions();
        _logger = logger ?? NullLogger<Ros2Bridge>.Instance;
    }

    /// <summary>Number of IMU messages published.</summary>
    public long ImuPublishCount => Interlocked.Read(ref _imuPublishCount);

    /// <summary>Number of odometry messages published.</summary>
    public long OdometryPublishCount => Interlocked.Read(ref _odometryPublishCount);

    /// <summary>Number of velocity commands accepted.</summary>
    public long CommandCount => Interlocked.Read(ref _commandCount);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IDdsParticipant participant = _robot.Participant;

        _imuPublisher = participant.CreatePublisher<Ros2Imu>(_options.ImuTopic);
        _odometryPublisher = participant.CreatePublisher<Ros2Odometry>(_options.OdometryTopic);

        if (_options.AcceptVelocityCommands)
        {
            _twistSubscriber = participant.CreateSubscriber<Ros2Twist>(_options.CommandVelocityTopic, 16);

            // An explicit command timeout, which is not the default. Here the thing driving the robot
            // is a ROS 2 node on another machine, and the bridge only reassigns Command when a Twist
            // arrives — so a publisher that dies, or a network that drops, must stop the robot rather
            // than let it coast on the last message. That is the whole reason the opt-in exists.
            _velocityStream = _robot.Sport.StartVelocityStream(
                commandTimeout: _robot.Options.Safety.CommandWatchdog);

            _logger.LogWarning(
                "ROS 2 bridge is accepting velocity commands on {Topic}; any node on this DDS domain can now drive the robot.",
                _options.CommandVelocityTopic);
        }

        _logger.LogInformation(
            "ROS 2 bridge started: publishing {ImuTopic} and {OdometryTopic} at {Rate} Hz.",
            _options.ImuTopic,
            _options.OdometryTopic,
            _options.PublishRateHz);

        Task commandTask = _options.AcceptVelocityCommands
            ? PumpVelocityCommandsAsync(stoppingToken)
            : Task.CompletedTask;

        await Task.WhenAll(PublishTelemetryAsync(stoppingToken), commandTask).ConfigureAwait(false);
    }

    private async Task PublishTelemetryAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / _options.PublishRateHz));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_robot.TryGetLowState(out LowState lowState))
                {
                    PublishImu(in lowState);
                }

                if (_robot.TryGetSportState(out SportModeState sportState))
                {
                    PublishOdometry(in sportState);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void PublishImu(in LowState state)
    {
        ImuState imu = state.ImuState;

        var message = new Ros2Imu
        {
            Header = Ros2Header.Now(_options.ImuFrameId),
            Orientation = imu.ToQuaternion(),
            AngularVelocity = new Vector3(imu.Gyroscope[0], imu.Gyroscope[1], imu.Gyroscope[2]),
            LinearAcceleration = new Vector3(imu.Accelerometer[0], imu.Accelerometer[1], imu.Accelerometer[2]),
        };

        _imuPublisher!.Publish(message);
        Interlocked.Increment(ref _imuPublishCount);
    }

    private void PublishOdometry(in SportModeState state)
    {
        Vector3 worldVelocity = state.GetVelocity();
        float yaw = state.ImuState.ToEuler().Yaw;

        // nav_msgs/Odometry expects the twist in the child frame, not the header frame, so the
        // world-frame velocity the robot reports has to be rotated into the body frame first.
        Vector2 bodyVelocity = RobotMath.WorldToBody(new Vector2(worldVelocity.X, worldVelocity.Y), yaw);

        var message = new Ros2Odometry
        {
            Header = Ros2Header.Now(_options.OdometryFrameId),
            ChildFrameId = _options.BaseFrameId,
            Pose = new Pose(state.GetPosition(), state.ImuState.ToQuaternion()),
            Twist = new Ros2Twist
            {
                Linear = new Vector3(bodyVelocity.X, bodyVelocity.Y, 0f),
                Angular = new Vector3(0f, 0f, state.YawSpeed),
            },
        };

        _odometryPublisher!.Publish(message);
        Interlocked.Increment(ref _odometryPublishCount);
    }

    private async Task PumpVelocityCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (Ros2Twist twist in
                _twistSubscriber!.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Assigning the stream's command also refreshes its watchdog, so a ROS 2 publisher that
                // stops sending causes the robot to stop rather than coast on the last command.
                _velocityStream!.Command = twist.ToVelocityCommand();
                Interlocked.Increment(ref _commandCount);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Velocity command pump failed.");
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _velocityStream?.Dispose();
        _twistSubscriber?.Dispose();
        _imuPublisher?.Dispose();
        _odometryPublisher?.Dispose();
        base.Dispose();
    }
}
