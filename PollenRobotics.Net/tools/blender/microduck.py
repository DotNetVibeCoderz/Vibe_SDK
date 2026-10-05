"""MicroDuck.

Shapes follow images/microduck.png, which shows four of them from four angles. What makes the
duck recognisable is the contrast between its parts, not any single silhouette:

  * pastel printed shells - a helmet-shaped head, a body, thigh plates, big feet - over
  * a bare black servo spine, brackets and horns left completely exposed, and
  * one oversized flat bill and one oversized round eye per side.

Three proportions do most of the work, and the first version of this model got all three wrong:

  * **The head is a helmet, not a box.** It is tall and round at the back, slopes down toward the
    front, and overhangs the bill. A rounded cube reads as a cartoon robot instead.
  * **The eye is enormous** - close to half the height of the head - and sits well forward, just
    behind the bill, with a bright bezel ring around it.
  * **The bill is two broad flat slabs**, as wide as the head and projecting well past it. The duck
    is front-heavy, and the whole silhouette leans forward because of it.

The feet are much bigger than a biped this size needs. That is not a modelling liberty: the
photographs show flat slabs roughly a third of the leg length, and a duck drawn with proportionate
feet stops reading as this robot.

Link lengths match the rig the simulation was tuned against, so the existing gait still produces a
sensible walk. Everything else is proportioned off the photographs.

Every location in this file is relative to its parent. Joint names match RobotCatalog.MicroDuck so
the viewport can resolve a joint by name rather than by index.
"""

import math

import common as c

# The duck is squat: legs are roughly 40% of its height, not the 55% the first version gave it.
# These lengths are purely visual - the simulation's gait is an amplitude in radians and its body
# velocity is computed independently, so nothing downstream depends on them.
STANDING_HIP = 0.105        # hip height with the legs straight
THIGH = 0.044
SHIN = 0.038
HIP_SEPARATION = 0.036

# Forward is -Y in Blender, which the Y-up export turns into +Z in three.js - the direction the
# rest of the viewport already treats as the front of a robot.
FORWARD = -1.0


def palette():
    """Colours from the cream-and-orange duck, which is the clearest of the four photographs."""
    return {
        "shell": c.material("shell", (0.88, 0.85, 0.78), roughness=0.55),
        "accent": c.material("accent", (0.95, 0.52, 0.10), roughness=0.45),
        "beak": c.material("beak", (0.97, 0.72, 0.08), roughness=0.42),
        "servo": c.material("servo", (0.055, 0.055, 0.06), roughness=0.50, metallic=0.25),
        "metal": c.material("metal", (0.62, 0.63, 0.66), roughness=0.32, metallic=0.85),
        "lens": c.material("lens", (0.02, 0.02, 0.025), roughness=0.10, metallic=0.2),
        "bezel": c.material("bezel", (0.97, 0.72, 0.08), roughness=0.35),
        "wire": c.material("wire", (0.10, 0.10, 0.12), roughness=0.60),
    }


def servo_block(name, mat, size=(0.022, 0.030, 0.034), parent=None):
    """One servo: a black body with a metal horn on each side.

    The horns are what make the leg read as built rather than moulded, and they are visible in every
    one of the reference photographs.
    """
    block = c.rounded_box(name, size, 0.0035, mat["servo"], segments=3)

    for sign in (-1, 1):
        horn = c.cylinder(
            f"{name}.horn.{'r' if sign < 0 else 'l'}",
            0.0082, 0.0035, mat["metal"], segments=16,
            location=(sign * (size[0] / 2 + 0.0012), 0, 0),
            rotation=(0, math.pi / 2, 0),
        )
        c.attach(horn, block)

    if parent is not None:
        c.attach(block, parent)

    return block


