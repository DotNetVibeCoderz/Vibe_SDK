"""MicroDuck, built from Pollen's own baked mesh.

Earlier versions of this file modelled the duck by eye from the photographs in images/. That is
gone: `src/PollenRobotics.Net.MicroDuck/Reference/duck.bin` is Pollen's own visual geometry, Apache
2.0 from pollen-robotics/microduck, and it carries 29 meshes, 59 coloured parts and the real body
hierarchy. Shapes modelled by eye cannot compete with the shapes the robot actually has.

The file is what `robotctl monitor` draws on the board. Its format is documented by the script that
writes it, `scripts/bake-duck-mesh.py` in the same repository:

    header   "DUCK", u32 version, u16 meshes, u16 bodies, u16 parts
    mesh     u16 vertex count, u16 face count, f32[3n] vertices, u16[3m] faces
    body     i16 parent, i16 joint, f32[3] pos, f32[4] quat (w x y z), f32[3] axis
    part     u16 body, u16 mesh, u8[4] rgb + pad, f32[3] pos, f32[4] quat

Two nodes are emitted per body. The outer one carries the body's fixed pos/quat from the file; the
inner one is the joint, named for its SDK joint, and starts unrotated. Baking both into a single
node would mean the viewport's `rotation.z = angle` wiped out the frame the hinge is measured in.

Every joint is a hinge about its own local Z - that is what the `axis` column says for all fifteen -
so the viewport drives them uniformly instead of carrying a per-joint axis convention.
"""

import struct
from pathlib import Path

import bmesh
import bpy
from mathutils import Quaternion

import common as c

# The wire order of duck_ipc_proto::JOINT_NAMES. `mouth` has no MuJoCo joint - the jaw is a fixed
# geom - so no body in the file points at index 9, but the runtime still reports a value there and
# RobotCatalog.MicroDuck must agree with this list, not with the MJCF.
JOINT_NAMES = [
    "left_hip_yaw", "left_hip_roll", "left_hip_pitch", "left_knee", "left_ankle",
    "neck_pitch", "head_pitch", "head_yaw", "head_roll", "mouth",
    "right_hip_yaw", "right_hip_roll", "right_hip_pitch", "right_knee", "right_ankle",
]

# The mouth is a fixed geom in the file, so the jaw has to be found by name and re-parented under a
# joint of its own, or the duck's one expressive part cannot move.
JAW_PART_HINT = "mouth"

DUCK_BIN = (Path(__file__).resolve().parents[2]
            / "src" / "PollenRobotics.Net.MicroDuck" / "Reference" / "duck.bin")


def read(path):
    """Decodes duck.bin into (meshes, bodies, parts)."""
    data = path.read_bytes()
    magic, version, n_mesh, n_body, n_part = struct.unpack_from("<4sIHHH", data, 0)

    if magic != b"DUCK":
        raise ValueError(f"{path.name} is not a baked duck mesh (magic {magic!r}).")

    if version != 1:
        raise ValueError(f"{path.name} is format version {version}; this reader handles 1.")

    off = struct.calcsize("<4sIHHH")
    meshes = []

    for _ in range(n_mesh):
        nv, nf = struct.unpack_from("<HH", data, off)
        off += 4
        verts = struct.unpack_from(f"<{nv * 3}f", data, off)
        off += nv * 12
        faces = struct.unpack_from(f"<{nf * 3}H", data, off)
        off += nf * 6
        meshes.append((
            [verts[i:i + 3] for i in range(0, len(verts), 3)],
            [faces[i:i + 3] for i in range(0, len(faces), 3)],
        ))

    bodies = []

    for _ in range(n_body):
        parent, joint = struct.unpack_from("<hh", data, off)
        off += 4
        pos = struct.unpack_from("<3f", data, off)
        off += 12
        quat = struct.unpack_from("<4f", data, off)
        off += 16
        off += 12   # axis: every joint is a hinge about local Z, so it is not needed here
        bodies.append({"parent": parent, "joint": joint, "pos": pos, "quat": quat})

    parts = []

    for _ in range(n_part):
        body, mesh, r, g, b, _pad = struct.unpack_from("<HHBBBB", data, off)
        off += 8
        pos = struct.unpack_from("<3f", data, off)
        off += 12
        quat = struct.unpack_from("<4f", data, off)
        off += 16
        parts.append({"body": body, "mesh": mesh, "rgb": (r, g, b), "pos": pos, "quat": quat})

    return meshes, bodies, parts


