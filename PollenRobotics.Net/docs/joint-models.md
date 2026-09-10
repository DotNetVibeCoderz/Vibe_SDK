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
[ 0] left.hip_yaw       -45 .. 45     Conservative guess
[ 1] left.hip_roll      -35 .. 35     Conservative guess
[ 2] left.hip_pitch     -90 .. 60     Conservative guess
[ 3] left.knee          -10 .. 130    Conservative guess
[ 4] left.ankle_pitch   -70 .. 70     Conservative guess
[ 5] right.hip_yaw      -45 .. 45     Conservative guess
[ 6] right.hip_roll     -35 .. 35     Conservative guess
[ 7] right.hip_pitch    -90 .. 60     Conservative guess
[ 8] right.knee         -10 .. 130    Conservative guess
[ 9] right.ankle_pitch  -70 .. 70     Conservative guess
[10] neck.pitch         -40 .. 45     Conservative guess
[11] neck.yaw           -90 .. 90     Conservative guess
[12] head.pitch         -30 .. 30     Conservative guess
[13] head.roll          -25 .. 25     Conservative guess
[14] beak                 0 .. 45     Conservative guess
```

**Everything here is a guess, and the whole table should be replaced.**

What *is* documented: fifteen servos, a 50 Hz control loop, and an RK3566. The joint names and
limits follow the usual biped convention — two five-DOF legs, a two-DOF neck, a head joint and a
beak — which fits fifteen servos and matches what the robot visibly does. Neither the names nor the
ordering is confirmed.

### How to correct it

```bash
robotctl monitor --json > duck-joints.jsonl
```

`robotctl monitor` prints one line per tick with joint state in radians. Move each joint through its
full travel by hand (with the servos relaxed) and take the extremes. Then:

1. Replace the names and limits in `RobotCatalog.MicroDuck`.
2. Run `dotnet run --project tools/PollenRobotics.Net.TemplateCheck` — the templates index joints by
   name and will not compile if a name goes away.
3. Re-run the tests: `EveryJointStaysInsideItsLimits` asserts the gait model stays legal, and it
   will fail if the new limits are tighter than the gait's range.
4. Update the *Written but not verified* table in [PROGRESS.md](../PROGRESS.md).

**None of this affects how you drive the duck.** You send velocity intents and action slots; a
policy owns the servos. The joint table matters for the simulation, for the instrument panels, and
for anyone reading state — not for commanding.

---

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