def build():
    c.reset_scene()
    c.forget_materials()
    mat = palette()

    root = c.joint("root")

    # `body` carries the world pose the simulation computes: the duck walks, so unlike the other
    # two robots its root moves.
    body = c.joint("body", location=(0, 0, STANDING_HIP), parent=root)

    # ---------------------------------------------------------------- torso

    # A rounded shell, long front-to-back and pitched nose-down. The lean is geometry, not a joint:
    # every joint is zero at rest, and a duck standing bolt upright is not what the photographs
    # show.
    torso = c.rounded_box(
        "body.shell", (0.078, 0.100, 0.068), 0.021, mat["shell"], segments=5,
        location=(0, FORWARD * 0.008, 0.044), rotation=(math.radians(-14), 0, 0),
    )
    c.attach(torso, body)

    # A short tail, which is what stops the body reading as a plain box from behind.
    tail = c.rounded_box(
        "body.tail", (0.044, 0.038, 0.022), 0.009, mat["shell"], segments=4,
        location=(0, -FORWARD * 0.054, 0.058), rotation=(math.radians(22), 0, 0),
    )
    c.attach(tail, body)

    # The hip bridge: exposed black structure between the two legs.
    bridge = c.rounded_box(
        "body.hips", (0.052, 0.036, 0.024), 0.004, mat["servo"], segments=3, location=(0, 0, 0.004)
    )
    c.attach(bridge, body)

    # ---------------------------------------------------------------- legs

    for side, sign in (("left", 1), ("right", -1)):
        hip_yaw = c.joint(f"{side}.hip_yaw", location=(sign * HIP_SEPARATION / 2, 0, -0.004), parent=body)
        hip_roll = c.joint(f"{side}.hip_roll", parent=hip_yaw)
        hip_pitch = c.joint(f"{side}.hip_pitch", parent=hip_roll)

        servo_block(f"{side}.hip.servo", mat, (0.028, 0.034, 0.034), parent=hip_pitch)

        thigh = c.rounded_box(
            f"{side}.thigh", (0.026, 0.034, THIGH), 0.005, mat["servo"], segments=3,
            location=(0, 0, -THIGH / 2 - 0.016),
        )
        c.attach(thigh, hip_pitch)

        # The pastel plate on the outside of the thigh: a shield shape in the photographs, and the
        # only colour on an otherwise black leg.
        plate = c.rounded_box(
            f"{side}.thigh.plate", (0.009, 0.040, THIGH * 0.80), 0.010, mat["shell"], segments=5,
            location=(sign * 0.018, FORWARD * 0.002, -THIGH / 2 - 0.016),
        )
        c.attach(plate, hip_pitch)

        knee = c.joint(f"{side}.knee", location=(0, 0, -THIGH - 0.016), parent=hip_pitch)
        servo_block(f"{side}.knee.servo", mat, (0.028, 0.032, 0.032), parent=knee)

        shin = c.rounded_box(
            f"{side}.shin", (0.024, 0.032, SHIN), 0.005, mat["servo"], segments=3,
            location=(0, 0, -SHIN / 2 - 0.015),
        )
        c.attach(shin, knee)

        ankle = c.joint(f"{side}.ankle_pitch", location=(0, 0, -SHIN - 0.015), parent=knee)
        servo_block(f"{side}.ankle.servo", mat, (0.026, 0.030, 0.028), parent=ankle)

        # The shoe. Big, flat, rounded, and in the accent colour.
        shoe = c.rounded_box(
            f"{side}.foot", (0.040, 0.074, 0.013), 0.0055, mat["accent"], segments=5,
            location=(0, FORWARD * 0.012, -0.021),
        )
        c.attach(shoe, ankle)

        sole = c.rounded_box(
            f"{side}.foot.sole", (0.037, 0.070, 0.004), 0.0018, mat["servo"], segments=3,
            location=(0, FORWARD * 0.012, -0.028),
        )
        c.attach(sole, ankle)

    # ---------------------------------------------------------------- neck

    # Exposed servo column, raked forward. The duck has no neck shell at all; leaving it bare is
    # what gives the silhouette its thin mechanical throat between two pastel masses.
    neck_pitch = c.joint("neck.pitch", location=(0, FORWARD * 0.032, 0.070), parent=body)
    neck_yaw = c.joint("neck.yaw", parent=neck_pitch)

    spine = c.rounded_box(
        "neck.spine", (0.025, 0.023, 0.036), 0.003, mat["servo"], segments=3, location=(0, 0, 0.018)
    )
    c.attach(spine, neck_yaw)

    for i, z in enumerate((0.008, 0.021, 0.034)):
        servo = servo_block(f"neck.servo.{i}", mat, (0.023, 0.026, 0.013), parent=neck_yaw)
        servo.location = (0, 0, z)

    cable = c.tube(
        "neck.cable",
        [(0.009, 0.014, 0.004), (0.013, 0.020, 0.020), (0.010, 0.021, 0.034), (0.005, 0.016, 0.046)],
        0.0018, mat["wire"], segments=6,
    )
    c.attach(cable, neck_yaw)

    # ---------------------------------------------------------------- head

    head_pitch = c.joint("head.pitch", location=(0, 0, 0.040), parent=neck_yaw)
    head_roll = c.joint("head.roll", parent=head_pitch)

    # The helmet, built as two overlapping rounded boxes rather than one: the back is tall and
    # round, the front lower and shorter, and the step between them is the brow the eye sits under.
    crown = c.rounded_box(
        "head.crown", (0.070, 0.066, 0.058), 0.027, mat["shell"], segments=6,
        location=(0, -FORWARD * 0.014, 0.022),
    )
    c.attach(crown, head_roll)

    snout = c.rounded_box(
        "head.snout", (0.063, 0.064, 0.046), 0.020, mat["shell"], segments=6,
        location=(0, FORWARD * 0.030, 0.012), rotation=(math.radians(-6), 0, 0),
    )
    c.attach(snout, head_roll)

    # One enormous eye per side, with a bright bezel. On the real duck these are the cameras, and
    # they are close to half the height of the head.
    for side, sign in (("right", -1), ("left", 1)):
        bezel = c.cylinder(
            f"head.eye.{side}.bezel", 0.0195, 0.011, mat["bezel"], segments=28,
            location=(sign * 0.031, FORWARD * 0.016, 0.022), rotation=(0, math.pi / 2, 0),
        )
        lens = c.cylinder(
            f"head.eye.{side}.lens", 0.0134, 0.013, mat["lens"], segments=28,
            location=(sign * 0.034, FORWARD * 0.016, 0.022), rotation=(0, math.pi / 2, 0),
        )
        c.attach_all(head_roll, bezel, lens)

    # ---------------------------------------------------------------- bill

    # Two broad slabs. The upper is fixed to the head and continues its line forward; the lower is
    # the only moving jaw. Both are as wide as the head and project well past it.
    upper = c.rounded_box(
        "head.bill.upper", (0.064, 0.060, 0.014), 0.0055, mat["accent"], segments=4,
        location=(0, FORWARD * 0.056, 0.000), rotation=(math.radians(-5), 0, 0),
    )
    c.attach(upper, head_roll)

    beak = c.joint("beak", location=(0, FORWARD * 0.028, -0.010), parent=head_roll)
    lower = c.rounded_box(
        "head.bill.lower", (0.058, 0.052, 0.011), 0.0045, mat["beak"], segments=4,
        location=(0, FORWARD * 0.027, 0.002),
    )
    c.attach(lower, beak)

    return root


def preview_pose(objects):
    """A small head tilt for the render, nothing more.

    An earlier version posed the legs into the simulation's standing crouch to make the preview
    comparable with the photographs. It made things worse: the leg chain's sign conventions belong
    to the viewport, and reproducing them here meant maintaining the same convention in two places
    with nothing to catch them drifting apart. The rest pose is what ships, so the rest pose is what
    gets checked.
    """
    head = objects.get("head_pitch")

    if head is not None:
        head.rotation_euler = (math.radians(8), 0, 0)
