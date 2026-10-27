# Joint models

Where every number in `RobotCatalog` comes from, and how confident to be about it.

This file exists because the joint tables are the part of the SDK most likely to be wrong, and the
part where being wrong is least visible: a limit that is too tight silently refuses a legal pose, and
one that is too loose lets a command through that the robot then clamps or refuses on its own.

**Correct these against hardware.** See [PROGRESS.md](../PROGRESS.md) for what that involves.

## How to read the tables

| Source | Meaning |
|---|---|
| **Documented** | Stated by Pollen in published documentation. Trust it |
| **Derived** | Follows from something documented, by an argument given below |
| **Conservative guess** | Not published. Chosen to be safe rather than accurate. Correct it |

The list order in `RobotDescription` is the **wire order**, and the constructor asserts that each
joint's declared index matches its position. Resolve a joint by name, never by a hard-coded integer:
a variant with a different joint count shifts every index after the one that changed.

---

## Reachy Mini — 9 joints

```
[0] body.yaw          -160 .. 160    Documented
[1] neck.branch_1      -90 .. 90     Conservative guess
[2] neck.branch_2      -90 .. 90     Conservative guess
[3] neck.branch_3      -90 .. 90     Conservative guess
[4] neck.branch_4      -90 .. 90     Conservative guess
[5] neck.branch_5      -90 .. 90     Conservative guess
[6] neck.branch_6      -90 .. 90     Conservative guess
[7] antenna.right     -180 .. 180    Derived
[8] antenna.left      -180 .. 180    Derived
```

**Wire order** follows the daemon's `head_joint_positions` field, which puts body yaw at index 0 and
the six neck branches at 1..6. The antennas are reported separately, in
`antennas_joint_positions` as `[right, left]`; this description flattens both into one vector by
appending the antennas after the neck.

**The branch limits are not the ones that bind.** User code never commands a branch angle — it
commands a head pose, and the daemon solves for the branches. The limits that actually constrain
anything are on the pose:

| Pose limit | Value | Source |
|---|---|---|
| Head pitch | ±40° | Documented |
| Head roll | ±40° | Documented |
| Head yaw | unrestricted by itself | Documented |
| Body yaw | ±160° | Documented |
| Head yaw vs body yaw | within 65° | Documented |

Those are enforced in `ReachyMiniClient.ValidateHeadPose` and reproduced by the simulation. The
±90° per branch is wide enough not to interfere, which is the intent.

**The antennas** are continuous in hardware. ±180° is a deliberate restriction: a full turn tangles
the cabling, so the SDK keeps them inside one revolution.

**Combined tilt.** "Pitch to ±40 and roll to ±40" is a per-axis statement, not a promise that both
can be 40 at once — that would be 53° of combined tilt. `StewartGeometry.ForEnvelope` sizes the
mechanism for 40° of *total* tilt, which is the sensible reading. See
[kinematics.md](kinematics.md).

---

## MicroDuck — 15 joints

```
[ 0] left_hip_yaw      -25 .. 30     Documented
[ 1] left_hip_roll     -22 .. 22     Documented
[ 2] left_hip_pitch    -90 .. 90     Documented
[ 3] left_knee         -90 .. 90     Documented
[ 4] left_ankle        -90 .. 90     Documented
[ 5] neck_pitch        -90 .. 60     Documented
[ 6] head_pitch        -90 .. 90     Documented
[ 7] head_yaw         -170 .. 170    Documented
[ 8] head_roll         -25 .. 25     Documented
[ 9] mouth               0 .. 45     Conservative guess   <- the only one left
[10] right_hip_yaw     -30 .. 25     Documented   <- mirrored
[11] right_hip_roll    -22 .. 22     Documented
[12] right_hip_pitch   -90 .. 90     Documented
[13] right_knee        -90 .. 90     Documented
[14] right_ankle       -90 .. 90     Documented
```

**Two different files, two different contracts.** This table has been wrong twice, both times by
trusting one of them and not looking for the other:

| Source | What it is | What it says |
|---|---|---|
| [`robot_walk.xml`](../src/PollenRobotics.Net.MicroDuck/Reference/robot_walk.xml) | MuJoCo **physics** model | 14 hinges, no mouth, and the limits |
| [`duck.bin`](../src/PollenRobotics.Net.MicroDuck/Reference/duck.bin) + `scripts/bake-duck-mesh.py` | The **runtime wire** order | 15 names, mouth at 9 |

Both are Pollen's, both Apache 2.0, both vendored. The physics model fixes the jaw as a geom
because it does not affect locomotion, so **it is not a list of servos** — it is a list of things
that move the robot's centre of mass. The wire contract is `duck_ipc_proto::JOINT_NAMES`, and
Pollen's bake script carries a copy of it with the comment that says so.

The consequence is sharp: **the right leg starts at index 10.** A 14-entry table reads the right
leg one slot early, so `right_hip_yaw` returns the mouth angle and everything after it is shifted.

