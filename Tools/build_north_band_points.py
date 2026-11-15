"""
Puntos de la FRANJA NORTE de Gale (de su borde norte hasta el norte de Babati, con el
mismo ancho que el TIN de Gale), listos para Tools/blender_dxf_points_to_tin.py --origin.

Fuentes, en Equirectangular Marte (R = 3 396 190 m), igual que el DXF de Gale:
  - SECTOR: el DXF de Civil 3D (malla de 200 m, coordenadas locales), georreferenciado con
    la traslación y el desfase Z que ajustó Tools/fit_sector_offset.py.
  - MOLA: píxeles del GeoTIFF global MOLA 463 m a los lados del sector (rellena lo que el
    DXF no cubre, para que no quede un hueco al norte de Gale).
  - BORDE_GALE: los puntos del borde norte del DXF de Gale (coordenadas exactas), así la
    franja y Gale comparten vértices y la unión no tiene rendijas.

Transición: dentro del sector, a menos de --blend metros de sus bordes este/oeste y del
borde de Gale, la cota se mezcla poco a poco con la de MOLA (interpolación bilineal), para que
no haya escalones donde cambia la fuente.

Uso (venv de TIFFdetallado: numpy, ezdxf, rasterio):
  python Tools/build_north_band_points.py --sector <sector.dxf> --gale <gale.dxf> --mola <mola.tif>
         --georef Assets/Gale/Models/Babati_georef.json --out <puntos.dxf>
"""
import argparse, json
import numpy as np
import ezdxf
import rasterio
from rasterio.windows import from_bounds

R = 3396190.0


def dxf_points(path, layer=None):
    doc = ezdxf.readfile(path)
    return np.array([tuple(e.dxf.location) for e in doc.modelspace().query("POINT")
                     if layer is None or e.dxf.layer == layer])


