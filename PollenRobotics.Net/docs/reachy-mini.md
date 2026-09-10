# Reachy Mini

Nine actuated degrees of freedom: a body yaw, a six-branch parallel neck, and two antennas. Plus a
camera, microphones and a speaker.

![Reachy Mini in the simulator](images/simulator-reachy-mini.png)

*The six struts are the neck's real mechanism, solved every frame from the branch angles. The joint
arcs on the right show the idle breath moving all six together.*

## The things that will bite you

**The head pose is in the world frame.** Sending a body yaw on its own rotates the body while the
head keeps its commanded world orientation, so the head visibly counter-rotates. That is correct per
the coordinate convention and almost never what you meant. To turn head and body together, set both
in the same call — or use `TurnAsync`, which does it for you.

**Build incremental motion on the last commanded pose, not on telemetry.** `CommandedHeadPose` is
what you asked for; `LastState.HeadPose` is what the robot reported a round trip ago. Deltas
accumulated against telemetry stall the moment the user moves faster than the state stream.

**Head yaw must stay within 65 degrees of body yaw.** With automatic body yaw on (the default), the
daemon rotates the body to keep that satisfied. With it off, the head is clamped and simply stops
turning, with no error.

**Pitch and roll stop at 40 degrees.** The SDK throws. See [safety.md](safety.md).

**`EnsureAwakeAsync`, not `WakeUpAsync`, on startup.** The second replays the greeting animation
every time. An application that does that on every restart looks broken rather than charming.

## Poses

A pose is a translation plus an orientation, in the head frame. `HeadPose.Create` mirrors the Python
SDK's `create_head_pose` exactly — degrees by default, metres unless you pass `mm: true` — so a
snippet from Pollen's documentation produces the same motion here.

```csharp
HeadPose.Create(z: 12, mm: true)                  // up 12 mm
HeadPose.Create(pitch: 15)                        // nose down 15 degrees
HeadPose.Create(roll: 20, yaw: 25, z: 8, mm: true) // tilt, turn and lift
HeadPose.Neutral                                   // home
```

Rotation order is intrinsic XYZ: roll about x, then pitch about y, then yaw about z. Getting that
backwards produces a rotation that looks right for small angles and drifts visibly past about 20
degrees.

## Moving

Two paths, and the choice matters.

```csharp
// Interpolated by the daemon, and awaited. For discrete movements.
await mini.GotoTargetAsync(
    head: HeadPose.Create(pitch: -10),
    antennas: (30.Degrees(), (-30).Degrees()),
    duration: TimeSpan.FromSeconds(1),
    method: InterpolationMethod.MinJerk);

// Applied immediately, no interpolation. For a joystick, a face follower, a generated trajectory.
await mini.SetTargetAsync(head: HeadPose.Create(yaw: yawFromJoystick));
```

`SetTargetAsync` does not wait for the robot to arrive, because there is nothing to wait for — the
next frame supersedes this one. Drive it from a `RealtimeLoop` at 50 Hz or so.

### Easing

| Method | Shape |
|---|---|
| `Linear` | Constant velocity. Starts and stops abruptly |
| `MinJerk` | Zero velocity *and* acceleration at both ends. The default, and right for most motion |
| `EaseInOut` | Smoothstep |
| `Cartoon` | Overshoots about ten percent, then settles. Reads as playful |

Every curve satisfies f(0)=0 and f(1)=1, so a trajectory always arrives exactly where it was asked
to. Personality comes from the easing as much as from the pose: the same angles played with
`MinJerk` read as thoughtful and with `Cartoon` as delighted.

## Antennas

Right first, then left — the order the daemon uses in both directions.

```csharp
await mini.SetAntennasAsync(rightDeg: 50, leftDeg: -50);
```

Opposite signs read as a wave. The same sign reads as a shrug. It is the single cheapest way to give
the robot an expression, and the only channel left when the daemon's face tracker owns the head.

## Motor modes

| Mode | Behaviour |
|---|---|
| `Enabled` | Position control. Holds what it was told |
| `Disabled` | No power. The head flops |
| `GravityCompensation` | Movable by hand, stays where you leave it |

Gravity compensation is the mode to record a move in — in position control the head fights back. It
needs the Placo kinematics backend.

## Face tracking

The daemon detects and tracks; you decide how much of the head it gets.

```csharp
await mini.StartHeadTrackingAsync(weight: 1.0);   // tracking owns the head
await mini.StartHeadTrackingAsync(weight: 0.0);   // detector stays warm, head is yours
await mini.StopHeadTrackingAsync();               // detector off, CPU back

FaceTarget face = await mini.GetTrackedFaceAsync();
if (face.Detected) { /* face.X and face.Y are in [-1, 1] */ }
```

Weight 0 is not the same as stopping: it keeps the detector running, which is much cheaper than
stopping and restarting it every turn of a conversation.

## Look at a point

```csharp
await mini.LookAtAsync(x: 1.0, y: 0.0, z: 0.06, duration: TimeSpan.FromSeconds(1.2));
```

World frame, metres: x forward, y left, z up. Aimed from the head's actual height rather than from
the base, so close targets are pointed at correctly.

## Recorded moves

```csharp
await mini.EnableGravityCompensationAsync();
await mini.StartRecordingAsync();
// ... move the head by hand ...
RecordedMove move = await mini.StopRecordingAsync("teach-in");

await File.WriteAllTextAsync("move.json", move.Resample(100).ToJson());
await mini.PlayMoveAsync(move);
```

`PlayMoveAsync` streams frames from your process, which pays a round trip per frame. That is fine on
a wired Lite and visibly rough over Wi-Fi. Anything long, and anything with audio, belongs on the
daemon's clock — not yet implemented, see [PLAN.md](../PLAN.md).

The JSON shape matches the Python `RecordedMove` parser, so moves are interchangeable.

## The neck

Nine joints are reported, but you do not command the six neck branches — you command a pose and the
daemon solves for them. `mini.Neck` exposes a local solver for previewing whether a pose is
reachable before you send it:

```csharp
if (!mini.Neck.IsReachable(pose))
{
    // outside the mechanism
}
```

That solver uses approximate link geometry. See [kinematics.md](kinematics.md).

## Not yet implemented

Camera, microphone and speaker are in the transport interface and not implemented. The IMU is
implemented and returns null on a Lite, which has no IMU — that is a fact about the hardware, not a
fault.