**The head sits between the legs.** Left leg, neck and head, mouth, right leg. Not leg-leg-head.

**Hip yaw is asymmetric and mirrors between the legs** — left −25..30, right −30..25. The same
commanded value on both legs does not give a symmetric stance.

**Head yaw reaches ±170°**, far beyond a neck's usual travel. It is the joint that lets the duck
look behind itself without moving its feet.

**`mouth` is the one remaining guess.** It has no MuJoCo joint, so no published travel; 0..45° is
deliberately conservative. Correct it against `robotctl monitor`.

### What this replaced

| | 0.1.x (guessed) | 0.2.0 (MJCF only) | 0.3.0 (wire contract) |
|---|---|---|---|
| Joint count | 15 | 14 | **15** |
| Jaw | `beak`, index 14 | absent | `mouth`, index 9 |
| `right_hip_yaw` | 5 | 9 | **10** |
| Names | `left.ankle_pitch` | `left_ankle` | `left_ankle` |

The lesson worth keeping: 0.2.0 was a real improvement and still wrong, because one authoritative
file is not the same as the authoritative file. `MicroDuckCatalogueTests` now pins the wire order
specifically, including the mouth's index.

## Reachy 2 — 21 joints

```
[ 0] r_arm.shoulder.pitch   -180 .. 90     Derived
[ 1] r_arm.shoulder.roll    -180 .. 10     Derived
[ 2] r_arm.elbow.yaw         -90 .. 90     Derived
[ 3] r_arm.elbow.pitch      -125 .. 0      Derived
[ 4] r_arm.wrist.roll        -45 .. 45     Conservative guess
[ 5] r_arm.wrist.pitch       -45 .. 45     Conservative guess
[ 6] r_arm.wrist.yaw         -45 .. 45     Conservative guess
[ 7] l_arm.shoulder.pitch   -180 .. 90     Derived
[ 8] l_arm.shoulder.roll     -10 .. 180    Derived   <- mirrored
[ 9] l_arm.elbow.yaw         -90 .. 90     Derived
[10] l_arm.elbow.pitch      -125 .. 0      Derived
[11] l_arm.wrist.roll        -45 .. 45     Conservative guess
[12] l_arm.wrist.pitch       -45 .. 45     Conservative guess
[13] l_arm.wrist.yaw         -45 .. 45     Conservative guess
[14] head.neck.roll          -45 .. 45     Conservative guess
[15] head.neck.pitch         -45 .. 45     Conservative guess
[16] head.neck.yaw           -90 .. 90     Conservative guess
[17] head.r_antenna         -150 .. 150    Conservative guess
[18] head.l_antenna         -150 .. 150    Conservative guess
[19] r_arm.gripper            -5 .. 130    Conservative guess
[20] l_arm.gripper            -5 .. 130    Conservative guess
```

**The names are documented.** They match the dotted paths the Python SDK exposes —
`r_arm.shoulder.pitch` and friends — so code generated by the wizard reads the same in both
languages. Joint *order* within an arm follows the `ArmJoints` enum in Pollen's own `arm.proto`:
shoulder pitch, shoulder roll, elbow yaw, elbow pitch, wrist roll, wrist pitch, wrist yaw.

**Shoulder roll mirrors between sides.** Its range on the left arm is the mirror image of the right,
which is why the same value on both arms sends one of them straight into a limit. This trips people
up on their first bimanual routine; the `reachy2.bimanual` template calls it out.

**Ask the robot instead.** Reachy 2 exposes `GetJointsLimits` per part, which returns the real
limits from the real robot. That is strictly better than this table, and the right thing to use on
hardware:

```csharp
// Preferred over the catalogue when a robot is present.
ArmLimits limits = await armService.GetJointsLimitsAsync(partId);
```

The catalogue values exist so that the simulation, the instrument panels and offline reasoning have
something to work with when no robot is attached.

**The mobile base is not a joint chain** and is not in this table. It is modelled as a holonomic
drive with odometry.

---

## Where the limits are enforced

```
Your code
   |
   |  ReachyMiniClient / MicroDuckClient / Reachy2Arm
   |     -> JointLimitGuard, from RobotSafetyOptions
   |        throws RobotSafetyException, or clamps if asked
   v
Transport
   |
   v
Robot  -> enforces its own limits regardless
```

The SDK's check is a courtesy that turns a silent clamp into a stack trace. The robot enforces its
own limits whatever this table says — a limit that is too *loose* here does not endanger anything,
it just means the refusal comes from the robot rather than from the SDK, and with a worse message.

A limit that is too *tight* is the more annoying failure: the SDK refuses a pose the robot would
have accepted. If you hit that, widen the entry here rather than reaching for
`RobotSafetyOptions.Permissive` — and send a correction.

See [safety.md](safety.md).
