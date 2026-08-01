# ROS 2 bridge

## Why this is small

Unitree's DDS traffic is already RTPS with ROS 2's `rt/` topic-name mangling. There is no protocol
translation to do — only **message-type** translation. The bridge republishes the robot's telemetry as
standard ROS 2 types and optionally accepts `geometry_msgs/Twist` for velocity commands.

The payoff is that Nav2, RViz, `ros2 bag` and the rest of the ecosystem work against the robot without
any of them knowing Unitree's own API exists.

## Setup

```csharp
builder.Services.AddUnitreeRobot(builder.Configuration);
builder.Services.AddUnitreeRobotHostedConnection();
builder.Services.AddUnitreeRos2Bridge(options =>
{
    options.ImuTopic = "rt/imu";
    options.OdometryTopic = "rt/odom";
    options.CommandVelocityTopic = "rt/cmd_vel";
    options.PublishRateHz = 50;

    // Off by default. See the warning below.
    options.AcceptVelocityCommands = false;
});
```

The bridge is a `BackgroundService`, so it starts and stops with the host.

## Topics

**Published:**

| Topic | Type | Frame | Source |
|---|---|---|---|
| `rt/imu` | `sensor_msgs/Imu` | `imu_link` | `rt/lowstate` |
| `rt/odom` | `nav_msgs/Odometry` | `odom` → `base_link` | `rt/sportmodestate` |

**Subscribed** (only when `AcceptVelocityCommands` is set):

| Topic | Type |
|---|---|
| `rt/cmd_vel` | `geometry_msgs/Twist` |

`rt/` is ROS 2's mangling of `/`, so `rt/cmd_vel` is `/cmd_vel` to any ROS 2 node.

## ⚠ Accepting velocity commands

`AcceptVelocityCommands` defaults to **false**, and that default is deliberate.

Publishing telemetry outward is read-only and harmless. Accepting motion commands means **any node on
the DDS domain can drive the robot** — including a stray `teleop_twist_keyboard` someone left running,
or a Nav2 stack still pointed at a simulation. Turning it on should be a considered decision.

When enabled, commands flow through the same `VelocityStream` as everything else, so the safety envelope
applies. The bridge also passes an explicit `commandTimeout`, which most callers do not: the thing
driving the robot is a node on another machine, and the bridge only refreshes the command when a
`Twist` arrives. A publisher that dies, or a network that drops, therefore stops the robot rather than
letting it coast on the last message.

## Conventions that had to be handled

**Frames match.** ROS 2 uses x forward, y left, z up, which is also Unitree's body frame. No axis
remapping is needed — a pleasant surprise given how often this goes wrong between ecosystems.

**Twist is in the child frame.** `nav_msgs/Odometry` expects the twist in `base_link`, but the robot
reports velocity in the world frame. The bridge rotates it:

```csharp
Vector2 bodyVelocity = RobotMath.WorldToBody(new Vector2(world.X, world.Y), yaw);
```

Publishing world-frame velocity as if it were body-frame is a subtle bug that only shows up when the
robot is not facing along +X.

**Covariances are honest.** IMU covariances lead with `-1`, ROS 2's convention for "not reported" —
Unitree does not publish them, and claiming a zero covariance would tell a consuming EKF the measurement
is perfect, making it trust the IMU absolutely and diverge. Odometry carries a diagonal reflecting the
drift the robot actually exhibits, so a filter can weight it sensibly instead of over-trusting it.

**`odom` is local only.** Unitree odometry drifts and resets on power cycle. That is exactly what the
ROS 2 `odom` frame already promises, so a localisation node supplying `map` → `odom` completes the
picture in the standard way.

## Checking it

```bash
ros2 topic list
ros2 topic echo /odom --once
ros2 topic hz /imu

# Drive it, once AcceptVelocityCommands is enabled:
ros2 topic pub /cmd_vel geometry_msgs/msg/Twist "{linear: {x: 0.3}}" -r 10
```

`ros2 topic hz /imu` should report close to the configured `PublishRateHz`.

## Adding a message type

1. Implement `ICdrSerializable<T>` in `Unitree.Net.Ros2`, matching the ROS 2 IDL field order —
   `geometry_msgs` and `nav_msgs` use `float64` throughout, not `float32`.
2. Publish or subscribe it in `Ros2Bridge`.
3. For the native transport, register the descriptor in the shim and in
   `CycloneDdsTransport.ResolveTypeName`. See [`native/README.md`](../native/README.md).

## Limitations

- **No TF.** Publish `tf2` transforms from a separate node if you need the tree.
- **No point clouds.** `sensor_msgs/PointCloud2` decoding exists in `Unitree.Net.Sensors`, but the
  bridge does not republish it; a frame is large and the cost is rarely worth it.
- **No ROS 2 services or actions.** Only topics.
