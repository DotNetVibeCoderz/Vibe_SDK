# Navigation

## What `WaypointNavigator` actually is

A proportional controller over the robot's **own dead-reckoned odometry**. It drives to a waypoint,
detects when it has stopped making progress, and gives up rather than pushing indefinitely.

That is genuinely useful for short legs, a patrol route with periodic re-localisation, or a demo. It is
**not** a substitute for SLAM, and there is no obstacle avoidance in it at all.

## Odometry drift

The robot dead-reckons position from leg odometry fused with the IMU. Two consequences shape everything
here:

- **It drifts** — typically a few percent of distance travelled. Over 50 m that is a metre or more, and
  the error accumulates rather than averaging out.
- **It resets on power cycle.** The origin is wherever the robot booted.

So `SportModeState.Position` is locally consistent and globally meaningless. This matches the ROS 2
`odom` frame convention exactly, which is why the ROS 2 bridge publishes it as `odom` → `base_link` and
leaves any global fix to a localisation node.

## Usage

```csharp
var navigator = new WaypointNavigator(robot, new NavigationOptions
{
    UpdateRateHz = 20,
    StallTimeout = TimeSpan.FromSeconds(8),
});

await robot.Sport.StandUpAsync();
await robot.Sport.BalanceStandAsync();

NavigationResult result = await navigator.FollowRouteAsync(
[
    Waypoint.At(2.0f, 0.0f),
    Waypoint.At(2.0f, 2.0f),
    new Waypoint(new Vector3(0f, 0f, 0f), ToleranceMetres: 0.2f, FinalHeading: 0f),
]);
```

`FollowRouteAsync` stops at the first failure and returns that leg's result rather than blindly
continuing to the next waypoint from an unknown position.

## Results

| Result | Meaning |
|---|---|
| `Arrived` | Within tolerance, and aligned if a final heading was requested |
| `Stalled` | No progress for `StallTimeout` — usually an obstacle |
| `Cancelled` | The caller cancelled |
| `NoOdometry` | No locomotion telemetry; nothing to navigate by |

## Two behaviours worth understanding

**Turn-in-place above 45° of heading error.** Driving forward while badly misaligned produces a long arc,
and in a corridor that arc is a collision. Turning first is slower but predictable. Tune with
`TurnInPlaceThreshold`.

**Stall detection compares against a reference that only advances on real progress.** A naive "did the
distance change since last tick" check would fire on slow-but-steady motion. Here the reference position
only moves when the robot has travelled more than `StallDistanceThreshold` (5 cm), so genuinely slow
progress is never mistaken for being stuck.

Without stall detection, a robot blocked by an obstacle pushes against it indefinitely — odometry keeps
reporting the same distance error, so the controller keeps commanding the same velocity.

## Obstacle avoidance

There is none here. Options:

1. **Enable the robot's own avoidance service** (`Services.ObstacleAvoid`). It is reactive and works
   without a map.
2. **Use the LiDAR directly** for a forward clearance check:

   ```csharp
   using var lidar = new LidarClient(robot.Participant);

   if (lidar.GetForwardClearance(sectorHalfWidthDegrees: 15f) is { } metres && metres < 0.8f)
   {
       stream.Stop();
   }
   ```

   LiDAR needs the native transport — a point-cloud frame is far larger than a UDP datagram.

3. **Put a planner in front.** Bridge to ROS 2 and let Nav2 own path planning; see
   [`ros2-bridge.md`](ros2-bridge.md). This is the right answer for anything beyond a short route.

## Tuning

| Option | Default | Raise it when | Lower it when |
|---|---|---|---|
| `DistanceGain` | 0.8 | Approach is sluggish | The robot overshoots |
| `HeadingGain` | 1.5 | Turns are slow to converge | The heading oscillates |
| `TurnInPlaceThreshold` | 45° | Space is open | Space is tight |
| `StallTimeout` | 10 s | The floor is slippery or slow | You want faster failure |
| `UpdateRateHz` | 20 | — | The link is congested |

Every command still passes through the safety envelope, so `DistanceGain` cannot produce a speed above
`Safety.Velocity.MaxForward` no matter how it is tuned.

## When to reach for something else

| Situation | Use |
|---|---|
| Route beyond ~20 m | SLAM plus a global planner |
| A map already exists | Nav2 through the ROS 2 bridge |
| Dynamic obstacles | The robot's avoidance service, or a reactive planner |
| Repeatable positioning | External localisation — fiducials, UWB, motion capture |
