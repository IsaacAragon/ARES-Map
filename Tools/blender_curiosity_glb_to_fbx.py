"""
NASA Curiosity GLB (science.nasa.gov, NASA/JPL-Caltech) -> FBX for Unity with 6 separate wheels.

The GLB is one mesh. This script:
  1. separates the faces that use the 'wheels' material,
  2. splits them into loose parts and groups the parts into 6 wheels by position,
  3. puts each wheel's origin at its centre (so Unity can spin it on its axle),
  4. turns the rover so its front (arm and mast side) faces Blender -Y, which is Unity +Z,
  5. exports FBX with embedded textures and the axis conversion baked into the meshes.

Usage:
  blender -b --factory-startup --python Tools/blender_curiosity_glb_to_fbx.py -- --input Curiosity.glb --output Assets/Gale/Models/Curiosity.fbx
"""
import sys, os, argparse, math
import bpy, bmesh
from mathutils import Vector


def args():
    a = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--input", required=True)
    p.add_argument("--output", required=True)
    return p.parse_args(a)


def centroid(o):
    vs = [o.matrix_world @ v.co for v in o.data.vertices]
    return sum(vs, Vector()) / len(vs)


def bounds(objs):
    lo = Vector((1e9,) * 3); hi = Vector((-1e9,) * 3)
    for o in objs:
        for v in o.data.vertices:
            w = o.matrix_world @ v.co
            lo = Vector(map(min, lo, w)); hi = Vector(map(max, hi, w))
    return lo, hi


def main():
    a = args()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=a.input)
    body = [o for o in bpy.data.objects if o.type == "MESH"][0]
    for o in bpy.data.objects:
        o.select_set(o == body)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    body.name = "Curiosity_Body"

    # Front = side of the highest point (the mast head). Rotate so the front faces -Y.
    top = max(body.data.vertices, key=lambda v: v.co.z).co.copy()
    lo, hi = bounds([body])
    cy = (lo.y + hi.y) / 2
    if top.y > cy:
        body.rotation_euler = (0, 0, math.pi)
        bpy.ops.object.transform_apply(rotation=True)
        print("[cur] rotated 180 deg so the mast side faces -Y")
    # Centre X/Y on the origin, wheels' bottom at Z=0.
    lo, hi = bounds([body])
    body.location = (-(lo.x + hi.x) / 2, -(lo.y + hi.y) / 2, -lo.z)
    bpy.ops.object.transform_apply(location=True)

    # Separate the 'wheels' material.
    wi = [i for i, m in enumerate(body.data.materials) if m and m.name == "wheels"][0]
    bpy.ops.object.mode_set(mode="EDIT")
    bm = bmesh.from_edit_mesh(body.data)
    for f in bm.faces:
        f.select = f.material_index == wi
    bmesh.update_edit_mesh(body.data)
    bpy.ops.mesh.separate(type="SELECTED")
    bpy.ops.object.mode_set(mode="OBJECT")
    wheels_obj = [o for o in bpy.context.selected_objects if o != body][0]

    for o in bpy.data.objects:
        o.select_set(o == wheels_obj)
    bpy.context.view_layer.objects.active = wheels_obj
    bpy.ops.mesh.separate(type="LOOSE")
    parts = [o for o in bpy.context.selected_objects]
    print(f"[cur] wheel loose parts: {len(parts)}")

    # Group parts into 6 wheels: side by sign of X, row by Y (3 rows).
    cents = {p.name: centroid(p) for p in parts}
    ys = sorted(c.y for c in cents.values())
    y_lo, y_hi = ys[0], ys[-1]
    groups = {}
    for p in parts:
        c = cents[p.name]
        side = "R" if c.x < 0 else "L"   # front faces -Y, so -X is the rover's right
        t = (c.y - y_lo) / max(1e-6, (y_hi - y_lo))
        row = 0 if t < 1 / 3 else (1 if t < 2 / 3 else 2)   # 0 = front (-Y)
        groups.setdefault((side, row), []).append(p)
    if len(groups) != 6:
        raise SystemExit(f"Expected 6 wheel groups, got {sorted(groups)}")

    names = {0: "Front", 1: "Mid", 2: "Rear"}
    for (side, row), ps in sorted(groups.items()):
        for o in bpy.data.objects:
            o.select_set(o in ps)
        bpy.context.view_layer.objects.active = ps[0]
        if len(ps) > 1:
            bpy.ops.object.join()
        w = bpy.context.view_layer.objects.active
        w.name = f"Wheel_{side}_{names[row]}"
        w.data.name = w.name
        lo, hi = bounds([w])
        c = (lo + hi) / 2
        bpy.ops.object.origin_set(type="ORIGIN_GEOMETRY", center="BOUNDS")
        print(f"[cur] {w.name}: parts={len(ps)} centre=({c.x:.3f},{c.y:.3f},{c.z:.3f}) "
              f"size=({hi.x-lo.x:.3f},{hi.y-lo.y:.3f},{hi.z-lo.z:.3f}) tris={sum(len(p.vertices)-2 for p in w.data.polygons)}")

    meshes = [o for o in bpy.data.objects if o.type == "MESH"]
    lo, hi = bounds(meshes)
    print(f"[cur] final bounds min {tuple(round(x,3) for x in lo)} max {tuple(round(x,3) for x in hi)}")
    lo, hi = bounds([body])
    print(f"[cur] body bounds min {tuple(round(x,3) for x in lo)} max {tuple(round(x,3) for x in hi)}")
    print(f"[cur] total tris {sum(sum(len(p.vertices)-2 for p in o.data.polygons) for o in meshes)}")

    os.makedirs(os.path.dirname(os.path.abspath(a.output)), exist_ok=True)
    for o in bpy.data.objects:
        o.select_set(o.type == "MESH")
    bpy.ops.export_scene.fbx(filepath=a.output, use_selection=True, object_types={"MESH"},
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
                             axis_forward="-Z", axis_up="Y", bake_space_transform=True,
                             mesh_smooth_type="FACE", path_mode="STRIP", embed_textures=False,
                             add_leaf_bones=False, bake_anim=False)
    print(f"[cur] wrote {a.output} {os.path.getsize(a.output)/1e6:.2f} MB")

    # Textures as plain files (Unity does not extract FBX-embedded media by default).
    # Unity materials are built by the scene builder from these, by material name.
    tex_dir = os.path.join(os.path.dirname(os.path.abspath(a.output)), "Textures")
    os.makedirs(tex_dir, exist_ok=True)
    saved = set()
    for img in bpy.data.images:
        if not img.has_data and img.packed_file is None:
            continue
        base = img.name
        for ext in (".jpg", ".png"):
            if ext in base:
                base = base[:base.index(ext) + len(ext)]   # 'tex_03.png.001' -> 'tex_03.png'
        if base in saved:
            continue
        path = os.path.join(tex_dir, base)
        if img.packed_file:
            with open(path, "wb") as f:
                f.write(img.packed_file.data)
        else:
            img.save(filepath=path)
        saved.add(base)
        print(f"[cur] texture {base} {img.size[0]}x{img.size[1]}")


main()
