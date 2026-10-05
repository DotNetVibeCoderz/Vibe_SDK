"""MicroDuck.

Shapes follow images/microduck.png, which shows four of them from four angles. What makes the
duck recognisable is the contrast between its parts, not any single silhouette:

  * pastel printed shells - head cap, body, thigh plates, feet - over
  * a bare black servo spine, brackets and horns left completely exposed, and
  * one oversized flat beak and one oversized round eye per side.

The feet are much bigger than a biped of this size needs. That is not a modelling liberty: the
photographs show flat slabs roughly a third of the leg length, and a duck drawn with proportionate
feet stops reading as this robot.

Every location in this file is relative to its parent. Joint names match RobotCatalog.MicroDuck so
the viewport can resolve a joint by name rather than by index.
"""

import math

import common as c

# Link lengths match the rig the simulation was tuned against, so the existing gait still produces
# a sensible walk. Everything else - shell sizes, head, feet - is re-proportioned off the
# photographs, where the duck is squat and front-heavy rather than tall and thin.
STANDING_HIP = 0.135        # hip height with the legs straight
THIGH = 0.058
SHIN = 0.050
HIP_SEPARATION = 0.034

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

    # A rounded backpack shell sitting above the hips, tilted nose-down like the photographs.
    torso = c.rounded_box(
        "body.shell", (0.070, 0.084, 0.058), 0.016, mat["shell"], segments=5,
        location=(0, FORWARD * 0.004, 0.038), rotation=(math.radians(-10), 0, 0),
    )
    c.attach(torso, body)

    # The hip bridge: exposed black structure between the two legs.
    bridge = c.rounded_box(
        "body.hips", (0.052, 0.034, 0.024), 0.004, mat["servo"], segments=3, location=(0, 0, 0.004)
    )
    c.attach(bridge, body)

    # ---------------------------------------------------------------- legs

    for side, sign in (("left", 1), ("right", -1)):
        hip_yaw = c.joint(f"{side}.hip_yaw", location=(sign * HIP_SEPARATION / 2, 0, -0.004), parent=body)
        hip_roll = c.joint(f"{side}.hip_roll", parent=hip_yaw)
        hip_pitch = c.joint(f"{side}.hip_pitch", parent=hip_roll)

        servo_block(f"{side}.hip.servo", mat, (0.028, 0.034, 0.034), parent=hip_pitch)

        # Thigh: a black strut with a pastel plate on the outside, as in the photographs.
        thigh = c.rounded_box(
            f"{side}.thigh", (0.026, 0.034, THIGH), 0.004, mat["servo"], segments=3,
            location=(0, 0, -THIGH / 2 - 0.016),
        )
        c.attach(thigh, hip_pitch)

        plate = c.rounded_box(
            f"{side}.thigh.plate", (0.008, 0.036, THIGH * 0.72), 0.007, mat["shell"], segments=4,
            location=(sign * 0.016, FORWARD * 0.002, -THIGH / 2 - 0.016),
        )
        c.attach(plate, hip_pitch)

        knee = c.joint(f"{side}.knee", location=(0, 0, -THIGH - 0.016), parent=hip_pitch)
        servo_block(f"{side}.knee.servo", mat, (0.028, 0.032, 0.032), parent=knee)

        shin = c.rounded_box(
            f"{side}.shin", (0.024, 0.032, SHIN), 0.004, mat["servo"], segments=3,
            location=(0, 0, -SHIN / 2 - 0.015),
        )
        c.attach(shin, knee)

        ankle = c.joint(f"{side}.ankle_pitch", location=(0, 0, -SHIN - 0.015), parent=knee)
        servo_block(f"{side}.ankle.servo", mat, (0.026, 0.030, 0.028), parent=ankle)

        # The shoe. Big, flat, rounded, and in the accent colour.
        shoe = c.rounded_box(
            f"{side}.foot", (0.040, 0.072, 0.013), 0.0055, mat["accent"], segments=5,
            location=(0, FORWARD * 0.010, -0.021),
        )
        c.attach(shoe, ankle)

        sole = c.rounded_box(
            f"{side}.foot.sole", (0.037, 0.068, 0.004), 0.0018, mat["servo"], segments=3,
            location=(0, FORWARD * 0.010, -0.028),
        )
        c.attach(sole, ankle)

    # ---------------------------------------------------------------- neck

    # Exposed servo column. The duck has no neck shell at all; leaving it bare is what gives the
    # silhouette its thin, mechanical throat between two pastel masses.
    neck_pitch = c.joint("neck.pitch", location=(0, FORWARD * 0.022, 0.058), parent=body)
    neck_yaw = c.joint("neck.yaw", parent=neck_pitch)

    spine = c.rounded_box(
        "neck.spine", (0.023, 0.021, 0.042), 0.003, mat["servo"], segments=3, location=(0, 0, 0.022)
    )
    c.attach(spine, neck_yaw)

    for i, z in enumerate((0.010, 0.026, 0.040)):
        servo = servo_block(f"neck.servo.{i}", mat, (0.022, 0.025, 0.013), parent=neck_yaw)
        servo.location = (0, 0, z)

    # A cable loop down the back, visible in every photograph.
    cable = c.tube(
        "neck.cable",
        [(0.009, 0.013, 0.004), (0.012, 0.019, 0.020), (0.010, 0.020, 0.034), (0.005, 0.015, 0.045)],
        0.0018, mat["wire"], segments=6,
    )
    c.attach(cable, neck_yaw)

    # ---------------------------------------------------------------- head

    head_pitch = c.joint("head.pitch", location=(0, 0, 0.048), parent=neck_yaw)
    head_roll = c.joint("head.roll", parent=head_pitch)

    # The cap: a shell closed on top and open underneath, which is why it is a lathe with a flat
    # front rather than a rounded box - the duck's head is a dome that overhangs the beak.
    cap = c.rounded_box(
        "head.cap", (0.058, 0.088, 0.050), 0.020, mat["shell"], segments=6, location=(0, FORWARD * 0.006, 0.016)
    )
    c.attach(cap, head_roll)

    # The brow band, in the accent colour, running round the front of the cap.
    brow = c.rounded_box(
        "head.brow", (0.059, 0.034, 0.013), 0.0050, mat["accent"], segments=4,
        location=(0, FORWARD * 0.030, 0.000),
    )
    c.attach(brow, head_roll)

    # One big round eye per side, with a coloured bezel. On the real duck these are the cameras.
    for side, sign in (("right", -1), ("left", 1)):
        bezel = c.cylinder(
            f"head.eye.{side}.bezel", 0.0150, 0.009, mat["bezel"], segments=24,
            location=(sign * 0.027, FORWARD * 0.012, 0.018), rotation=(0, math.pi / 2, 0),
        )
        lens = c.cylinder(
            f"head.eye.{side}.lens", 0.0096, 0.011, mat["lens"], segments=24,
            location=(sign * 0.030, FORWARD * 0.012, 0.018), rotation=(0, math.pi / 2, 0),
        )
        c.attach_all(head_roll, bezel, lens)

    # ---------------------------------------------------------------- beak

    # Upper beak is fixed to the head; the lower one is the only moving jaw. Both are flat slabs
    # wider than the head is deep - that overhang is most of the duck.
    upper = c.rounded_box(
        "head.beak.upper", (0.057, 0.054, 0.013), 0.0045, mat["beak"], segments=4,
        location=(0, FORWARD * 0.046, -0.003), rotation=(math.radians(-4), 0, 0),
    )
    c.attach(upper, head_roll)

    beak = c.joint("beak", location=(0, FORWARD * 0.021, -0.011), parent=head_roll)
    lower = c.rounded_box(
        "head.beak.lower", (0.051, 0.046, 0.009), 0.0038, mat["beak"], segments=4,
        location=(0, FORWARD * 0.023, -0.002),
    )
    c.attach(lower, beak)

    return root


def preview_pose(objects):
    """A small head tilt for the render, nothing more.

    An earlier version posed the legs into the simulation's standing crouch to make the preview
    comparable with the photographs. It made things worse, not better: the leg chain's sign
    conventions belong to the viewport, and reproducing them here meant maintaining the same
    convention in two places with nothing to catch them drifting apart. The rest pose is what ships,
    so the rest pose is what gets checked.
    """
    head = objects.get("head.pitch")

    if head is not None:
        head.rotation_euler = (math.radians(10), 0, 0)
