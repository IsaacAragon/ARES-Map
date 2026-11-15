"""
Georreferencia de un sector DXF en coordenadas locales (Civil 3D) respecto al terreno
MOLA de Gale (Equirectangular Marte, R = 3 396 190 m).

1. Hipótesis inicial: el sistema local es una traslación de la proyección
   equirectangular. Se ancla el centro del contorno USGS de Babati a su coordenada IAU
   (137.93° E, 3.16° S).
2. Ajuste: se buscan la traslación XY (y un desfase vertical) que minimizan la diferencia
   de cota entre los puntos del sector y el TIN MOLA de Gale en la franja donde se solapan.

Uso (venv de TIFFdetallado: numpy, ezdxf, matplotlib):
  python Tools/fit_sector_offset.py --sector <sector.dxf> --gale <gale_mola.dxf> --out <informe.json>
"""
import argparse, json, math
import numpy as np
import ezdxf
from matplotlib.tri import Triangulation, LinearTriInterpolator

R = 3396190.0
BABATI_LON, BABATI_LAT = 137.93, -3.16


def equirect(lon, lat):
    return R * math.radians(lon), R * math.radians(lat)


def load(path):
    doc = ezdxf.readfile(path)
    msp = doc.modelspace()
    pts, polys = {}, {}
    for e in msp:
        lay = e.dxf.layer
        if e.dxftype() == "POINT":
            pts.setdefault(lay, []).append(tuple(e.dxf.location))
        elif e.dxftype() == "LWPOLYLINE":
            polys.setdefault(lay, []).append([(p[0], p[1]) for p in e.get_points("xy")])
    return {k: np.array(v) for k, v in pts.items()}, polys


def score(sec_xy, sec_z, interp, off):
    zi = interp(sec_xy[:, 0] + off[0], sec_xy[:, 1] + off[1])
    m = ~np.ma.getmaskarray(zi)
    if m.sum() < 200:
        return None
    d = sec_z[m] - np.asarray(zi)[m]
    dz = float(np.median(d))
    rms = float(np.sqrt(np.mean((d - dz) ** 2)))
    return rms, dz, int(m.sum()), float(np.mean(np.abs(d - dz)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sector", required=True)
    ap.add_argument("--gale", required=True)
    ap.add_argument("--layer", default="COTAS_DEM_200M")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    sec_pts, sec_polys = load(a.sector)
    sec = sec_pts[a.layer]
    sec_xy, sec_z = sec[:, :2], sec[:, 2]

    # Contorno USGS de Babati = el que no toca el borde sur del sector.
    usgs = sec_polys.get("CONTORNOS_USGS", [])
    bab = max(usgs, key=lambda p: np.mean([q[1] for q in p]))
    bab = np.array(bab)
    bab_c = bab.mean(axis=0)
    bab_d = float(np.mean([np.linalg.norm(q - bab_c) for q in bab]) * 2)

    gale_pts, _ = load(a.gale)
    gale = np.vstack(list(gale_pts.values()))
    tri = Triangulation(gale[:, 0], gale[:, 1])
    interp = LinearTriInterpolator(tri, gale[:, 2])

    bx, by = equirect(BABATI_LON, BABATI_LAT)
    t0 = np.array([bx - bab_c[0], by - bab_c[1]])
    s0 = score(sec_xy, sec_z, interp, t0)

    # Búsqueda en malla: gruesa (±20 km, 1 km) y fina (±1.5 km, 100 m).
    best = (s0[0] if s0 else 1e9, t0)
    for span, step in ((20000, 1000), (1500, 100), (150, 10)):
        c = best[1]
        for dx in np.arange(-span, span + 1, step):
            for dy in np.arange(-span, span + 1, step):
                s = score(sec_xy, sec_z, interp, c + (dx, dy))
                if s and s[0] < best[0]:
                    best = (s[0], c + np.array([dx, dy]))
    t = best[1]
    s = score(sec_xy, sec_z, interp, t)

    babc_eq = bab_c + t
    rep = {
        "sector": a.sector, "gale": a.gale, "layer": a.layer,
        "sector_points": int(len(sec)),
        "babati_contour_center_local": bab_c.tolist(),
        "babati_contour_diameter_m": bab_d,
        "hypothesis_offset": t0.tolist(),
        "hypothesis_score": {"rms": s0[0], "dz": s0[1], "n": s0[2], "mae": s0[3]} if s0 else None,
        "fitted_offset": t.tolist(),
        "fitted_score": {"rms": s[0], "dz_median": s[1], "n_overlap": s[2], "mae": s[3]},
        "fit_minus_hypothesis_m": (t - t0).tolist(),
        "babati_center_equirect": babc_eq.tolist(),
        "babati_center_lonlat": [math.degrees(babc_eq[0] / R), math.degrees(babc_eq[1] / R)],
    }
    json.dump(rep, open(a.out, "w"), indent=2)
    print(json.dumps(rep, indent=2))


if __name__ == "__main__":
    main()
