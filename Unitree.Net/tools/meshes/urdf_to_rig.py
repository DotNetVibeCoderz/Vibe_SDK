"""Prints the RobotRig rows for a humanoid URDF, ready to paste into RobotRig.cs.

usage:
    python urdf_to_rig.py <urdf> <name map>

    <name map> is a Python file defining NAMES = {urdf_link: rig_link} and, optionally,
    MESHES = {urdf_link: glb_name} for links whose mesh was merged or renamed on conversion.
    Links not in NAMES are skipped, along with everything below them.

Why a generator: a humanoid URDF is a hundred-odd numbers, and every one copied by hand is a chance
to put a knee a centimetre off. The output is the starting point, not the end - read it, then pin
it with a forward-kinematics test against the URDF (see HumanoidForwardKinematicsMatchTheUrdf). The rig
names are fixed by the gait, which finds its joints by name: <side>_hip_pitch, <side>_calf (knee),
<side>_ankle (ankle pitch), <side>_shoulder_pitch, <side>_forearm (elbow), and <side>_foot (contact).
"""
import runpy
import sys
import xml.etree.ElementTree as ET


def floats(s):
    return [float(x) for x in (s or "0 0 0").split()]


def num(v):
    if abs(v) < 1e-12:
        return "0f"
    return f"{v:.6g}f"


def vec(v):
    if all(abs(x) < 1e-12 for x in v):
        return "Vector3.Zero"
    for i, unit in enumerate(("UnitX", "UnitY", "UnitZ")):
        if abs(v[i] - 1) < 1e-9 and all(abs(v[j]) < 1e-12 for j in range(3) if j != i):
            return f"Vector3.{unit}"
    return f"new Vector3({num(v[0])}, {num(v[1])}, {num(v[2])})"


def main(urdf, name_map):
    spec = runpy.run_path(name_map)
    names, meshes = spec["NAMES"], spec.get("MESHES", {})
    root = ET.parse(urdf).getroot()

    visuals = {}
    for link in root.findall("link"):
        v = link.find("visual")
        if v is None or v.find("geometry/mesh") is None:
            continue
        o = v.find("origin")
        stem = v.find("geometry/mesh").get("filename").replace("\\", "/").split("/")[-1].rsplit(".", 1)[0]
        visuals[link.get("name")] = (stem, floats(o.get("xyz") if o is not None else None),
                                     floats(o.get("rpy") if o is not None else None))

    joints = []
    for j in root.findall("joint"):
        o, a = j.find("origin"), j.find("axis")
        joints.append(dict(
            child=j.find("child").get("link"), parent=j.find("parent").get("link"),
            xyz=floats(o.get("xyz") if o is not None else None),
            rpy=floats(o.get("rpy") if o is not None else None),
            axis=floats(a.get("xyz")) if a is not None and j.get("type") != "fixed" else [0, 0, 0]))

    def continuation(link):
        """The first moving joint below a link: where its fallback bone points."""
        for j in joints:
            if j["parent"] == link and any(j["axis"]) and j["child"] in names:
                return j["xyz"]
        return None

    rootname = ({l.get("name") for l in root.findall("link")} - {j["child"] for j in joints}).pop()
    mesh = meshes.get(rootname, visuals.get(rootname, (None,))[0])
    print(f'rig.Add("{names[rootname]}", null, Vector3.Zero, Vector3.Zero, "{mesh}", []);')

    moving = 0
    for j in joints:
        if j["child"] not in names or j["parent"] not in names:
            continue
        child = j["child"]
        visual = visuals.get(child)
        mesh = meshes.get(child, visual[0] if visual else None)
        bone = continuation(child)
        shapes = f"[UrdfRig.Bone({vec(bone)})]" if bone and any(abs(x) > 0.02 for x in bone) else "[]"
        args = [f'"{names[child]}"', f'"{names[j["parent"]]}"', vec(j["xyz"]), vec(j["axis"]),
                f'"{mesh}"' if mesh else "null", shapes]
        extras = []
        if any(abs(x) > 1e-12 for x in j["rpy"]):
            extras.append(f"rotation: {vec(j['rpy'])}")
        if visual and any(abs(x) > 1e-12 for x in visual[1]):
            extras.append(f"meshOffset: {vec(visual[1])}")
        if visual and any(abs(x) > 1e-12 for x in visual[2]):
            extras.append(f"meshRotation: {vec(visual[2])}")
        if any(j["axis"]):
            moving += 1
        print(f"rig.Add({', '.join(args + extras)});")
    print(f"// {moving} moving joints")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main(sys.argv[1], sys.argv[2])