def material_for(rgb, cache):
    """One material per distinct colour. 59 parts share far fewer than 59 colours."""
    if rgb in cache:
        return cache[rgb]

    # sRGB bytes to linear, which is what Blender's Base Color wants.
    def linear(v):
        v /= 255.0
        return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4

    name = "duck_%02x%02x%02x" % rgb
    # Pollen's palette is printed plastic: matte, no metal. The darker parts are the servo brackets
    # and get a little more sheen than the shells.
    rough = 0.42 if sum(rgb) < 200 else 0.58
    cache[rgb] = c.material(name, tuple(linear(v) for v in rgb), roughness=rough)
    return cache[rgb]


def mesh_object(name, verts, faces, mat):
    bm = bmesh.new()
    bverts = [bm.verts.new(v) for v in verts]
    bm.verts.ensure_lookup_table()

    for a, b, d in faces:
        try:
            bm.faces.new((bverts[a], bverts[b], bverts[d]))
        except ValueError:
            # A duplicate face. The bake decimates, and decimation occasionally emits one.
            pass

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()

    for polygon in mesh.polygons:
        polygon.use_smooth = False   # printed plastic reads better faceted at this triangle count

    mesh.materials.append(mat)
    return c.link(bpy.data.objects.new(name, mesh))


def build():
    c.reset_scene()
    c.forget_materials()

    if not DUCK_BIN.exists():
        raise FileNotFoundError(
            f"{DUCK_BIN} is missing. It is vendored from pollen-robotics/microduck under Apache 2.0."
        )

    meshes, bodies, parts = read(DUCK_BIN)

    root = c.joint("root")

    # `body` carries the world pose the simulation computes: the duck walks, so unlike the other two
    # robots its root moves. The file's first body is the trunk, already at its standing height.
    world = c.joint("body", parent=root)

    frames = {}     # body index -> the node its children and parts hang from
    colours = {}

    for index, body in enumerate(bodies):
        parent = frames[body["parent"]] if body["parent"] >= 0 else world

        # The fixed frame: where this link sits relative to its parent.
        frame = c.joint(f"frame.{index}", location=body["pos"], parent=parent)
        frame.rotation_mode = "QUATERNION"
        frame.rotation_quaternion = Quaternion(body["quat"])

        if body["joint"] >= 0:
            # The hinge itself, named so the viewport can find it. Left unrotated: the viewport
            # writes rotation.z here every frame.
            frames[index] = c.joint(JOINT_NAMES[body["joint"]], parent=frame)
        else:
            frames[index] = frame

    jaw = None

    for index, part in enumerate(parts):
        verts, faces = meshes[part["mesh"]]
        obj = mesh_object(f"part.{index}", verts, faces, material_for(part["rgb"], colours))
        obj.location = part["pos"]
        obj.rotation_mode = "QUATERNION"
        obj.rotation_quaternion = Quaternion(part["quat"])
        c.attach(obj, frames[part["body"]])

    # The mouth. It is a fixed geom in the bake, so nothing moves it; give it a joint of its own
    # under the head so the duck can still open its bill.
    head = bpy.data.objects.get(c.node_name("head_roll"))

    if head is not None:
        jaw = c.joint("mouth", parent=head)

        # Re-parent the lowest-sitting parts of the head to the jaw. The bill is the part of the
        # head shell that hangs below the camera line, and it is the only thing down there.
        for child in list(head.children):
            if child.type == "MESH" and child.location.z < -0.055:
                c.attach(child, jaw)

    return root
