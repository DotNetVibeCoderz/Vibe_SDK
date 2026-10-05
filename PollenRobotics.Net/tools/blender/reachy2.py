"""Reachy 2.

Shapes follow images/reachy2.png and images/reachy2_stand.jpg. The head is the same rounded slab
and spectacle face as the Mini - deliberately, it is the same design language - but everything
below it is different:

  * a white chest and shoulder yoke wearing a navy-striped Breton shirt, which is the single most
    recognisable thing about this robot and the one detail a grey humanoid placeholder always
    misses;
  * white limb shells over exposed silver actuator barrels, with the barrel showing at every joint;
  * two long thin antennas with ball tips, raked back;
  * a round mobile base, since that is what this SDK models - the tripod in one photograph is a
    stand, not part of the robot.

Joint names match RobotCatalog.Reachy2 so the viewport can resolve by name. Every location is
relative to its parent.
"""

import math

import common as c

BASE_HEIGHT = 0.105
BASE_RADIUS = 0.175
COLUMN_HEIGHT = 0.470       # base top to the underside of the torso
TORSO_HEIGHT = 0.280
SHOULDER_DROP = 0.045       # below the top of the torso
SHOULDER_SPAN = 0.150       # from centre line to each shoulder

UPPER_ARM = 0.190
FOREARM = 0.180

# The head is large - roughly two thirds of the shoulder span. Scaled down to "humanoid"
# proportions it stops reading as Reachy and starts reading as a generic robot.
HEAD_SIZE = (0.158, 0.068, 0.100)
HEAD_RADIUS = 0.030

STRIPE_COUNT = 11


def palette():
    return {
        "shell": c.material("shell", (0.91, 0.91, 0.90), roughness=0.34),
        "navy": c.material("navy", (0.055, 0.075, 0.145), roughness=0.80),
        "cloth": c.material("cloth", (0.90, 0.89, 0.86), roughness=0.85),
        "metal": c.material("metal", (0.70, 0.71, 0.74), roughness=0.26, metallic=0.88),
        "dark": c.material("dark", (0.075, 0.075, 0.085), roughness=0.42, metallic=0.2),
        "lens": c.material("lens", (0.015, 0.015, 0.02), roughness=0.10, metallic=0.2),
        "glass": c.material("glass", (0.03, 0.035, 0.05), roughness=0.06, metallic=0.4),
        "wire": c.material("wire", (0.045, 0.045, 0.05), roughness=0.42),
    }


