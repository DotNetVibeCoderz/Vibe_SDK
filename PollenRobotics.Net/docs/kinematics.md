# Kinematics

What is exact, what is approximate, and which to trust for what.

## The short version

| | Status |
|---|---|
| Pose maths — `Pose`, `Angle`, `Rotation`, the row-major wire format | **Exact** |
| Interpolation curves | **Exact** |
| Stewart platform *solver* | **Exact** for the geometry it is given |
| Stewart platform *geometry for Reachy Mini* | **Approximate** |
| Serial chain solver — FK, Jacobian, damped least squares | **Exact** for the chain it is given |
| Reachy 2 arm *link lengths* | **Approximate** |
| The robot's own IK, over gRPC | **Exact** — it uses the real model |

The solvers are correct. What is approximate is the *dimensions* fed to them, because Pollen
publishes the mechanisms and the envelopes but not the CAD.

**On hardware, prefer the robot's own kinematics.** Reachy Mini's daemon takes a pose and solves it
itself. Reachy 2 exposes `ComputeArmIK`. Use the local solvers for previewing reachability, for the
simulator, and for reasoning about a trajectory before you send it.

## Conventions

The SDK works in the robotics convention: **z up, x forward, y left**, metres and radians.

Euler angles are **intrinsic XYZ** - roll about x, then pitch about y, then yaw about z, which is
the same as an extrinsic ZYX. That is what `create_head_pose` and the JavaScript SDK's
`rpyToMatrix` both do. Getting it backwards produces a rotation that looks right for small angles
and drifts visibly past about 20 degrees.

### The transpose trap

`System.Numerics.Matrix4x4` is stored with the translation in `M41..M43` - the transpose of the
robotics convention the daemons use, which puts it in the fourth *column*.

`Pose.ToRowMajor()` and `Pose.FromRowMajor()` are the only sanctioned crossing. A memory
reinterpret would be faster and wrong, and wrong in a way that round-trips through this SDK
perfectly while moving the robot somewhere else. There is a test for it.

## The Stewart platform

Reachy Mini's neck: six branches driven by rotary servos, each with a horn and a push rod.

### The solver

The inverse problem has a closed form. With the horn tip on a circle about the servo axis and the
rod a fixed length:

```
L² = |reach|² + crank² − 2·crank·(reach · dir(a))
```

which reduces to `A sin(a) + B cos(a) = C` and solves through the auxiliary-angle identity.

Two subtleties, both of which caused visible bugs:

**There are two solutions.** The horn can swing either way to reach the same rod length. Taking
whichever `asin` returns picks arbitrarily, and the arbitrary choice flips as the pose moves - which
shows up as a branch angle jumping by most of a revolution mid-trajectory. On hardware that is a
servo slamming across its travel. The solver prefers the solution nearer zero, which is the elbow
the mechanism is actually assembled with.

**Horn angles are not zero at the home pose.** There is no reason they should be - it depends
entirely on the link lengths. Real hardware defines servo zero at the home pose and reports relative
to it, and so does this solver: it calibrates once at construction and subtracts the offset. Without
that, a neutral head reads as a hundred degrees of branch travel, which looks like a robot in
trouble.

The forward problem has no closed form. `SolveForward` runs a damped Gauss-Newton search on the
six-dimensional pose. It converges in a handful of iterations from a sensible seed and is **not**
fast enough for a control loop - read the pose from the daemon instead.

### The geometry

`StewartGeometry.ReachyMiniApproximation` uses radii and heights chosen to be the right scale for a
desk robot. The rod and horn lengths are **derived**, not guessed:

```csharp
StewartGeometry.ForEnvelope(
    baseRadius: 0.045,
    platformRadius: 0.022,
    ...,
    maxTilt: Angle.FromDegrees(40),
    maxYaw: Angle.FromDegrees(25),
    maxLift: 0.012);
```

`ForEnvelope` sweeps the pose envelope, measures how far the rod endpoints actually move apart, and
sizes the horn from half that swing plus a margin, with the rod in the middle of the range. That is
the only way to get a mechanism that provably covers its own envelope - guessing produced one that
threw inside Pollen's documented limits.

Two things that fell out of doing it this way:

**Platform phase matters more than anything else.** Textbook 6-UPS Stewart platforms rotate the
platform triad 60 degrees against the base. That is right when every leg is a linear actuator that
can change length freely. A rotary branch cannot: its horn changes the leg distance by at most its
own length. With a 60-degree phase the anchors swing so far during a tilt that the required leg
distance ranges over 80 mm, needing a horn longer than the platform is wide. A small phase keeps
each platform anchor near its base anchor.

**Tilt is the dominant cost, not yaw.** Tilting by `t` moves an anchor vertically by
`platformRadius · sin(t)`, so the horn length scales directly with the platform radius. That is why
a compact neck has a small platform triangle.

**A rotary neck cannot do large yaw.** The model covers about ±25 degrees. Reachy Mini's head yaw of
±180 degrees comes overwhelmingly from the `body.yaw` joint - which is exactly why the documented
constraint is that head yaw stays within 65 degrees of body yaw.

### What this means for you

- Trust it for: "is this pose inside the neck's envelope", and for driving the simulator.
- Do not trust it for: commanding branch angles on hardware. Command a pose; the daemon solves it.

## Serial chains

`SerialChain` does forward kinematics, a geometric Jacobian, and damped least-squares inverse
kinematics for an open revolute chain. Used for the Reachy 2 arms.

### Why damped

Reachy 2's arm has seven joints and the task is six-dimensional, so the arm is **redundant**: an
infinite family of joint vectors reaches any given pose.

Plain Gauss-Newton asks for an unbounded joint velocity when the arm approaches a singularity. The
damped form trades a little tracking accuracy for a step that stays finite, which on hardware is the
difference between a smooth approach and the arm snapping.

### Why the seed matters

The solver resolves the redundancy by staying near the configuration it started from. That is what
makes successive calls along a trajectory produce continuous joint motion instead of jumping between
elbow configurations.

**Seed with the arm's present position.** Seeding from zero gives a solution that is geometrically
correct and physically absurd. There is a test asserting that a 5 mm Cartesian step never moves any
joint more than 25 degrees.

### The link lengths

`ReachyArmChains.Arm` reproduces Reachy 2's published reach and proportions. They are not from
Pollen's URDF, which is not part of this repository. That makes them right for the simulator and for
reasoning about reachability, and wrong as the last word on a real robot.

## Testing

`tests/PollenRobotics.Net.Tests/KinematicsTests.cs` asserts:

- the row-major round trip, and that translation lands in the fourth column
- that RPY decomposition stays finite at the pitch singularity
- that the Stewart solver zeroes at home
- that it covers the published tilt envelope
- that branch angles stay continuous across a 40-degree sweep (max 12 degrees per degree of pitch)
- that a pure lift moves all six branches by a similar amount, in alternating directions
- that the arm solver converges, respects joint limits, and stays near its seed

Every one of those exists because the corresponding bug happened.
