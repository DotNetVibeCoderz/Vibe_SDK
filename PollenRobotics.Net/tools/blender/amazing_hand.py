"""Amazing Hand, built from Pollen's own MuJoCo model and STL assets.

The model is `src/PollenRobotics.Net.AmazingHand/Reference/robot.xml`, vendored from
pollen-robotics/AmazingHand under Apache 2.0. The meshes it references are 7.5 MB across 50 STL
files, which is build input rather than an asset, so they are fetched on first run and cached
outside the repository. The .glb is what ships.

Two filters keep the result at viewport weight:

  * **Fasteners are skipped.** The CAD assembly carries every screw, washer, pin and bushing - 162
    visual geoms in total. At the size a hand is drawn on screen they are invisible, and their
    triangles are most of the file.
  * **Shells are decimated.** `distal_shell.stl` alone is 1.1 MB of CAD tessellation. A few hundred
    triangles carry the same silhouette at this scale.

The joint names are the ones RobotCatalog.AmazingHand uses: `finger1_motor1` and friends. Passive
joints - the ball joints and idler hinges that make each finger a four-bar - are left as fixed
frames, so the model shows the linkage without pretending the viewport solves it.
"""

import re
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

import bpy
from mathutils import Quaternion

import common as c

REFERENCE = (Path(__file__).resolve().parents[2]
             / "src" / "PollenRobotics.Net.AmazingHand" / "Reference" / "robot.xml")

CACHE = Path(__file__).parent / ".stl-cache" / "amazing-hand"

ASSETS = ("https://raw.githubusercontent.com/pollen-robotics/AmazingHand/main"
          "/Demo/AHSimulation/AHSimulation/AH_Right/mjcf/assets")

# Hardware that exists in the CAD and is invisible on screen. Matched as substrings, because the
# exported names carry a configuration hash: `parallel_pin_2_x_10__fee063...`.
FASTENERS = ("screw", "washer", "parallel_pin", "bushing", "std00333", "m2_rod")

# Roughly how many triangles each imported mesh is worth keeping. The shells are smooth organic
# surfaces and need the most; plates and levers are mostly flat.
TRIANGLE_BUDGET = 400

PALETTE = {
    "shell": (0.86, 0.86, 0.88),
    "frame": (0.14, 0.15, 0.17),
    "servo": (0.10, 0.11, 0.13),
    "metal": (0.62, 0.63, 0.67),
    "accent": (0.95, 0.52, 0.10),
}


def colour_for(mesh_name):
    """Pollen's MJCF carries materials, but the STL names say more than the material names do."""
    name = mesh_name.lower()

    if "shell" in name:
        return "shell", PALETTE["shell"], 0.45, 0.0
    if "scs0009" in name or "servo" in name:
        return "servo", PALETTE["servo"], 0.50, 0.1
    if "plate" in name or "interface" in name:
        return "frame", PALETTE["frame"], 0.45, 0.1
    if "rotule" in name or "ball" in name or "gimbal" in name:
        return "metal", PALETTE["metal"], 0.30, 0.8

    return "frame", PALETTE["frame"], 0.48, 0.1


def fetch(mesh_name):
    """Returns a local path to an STL, downloading it once."""
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / f"{mesh_name}.stl"

    if path.exists() and path.stat().st_size > 0:
        return path

    url = f"{ASSETS}/{mesh_name}.stl"
    print(f"  fetching {mesh_name}.stl")

    try:
        with urllib.request.urlopen(url, timeout=60) as response:
            path.write_bytes(response.read())
    except Exception as error:
        raise RuntimeError(
            f"Could not fetch {url}. The Amazing Hand's meshes are build input and are not "
            f"vendored; this step needs network access once. ({error})"
        ) from error

    return path


