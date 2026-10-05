"""Shared modelling helpers for the robot rigs.

Everything here is written for `blender --background --python`, so it never touches the UI and
never relies on selection state: operators that work on "the active object" behave differently
depending on what ran before them, which is exactly the kind of order dependence a build script
cannot afford.

Units are metres, matching the SDK. Blender is Z-up and glTF is Y-up; the exporter converts, so
models are authored Z-up here and arrive Y-up in three.js without anything rotating a parent to
compensate.
"""

import math

import bmesh
import bpy
from mathutils import Matrix, Vector


# --------------------------------------------------------------------------- scene


def reset_scene():
    """Empties the file. Factory startup still ships a cube, a camera and a light."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.unit_settings.system = "METRIC"
    bpy.context.scene.unit_settings.length_unit = "METERS"


def link(obj):
    bpy.context.scene.collection.objects.link(obj)
    return obj


# --------------------------------------------------------------------------- materials

_materials: dict[str, bpy.types.Material] = {}


def material(name, color, roughness=0.45, metallic=0.0):
    """A named PBR material, created once and shared.

    The name matters as much as the colour: the viewport looks materials up by name to re-tint a
    rig when the theme changes, so renaming one here silently stops it following the theme.
    """
    if name in _materials:
        return _materials[name]

    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic

    _materials[name] = mat
    return mat


def forget_materials():
    _materials.clear()


# --------------------------------------------------------------------------- naming


def node_name(name):
    """Turns an SDK joint name into one a glTF loader will preserve.

    three.js strips `. : / [ ]` out of every node name as it loads, so an empty called
    `left.hip_yaw` arrives as `lefthip_yaw` and a lookup by the name the catalogue uses finds
    nothing. Dots become underscores here and the viewport applies the same substitution, so both
    sides can go on writing the joint names the SDK actually uses.
    """
    return name.replace(".", "_")


# --------------------------------------------------------------------------- primitives


def _finish(bm, name, mat, location=(0, 0, 0), rotation=(0, 0, 0), smooth=True):
    name = node_name(name)
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()

    if smooth:
        for polygon in mesh.polygons:
            polygon.use_smooth = True

    mesh.materials.append(mat)

    obj = bpy.data.objects.new(name, mesh)
    obj.location = location
    obj.rotation_euler = rotation
    return link(obj)


def rounded_box(name, size, radius, mat, segments=4, location=(0, 0, 0), rotation=(0, 0, 0)):
    """A box with bevelled edges.

    Both robot heads and most of the duck's shells are this shape. A plain cube reads as a prop; the
    corner radius is most of what makes these designs look like the product rather than like a
    programmer's placeholder.
    """
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=Vector(size), verts=bm.verts)

    bmesh.ops.bevel(
        bm,
        geom=[*bm.verts, *bm.edges, *bm.faces],
        offset=radius,
        offset_type="OFFSET",
        segments=segments,
        profile=0.5,
        affect="EDGES",
        clamp_overlap=True,
    )

    return _finish(bm, name, mat, location, rotation)


def lathe(name, profile, mat, segments=48, location=(0, 0, 0), rotation=(0, 0, 0), cap=True):
    """Spins a 2D profile around the Z axis.

    `profile` is a list of (radius, z) going bottom to top. This is how the Mini's body is built:
    its silhouette is a single curve in the photographs - a bell that swells below the middle and
    tucks back in at the base - and no stack of cylinders reproduces that.
    """
    bm = bmesh.new()

    verts = [bm.verts.new((r, 0.0, z)) for r, z in profile]
    edges = [bm.edges.new((verts[i], verts[i + 1])) for i in range(len(verts) - 1)]

    bmesh.ops.spin(
        bm,
        geom=[*verts, *edges],
        axis=(0, 0, 1),
        steps=segments,
        angle=2 * math.pi,
        dvec=(0, 0, 0),
        cent=(0, 0, 0),
        use_merge=True,
    )

    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)

    if cap:
        bmesh.ops.holes_fill(bm, edges=bm.edges)

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return _finish(bm, name, mat, location, rotation)


def cylinder(name, radius, depth, mat, segments=32, location=(0, 0, 0), rotation=(0, 0, 0), radius_top=None):
    bm = bmesh.new()
    bmesh.ops.create_cone(
        bm,
        cap_ends=True,
        cap_tris=False,
        segments=segments,
        radius1=radius,
        radius2=radius if radius_top is None else radius_top,
        depth=depth,
    )
    return _finish(bm, name, mat, location, rotation)


def span_cylinder(name, start, end, radius, mat, segments=12):
    """A cylinder spanning two points.

    The geometry is one unit tall and the length lives in the node's scale, not in the mesh. That
    matters for the Stewart struts: the viewport re-aims them every frame by writing position,
    quaternion and scale.y, which only works if the mesh itself is a unit cylinder. Baking the
    length into the vertices instead would need the viewport to know each strut's rest length to
    divide it back out.
    """
    start = Vector(start)
    end = Vector(end)
    span = end - start
    rotation = Vector((0, 0, 1)).rotation_difference(span.normalized()).to_euler()

    obj = cylinder(
        name, radius, 1.0, mat, segments=segments,
        location=tuple(start + span / 2), rotation=tuple(rotation),
    )
    obj.scale = (1.0, 1.0, span.length)
    return obj


def sphere(name, radius, mat, segments=32, location=(0, 0, 0), scale=(1, 1, 1)):
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=segments, v_segments=segments // 2, radius=radius)
    obj = _finish(bm, name, mat, location)
    obj.scale = scale
    return obj


def tube(name, points, radius, mat, segments=10):
    """A tube through a polyline, built from one capped cylinder per span.

    Cables and antenna wire only. Welding the spans leaves the joins slightly faceted, which never
    shows at the distance these are seen from and avoids the fragility of sweeping a profile along
    an arbitrary path.
    """
    bm = bmesh.new()

    for a, b in zip(points, points[1:]):
        a = Vector(a)
        b = Vector(b)
        span = b - a
        length = span.length

        if length < 1e-6:
            continue

        segment = bmesh.new()
        bmesh.ops.create_cone(
            segment,
            cap_ends=True,
            cap_tris=False,
            segments=segments,
            radius1=radius,
            radius2=radius,
            depth=length,
        )

        rotation = Vector((0, 0, 1)).rotation_difference(span.normalized()).to_matrix().to_4x4()
        bmesh.ops.transform(segment, matrix=Matrix.Translation(a + span / 2) @ rotation, verts=segment.verts)

        mesh = bpy.data.meshes.new("_tmp")
        segment.to_mesh(mesh)
        segment.free()
        bm.from_mesh(mesh)
        bpy.data.meshes.remove(mesh)

    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
    return _finish(bm, name, mat, location=(0, 0, 0))


def helix(name, start, end, radius, coils, wire_radius, mat, lead_in=0.0, lead_out=0.0):
    """A coiled wire between two points.

    The Mini's antennas are a straight wire with a spring wound into the middle of it, and that
    spring is the single most recognisable thing about the robot's silhouette.
    """
    start = Vector(start)
    end = Vector(end)
    span = end - start
    length = span.length
    direction = span.normalized()

    basis = Vector((0, 0, 1)).rotation_difference(direction).to_matrix()

    points = []

    if lead_in > 0:
        points.append(start)

    coil_start = lead_in
    coil_end = length - lead_out
    steps = max(8, int(coils * 16))

    for i in range(steps + 1):
        t = i / steps
        z = coil_start + (coil_end - coil_start) * t
        angle = 2 * math.pi * coils * t
        local = Vector((radius * math.cos(angle), radius * math.sin(angle), z))
        points.append(start + basis @ local)

    if lead_out > 0:
        points.append(end)

    return tube(name, points, wire_radius, mat, segments=6)


# --------------------------------------------------------------------------- rig


def joint(name, location=(0, 0, 0), parent=None):
    """An empty used as a joint pivot. `location` is relative to `parent`.

    glTF exports empties as plain nodes, which is exactly what the viewport wants: it looks a joint
    up by name and sets its rotation, and the meshes parented underneath follow. Driving meshes
    directly instead means every visual tweak risks moving a rotation centre.
    """
    obj = bpy.data.objects.new(node_name(name), None)
    obj.empty_display_type = "PLAIN_AXES"
    obj.empty_display_size = 0.01
    obj.location = location
    link(obj)

    if parent is not None:
        attach(obj, parent)

    return obj


def attach(child, parent):
    """Parents, treating the child's existing location as parent-local.

    Deliberately not the keep-world-position form. That one needs `parent.matrix_world`, which in a
    background build is still the identity until the depsgraph runs - so it silently does nothing,
    and every part lands wherever its local offset happened to put it. Authoring in parent-local
    coordinates throughout means what the script says is what gets built.
    """
    child.parent = parent
    return child


def attach_all(parent, *children):
    for child in children:
        attach(child, parent)
    return parent


# --------------------------------------------------------------------------- export


def export_glb(path, name):
    """Writes the whole scene to a .glb.

    Y-up conversion is the exporter's default and is left on: three.js is Y-up, so turning it off
    would mean rotating the loaded root by -90 degrees about X, and every joint axis in the rig
    would then need the same correction applied in the opposite direction.
    """
    bpy.ops.object.select_all(action="DESELECT")

    bpy.ops.export_scene.gltf(
        filepath=str(path),
        export_format="GLB",
        export_yup=True,
        export_apply=True,
        export_materials="EXPORT",
        export_cameras=False,
        export_lights=False,
        export_extras=False,
        export_skins=False,
        export_animations=False,
        use_selection=False,
    )

    size = path.stat().st_size
    print(f"[{name}] wrote {path.name} ({size / 1024:.0f} KB)")
