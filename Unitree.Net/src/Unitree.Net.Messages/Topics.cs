using Unitree.Net.Core;

namespace Unitree.Net.Messages;

/// <summary>
/// DDS topic names used by Unitree firmware.
/// </summary>
/// <remarks>
/// The <c>rt/</c> prefix is ROS 2's topic-name mangling for <c>/topic</c>. Unitree keeps it even on the
/// non-ROS path, which is why these strings look ROS-flavoured on a plain DDS link.
/// </remarks>
public static class Topics
{
    /// <summary>Low-level motor command, host to robot. 500 Hz.</summary>
    public const string LowCommand = "rt/lowcmd";

    /// <summary>Low-level state, robot to host. 500 Hz.</summary>
    public const string LowState = "rt/lowstate";

    /// <summary>Low-frequency mirror of <see cref="LowState"/>, published at a few hertz.</summary>
    public const string LowStateLowFrequency = "rt/lf/lowstate";

    /// <summary>High-level locomotion state, robot to host.</summary>
    public const string SportModeState = "rt/sportmodestate";

    /// <summary>Low-frequency mirror of <see cref="SportModeState"/>.</summary>
    public const string SportModeStateLowFrequency = "rt/lf/sportmodestate";

    /// <summary>Wireless remote controller state.</summary>
    public const string WirelessController = "rt/wirelesscontroller";

    /// <summary>LiDAR point cloud.</summary>
    public const string LidarCloud = "rt/utlidar/cloud";

    /// <summary>LiDAR-integrated IMU.</summary>
    public const string LidarImu = "rt/utlidar/imu";

    /// <summary>LiDAR-derived robot pose.</summary>
    public const string LidarPose = "rt/utlidar/robot_pose";

    /// <summary>Front camera compressed video stream.</summary>
    public const string FrontVideoStream = "rt/frontvideostream";

    /// <summary>Audio playback and capture channel.</summary>
    public const string AudioHub = "rt/audiohub";

    /// <summary>Multi-modal state for humanoids.</summary>
    public const string MultipleState = "rt/multiplestate";

    /// <summary>Arm SDK command topic for humanoid manipulators.</summary>
    public const string ArmSdk = "rt/arm_sdk";

    /// <summary>Builds the request topic for a named service.</summary>
    /// <param name="serviceName">Service name, e.g. <c>sport</c> or <c>obstacles_avoid</c>.</param>
    public static string RequestTopic(string serviceName) => $"rt/api/{serviceName}/request";

    /// <summary>Builds the response topic for a named service.</summary>
    /// <param name="serviceName">Service name, e.g. <c>sport</c> or <c>obstacles_avoid</c>.</param>
    public static string ResponseTopic(string serviceName) => $"rt/api/{serviceName}/response";
}

/// <summary>
/// Names of the request/response services exposed by Unitree firmware.
/// </summary>
public static class Services
{
    /// <summary>High-level locomotion service.</summary>
    public const string Sport = "sport";

    /// <summary>Obstacle avoidance service.</summary>
    public const string ObstacleAvoid = "obstacles_avoid";

    /// <summary>Vui service — LEDs, volume, brightness.</summary>
    public const string Vui = "vui";

    /// <summary>Audio playback service.</summary>
    public const string AudioHub = "audiohub";

    /// <summary>Robot state service — starting and stopping on-board services.</summary>
    public const string RobotState = "robot_state";

    /// <summary>Motion switcher — selects which motion controller owns the robot.</summary>
    public const string MotionSwitcher = "motion_switcher";

    /// <summary>Humanoid arm control service.</summary>
    public const string ArmAction = "arm";
}

/// <summary>
/// Unitree sport-mode API identifiers.
/// </summary>
/// <remarks>
/// These are published on <c>rt/api/sport/request</c> in the <c>api_id</c> field. Not every identifier is
/// implemented on every platform; unsupported ones return a non-zero status rather than failing silently.
/// </remarks>
public static class SportApi
{
    /// <summary>Enter damping mode. The robot yields to gravity under joint damping.</summary>
    public const long Damp = 1001;

    /// <summary>Enter balanced standing, which is the mode that accepts velocity commands.</summary>
    public const long BalanceStand = 1002;

    /// <summary>Stop all motion but stay standing.</summary>
    public const long StopMove = 1003;

    /// <summary>Stand up to the nominal body height.</summary>
    public const long StandUp = 1004;

    /// <summary>Lower the body to the crouched position.</summary>
    public const long StandDown = 1005;

    /// <summary>Recover to standing from a fallen or lying posture.</summary>
    public const long RecoveryStand = 1006;

    /// <summary>Set body orientation as roll/pitch/yaw while standing.</summary>
    public const long Euler = 1007;

    /// <summary>Command a body-frame velocity. Must be refreshed continuously.</summary>
    public const long Move = 1008;

    /// <summary>Sit down.</summary>
    public const long Sit = 1009;

    /// <summary>Rise from sitting.</summary>
    public const long RiseSit = 1010;

    /// <summary>Change gait type.</summary>
    public const long SwitchGait = 1011;