def _arm(side, sign, mat, torso):
    """One seven-axis arm.

    The chain is shoulder pitch, shoulder roll, elbow yaw, elbow pitch, wrist roll, wrist pitch,
    wrist yaw - the order of Pollen's own ArmJoints enum, so an angle from the robot lands on the
    joint it is named for without a lookup table in between.
    """
    prefix = "r_arm" if sign < 0 else "l_arm"

    shoulder_pitch = c.joint(
        f"{prefix}.shoulder.pitch",
        location=(sign * SHOULDER_SPAN, 0, TORSO_HEIGHT - SHOULDER_DROP),
        parent=torso,
    )
    shoulder_roll = c.joint(f"{prefix}.shoulder.roll", parent=shoulder_pitch)

    # The silver shoulder barrel, pointing out along X.
    barrel = c.cylinder(
        f"{prefix}.shoulder.barrel", 0.044, 0.070, mat["metal"], segments=24,
        location=(sign * 0.012, 0, 0), rotation=(0, math.pi / 2, 0),
    )
    c.attach(barrel, shoulder_roll)

    cap = c.sphere(f"{prefix}.shoulder.cap", 0.045, mat["shell"], segments=20, location=(0, 0, 0.004))
    c.attach(cap, shoulder_roll)

    elbow_yaw = c.joint(f"{prefix}.elbow.yaw", parent=shoulder_roll)

    upper = c.rounded_box(
        f"{prefix}.upper_arm", (0.072, 0.078, UPPER_ARM * 0.86), 0.028, mat["shell"], segments=5,
        location=(0, 0, -UPPER_ARM * 0.46),
    )
    c.attach(upper, elbow_yaw)

    elbow_pitch = c.joint(f"{prefix}.elbow.pitch", location=(0, 0, -UPPER_ARM), parent=elbow_yaw)

    elbow_barrel = c.cylinder(
        f"{prefix}.elbow.barrel", 0.038, 0.064, mat["metal"], segments=24,
        location=(0, 0, 0), rotation=(0, math.pi / 2, 0),
    )
    c.attach(elbow_barrel, elbow_pitch)

    wrist_roll = c.joint(f"{prefix}.wrist.roll", parent=elbow_pitch)

    fore = c.rounded_box(
        f"{prefix}.forearm", (0.060, 0.066, FOREARM * 0.80), 0.024, mat["shell"], segments=5,
        location=(0, 0, -FOREARM * 0.44),
    )
    c.attach(fore, wrist_roll)

    # The forearm's actuator pack: a dark barrel in a cut-out, clearly visible in both photographs.
    pack = c.cylinder(
        f"{prefix}.forearm.actuator", 0.024, 0.060, mat["dark"], segments=20,
        location=(sign * 0.028, -0.008, -FOREARM * 0.34),
    )
    c.attach(pack, wrist_roll)

    wrist_pitch = c.joint(f"{prefix}.wrist.pitch", location=(0, 0, -FOREARM), parent=wrist_roll)
    wrist_yaw = c.joint(f"{prefix}.wrist.yaw", parent=wrist_pitch)

    wrist_barrel = c.cylinder(
        f"{prefix}.wrist.barrel", 0.030, 0.046, mat["metal"], segments=20, location=(0, 0, 0.004)
    )
    c.attach(wrist_barrel, wrist_yaw)

    # ---- gripper: a dark palm block and two fingers that close toward each other
    gripper = c.joint(f"{prefix}.gripper", location=(0, 0, -0.030), parent=wrist_yaw)

    palm = c.rounded_box(
        f"{prefix}.palm", (0.054, 0.046, 0.030), 0.008, mat["dark"], segments=4, location=(0, 0, -0.008)
    )
    c.attach(palm, gripper)

    for finger_side in (-1, 1):
        finger = c.joint(f"{prefix}.finger.{'a' if finger_side < 0 else 'b'}",
                         location=(finger_side * 0.020, 0, -0.022), parent=gripper)

        proximal = c.rounded_box(
            f"{prefix}.finger.{'a' if finger_side < 0 else 'b'}.proximal",
            (0.014, 0.020, 0.044), 0.005, mat["shell"], segments=3, location=(0, 0, -0.020),
        )
        pad = c.rounded_box(
            f"{prefix}.finger.{'a' if finger_side < 0 else 'b'}.pad",
            (0.016, 0.024, 0.016), 0.004, mat["dark"], segments=3,
            location=(0, -0.004, -0.046),
        )
        c.attach_all(finger, proximal, pad)

    return shoulder_pitch


