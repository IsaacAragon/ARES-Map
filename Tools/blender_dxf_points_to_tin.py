"""
DXF POINT cloud -> TIN terrain mesh (Delaunay 2.5D) -> FBX for Unity. Run inside Blender, headless.

Every DXF POINT becomes one mesh vertex (X, Y, Z untouched apart from --scale and the
horizontal recentre). Triangles come from a 2D Delaunay triangulation on X/Y, so no
height is interpolated or invented: the surface passes exactly through every point.

The DXF layers (MDT_P001..P008) are quadtree step levels of the same surface, not
separate engineering objects, so they are merged into one terrain mesh. The count per
layer is printed so the merge can be audited.

Usage:
  blender -b --factory-startup --python Tools/blender_dxf_points_to_tin.py -- \
      --input <file.dxf> --output Assets/Gale/Models/Gale_TIN.fbx --scale 1 --center-xy

Options:
  --input       DXF with POINT entities (required, opened read-only)
  --output      FBX to write (required)
  --scale       Source units -> meters. Default 1.
  --center-xy   Subtract the X/Y centre of the bounds (Z keeps the source datum).
  --origin X Y  Subtract this X/Y instead (e.g. the Gale TIN offset, so a second block lines up).
  --name        Object name. Default Gale_TIN.
"""
import sys, os, math, argparse, json
from collections import Counter

import bpy
from mathutils import Vector
from mathutils.geometry import delaunay_2d_cdt


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--input", required=True)
    p.add_argument("--output", required=True)
    p.add_argument("--scale", type=float, default=1.0)
    p.add_argument("--center-xy", action="store_true")
    p.add_argument("--origin", type=float, nargs=2, metavar=("X", "Y"),
                   help="Subtract this X/Y instead of the bounds centre (to share another TIN's origin).")
    p.add_argument("--name", default="Gale_TIN")
    p.add_argument("--halo", nargs="+", metavar="DXF",
                   help="Point DXFs of the neighbouring blocks. Their points within --halo-dist of this block are used "
                        "ONLY to compute vertex normals (exported as custom normals), so the shading of a shared edge "
                        "is the same on both blocks. They do not become vertices.")
    p.add_argument("--halo-dist", type=float, default=3000.0)
    p.add_argument("--hole", type=float, nargs=4, action="append", metavar=("X0", "X1", "Y0", "Y1"),
                   help="Rectangle (source units) of an inner block: faces whose centroid falls inside are removed, "
                        "and halo points inside it are used for the normals. Repeatable.")
    return p.parse_args(argv)


def halo_normals(pts, halo_files, dist, cx, cy, scale, holes=()):
    """Area-weighted vertex normals of `pts`, computed on a triangulation of pts + nearby neighbour points."""
    xmin = min(p[0] for p in pts); xmax = max(p[0] for p in pts)
    ymin = min(p[1] for p in pts); ymax = max(p[1] for p in pts)
    eps = 1.0
    extra = []
    for f in halo_files:
        hp, _, _ = read_points(f)
        for x, y, z in hp:
            outside = x < xmin - eps or x > xmax + eps or y < ymin - eps or y > ymax + eps
            near = xmin - dist <= x <= xmax + dist and ymin - dist <= y <= ymax + dist
            in_hole = any(h[0] + eps < x < h[1] - eps and h[2] + eps < y < h[3] - eps for h in holes)
            if (outside and near) or in_hole:
                extra.append((x, y, z))
    allp = list(pts) + extra
    v2 = [Vector(((x - cx) * scale, (y - cy) * scale)) for x, y, _ in allp]
    ov, _e, of, orig, _oe, _of = delaunay_2d_cdt(v2, [], [], 0, 1e-6)
    src = [o[0] for o in orig]
    acc = [[0.0, 0.0, 0.0] for _ in pts]
    n = len(pts)
    for f in of:
        ids = [src[k] for k in f]
        if not any(i < n for i in ids):
            continue
        p0, p1, p2 = (allp[i] for i in ids[:3])
        ax, ay, az = (p1[0] - p0[0]) * scale, (p1[1] - p0[1]) * scale, (p1[2] - p0[2]) * scale
        bx, by, bz = (p2[0] - p0[0]) * scale, (p2[1] - p0[1]) * scale, (p2[2] - p0[2]) * scale
        nx, ny, nz = ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx   # |n| = 2 * area
        if nz < 0:
            nx, ny, nz = -nx, -ny, -nz
        for i in ids:
            if i < n:
                acc[i][0] += nx; acc[i][1] += ny; acc[i][2] += nz
    out = []
    for x, y, z in acc:
        l = math.sqrt(x * x + y * y + z * z) or 1.0
        out.append((x / l, y / l, z / l))
    print(f"[tin] halo: {len(extra)} neighbour points used for normals")
    return out


def read_points(path):
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        lines = [ln.strip() for ln in f]
    pts, layers, other = [], Counter(), Counter()
    i, n = 0, len(lines) - 1
    in_ent = False
    cur = None
    def flush(c):
        if c is None:
            return
        if c["kind"] == "POINT":
            pts.append((c.get(10, 0.0), c.get(20, 0.0), c.get(30, 0.0)))
            layers[c.get(8, "0")] += 1
        else:
            other[c["kind"]] += 1
    while i < n:
        code, val = lines[i], lines[i + 1]
        i += 2
        try:
            code = int(code)
        except ValueError:
            continue
        if code == 0 and val == "SECTION":
            in_ent = False
            continue
        if code == 2 and val == "ENTITIES":
            in_ent = True
            continue
        if not in_ent:
            continue
        if code == 0:
            flush(cur)
            cur = None if val == "ENDSEC" else {"kind": val}
            if val == "ENDSEC":
                in_ent = False
        elif cur is not None:
            if code == 8:
                cur[8] = val
            elif code in (10, 20, 30):
                cur[code] = float(val)
    flush(cur)
    return pts, layers, other


