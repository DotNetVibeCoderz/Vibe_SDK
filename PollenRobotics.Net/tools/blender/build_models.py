"""Builds the simulator's robot meshes.

    blender --background --factory-startup --python tools/blender/build_models.py -- [options]

    --only <name>     build just reachy-mini, microduck or reachy2
    --preview         also render a three-quarter PNG of each model next to the .glb
    --out <dir>       where the .glb files go (defaults to the simulator's wwwroot/models)

The models are generated rather than hand-sculpted so that a proportion can be corrected by editing
one number and re-running, and so the repository carries the recipe rather than only the result.
Pollen publishes no meshes under a licence this project could vendor; everything here is modelled
from the photographs in images/.
"""

import argparse
import importlib
import math
import sys
from pathlib import Path

import bpy

sys.path.insert(0, str(Path(__file__).parent))

import common as c  # noqa: E402  (needs the path above)

# Imported on demand so that --only can build one robot without the other modules having to parse.
# Useful while a model is being reshaped: a syntax error in one should not block the other two.
BUILDERS = {
    "reachy-mini": ("reachy_mini", 0.34),
    "microduck": ("microduck", 0.40),
    "reachy2": ("reachy2", 1.70),
}

DEFAULT_OUT = Path(__file__).resolve().parents[2] / "apps" / "PollenRobotics.Net.Simulator" / "wwwroot" / "models"


def render_preview(path, height):
    """A quick three-quarter render, for comparing against the reference photographs.

    Lighting is deliberately dim. The first pass used an energy proportional to the subject size and
    blew every surface to white - a 0.10 grey read the same as a 0.93 one, so the render could not
    be used to check a colour at all, only a silhouette. Area-light energy has to fall off with the
    square of the distance to hold exposure steady across robots that differ fourfold in height.
    """
    scene = bpy.context.scene

    distance = height * 1.6

    camera_data = bpy.data.cameras.new("preview")
    camera_data.lens = 60
    camera = bpy.data.objects.new("preview", camera_data)
    c.link(camera)
    scene.camera = camera

    camera.location = (distance * 0.78, -distance * 1.05, height * 0.60)
    camera.rotation_euler = (math.radians(82), 0, math.radians(37))

    def area(name, energy_per_m2, size, location, rotation):
        data = bpy.data.lights.new(name, type="AREA")
        data.energy = energy_per_m2 * distance * distance
        data.size = size
        obj = bpy.data.objects.new(name, data)
        obj.location = location
        obj.rotation_euler = rotation
        c.link(obj)

    area("key", 26, height * 1.2,
         (distance * 0.9, -distance * 1.0, height * 1.5),
         (math.radians(48), 0, math.radians(42)))
    area("fill", 8, height * 1.6,
         (-distance * 1.2, -distance * 0.8, height * 0.7),
         (math.radians(74), 0, math.radians(-56)))
    area("rim", 14, height * 1.0,
         (-distance * 0.4, distance * 1.1, height * 1.3),
         (math.radians(124), 0, math.radians(-160)))

    available = {item.identifier for item in type(scene.render).bl_rna.properties["engine"].enum_items}
    scene.render.engine = "BLENDER_EEVEE" if "BLENDER_EEVEE" in available else "BLENDER_EEVEE_NEXT"
    scene.render.resolution_x = 720
    scene.render.resolution_y = 820
    scene.render.filepath = str(path)

    # Standard rather than AgX: this render exists to check that a colour is the colour it was
    # asked for, and a filmic transform is exactly what makes that impossible to tell.
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"

    scene.world = bpy.data.worlds.new("world")
    scene.world.use_nodes = True
    scene.world.node_tree.nodes["Background"].inputs[0].default_value = (0.045, 0.05, 0.06, 1)
    scene.world.node_tree.nodes["Background"].inputs[1].default_value = 0.9

    bpy.ops.render.render(write_still=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []

    parser = argparse.ArgumentParser()
    parser.add_argument("--only", choices=sorted(BUILDERS))
    parser.add_argument("--preview", action="store_true")
    parser.add_argument("--preview-out", type=Path, help="where preview PNGs go; they are build output, not assets")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    args = parser.parse_args(argv)

    args.out.mkdir(parents=True, exist_ok=True)
    names = [args.only] if args.only else list(BUILDERS)

    for name in names:
        module_name, height = BUILDERS[name]
        module = importlib.import_module(module_name)
        module.build()

        target = args.out / f"{name}.glb"
        c.export_glb(target, name)

        if args.preview and hasattr(module, "preview_pose"):
            # Posed only for the render. The .glb is exported first, so what ships is always the
            # rest pose regardless of what the preview does to it.
            module.preview_pose({obj.name: obj for obj in bpy.data.objects})

        if args.preview:
            # Previews never go next to the .glb: wwwroot is copied into the app, and a render is
            # build output, not an asset.
            preview_dir = args.preview_out or (Path(__file__).parent / "preview")
            preview_dir.mkdir(parents=True, exist_ok=True)
            preview = preview_dir / f"{name}.png"
            render_preview(preview, height)
            print(f"[{name}] preview {preview.name}")

    print(f"done: {', '.join(names)}")


main()