def build():
    c.reset_scene()
    c.forget_materials()
    mat = palette()

    root = c.joint("root")

    # ---------------------------------------------------------------- mobile base

    base_node = c.joint("base", parent=root)

    drum = c.lathe(
        "base.drum",
        [
            (0.0000, 0.0000),
            (BASE_RADIUS - 0.012, 0.0000),
            (BASE_RADIUS, 0.012),
            (BASE_RADIUS, BASE_HEIGHT - 0.018),
            (BASE_RADIUS - 0.014, BASE_HEIGHT),
            (0.0000, BASE_HEIGHT),
        ],
        mat["shell"],
    )
    c.attach(drum, base_node)

    skirt = c.lathe(
        "base.skirt",
        [(0.0000, 0.0000), (BASE_RADIUS - 0.006, 0.0000), (BASE_RADIUS - 0.004, 0.016), (0.0000, 0.016)],
        mat["dark"],
        location=(0, 0, 0.002),
    )
    c.attach(skirt, base_node)

    # Three omni wheels, visible under the skirt.
    for i in range(3):
        angle = i * 2 * math.pi / 3 + math.pi / 6
        wheel = c.cylinder(
            f"base.wheel.{i}", 0.030, 0.022, mat["dark"], segments=18,
            location=((BASE_RADIUS - 0.038) * math.cos(angle), (BASE_RADIUS - 0.038) * math.sin(angle), 0.030),
            rotation=(math.pi / 2, 0, -angle),
        )
        c.attach(wheel, base_node)

    # The telescopic column.
    for i, (radius, z0, z1) in enumerate(((0.052, 0.0, 0.26), (0.042, 0.24, 0.46), (0.034, 0.44, COLUMN_HEIGHT))):
        section = c.cylinder(
            f"base.column.{i}", radius, z1 - z0, mat["metal"], segments=24,
            location=(0, 0, BASE_HEIGHT + (z0 + z1) / 2),
        )
        c.attach(section, base_node)

    # ---------------------------------------------------------------- torso

    torso = c.joint("torso", location=(0, 0, BASE_HEIGHT + COLUMN_HEIGHT), parent=root)

    # The chest: a rounded slab that widens into the shoulder yoke at the top.
    chest = c.lathe(
        "torso.chest",
        [
            (0.0000, 0.0000),
            (0.1000, 0.0000),
            (0.1080, 0.0200),
            (0.1120, 0.0800),
            (0.1140, 0.1600),
            (0.1150, 0.2100),
            (0.1080, 0.2450),
            (0.0900, 0.2680),
            (0.0640, 0.2800),
            (0.0000, 0.2800),
        ],
        mat["cloth"],
        segments=40,
    )
    chest.scale = (1.0, 0.62, 1.0)   # the torso is much deeper than it is wide
    c.attach(chest, torso)

    # The Breton shirt. Stripes are separate rings rather than a texture: the viewport has no
    # texture pipeline, and the stripe spacing is the detail that carries the likeness.
    for i in range(STRIPE_COUNT):
        z = 0.030 + i * 0.0168
        ring = c.lathe(
            f"torso.stripe.{i}",
            [(0.1135, 0.0000), (0.1160, 0.0030), (0.1135, 0.0070)],
            mat["navy"],
            segments=40,
            location=(0, 0, z),
            cap=False,
        )
        ring.scale = (1.0, 0.625, 1.0)
        c.attach(ring, torso)

    # The yoke: the navy band round the neck and over each shoulder.
    yoke = c.lathe(
        "torso.yoke",
        [(0.0960, 0.0000), (0.1010, 0.0060), (0.0990, 0.0140), (0.0930, 0.0180)],
        mat["navy"],
        segments=40,
        location=(0, 0, 0.2380),
        cap=False,
    )
    yoke.scale = (1.0, 0.64, 1.0)
    c.attach(yoke, torso)

    # Chest badge and the dark speaker slot beneath it.
    badge = c.cylinder(
        "torso.badge", 0.016, 0.004, mat["metal"], segments=6,
        location=(0, -0.072, 0.2050), rotation=(math.pi / 2, 0, 0),
    )
    c.attach(badge, torso)

    slot = c.rounded_box(
        "torso.speaker", (0.064, 0.010, 0.013), 0.0040, mat["dark"], segments=3,
        location=(0, -0.070, 0.1700),
    )
    c.attach(slot, torso)

    # The neck: a white column out of the chest, carrying the collar the yoke wraps.
    neck_column = c.lathe(
        "torso.neck",
        [
            (0.0000, 0.0000),
            (0.0560, 0.0000),
            (0.0520, 0.0180),
            (0.0450, 0.0360),
            (0.0405, 0.0520),
            (0.0000, 0.0520),
        ],
        mat["shell"],
        segments=32,
        location=(0, 0, TORSO_HEIGHT - 0.012),
    )
    c.attach(neck_column, torso)

    # ---------------------------------------------------------------- arms

    _arm("right", -1, mat, torso)
    _arm("left", 1, mat, torso)

    # ---------------------------------------------------------------- head

    neck_roll = c.joint("head.neck.roll", location=(0, 0, TORSO_HEIGHT + 0.040), parent=torso)
    neck_pitch = c.joint("head.neck.pitch", parent=neck_roll)
    neck_yaw = c.joint("head.neck.yaw", parent=neck_pitch)

    # The Orbita wrist: a short stack of silver discs.
    for i, z in enumerate((0.000, 0.016, 0.030)):
        disc = c.cylinder(
            f"head.neck.disc.{i}", 0.026 - i * 0.003, 0.012, mat["metal"], segments=20, location=(0, 0, z)
        )
        c.attach(disc, neck_yaw)

    head_centre = 0.030 + HEAD_SIZE[2] / 2 + 0.014

    skull = c.rounded_box(
        "head.shell", HEAD_SIZE, HEAD_RADIUS, mat["shell"], segments=6, location=(0, 0, head_centre)
    )
    c.attach(skull, neck_yaw)

    face_y = -HEAD_SIZE[1] / 2
    eye_z = head_centre + 0.004

    for side, x in (("right", -0.036), ("left", 0.036)):
        bezel = c.cylinder(
            f"head.eye.{side}.bezel", 0.0250, 0.015, mat["dark"], segments=28,
            location=(x, face_y + 0.005, eye_z), rotation=(math.pi / 2, 0, 0),
        )
        lens = c.cylinder(
            f"head.eye.{side}.lens", 0.0200, 0.013, mat["lens"], segments=28,
            location=(x, face_y + 0.001, eye_z), rotation=(math.pi / 2, 0, 0),
        )
        glass = c.sphere(
            f"head.eye.{side}.glass", 0.0162, mat["glass"], segments=20,
            location=(x, face_y - 0.003, eye_z), scale=(1, 0.40, 1),
        )
        c.attach_all(neck_yaw, bezel, lens, glass)

    bridge = c.rounded_box(
        "head.bridge", (0.048, 0.011, 0.0100), 0.0038, mat["dark"], segments=3,
        location=(0, face_y + 0.004, eye_z),
    )
    c.attach(bridge, neck_yaw)

    # ---------------------------------------------------------------- antennas

    top = head_centre + HEAD_SIZE[2] / 2 - 0.006

    for side, sign in (("r_antenna", -1), ("l_antenna", 1)):
        pivot = c.joint(f"head.{side}", location=(sign * 0.048, 0.016, top), parent=neck_yaw)

        rod = c.tube(
            f"head.{side}.rod",
            [(0, 0, 0), (sign * 0.068, -0.004, 0.205)],
            0.0022, mat["wire"], segments=8,
        )
        tip = c.sphere(f"head.{side}.tip", 0.0065, mat["wire"], segments=14,
                       location=(sign * 0.068, -0.004, 0.205))
        c.attach_all(pivot, rod, tip)

    return root


def preview_pose(objects):
    """Arms lowered and slightly out, as in the photographs.

    At rest every joint is zero, which hangs both arms dead straight down against the torso - a pose
    the robot is never photographed in and one that hides the elbow and wrist entirely.
    """
    for prefix, sign in (("r_arm", -1), ("l_arm", 1)):
        for name, rotation in (
            (f"{prefix}.shoulder.roll", (0, sign * math.radians(-14), 0)),
            (f"{prefix}.elbow.pitch", (math.radians(-38), 0, 0)),
        ):
            node = objects.get(name)

            if node is not None:
                node.rotation_euler = rotation