def main():
    a = parse_args()
    if not os.path.isfile(a.input):
        raise SystemExit(f"Input not found: {a.input}")
    bpy.ops.wm.read_factory_settings(use_empty=True)

    pts, layers, other = read_points(a.input)
    print(f"[tin] POINT read: {len(pts)}  per layer: {dict(layers)}  other entities: {dict(other)}")
    if len(pts) < 3:
        raise SystemExit("Not enough points.")

    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]; zs = [p[2] for p in pts]
    xmin, xmax, ymin, ymax = min(xs), max(xs), min(ys), max(ys)
    cx = (xmin + xmax) / 2 if a.center_xy else 0.0
    cy = (ymin + ymax) / 2 if a.center_xy else 0.0
    if a.origin:
        cx, cy = a.origin

    # Triangulate in local coordinates (better float precision than 8e6-scale numbers).
    v2 = [Vector(((x - cx) * a.scale, (y - cy) * a.scale)) for x, y, _ in pts]
    out_verts, _edges, out_faces, orig_verts, _oe, _of = delaunay_2d_cdt(v2, [], [], 0, 1e-6)

    zmap = []
    for k, orig in enumerate(orig_verts):
        if not orig:
            raise SystemExit(f"Output vertex {k} has no source point (CDT added a vertex).")
        zmap.append(pts[orig[0]][2] * a.scale)
    merged = sum(len(o) - 1 for o in orig_verts)
    verts3 = [(v.x, v.y, z) for v, z in zip(out_verts, zmap)]
    if a.hole:
        # Huecos para bloques interiores: fuera las caras con el centroide dentro del rectángulo.
        holes = [((x0 - cx) * a.scale, (x1 - cx) * a.scale, (y0 - cy) * a.scale, (y1 - cy) * a.scale) for x0, x1, y0, y1 in a.hole]
        before = len(out_faces)
        kept = []
        for f in out_faces:
            mx = sum(out_verts[i].x for i in f) / len(f); my = sum(out_verts[i].y for i in f) / len(f)
            if not any(h[0] < mx < h[1] and h[2] < my < h[3] for h in holes):
                kept.append(f)
        out_faces = kept
        print(f"[tin] holes: removed {before - len(out_faces)} faces inside {len(holes)} rectangle(s)")
    print(f"[tin] vertices out: {len(verts3)}  (duplicates merged: {merged})  triangles: {len(out_faces)}")

    mesh = bpy.data.meshes.new(a.name)
    mesh.from_pydata(verts3, [], [list(f) for f in out_faces])
    mesh.validate()
    mesh.update()

    # Make every face point up (+Z) so the outside of the terrain faces the sky.
    flipped = 0
    for poly in mesh.polygons:
        if poly.normal.z < 0:
            poly.flip(); flipped += 1
    mesh.update()
    down = sum(1 for p in mesh.polygons if p.normal.z < 0)
    print(f"[tin] normals: flipped {flipped} faces to +Z; faces still facing down: {down}")

    # Planar UVs over the bounds (0..1), used for a tiling detail texture in Unity.
    w = (xmax - xmin) * a.scale; d = (ymax - ymin) * a.scale
    x0 = (xmin - cx) * a.scale; y0 = (ymin - cy) * a.scale
    uv = mesh.uv_layers.new(name="UVMap")
    for loop in mesh.loops:
        co = mesh.vertices[loop.vertex_index].co
        uv.data[loop.index].uv = ((co.x - x0) / w, (co.y - y0) / d)
    for p in mesh.polygons:
        p.use_smooth = True

    if a.halo:
        hn = halo_normals(pts, a.halo, a.halo_dist, cx, cy, a.scale, a.hole or ())
        # output vertex k comes from source point orig_verts[k][0]
        mesh.normals_split_custom_set_from_vertices([hn[orig_verts[k][0]] for k in range(len(verts3))])
        mesh.update()

    obj = bpy.data.objects.new(a.name, mesh)
    bpy.context.collection.objects.link(obj)

    # Marker: highest vertex, to check orientation after the Unity import.
    zi = max(range(len(verts3)), key=lambda k: verts3[k][2])
    print(f"[tin] highest vertex (local, Blender XYZ): {tuple(round(c, 3) for c in verts3[zi])}")
    lo = [min(v[i] for v in verts3) for i in range(3)]
    hi = [max(v[i] for v in verts3) for i in range(3)]
    print(f"[tin] bounds min {tuple(round(c,3) for c in lo)} max {tuple(round(c,3) for c in hi)}")
    print(f"[tin] removed XY offset: ({cx:.6f}, {cy:.6f}) source units")

    os.makedirs(os.path.dirname(os.path.abspath(a.output)), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.ops.export_scene.fbx(filepath=a.output, use_selection=True, object_types={"MESH"},
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
                             axis_forward="-Z", axis_up="Y", mesh_smooth_type="FACE",
                             use_mesh_modifiers=False, add_leaf_bones=False, bake_anim=False)
    size = os.path.getsize(a.output)
    print(f"[tin] wrote {a.output}  {size/1e6:.2f} MB")

    report = {
        "input": a.input, "points": len(pts), "layers": dict(layers), "other_entities": dict(other),
        "vertices": len(verts3), "duplicates_merged": merged, "triangles": len(out_faces),
        "flipped": flipped, "removed_xy_offset": [cx, cy], "scale": a.scale,
        "bounds_min": lo, "bounds_max": hi, "highest_vertex": verts3[zi], "fbx_bytes": size,
    }
    with open(os.path.splitext(a.output)[0] + "_conversion.json", "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)


main()
