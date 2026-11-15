"""
Georreferencia conjunta de los DXF de Civil 3D (sectores 6, 7, 9...) respecto al GeoTIFF
global MOLA 463 m (Equirectangular Marte, R = 3 396 190 m, paralelo estándar 0°).

Los sectores comparten un sistema LOCAL: una equirectangular con el paralelo estándar en la
latitud de su origen (phi0 ~ -4.59°). Por eso, respecto al MOLA de Gale:

    X_equirect = X0 + x_local / cos(phi0)        phi0 = Y0 / R
    Y_equirect = Y0 + y_local
    Z_mola     = z_local - dz_sector

Se ajustan X0 e Y0 (búsqueda por coordenadas) minimizando el RMS de la diferencia de cota
contra MOLA (bilineal) de todos los sectores juntos; dz es la mediana por sector.
Punto de partida: el desfase de Babati (Babati_georef.json).

Uso (venv de TIFFdetallado: numpy, ezdxf, rasterio):
  python Tools/fit_sectors_georef.py --mola <mola.tif> --init-georef Assets/Gale/Models/Babati_georef.json
         --sector 6=<dxf> --sector 7=<dxf> ... --out Assets/Gale/Models/Sectores_georef.json
"""
import argparse, json, math, os
import numpy as np
import ezdxf
import rasterio
from rasterio.windows import from_bounds

R = 3396190.0


def load_grid(path, layer):
    a = np.array([tuple(e.dxf.location) for e in ezdxf.readfile(path).modelspace().query("POINT")
                  if e.dxf.layer == layer])
    return a


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mola", required=True)
    ap.add_argument("--init-georef", required=True)
    ap.add_argument("--sector", action="append", required=True, metavar="ID=DXF")
    ap.add_argument("--layer", default="COTAS_DEM_200M")
    ap.add_argument("--subsample", type=int, default=5)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    off0 = np.array(json.load(open(a.init_georef))["fitted_offset"], dtype=float)
    sectors = {}
    for s in a.sector:
        sid, path = s.split("=", 1)
        pts = load_grid(path, a.layer)
        sectors[sid] = {"path": path, "pts": pts, "sub": pts[::a.subsample]}

    allp = np.vstack([s["sub"] for s in sectors.values()])
    x0, y0 = allp[:, :2].min(0) * 1.01 + off0 - 5000
    x1, y1 = allp[:, :2].max(0) * 1.01 + off0 + 5000
    with rasterio.open(a.mola) as ds:
        win = from_bounds(min(x0, x1), y0, max(x0, x1), y1, ds.transform).round_offsets().round_lengths()
        z = ds.read(1, window=win).astype(np.float64)
        t = ds.window_transform(win)

    def bil(x, y):
        c = (x - t.c) / t.a - 0.5; r = (y - t.f) / t.e - 0.5
        c0 = np.clip(np.floor(c).astype(int), 0, z.shape[1] - 2); r0 = np.clip(np.floor(r).astype(int), 0, z.shape[0] - 2)
        fc = c - c0; fr = r - r0
        return (z[r0, c0] * (1 - fc) * (1 - fr) + z[r0, c0 + 1] * fc * (1 - fr)
                + z[r0 + 1, c0] * (1 - fc) * fr + z[r0 + 1, c0 + 1] * fc * fr)

    def score(X0, Y0, sx=None):
        sx = sx if sx is not None else 1.0 / math.cos(Y0 / R)
        res, per = [], {}
        for sid, s in sectors.items():
            p = s["sub"]
            d = p[:, 2] - bil(X0 + p[:, 0] * sx, Y0 + p[:, 1])
            med = float(np.median(d)); d = d - med
            per[sid] = {"rms": float(np.sqrt(np.mean(d ** 2))), "mae": float(np.mean(np.abs(d))), "dz_median": med}
            res.append(d)
        return float(np.sqrt(np.mean(np.concatenate(res) ** 2))), per

    p = off0.copy(); best = score(*p)[0]; step = 400.0
    while step > 1.0:
        moved = False
        for d in ((step, 0), (-step, 0), (0, step), (0, -step)):
            r = score(p[0] + d[0], p[1] + d[1])[0]
            if r < best:
                best, p, moved = r, p + np.array(d), True
        if not moved:
            step /= 2
    total, per = score(*p)
    _, per_old = score(*off0, sx=1.0)
    phi0 = p[1] / R

    out = {
        "model": "X = X0 + x_local / cos(phi0); Y = Y0 + y_local; Z_mola = z_local - dz",
        "X0": float(p[0]), "Y0": float(p[1]),
        "phi0_deg": math.degrees(phi0), "x_scale": 1.0 / math.cos(phi0),
        "rms_all": total,
        "sectors": {sid: {"dxf": os.path.basename(s["path"]), "points": int(len(s["pts"])),
                          "local_min": s["pts"].min(0).tolist(), "local_max": s["pts"].max(0).tolist(),
                          "dz": per[sid]["dz_median"], "rms": per[sid]["rms"], "mae": per[sid]["mae"],
                          "rms_with_babati_offset_no_scale": per_old[sid]["rms"]}
                    for sid, s in sectors.items()},
        "init_offset": off0.tolist(), "mola": os.path.basename(a.mola),
    }
    json.dump(out, open(a.out, "w"), indent=2)
    print(json.dumps(out, indent=2))


if __name__ == "__main__":
    main()
