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

## MicroDuck — 14 joints

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
[ 9] right_hip_yaw     -30 .. 25     Documented   <- mirrored
[10] right_hip_roll    -22 .. 22     Documented
[11] right_hip_pitch   -90 .. 90     Documented
[12] right_knee        -90 .. 90     Documented
[13] right_ankle       -90 .. 90     Documented
```

Transcribed from Pollen's own MuJoCo model, vendored verbatim at
[`src/PollenRobotics.Net.MicroDuck/Reference/robot_walk.xml`](../src/PollenRobotics.Net.MicroDuck/Reference/robot_walk.xml)
under Apache 2.0 from [pollen-robotics/microduck](https://github.com/pollen-robotics/microduck),
upstream commit `8904b65d3628`. Refresh it from there rather than editing it.

**The head sits between the legs.** The order is the model's depth-first DOF order: left leg, then
the neck and head, then the right leg. It is not leg-leg-head, and that is the single most
dangerous thing on this page to get wrong - every index past the left ankle shifts, so code that
thinks it is reading the right knee reads head yaw instead.

**There is no beak joint.** The head carries a `mouth_tip` site but nothing actuates it, so the bill
is fixed geometry. `DuckActionSlot.GroundPick` still picks things up; it does so by moving the whole
head, which is what the robot does.

**Hip yaw is asymmetric and mirrors between the legs** - the left runs −25..30 and the right
−30..25. The same commanded value on both legs therefore does not give a symmetric stance: one leg
has travel left when the other has reached its stop.

**Head yaw reaches ±170°**, far beyond a neck's usual travel. It is the joint that lets the duck
look behind itself without moving its feet, and a table that clamps it to ±90 silently refuses half
of what the robot can do.

### What this replaced

Every row above used to be a conservative guess, and it was wrong in ways that would have failed
silently on hardware:

| | Guessed | Actual |
|---|---|---|
| Joint count | 15 | 14 |
| `beak` | 0..45° | does not exist |
| Order | leg, leg, neck, head, beak | leg, neck+head, leg |
| Names | `left.ankle_pitch`, `neck.yaw` | `left_ankle`, `head_yaw` |
| `left_hip_yaw` | ±45 | −25..30 |
| `left_knee` | −10..130 | ±90 |
| `head_yaw` | ±90 | ±170 |

The lesson worth keeping: the tests agreed with the wrong table, because they took the catalogue as
the definition of truth. `MicroDuckCatalogueTests` now restates what the vendored model says, so the
catalogue is checked against Pollen's file rather than against itself.

### Other things the model settles

The sites in `robot_walk.xml` corroborate parts of the SDK that were also assumptions: there is a
`tof` site on the head shell (so `ReadTimeOfFlightAsync` is pointed at something real), an `imu` on
the trunk and a second `head_imu`, and a `head_camera`. Link lengths come from the same file -
thigh 35.8 mm, shin 42.0 mm, trunk at 120 mm - and are what the simulator's 3D model is built to.

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