def frame(path):
    doc = ezdxf.readfile(path)
    for e in doc.modelspace().query("LWPOLYLINE"):
        if e.dxf.layer == "MARCO_SECTOR":
            p = np.array([(q[0], q[1]) for q in e.get_points("xy")])
            return p[:, 0].min(), p[:, 0].max(), p[:, 1].min(), p[:, 1].max()
    raise SystemExit("MARCO_SECTOR no encontrado")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sector", required=True)
    ap.add_argument("--gale", required=True)
    ap.add_argument("--mola", required=True)
    ap.add_argument("--georef", required=True)
    ap.add_argument("--layer", default="COTAS_DEM_200M")
    ap.add_argument("--margin", type=float, default=150.0, help="Puntos del sector a menos de esto (m) del borde de Gale se descartan.")
    ap.add_argument("--gap", type=float, default=250.0, help="Puntos MOLA a menos de esto (m) del marco del sector se descartan.")
    ap.add_argument("--blend", type=float, default=2000.0, help="Ancho (m) de la transición sector -> MOLA.")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    g = json.load(open(a.georef))
    off = np.array(g["fitted_offset"]); dz = g["fitted_score"]["dz_median"]

    # ---- Gale: borde norte y ancho
    gale = dxf_points(a.gale)
    edge_y = gale[:, 1].max()
    gx0, gx1 = gale[:, 0].min(), gale[:, 0].max()
    edge = gale[np.abs(gale[:, 1] - edge_y) < 1e-3]

    # ---- Sector georreferenciado
    sec = dxf_points(a.sector, a.layer)
    sec[:, :2] += off; sec[:, 2] -= dz
    fx0, fx1, fy0, fy1 = frame(a.sector)
    fx0 += off[0]; fx1 += off[0]; fy0 += off[1]; fy1 += off[1]
    top_y = sec[:, 1].max()

    # ---- MOLA: ventana que cubre la franja (con un píxel de margen)
    with rasterio.open(a.mola) as ds:
        px = ds.transform.a
        win = from_bounds(gx0 - 2 * px, edge_y - 2 * px, gx1 + 2 * px, top_y + 2 * px, ds.transform).round_offsets().round_lengths()
        z = ds.read(1, window=win).astype(np.float64)
        nod = ds.nodata
        t = ds.window_transform(win)
    rows, cols = np.mgrid[0:z.shape[0], 0:z.shape[1]]
    mx = t.c + (cols + 0.5) * t.a
    my = t.f + (rows + 0.5) * t.e
    if nod is not None and (z == nod).any():
        raise SystemExit("nodata dentro de la franja")

    def mola_bilinear(x, y):
        c = (x - t.c) / t.a - 0.5
        r = (y - t.f) / t.e - 0.5
        c0 = np.clip(np.floor(c).astype(int), 0, z.shape[1] - 2); r0 = np.clip(np.floor(r).astype(int), 0, z.shape[0] - 2)
        fc = np.clip(c - c0, 0, 1); fr = np.clip(r - r0, 0, 1)
        return (z[r0, c0] * (1 - fc) * (1 - fr) + z[r0, c0 + 1] * fc * (1 - fr)
                + z[r0 + 1, c0] * (1 - fc) * fr + z[r0 + 1, c0 + 1] * fc * fr)

    # ---- Sector: recorte y transición hacia MOLA
    sec = sec[sec[:, 1] > edge_y + a.margin]
    d_edge = np.minimum.reduce([sec[:, 0] - fx0, fx1 - sec[:, 0], sec[:, 1] - edge_y])
    w = np.clip(d_edge / a.blend, 0, 1)
    w = w * w * (3 - 2 * w)  # suavizado (smoothstep)
    zm = mola_bilinear(sec[:, 0], sec[:, 1])
    sec_z_before = sec[:, 2].copy()
    sec[:, 2] = w * sec[:, 2] + (1 - w) * zm

    # ---- MOLA fuera del sector
    m = np.column_stack([mx.ravel(), my.ravel(), z.ravel()])
    inside_band = (m[:, 0] >= gx0 - 1e-3) & (m[:, 0] <= gx1 + 1e-3) & (m[:, 1] > edge_y + 1.0) & (m[:, 1] <= top_y + px * 0.999)
    near_frame = (m[:, 0] > fx0 - a.gap) & (m[:, 0] < fx1 + a.gap) & (m[:, 1] < fy1 + a.gap)
    mola = m[inside_band & ~near_frame]

    doc = ezdxf.new("R2010"); doc.header["$INSUNITS"] = 6
    msp = doc.modelspace()
    for lay, arr in (("SECTOR", sec), ("MOLA", mola), ("BORDE_GALE", edge)):
        for x, y, zz in arr:
            msp.add_point((x, y, zz), dxfattribs={"layer": lay})
    doc.saveas(a.out)

    allp = np.vstack([sec, mola, edge])
    lon = np.degrees(allp[:, 0] / R); lat = np.degrees(allp[:, 1] / R)
    rep = {
        "out": a.out,
        "points": {"SECTOR": int(len(sec)), "MOLA": int(len(mola)), "BORDE_GALE": int(len(edge))},
        "gale_edge_y": float(edge_y), "band_x": [float(gx0), float(gx1)],
        "y_range": [float(allp[:, 1].min()), float(allp[:, 1].max())],
        "sector_frame_equirect": [float(fx0), float(fx1), float(fy0), float(fy1)],
        "blend_m": a.blend, "blend_max_change_m": float(np.abs(sec[:, 2] - sec_z_before).max()),
        "blend_points": int((w < 1).sum()),
        "offset_applied": off.tolist(), "dz_applied": dz,
        "z_range": [float(allp[:, 2].min()), float(allp[:, 2].max())],
        "bbox_lon": [float(lon.min()), float(lon.max())], "bbox_lat": [float(lat.min()), float(lat.max())],
    }
    print(json.dumps(rep, indent=2))
    json.dump(rep, open(a.out.rsplit(".", 1)[0] + "_prep.json", "w"), indent=2)


if __name__ == "__main__":
    main()
