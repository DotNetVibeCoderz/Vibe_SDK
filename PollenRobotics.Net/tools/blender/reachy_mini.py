"""Reachy Mini.

Shapes follow images/reachy_mini.jpg, reachy_mini2.jpg and - for the neck - reachy_mini3.jpg,
which is a front-on photograph of one part-assembled and is the only clear view of the mechanism.

What that photograph settles:

  * **The neck is open.** The body is a bowl, and six thin steel rods rise out of it to a plate
    under the head, with brass fittings where they meet it and a bundle of black wire up the
    middle. There is no shroud. An earlier version put a grey fabric cuff in the gap, which was
    wrong twice over - the robot has no such part, and it hid the one mechanism this simulator
    exists to show.
  * **The antenna spring sits at the base**, immediately where the wire leaves the head, not
    half-way up. The rest of the antenna is a long straight whip.
  * **The lenses are large** - each about a third of the head's width - and joined by a short thin
    bar at their centre line.

The ratio that matters most is head-to-body width: on the real robot the head is very nearly as
wide as the body, and a head any smaller immediately reads as a different machine.

Every location in this file is relative to its parent.
"""

import math

import common as c

BASE_HEIGHT = 0.009         # the dark puck the body overhangs
BODY_RIM = 0.102            # top of the bowl, measured from the top of the puck

# The neck. These numbers are mirrored in viewport.js, which re-aims the struts every frame and
# cannot read them back out of the .glb.
STRUT_BASE_RADIUS = 0.0345
STRUT_BASE_Z = 0.082
STRUT_PLATFORM_RADIUS = 0.0255
PLATFORM_Z = 0.138

NECK_Z = 0.140              # head plate, just above the strut tops
HEAD_RISE = 0.036           # head centre above the plate

HEAD_SIZE = (0.098, 0.060, 0.064)   # width (x), depth (y), height (z)
HEAD_RADIUS = 0.024

# A bowl: the outer wall rises to a rim, rolls inward, and comes back down to a floor. A closed
# shell would be simpler and would also mean the struts emerged from nothing.
BODY_PROFILE = [
    (0.0000, 0.0000),
    (0.0450, 0.0000),
    (0.0492, 0.0045),
    (0.0506, 0.0105),
    (0.0520, 0.0200),
    (0.0527, 0.0340),   # widest, low down
    (0.0522, 0.0500),
    (0.0505, 0.0660),
    (0.0478, 0.0800),
    (0.0442, 0.0920),
    (0.0415, 0.0990),
    (0.0400, BODY_RIM),  # rim
    (0.0384, 0.0995),    # and back down inside
    (0.0376, 0.0900),
    (0.0370, 0.0780),
    (0.0000, 0.0760),
]

# Base and platform anchor angles. Three pairs: the feet splay apart and the tops converge, which is
# what makes a rotary Stewart platform look like one rather than like a six-sided cage.
STRUT_ANCHORS = [
    (pair * 2 * math.pi / 3 + sign * math.radians(26),
     pair * 2 * math.pi / 3 + math.radians(12) + sign * math.radians(20))
    for pair in range(3)
    for sign in (-1, 1)
]


def palette():
    return {
        "shell": c.material("shell", (0.92, 0.92, 0.91), roughness=0.36),
        "shell_dark": c.material("shell_dark", (0.055, 0.055, 0.065), roughness=0.28, metallic=0.1),
        "lens": c.material("lens", (0.015, 0.015, 0.02), roughness=0.10, metallic=0.2),
        "glass": c.material("glass", (0.03, 0.035, 0.05), roughness=0.06, metallic=0.4),
        "steel": c.material("steel", (0.78, 0.79, 0.82), roughness=0.18, metallic=0.95),
        "brass": c.material("brass", (0.80, 0.62, 0.26), roughness=0.26, metallic=0.9),
        "wire": c.material("wire", (0.045, 0.045, 0.05), roughness=0.42),
    }