def import_stl(path, name):
    """Imports one STL and decimates it to the triangle budget."""
    before = set(bpy.data.objects)

    # Blender 4.x renamed the operator; 5.x keeps the new name.
    if hasattr(bpy.ops.wm, "stl_import"):
        bpy.ops.wm.stl_import(filepath=str(path))
    else:
        bpy.ops.import_mesh.stl(filepath=str(path))

    created = [o for o in bpy.data.objects if o not in before]

    if not created:
        raise RuntimeError(f"Importing {path.name} produced no object.")

    obj = created[0]
    obj.name = name

    faces = len(obj.data.polygons)

    if faces > TRIANGLE_BUDGET:
        modifier = obj.modifiers.new("decimate", "DECIMATE")
        modifier.ratio = TRIANGLE_BUDGET / faces

        with bpy.context.temp_override(object=obj, active_object=obj, selected_objects=[obj]):
            bpy.ops.object.modifier_apply(modifier=modifier.name)

    return obj


def build():
    c.reset_scene()
    c.forget_materials()

    if not REFERENCE.exists():
        raise FileNotFoundError(f"{REFERENCE} is missing. It is vendored under Apache 2.0.")

    tree = ET.parse(REFERENCE)
    worldbody = tree.getroot().find("worldbody")

    if worldbody is None:
        raise ValueError(f"{REFERENCE.name} has no <worldbody>.")

    root = c.joint("root")
    # The hand is bolted to a wrist and does not travel, but the viewport expects a `body` node on
    # every rig, so it gets one and simply never moves it.
    world = c.joint("body", parent=root)

    materials = {}
    imported = {}
    kept = 0
    skipped = 0

    def walk(element, parent):
        nonlocal kept, skipped

        name = element.get("name", "body")
        pos = [float(v) for v in (element.get("pos") or "0 0 0").split()]
        quat = [float(v) for v in (element.get("quat") or "1 0 0 0").split()]

        frame = c.joint(f"frame.{name}", location=pos, parent=parent)
        frame.rotation_mode = "QUATERNION"
        frame.rotation_quaternion = Quaternion(quat)

        # An actuated joint gets a node of its own, named for the catalogue. Passive joints - the
        # ball joints and idlers that close each four-bar - stay fixed: the viewport is given the
        # eight angles the robot reports, and solving the linkage from them is not its job.
        attach_to = frame
        joint = element.find("joint")

        if joint is not None and re.fullmatch(r"finger[1-4]_motor[12]", joint.get("name") or ""):
            attach_to = c.joint(joint.get("name"), parent=frame)

        for geom in element.findall("geom"):
            mesh_name = geom.get("mesh")

            if mesh_name is None or geom.get("class") != "visual":
                continue

            if any(token in mesh_name.lower() for token in FASTENERS):
                skipped += 1
                continue

            key, rgb, rough, metal = colour_for(mesh_name)

            if key not in materials:
                materials[key] = c.material(key, rgb, roughness=rough, metallic=metal)

            if mesh_name not in imported:
                imported[mesh_name] = import_stl(fetch(mesh_name), f"mesh.{mesh_name}")
                imported[mesh_name].hide_viewport = True
                imported[mesh_name].hide_render = True

            source = imported[mesh_name]
            copy = source.copy()
            copy.data = source.data
            copy.hide_viewport = False
            copy.hide_render = False
            copy.name = f"part.{kept}"
            copy.data.materials.clear()
            copy.data.materials.append(materials[key])
            c.link(copy)

            copy.location = [float(v) for v in (geom.get("pos") or "0 0 0").split()]
            copy.rotation_mode = "QUATERNION"
            copy.rotation_quaternion = Quaternion([float(v) for v in (geom.get("quat") or "1 0 0 0").split()])
            c.attach(copy, attach_to)
            kept += 1

        for child in element.findall("body"):
            walk(child, attach_to)

    for top in worldbody.findall("body"):
        walk(top, world)

    # The prototypes were only ever templates for the copies.
    for obj in imported.values():
        bpy.data.objects.remove(obj, do_unlink=True)

    print(f"  parts kept {kept}, fasteners skipped {skipped}, meshes {len(imported)}")
    return root


def preview_pose(objects):
    """Half-closed fingers, so the linkage reads as a linkage rather than as a flat plate."""
    import math

    for finger in range(1, 5):
        for motor in (1, 2):
            node = objects.get(c.node_name(f"finger{finger}_motor{motor}"))

            if node is not None:
                node.rotation_euler = (0, 0, math.radians(35))
