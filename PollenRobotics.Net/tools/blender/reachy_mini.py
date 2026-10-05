"""Reachy Mini.

Shapes follow images/reachy_mini.jpg and images/reachy_mini2.jpg. Three things carry the
likeness, and they are the three the old primitive rig got wrong:

  * the body is a bell, not a cylinder - it swells low down and tucks back under to a dark base;
  * the head is a rounded slab, not a sphere, with two large lenses joined by a bar across the
    bridge, like spectacles;
  * the antennas have a spring wound into the middle of the wire.

The ratio that matters most is head-to-body width: on the real robot the head is very nearly as
wide as the body, and a head any smaller immediately reads as a different machine.

Every location in this file is relative to its parent.
"""

import math

import common as c

BASE_HEIGHT = 0.009         # the dark puck the body overhangs
BODY_HEIGHT = 0.119         # white shell, measured from the top of the puck
NECK_GAP = 0.010            # fabric showing between shell and head
HEAD_RISE = 0.041           # head centre above the neck platform

HEAD_SIZE = (0.096, 0.058, 0.062)   # width (x), depth (y), height (z)
HEAD_RADIUS = 0.024

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
    (0.0400, 0.1020),
    (0.0355, 0.1100),
    (0.0310, 0.1160),
    (0.0285, 0.1190),
    (0.0000, 0.1190),
]


# Branch anchors, in body-local space. The viewport uses the same numbers: three pairs of struts
# whose base feet splay apart and whose platform ends converge, which is what makes a rotary Stewart
# platform look like one rather than like a six-sided cage.
STRUT_BASE_RADIUS = 0.0430
STRUT_PLATFORM_RADIUS = 0.0235

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
        "fabric": c.material("fabric", (0.40, 0.41, 0.44), roughness=0.88),
        "strut": c.material("strut", (0.70, 0.71, 0.74), roughness=0.30, metallic=0.7),
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

    # Vent slots around the foot of the body, just visible in both photographs.
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

    # A grey fabric cuff fills the gap between shell and head. It is the only thing stopping the
    # head from looking like it is floating, and on the robot it hides the neck mechanism.
    bellows = c.lathe(
        "neck.bellows",
        # Narrower than the strut circle on purpose. The real robot hides its neck completely, but
        # a simulator whose whole point is showing a parallel mechanism move cannot also bury it:
        # the cuff sits inside the struts, so the mechanism stays readable from any angle.
        [
            (0.0205, 0.0000),
            (0.0198, 0.0040),
            (0.0186, 0.0090),
            (0.0176, 0.0135),
            (0.0170, 0.0170),
        ],
        mat["fabric"],
        segments=28,
        location=(0, 0, BODY_HEIGHT - 0.008),
        cap=False,
    )
    c.attach(bellows, body)

    # The six Stewart struts, placed where they rest. The viewport re-aims them every frame in this
    # same body-local space; building them here as well means a model that has not been posed yet
    # still looks like a neck rather than like six rods stacked at the origin.
    for i, (base_angle, platform_angle) in enumerate(STRUT_ANCHORS):
        strut = c.span_cylinder(
            f"strut.{i}",
            (STRUT_BASE_RADIUS * math.cos(base_angle), STRUT_BASE_RADIUS * math.sin(base_angle), 0.012),
            (STRUT_PLATFORM_RADIUS * math.cos(platform_angle),
             STRUT_PLATFORM_RADIUS * math.sin(platform_angle),
             BODY_HEIGHT + NECK_GAP - 0.004),
            0.0028,
            mat["strut"],
            segments=8,
        )
        c.attach(strut, body)

    # ---------------------------------------------------------------- head

    # Head pose is applied to the neck node; the head hangs off it.
    neck = c.joint("neck", location=(0, 0, BODY_HEIGHT + NECK_GAP), parent=body)
    head = c.joint("head", parent=neck)

    skull = c.rounded_box(
        "head.shell", HEAD_SIZE, HEAD_RADIUS, mat["shell"], segments=6, location=(0, 0, HEAD_RISE)
    )
    c.attach(skull, head)

    # The face: two big lenses with a bar between them. On the robot that is a camera pair and a
    # sensor strip; in silhouette it is unmistakably a pair of spectacles, and it is the only cue
    # that says which way the head is pointing.
    face_y = -HEAD_SIZE[1] / 2
    eye_z = HEAD_RISE + 0.004

    for side, x in (("right", -0.0215), ("left", 0.0215)):
        bezel = c.cylinder(
            f"head.eye.{side}.bezel", 0.0160, 0.013, mat["shell_dark"], segments=28,
            location=(x, face_y + 0.005, eye_z), rotation=(math.pi / 2, 0, 0),
        )
        lens = c.cylinder(
            f"head.eye.{side}.lens", 0.0128, 0.011, mat["lens"], segments=28,
            location=(x, face_y + 0.001, eye_z), rotation=(math.pi / 2, 0, 0),
        )
        glass = c.sphere(
            f"head.eye.{side}.glass", 0.0102, mat["glass"], segments=20,
            location=(x, face_y - 0.0025, eye_z), scale=(1, 0.40, 1),
        )
        c.attach_all(head, bezel, lens, glass)

    bridge = c.rounded_box(
        "head.bridge", (0.026, 0.009, 0.0060), 0.0024, mat["shell_dark"], segments=3,
        location=(0, face_y + 0.004, eye_z),
    )
    c.attach(bridge, head)

    # ---------------------------------------------------------------- antennas

    top = HEAD_RISE + HEAD_SIZE[2] / 2 - 0.004

    for side, sign in (("right", -1), ("left", 1)):
        pivot = c.joint(f"antenna.{side}", location=(sign * 0.029, 0.004, top), parent=head)

        stalk = c.tube(
            f"antenna.{side}.stalk", [(0, 0, 0), (sign * 0.003, 0, 0.014)], 0.0012, mat["wire"], segments=6
        )

        spring_start = (sign * 0.003, 0.0, 0.014)
        spring_end = (sign * 0.011, 0.0, 0.036)

        spring = c.helix(
            f"antenna.{side}.spring", spring_start, spring_end,
            radius=0.0040, coils=7, wire_radius=0.0010, mat=mat["wire"],
        )

        whip = c.tube(
            f"antenna.{side}.whip", [spring_end, (sign * 0.052, -0.008, 0.092)], 0.0011, mat["wire"], segments=6
        )

        c.attach_all(pivot, stalk, spring, whip)

    return root