    /// <summary>Trigger a scripted action.</summary>
    public const long Trigger = 1012;

    /// <summary>Set standing body height offset, metres.</summary>
    public const long BodyHeight = 1013;

    /// <summary>Set swing height, metres.</summary>
    public const long FootRaiseHeight = 1014;

    /// <summary>Set the speed level.</summary>
    public const long SpeedLevel = 1015;

    /// <summary>Wave a front leg.</summary>
    public const long Hello = 1016;

    /// <summary>Perform the stretch routine.</summary>
    public const long Stretch = 1017;

    /// <summary>Follow a pre-planned trajectory.</summary>
    public const long TrajectoryFollow = 1018;

    /// <summary>Enable or disable continuous gait.</summary>
    public const long ContinuousGait = 1019;

    /// <summary>Perform the content routine.</summary>
    public const long Content = 1020;

    /// <summary>Roll onto the robot's back.</summary>
    public const long Wallow = 1021;

    /// <summary>Dance routine one.</summary>
    public const long Dance1 = 1022;

    /// <summary>Dance routine two.</summary>
    public const long Dance2 = 1023;

    /// <summary>Query the current body height.</summary>
    public const long GetBodyHeight = 1024;

    /// <summary>Query the current swing height.</summary>
    public const long GetFootRaiseHeight = 1025;

    /// <summary>Query the current speed level.</summary>
    public const long GetSpeedLevel = 1026;

    /// <summary>Enable or disable the physical joystick.</summary>
    public const long SwitchJoystick = 1027;

    /// <summary>Enter or leave pose mode.</summary>
    public const long Pose = 1028;

    /// <summary>Scrape routine.</summary>
    public const long Scrape = 1029;

    /// <summary>Front flip. Requires clear space and a charged battery.</summary>
    public const long FrontFlip = 1030;

    /// <summary>Front jump.</summary>
    public const long FrontJump = 1031;

    /// <summary>Front pounce.</summary>
    public const long FrontPounce = 1032;

    /// <summary>Wiggle hips routine.</summary>
    public const long WiggleHips = 1033;

    /// <summary>Query controller state.</summary>
    public const long GetState = 1034;

    /// <summary>Enable or disable economic gait.</summary>
    public const long EconomicGait = 1035;

    /// <summary>Heart gesture routine.</summary>
    public const long Heart = 1036;
}

/// <summary>
/// Identifiers for the robot-state service, used to start and stop on-board services.
/// </summary>
public static class RobotStateApi
{
    /// <summary>Start a named service.</summary>
    public const long ServiceSwitch = 1001;

    /// <summary>Set the report frequency.</summary>
    public const long SetReportFrequency = 1002;

    /// <summary>List services and their running state.</summary>
    public const long ServiceList = 1003;
}

/// <summary>
/// Identifiers for the motion-switcher service.
/// </summary>
/// <remarks>
/// Releasing the current motion controller is a prerequisite for low-level control: while the sport
/// service owns the motors, <c>rt/lowcmd</c> is ignored.
/// </remarks>
public static class MotionSwitcherApi
{
    /// <summary>Check which motion controller currently owns the robot.</summary>
    public const long CheckMode = 1001;

    /// <summary>Select a motion controller by name.</summary>
    public const long SelectMode = 1002;

    /// <summary>Release the active motion controller, freeing the motors for low-level control.</summary>
    public const long ReleaseMode = 1003;

    /// <summary>Set the silent flag.</summary>
    public const long SetSilent = 1004;

    /// <summary>Query the silent flag.</summary>
    public const long GetSilent = 1005;
}

/// <summary>
/// Resolves the topic set appropriate to a robot model.
/// </summary>
public static class TopicResolver
{
    /// <summary>
    /// Gets the low-level command topic for <paramref name="model"/>.
    /// </summary>
    /// <remarks>
    /// Quadrupeds and humanoids share the topic name but not the payload type: the former carries
    /// <c>unitree_go</c> messages, the latter <c>unitree_hg</c>. Subscribing with the wrong type yields
    /// garbage rather than an error, so the model must be known up front.
    /// </remarks>
    public static string GetLowCommandTopic(RobotModel model) => Topics.LowCommand;

    /// <summary>Gets the low-level state topic for <paramref name="model"/>.</summary>
    public static string GetLowStateTopic(RobotModel model) => Topics.LowState;

    /// <summary>Gets the DDS type name of the low-level command for <paramref name="model"/>.</summary>
    public static string GetLowCommandTypeName(RobotModel model) =>
        RobotModelInfo.GetIdlFamily(model) == IdlFamily.Go
            ? "unitree_go::msg::dds_::LowCmd_"
            : "unitree_hg::msg::dds_::LowCmd_";

    /// <summary>Gets the DDS type name of the low-level state for <paramref name="model"/>.</summary>
    public static string GetLowStateTypeName(RobotModel model) =>
        RobotModelInfo.GetIdlFamily(model) == IdlFamily.Go
            ? "unitree_go::msg::dds_::LowState_"
            : "unitree_hg::msg::dds_::LowState_";
}
