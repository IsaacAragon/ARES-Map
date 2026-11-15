"""
Puntos de un BLOQUE del mosaico de terreno alrededor de Gale, listos para
Tools/blender_dxf_points_to_tin.py --origin (mismo origen XY que Gale_TIN).

El mosaico (Tools/mosaic_blocks.json) es un conjunto de rectángulos en Equirectangular Marte
(R = 3 396 190 m) que comparten bordes. Cada bloque tiene:
  - base:     "gale"  -> puntos del DXF MOLA de Gale (quadtree, cotas tal cual)
              "mola"  -> centros de píxel del GeoTIFF global MOLA 463 m
              "points:<dxf>" -> un DXF de puntos ya preparado (p. ej. la franja norte)
  - sectors:  DXF de Civil 3D (malla regular de 200 m, sistema local) que se superponen a la
              base, georreferenciados con Assets/Gale/Models/Sectores_georef.json.

Superposición de un sector s en el bloque B:
    z = (1 - w) * z_base + w * z_s(x, y)        w = smoothstep(d / blend)
  d = distancia al lado más cercano del marco del sector (los lados que caen en el borde
  exterior del mosaico no cuentan: ahí no hay nada con qué empalmar) y a los bordes de B cuyo
  vecino no lleva ese sector (así el borde compartido queda con la cota del vecino).

Bordes compartidos: los dos bloques reciben EXACTAMENTE los mismos puntos de borde.
  - En el contorno de Gale: los puntos del DXF de Gale en ese borde (cada 463 m).
  - En el resto: los centros de píxel MOLA de esa línea (cada 463 m), más las esquinas.
  Donde algún sector pesa (w > 0) se añade un punto intermedio (cada ~231 m), con la cota
  base interpolada a lo largo del borde, para no perder el detalle del sector en la unión.
Los bordes de los bloques caen en líneas de centros de píxel MOLA, así que los rectángulos
encajan sin rendijas.

Uso (venv de TIFFdetallado: numpy, ezdxf, rasterio):
  python Tools/build_mosaic_block.py --config Tools/mosaic_blocks.json --src-dir <carpeta DXF/TIF>
         --block GaleSur --out <puntos.dxf>
"""
import argparse, json, os, math
import numpy as np
import ezdxf
import rasterio
from rasterio.windows import from_bounds

R = 3396190.0
EPS = 1.0  # m: un punto a menos de esto de un borde se considera "en el borde"


def dxf_points(path, layer=None):
    doc = ezdxf.readfile(path)
    return np.array([tuple(e.dxf.location) for e in doc.modelspace().query("POINT")
                     if layer is None or e.dxf.layer == layer], dtype=np.float64)


def smoothstep(t):
    t = np.clip(t, 0.0, 1.0)
    return t * t * (3 - 2 * t)


class Sector:
    def __init__(self, sid, path, geo, step, layer):
        p = dxf_points(path, layer)
        xs = np.unique(p[:, 0]); ys = np.unique(p[:, 1])
        if len(xs) * len(ys) != len(p):
            raise SystemExit(f"sector {sid}: la malla no es regular ({len(xs)}x{len(ys)} != {len(p)})")
        o = np.lexsort((p[:, 0], p[:, 1]))
        self.grid = p[o, 2].reshape(len(ys), len(xs)) - geo["sectors"][sid]["dz"]
        self.xl, self.yl = xs, ys
        self.dxl = (xs[-1] - xs[0]) / (len(xs) - 1); self.dyl = (ys[-1] - ys[0]) / (len(ys) - 1)
        self.X0, self.Y0, self.sx = geo["X0"], geo["Y0"], geo["x_scale"]
        self.id, self.step = sid, step
        # marco en equirect (rectángulo: la transformación solo escala X)
        self.box = (self.X0 + xs[0] * self.sx, self.X0 + xs[-1] * self.sx, self.Y0 + ys[0], self.Y0 + ys[-1])

    def sample(self, x, y):
        c = ((x - self.X0) / self.sx - self.xl[0]) / self.dxl
        r = (y - self.Y0 - self.yl[0]) / self.dyl
        c0 = np.clip(np.floor(c).astype(int), 0, self.grid.shape[1] - 2)
        r0 = np.clip(np.floor(r).astype(int), 0, self.grid.shape[0] - 2)
        fc = np.clip(c - c0, 0, 1); fr = np.clip(r - r0, 0, 1)
        g = self.grid
        return (g[r0, c0] * (1 - fc) * (1 - fr) + g[r0, c0 + 1] * fc * (1 - fr)
                + g[r0 + 1, c0] * (1 - fc) * fr + g[r0 + 1, c0 + 1] * fc * fr)

    def points(self):
        """Puntos de la malla (diezmada cada `step`, alineada al índice global)."""
        gy, gx = np.mgrid[0:self.grid.shape[0]:self.step, 0:self.grid.shape[1]:self.step]
        x = self.X0 + self.xl[gx.ravel()] * self.sx
        y = self.Y0 + self.yl[gy.ravel()]
        return x, y


