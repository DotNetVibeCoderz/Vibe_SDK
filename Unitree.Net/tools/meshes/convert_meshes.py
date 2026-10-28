"""Converts Unitree URDF visual meshes (DAE/STL) into compact GLB files for the simulator viewport.

usage:
    python convert_meshes.py <mesh dir> <out dir> <spec> [<spec> ...]

    spec:  [<out>:]<source>[+<source>...]=<triangle budget>[@<role>]

    <out>:   output name when it differs from the (first) source's, e.g. when merging
    +        merges sources that sit at the same URDF origin into one file (pelvis + its contour)
    @<role>  forces every face to one role: shell, limb or contact. STL carries no colour, so for
             STL meshes the role comes from the URDF's material name and is given here

    python convert_meshes.py unitree_ros/robots/go2_description/dae \\
        ../../apps/Unitree.Net.Simulator/wwwroot/models/go2 \\
        base=14000 hip=5000 thigh=5000 thigh_mirror=5000 calf=2500 calf_mirror=2500 foot=800

    python convert_meshes.py unitree_ros/robots/g1_description/meshes models/g1         pelvis:pelvis+pelvis_contour_link=4000@limb left_knee_link=3000@shell ...

requires: pip install trimesh pycollada fast_simplification numpy scipy

Why each step is here:

* Unitree's DAE exports are 0.5-10 MB apiece and 26 MB for one Go2. The viewport runs inside a WebView
  that loads them on every model switch, so each mesh is decimated to a triangle budget.
* Faces are grouped by the source material and each group is decimated separately. Simplifying the
  whole mesh at once lets the decimator collapse edges across a colour boundary, which smears the
  two-tone panels into each other.
* Colours are not kept. Each group is tagged with a surface role (shell / limb / contact) by the
  luminance of its source colour, and the viewport paints that role from the active theme. A baked
  colour would ignore the light/dark switch that every primitive in the scene already follows.
* Vertices are written in the link frame, Z up, metres — exactly as the URDF has them. glTF is
  nominally Y up, but the viewport hangs these under link nodes that already sit in the robot's Z-up
  frame, so converting here would only have to be undone there.
"""
import math
import sys
from pathlib import Path

import fast_simplification
import numpy as np
import trimesh
from trimesh.visual.material import PBRMaterial

# Normals are split at creases sharper than this, so machined edges stay crisp instead of being
# smoothed into a blob, while curved housings still shade smoothly.
CREASE_DEGREES = 35.0


def role_of(rgb):
    """Maps a source colour to the surface role the viewport themes."""
    lum = 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2]
    if lum >= 0.85:
        return "shell"
    if lum >= 0.25:
        return "limb"
    return "contact"


def colour_of(geometry):
    material = getattr(geometry.visual, "material", None)
    for attr in ("baseColorFactor", "diffuse"):
        value = getattr(material, attr, None)
        if value is not None:
            c = np.asarray(value, dtype=float)[:3]
            return c / 255.0 if c.max() > 1.0 else c
    return np.array([0.7, 0.7, 0.7])


def load_groups(path, groups, forced_role=None):
    """Adds {role: [Trimesh]} for every geometry in the file, baked into the file's root frame."""
    scene = trimesh.load(path, force="scene")
    for node in scene.graph.nodes_geometry:
        transform, geometry_name = scene.graph[node]
        geometry = scene.geometry[geometry_name]
        mesh = trimesh.Trimesh(geometry.vertices.copy(), geometry.faces.copy(), process=False)
        mesh.apply_transform(transform)
        groups.setdefault(forced_role or role_of(colour_of(geometry)), []).append(mesh)


def decimate(mesh, budget):
    mesh.merge_vertices()
    if len(mesh.faces) <= budget:
        return mesh
    vertices, faces = fast_simplification.simplify(
        mesh.vertices.astype(np.float32), mesh.faces.astype(np.int64), target_count=budget)
    return trimesh.Trimesh(vertices, faces, process=True)


def convert(sources, target, budget, forced_role=None):
    parts = {}
    for source in sources:
        load_groups(source, parts, forced_role)
    groups = {role: trimesh.util.concatenate(meshes) for role, meshes in parts.items()}
    total = sum(len(g.faces) for g in groups.values())

    scene = trimesh.Scene()
    kept = 0
    for role, mesh in sorted(groups.items()):
        # Each colour group gets a share of the budget proportional to its size, with a floor so a
        # small but visible detail — a logo, a foot pad — is not decimated away entirely.
        share = max(200, int(budget * len(mesh.faces) / total))
        mesh = decimate(mesh, share)
        mesh = trimesh.graph.smooth_shade(mesh, angle=math.radians(CREASE_DEGREES))
        mesh.visual = trimesh.visual.TextureVisuals(material=PBRMaterial(name=role))
        scene.add_geometry(mesh, geom_name=role, node_name=role)
        kept += len(mesh.faces)

    # Normals are opt-in in trimesh. Without them the crease split above is lost and three.js
    # has nothing to light the surface with.
    target.write_bytes(scene.export(file_type="glb", include_normals=True))
    print(f"{target.name:<34} {total:>7} -> {kept:>6} tris  {target.stat().st_size / 1024:>7.0f} KB  "
          f"roles: {', '.join(sorted(groups))}")


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2

    source_dir, out_dir = Path(argv[0]), Path(argv[1])
    out_dir.mkdir(parents=True, exist_ok=True)

    for spec in argv[2:]:
        names, _, rest = spec.partition("=")
        budget, _, role = rest.partition("@")
        out, _, names = names.rpartition(":")
        sources = []
        for name in names.split("+"):
            source = next(
                (p for p in (source_dir / f"{name}.{ext}" for ext in ("dae", "DAE", "stl", "STL")) if p.exists()),
                None)
            if source is None:
                print(f"missing: {name}")
                return 1
            sources.append(source)
        convert(sources, out_dir / f"{out or names.split('+')[0]}.glb", int(budget), role or None)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
