"""
Exporta a DXF (para Civil 3D) las partes de la franja norte de Gale que NO vienen del DXF
del sector Gale-Babati: los laterales oeste y este (y la tira sobre el sector), tomados del
GeoTIFF global MOLA 463 m. Mismas capas y estilo que "4. Gale_Babati_sector_norte_Civil3D.dxf".

Genera dos archivos:
  <out>_local.dxf         en el sistema LOCAL del DXF de Civil 3D (se superpone al sector);
  <out>_equirect.dxf      en Equirectangular Marte (R = 3 396 190 m), como el DXF de Gale.
Y un PENZ (punto, Este, Norte, Z) en el sistema local.

Capas: COTAS_MOLA_463M (POINT, un punto por píxel), CURVAS_50M y CURVAS_250M
(LWPOLYLINE con su cota en elevation), MARCO_LATERALES, ROTULOS.
Z = cota MOLA tal cual (areoide Mars 2000). El DXF del sector está, de mediana, 7,8 m
por encima de MOLA (ver Babati_georef.json).

Uso (venv de TIFFdetallado: numpy, ezdxf, rasterio, matplotlib):
  python Tools/export_band_sides_dxf.py --sector <sector.dxf> --gale <gale.dxf> --mola <mola.tif>
         --georef Assets/Gale/Models/Babati_georef.json --out <ruta_sin_extension>
"""
import argparse, json, math
import numpy as np
import ezdxf
import rasterio
from rasterio.windows import from_bounds
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

R = 3396190.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sector", required=True)
    ap.add_argument("--gale", required=True)
    ap.add_argument("--mola", required=True)
    ap.add_argument("--georef", required=True)
    ap.add_argument("--gap", type=float, default=250.0)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    off = np.array(json.load(open(a.georef))["fitted_offset"])

    gale = np.array([tuple(e.dxf.location) for e in ezdxf.readfile(a.gale).modelspace().query("POINT")])
    edge_y = gale[:, 1].max(); gx0, gx1 = gale[:, 0].min(), gale[:, 0].max()

    sdoc = ezdxf.readfile(a.sector)
    fr = next(e for e in sdoc.modelspace().query("LWPOLYLINE") if e.dxf.layer == "MARCO_SECTOR")
    fp = np.array([(q[0], q[1]) for q in fr.get_points("xy")])
    fx0, fx1 = fp[:, 0].min() + off[0], fp[:, 0].max() + off[0]
    fy1 = fp[:, 1].max() + off[1]
    top_y = max(e.dxf.location[1] for e in sdoc.modelspace().query("POINT")) + off[1]

    with rasterio.open(a.mola) as ds:
        px = ds.transform.a
        win = from_bounds(gx0 - px, edge_y - px, gx1 + px, top_y + 2 * px, ds.transform).round_offsets().round_lengths()
        z = ds.read(1, window=win).astype(np.float64)
        t = ds.window_transform(win)
    cols = t.c + (np.arange(z.shape[1]) + 0.5) * t.a
    rows = t.f + (np.arange(z.shape[0]) + 0.5) * t.e
    X, Y = np.meshgrid(cols, rows)

    band = (X >= gx0 - 1e-3) & (X <= gx1 + 1e-3) & (Y > edge_y + 1.0) & (Y <= top_y + px * 0.999)
    near = (X > fx0 - a.gap) & (X < fx1 + a.gap) & (Y < fy1 + a.gap)
    keep = band & ~near

    # Curvas sobre la rejilla (enmascarando lo que no se exporta); se incluye un píxel de borde
    # para que las curvas lleguen hasta el límite.
    zc = np.ma.array(z, mask=~keep)
    fig, ax = plt.subplots()
    lev50 = np.arange(math.floor(z[keep].min() / 50) * 50, z[keep].max() + 50, 50)
    cs = ax.contour(X, Y, zc, levels=lev50)
    curves = []
    for lev, segs in zip(cs.levels, cs.allsegs):
        for s in segs:
            if len(s) >= 2:
                curves.append((float(lev), s))
    plt.close(fig)

    pts = np.column_stack([X[keep], Y[keep], z[keep]])
    west = pts[pts[:, 0] < fx0]; east = pts[pts[:, 0] > fx1]; north = pts[(pts[:, 0] >= fx0) & (pts[:, 0] <= fx1)]

    def write(path, dx, dy):
        doc = ezdxf.new("R2013")
        doc.header["$INSUNITS"] = 6
        for name, color in (("COTAS_MOLA_463M", 8), ("CURVAS_50M", 9), ("CURVAS_250M", 1),
                            ("MARCO_LATERALES", 3), ("ROTULOS", 7)):
            doc.layers.add(name, color=color)
        msp = doc.modelspace()
        for x, y, zz in pts:
            msp.add_point((x - dx, y - dy, zz), dxfattribs={"layer": "COTAS_MOLA_463M"})
        for lev, s in curves:
            lay = "CURVAS_250M" if abs(lev % 250) < 1e-6 else "CURVAS_50M"
            msp.add_lwpolyline([(x - dx, y - dy) for x, y in s], dxfattribs={"layer": lay, "elevation": lev})
        for part, label in ((west, "LATERAL OESTE (MOLA 463 m)"), (east, "LATERAL ESTE (MOLA 463 m)")):
            if len(part) == 0:
                continue
            x0, x1 = part[:, 0].min() - px / 2, part[:, 0].max() + px / 2
            y0, y1 = part[:, 1].min() - px / 2, part[:, 1].max() + px / 2
            msp.add_lwpolyline([(x0 - dx, y0 - dy), (x1 - dx, y0 - dy), (x1 - dx, y1 - dy), (x0 - dx, y1 - dy)],
                               close=True, dxfattribs={"layer": "MARCO_LATERALES"})
            msp.add_text(label, height=800, dxfattribs={"layer": "ROTULOS"}).set_placement(
                ((x0 + x1) / 2 - dx, (y0 + y1) / 2 - dy), align=ezdxf.enums.TextEntityAlignment.MIDDLE_CENTER)
        doc.saveas(path)

    write(a.out + "_local.dxf", off[0], off[1])
    write(a.out + "_equirect.dxf", 0.0, 0.0)
    with open(a.out + "_local_PENZ.txt", "w", encoding="utf-8") as f:
        for i, (x, y, zz) in enumerate(pts, 1):
            f.write(f"{i},{x - off[0]:.3f},{y - off[1]:.3f},{zz:.1f}\n")

    lon = np.degrees(pts[:, 0] / R); lat = np.degrees(pts[:, 1] / R)
    rep = {
        "points": int(len(pts)), "west": int(len(west)), "east": int(len(east)), "north_strip": int(len(north)),
        "contours": len(curves), "z_range": [float(pts[:, 2].min()), float(pts[:, 2].max())],
        "local_offset_subtracted": off.tolist(),
        "local_x": [float(pts[:, 0].min() - off[0]), float(pts[:, 0].max() - off[0])],
        "local_y": [float(pts[:, 1].min() - off[1]), float(pts[:, 1].max() - off[1])],
        "lon": [float(lon.min()), float(lon.max())], "lat": [float(lat.min()), float(lat.max())],
    }
    print(json.dumps(rep, indent=2))
    json.dump(rep, open(a.out + "_info.json", "w"), indent=2)


if __name__ == "__main__":
    main()