class Mosaic:
    def __init__(self, cfg, src, only_sectors):
        self.cfg = cfg
        self.src = src
        self.blocks = cfg["blocks"]
        geo = json.load(open(cfg["georef"]))
        self.sectors = {}
        for sid in only_sectors:
            s = cfg["sectors"][sid]
            self.sectors[sid] = Sector(sid, os.path.join(src, s["dxf"]), geo, s.get("step", 1), cfg.get("layer", "COTAS_DEM_200M"))
        self.blend = cfg.get("blend_m", 2000.0)
        rects = np.array([b["rect"] for b in self.blocks.values()])
        self.extent = (rects[:, 0].min(), rects[:, 1].max(), rects[:, 2].min(), rects[:, 3].max())
        self.gale = dxf_points(os.path.join(src, cfg["gale_dxf"]))
        self.grect = self.blocks["Gale"]["rect"]
        with rasterio.open(os.path.join(src, cfg["mola"])) as ds:
            self.mola_path = ds.name
            self.px = ds.transform.a
            self.mt = ds.transform

    # ---- MOLA
    def load_mola(self, x0, x1, y0, y1):
        with rasterio.open(self.mola_path) as ds:
            win = from_bounds(x0 - 3 * self.px, y0 - 3 * self.px, x1 + 3 * self.px, y1 + 3 * self.px, ds.transform)
            win = win.round_offsets().round_lengths()
            self.z = ds.read(1, window=win).astype(np.float64)
            self.t = ds.window_transform(win)
            if ds.nodata is not None and (self.z == ds.nodata).any():
                raise SystemExit("nodata MOLA dentro del bloque")

    def mola(self, x, y):
        t, z = self.t, self.z
        c = (x - t.c) / t.a - 0.5; r = (y - t.f) / t.e - 0.5
        c0 = np.clip(np.floor(c).astype(int), 0, z.shape[1] - 2); r0 = np.clip(np.floor(r).astype(int), 0, z.shape[0] - 2)
        fc = np.clip(c - c0, 0, 1); fr = np.clip(r - r0, 0, 1)
        return (z[r0, c0] * (1 - fc) * (1 - fr) + z[r0, c0 + 1] * fc * (1 - fr)
                + z[r0 + 1, c0] * (1 - fc) * fr + z[r0 + 1, c0 + 1] * fc * fr)

    def mola_centers(self, x0, x1, y0, y1):
        t = self.t
        cx = t.c + (np.arange(self.z.shape[1]) + 0.5) * t.a
        cy = t.f + (np.arange(self.z.shape[0]) + 0.5) * t.e
        X, Y = np.meshgrid(cx, cy)
        m = (X > x0 + EPS) & (X < x1 - EPS) & (Y > y0 + EPS) & (Y < y1 - EPS)
        return X[m], Y[m], self.z[m]

    # ---- vecinos y pesos
    def neighbours(self, name):
        """Para cada borde del bloque: lista de (vecino, a, b) con el tramo compartido."""
        x0, x1, y0, y1 = self.blocks[name]["rect"]
        out = {"W": [], "E": [], "S": [], "N": []}
        for on, ob in self.blocks.items():
            if on == name:
                continue
            u0, u1, v0, v1 = ob["rect"]
            if abs(u1 - x0) < EPS and min(y1, v1) - max(y0, v0) > EPS: out["W"].append((on, max(y0, v0), min(y1, v1)))
            if abs(u0 - x1) < EPS and min(y1, v1) - max(y0, v0) > EPS: out["E"].append((on, max(y0, v0), min(y1, v1)))
            if abs(v1 - y0) < EPS and min(x1, u1) - max(x0, u0) > EPS: out["S"].append((on, max(x0, u0), min(x1, u1)))
            if abs(v0 - y1) < EPS and min(x1, u1) - max(x0, u0) > EPS: out["N"].append((on, max(x0, u0), min(x1, u1)))
        return out

    def weight(self, s, name, x, y, extra_blocks=()):
        """Peso del sector s en (x, y) para el bloque `name` (y, en bordes, también sus vecinos)."""
        bx0, bx1, by0, by1 = s.box
        ex0, ex1, ey0, ey1 = self.extent
        d = np.full(x.shape, np.inf)
        inside = (x >= bx0 - EPS) & (x <= bx1 + EPS) & (y >= by0 - EPS) & (y <= by1 + EPS)
        if bx0 > ex0 + EPS: d = np.minimum(d, x - bx0)
        if bx1 < ex1 - EPS: d = np.minimum(d, bx1 - x)
        if by0 > ey0 + EPS: d = np.minimum(d, y - by0)
        if by1 < ey1 - EPS: d = np.minimum(d, by1 - y)
        for bn in (name,) + tuple(extra_blocks):
            r = self.blocks[bn]["rect"]
            nb = self.neighbours(bn)
            for edge, lst in nb.items():
                for on, a, b in lst:
                    if s.id in self.blocks[on].get("sectors", []):
                        continue
                    if edge == "W": dd = x - r[0]; along = y
                    elif edge == "E": dd = r[1] - x; along = y
                    elif edge == "S": dd = y - r[2]; along = x
                    else: dd = r[3] - y; along = x
                    # distancia al tramo (no a la recta entera)
                    gap = np.maximum(0, np.maximum(a - along, along - b))
                    d = np.minimum(d, np.hypot(np.maximum(dd, 0), gap))
        w = smoothstep(d / self.blend)
        return np.where(inside, w, 0.0)

    def overlay(self, name, x, y, z, extra=()):
        z = z.copy()
        wmax = np.zeros_like(z)
        for sid in self.blocks[name].get("sectors", []):
            if extra and not all(sid in self.blocks[e].get("sectors", []) for e in extra):
                continue
            s = self.sectors[sid]
            w = self.weight(s, name, x, y, extra)
            m = w > 0
            if m.any():
                z[m] = (1 - w[m]) * z[m] + w[m] * s.sample(x[m], y[m])
            wmax = np.maximum(wmax, w)
        return z, wmax

    # ---- bordes
    def seam(self, name, edge, a, b, neighbour):
        """Puntos (x, y, z) del tramo [a, b] del borde `edge` del bloque `name`."""
        x0, x1, y0, y1 = self.blocks[name]["rect"]
        horiz = edge in "SN"
        line = {"W": x0, "E": x1, "S": y0, "N": y1}[edge]
        gx0, gx1, gy0, gy1 = self.grect
        on_gale = ((horiz and (abs(line - gy0) < EPS or abs(line - gy1) < EPS) and a >= gx0 - EPS and b <= gx1 + EPS) or
                   (not horiz and (abs(line - gx0) < EPS or abs(line - gx1) < EPS) and a >= gy0 - EPS and b <= gy1 + EPS))
        if on_gale:
            g = self.gale
            m = np.abs((g[:, 1] if horiz else g[:, 0]) - line) < 1e-3
            e = g[m]
            al = e[:, 0] if horiz else e[:, 1]
            k = (al >= a - EPS) & (al <= b + EPS)
            al, zb = al[k], e[k, 2]
            o = np.argsort(al); al, zb = al[o], zb[o]
        else:
            ref = self.mt.c if horiz else self.mt.f
            step = self.px if horiz else -self.mt.e
            i0 = math.ceil((a - ref) / step - 0.5 - 1e-9); i1 = math.floor((b - ref) / step - 0.5 + 1e-9)
            al = ref + (np.arange(i0, i1 + 1) + 0.5) * step
            al = al[(al > a + EPS) & (al < b - EPS)]
            al = np.concatenate([[a], al, [b]])
            xx, yy = (al, np.full_like(al, line)) if horiz else (np.full_like(al, line), al)
            zb = self.mola(xx, yy)
        xx, yy = (al, np.full_like(al, line)) if horiz else (np.full_like(al, line), al)
        extra = (neighbour,) if neighbour else ()
        z, w = self.overlay(name, xx, yy, zb, extra)
        # Densificar (punto medio) donde pesa algún sector
        mid = (al[:-1] + al[1:]) / 2
        mx, my = (mid, np.full_like(mid, line)) if horiz else (np.full_like(mid, line), mid)
        zm_b = (zb[:-1] + zb[1:]) / 2
        zm, wm = self.overlay(name, mx, my, zm_b, extra)
        keep = wm > 0
        X = np.concatenate([xx, mx[keep]]); Y = np.concatenate([yy, my[keep]]); Z = np.concatenate([z, zm[keep]])
        return np.column_stack([X, Y, Z]), bool(on_gale)

    def edge_segments(self, name):
        x0, x1, y0, y1 = self.blocks[name]["rect"]
        nb = self.neighbours(name)
        segs = []
        for edge, (a0, b0) in (("W", (y0, y1)), ("E", (y0, y1)), ("S", (x0, x1)), ("N", (x0, x1))):
            cuts = sorted({a0, b0} | {v for _, a, b in nb[edge] for v in (a, b)})
            for a, b in zip(cuts[:-1], cuts[1:]):
                if b - a <= EPS:
                    continue
                owner = next((on for on, u, v in nb[edge] if u <= a + EPS and v >= b - EPS), None)
                segs.append((edge, a, b, owner))
        return segs

    # ---- bloque
    def build(self, name):
        blk = self.blocks[name]
        x0, x1, y0, y1 = blk["rect"]
        self.load_mola(x0, x1, y0, y1)
        layers = {}

        base = blk["base"]
        if base == "gale":
            g = self.gale
            m = (g[:, 0] > x0 + EPS) & (g[:, 0] < x1 - EPS) & (g[:, 1] > y0 + EPS) & (g[:, 1] < y1 - EPS)
            bx, by, bz = g[m, 0], g[m, 1], g[m, 2]
        elif base == "mola":
            bx, by, bz = self.mola_centers(x0, x1, y0, y1)
        elif base.startswith("points:"):
            p = dxf_points(os.path.join(self.src, base.split(":", 1)[1]))
            m = (p[:, 0] > x0 + EPS) & (p[:, 0] < x1 - EPS) & (p[:, 1] > y0 + EPS) & (p[:, 1] < y1 - EPS)
            bx, by, bz = p[m, 0], p[m, 1], p[m, 2]
        else:
            raise SystemExit(f"base desconocida: {base}")
        bz2, bw = self.overlay(name, bx, by, bz)
        k = bw < 1.0 - 1e-6
        layers["BASE"] = np.column_stack([bx[k], by[k], bz2[k]])
        base_dropped = int((~k).sum())

        sec_stats = {}
        for sid in blk.get("sectors", []):
            s = self.sectors[sid]
            sx, sy = s.points()
            gap = 0.3 * self.px
            m = (sx > x0 + gap) & (sx < x1 - gap) & (sy > y0 + gap) & (sy < y1 - gap)
            sx, sy = sx[m], sy[m]
            w = self.weight(s, name, sx, sy)
            m = w > 0
            sx, sy = sx[m], sy[m]
            z0 = self.mola(sx, sy)
            # superponer en orden (este sector y los anteriores de la lista)
            z, _ = self.overlay(name, sx, sy, z0)
            layers[f"SECTOR_{sid}"] = np.column_stack([sx, sy, z])
            sec_stats[sid] = {"points": int(m.sum()), "step": s.step, "box_equirect": list(s.box),
                              "max_change_vs_mola_m": float(np.abs(z - z0).max()) if len(z) else 0.0}

        seams, seam_info = [], []
        for edge, a, b, owner in self.edge_segments(name):
            pts, on_gale = self.seam(name, edge, a, b, owner)
            seams.append(pts)
            seam_info.append({"edge": edge, "from": a, "to": b, "neighbour": owner, "gale_points": on_gale, "points": int(len(pts))})
        sp = np.vstack(seams)
        # quitar duplicados (esquinas compartidas por dos bordes)
        _, ui = np.unique(np.round(sp[:, :2], 3), axis=0, return_index=True)
        layers["BORDE"] = sp[np.sort(ui)]
        return layers, {"base": base, "base_points": int(len(layers["BASE"])), "base_replaced_by_sector": base_dropped,
                        "sectors": sec_stats, "seams": seam_info}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True)
    ap.add_argument("--src-dir", required=True)
    ap.add_argument("--block", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    cfg = json.load(open(a.config, encoding="utf-8"))
    needed = sorted({s for b in cfg["blocks"].values() for s in b.get("sectors", [])})
    mos = Mosaic(cfg, a.src_dir, needed)
    layers, info = mos.build(a.block)

    doc = ezdxf.new("R2010"); doc.header["$INSUNITS"] = 6
    msp = doc.modelspace()
    for lay, arr in layers.items():
        for x, y, z in arr:
            msp.add_point((float(x), float(y), float(z)), dxfattribs={"layer": lay})
    doc.saveas(a.out)

    allp = np.vstack(list(layers.values()))
    lon = np.degrees(allp[:, 0] / R); lat = np.degrees(allp[:, 1] / R)
    rep = {"block": a.block, "rect": cfg["blocks"][a.block]["rect"], "out": a.out,
           "points": {k: int(len(v)) for k, v in layers.items()}, "total": int(len(allp)),
           "z_range": [float(allp[:, 2].min()), float(allp[:, 2].max())],
           "bbox_lon": [float(lon.min()), float(lon.max())], "bbox_lat": [float(lat.min()), float(lat.max())],
           "mosaic_extent": list(mos.extent), **info}
    print(json.dumps(rep, indent=2))
    json.dump(rep, open(a.out.rsplit(".", 1)[0] + "_prep.json", "w"), indent=2)


if __name__ == "__main__":
    main()