def build():
    c.reset_scene()
    c.forget_materials()
    mat = palette()

    root = c.joint("root")

    # ---------------------------------------------------------------- base

    base = c.lathe(
        "base",
        [
            (0.0000, 0.0000),
            (0.0370, 0.0000),
            (0.0420, 0.0025),
            (0.0430, 0.0060),
            (0.0415, BASE_HEIGHT),
            (0.0000, BASE_HEIGHT),
        ],
        mat["shell_dark"],
    )
    c.attach(base, root)

    # ---------------------------------------------------------------- body

    # Body yaw turns everything above the base.
    body = c.joint("body", location=(0, 0, BASE_HEIGHT), parent=root)

    shell = c.lathe("body.shell", BODY_PROFILE, mat["shell"])
    c.attach(shell, body)

    # The inside of the bowl is dark, so the opening reads as an opening.
    interior = c.lathe(
        "body.interior",
        [(0.0000, 0.0000), (0.0366, 0.0000), (0.0362, 0.0180), (0.0358, 0.0225), (0.0000, 0.0225)],
        mat["shell_dark"],
        segments=36,
        location=(0, 0, 0.0765),
    )
    c.attach(interior, body)

    # Vent slots around the foot of the body, just visible in both finished photographs.
    for i in range(8):
        angle = (i / 8) * 2 * math.pi + math.pi / 16
        slot = c.rounded_box(
            f"body.vent.{i}",
            (0.0022, 0.0022, 0.013),
            0.0009,
            mat["shell_dark"],
            segments=2,
            location=(0.0510 * math.cos(angle), 0.0510 * math.sin(angle), 0.015),
            rotation=(0, 0, angle),
        )
        c.attach(slot, body)

    # ---------------------------------------------------------------- neck

    # The six struts, built where they rest. The viewport re-aims them every frame in this same
    # body-local space; building them here as well means a model that has not been posed yet still
    # looks like a neck rather than like six rods stacked at the origin.
    for i, (base_angle, platform_angle) in enumerate(STRUT_ANCHORS):
        strut = c.span_cylinder(
            f"strut.{i}",
            (STRUT_BASE_RADIUS * math.cos(base_angle), STRUT_BASE_RADIUS * math.sin(base_angle), STRUT_BASE_Z),
            (STRUT_PLATFORM_RADIUS * math.cos(platform_angle),
             STRUT_PLATFORM_RADIUS * math.sin(platform_angle),
             PLATFORM_Z),
            0.0019,
            mat["steel"],
            segments=10,
        )
        c.attach(strut, body)

        # The motor horn each rod stands on, down in the bowl.
        horn = c.cylinder(
            f"strut.{i}.horn", 0.0062, 0.0040, mat["steel"], segments=12,
            location=(STRUT_BASE_RADIUS * math.cos(base_angle),
                      STRUT_BASE_RADIUS * math.sin(base_angle),
                      STRUT_BASE_Z - 0.003),
        )
        c.attach(horn, body)

    # The wiring loom up the middle of the neck. Without it the gap reads as empty rather than as
    # the inside of a robot.
    for i, angle in enumerate((0.4, 2.5, 4.6)):
        loom = c.tube(
            f"neck.loom.{i}",
            [
                (0.006 * math.cos(angle), 0.006 * math.sin(angle), 0.078),
                (0.010 * math.cos(angle), 0.010 * math.sin(angle), 0.100),
                (0.008 * math.cos(angle), 0.008 * math.sin(angle), 0.124),
                (0.004 * math.cos(angle), 0.004 * math.sin(angle), 0.138),
            ],
            0.0016,
            mat["wire"],
            segments=6,
        )
        c.attach(loom, body)

    # ---------------------------------------------------------------- head

    # Head pose is applied to the neck node; everything above hangs off it.
    neck = c.joint("neck", location=(0, 0, NECK_Z), parent=body)
    head = c.joint("head", parent=neck)

    # The plate the struts push against, and the brass fittings at their tips.
    plate = c.lathe(
        "head.plate",
        [(0.0000, 0.0000), (0.0320, 0.0000), (0.0316, 0.0045), (0.0000, 0.0045)],
        mat["shell"],
        segments=36,
        location=(0, 0, -0.002),
    )
    c.attach(plate, head)

    for i, (_, platform_angle) in enumerate(STRUT_ANCHORS):
        fitting = c.cylinder(
            f"head.fitting.{i}", 0.0042, 0.0060, mat["brass"], segments=12,
            location=(STRUT_PLATFORM_RADIUS * math.cos(platform_angle),
                      STRUT_PLATFORM_RADIUS * math.sin(platform_angle),
                      -0.003),
        )
        c.attach(fitting, head)

    skull = c.rounded_box(
        "head.shell", HEAD_SIZE, HEAD_RADIUS, mat["shell"], segments=6, location=(0, 0, HEAD_RISE)
    )
    c.attach(skull, head)

    # The face: two big lenses with a bar between them. On the robot that is a camera pair and a
    # sensor strip; in silhouette it is unmistakably a pair of spectacles, and it is the only cue
    # that says which way the head is pointing.
    face_y = -HEAD_SIZE[1] / 2
    eye_z = HEAD_RISE + 0.003

    for side, x in (("right", -0.0235), ("left", 0.0235)):
        bezel = c.cylinder(
            f"head.eye.{side}.bezel", 0.0192, 0.013, mat["shell_dark"], segments=32,
            location=(x, face_y + 0.005, eye_z), rotation=(math.pi / 2, 0, 0),
        )
        lens = c.cylinder(
            f"head.eye.{side}.lens", 0.0168, 0.011, mat["lens"], segments=32,
            location=(x, face_y + 0.001, eye_z), rotation=(math.pi / 2, 0, 0),
        )
        glass = c.sphere(
            f"head.eye.{side}.glass", 0.0140, mat["glass"], segments=22,
            location=(x, face_y - 0.0035, eye_z), scale=(1, 0.36, 1),
        )
        c.attach_all(head, bezel, lens, glass)

    bridge = c.rounded_box(
        "head.bridge", (0.022, 0.009, 0.0050), 0.0020, mat["shell_dark"], segments=3,
        location=(0, face_y + 0.003, eye_z),
    )
    c.attach(bridge, head)

    # ---------------------------------------------------------------- antennas

    top = HEAD_RISE + HEAD_SIZE[2] / 2 - 0.004

    for side, sign in (("right", -1), ("left", 1)):
        pivot = c.joint(f"antenna.{side}", location=(sign * 0.030, 0.006, top), parent=head)

        # The spring is at the base, where the wire leaves the head. Putting it half-way up - which
        # the first version did - loses the detail entirely: at viewing distance all that reads is a
        # kink in a straight rod.
        spring = c.helix(
            f"antenna.{side}.spring",
            (0.0, 0.0, 0.0),
            (sign * 0.003, 0.0, 0.017),
            radius=0.0036,
            coils=6,
            wire_radius=0.0010,
            mat=mat["wire"],
        )

        whip = c.tube(
            f"antenna.{side}.whip",
            [(sign * 0.003, 0.0, 0.017), (sign * 0.026, -0.008, 0.096)],
            0.0011,
            mat["wire"],
            segments=6,
        )

        c.attach_all(pivot, spring, whip)

    return root
