"""
Descarga la ruta real de Curiosity (MSL) desde el MMGIS de JPL y la deja en CSV para Unity
(Assets/Gale/Route, la lee CuriosityRoute.cs):
  MSL_ruta.csv       lon_E, lat, elev_m      (polilínea simplificada con Douglas-Peucker, --tol m)
  MSL_waypoints.csv  sol, site, drive, lon_E, lat, elev_m, dist_km   (un punto por tramo conducido)
Fuentes: https://mars.nasa.gov/mmgis-maps/MSL/Layers/json/MSL_traverse.json y MSL_waypoints.json
(lon/lat planetocéntricas, Este+, y cota sobre el areoide, como MOLA).

Uso (venv de TIFFdetallado: numpy):
  python Tools/build_curiosity_route.py --out Assets/Gale/Route [--tol 1.0]
"""
import argparse, json, os, urllib.request
import numpy as np

R = 3396190.0
BASE = "https://mars.nasa.gov/mmgis-maps/MSL/Layers/json/"


def fetch(name):
    req = urllib.request.Request(BASE + name, headers={"User-Agent": "ares-map-terrain-tools"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.load(r)


def douglas_peucker(P, tol):
    keep = np.zeros(len(P), bool); keep[0] = keep[-1] = True
    stack = [(0, len(P) - 1)]
    while stack:
        i, j = stack.pop()
        if j <= i + 1:
            continue
        A, B = P[i], P[j]; AB = B - A; L = np.hypot(*AB)
        Q = P[i + 1:j] - A
        d = np.abs(AB[0] * Q[:, 1] - AB[1] * Q[:, 0]) / L if L > 0 else np.hypot(Q[:, 0], Q[:, 1])
        k = int(np.argmax(d))
        if d[k] > tol:
            m = i + 1 + k; keep[m] = True; stack += [(i, m), (m, j)]
    return keep


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--tol", type=float, default=1.0, help="tolerancia de simplificación (m)")
    a = ap.parse_args()

    trav = fetch("MSL_traverse.json")
    pts = []
    for f in trav["features"]:
        g = f["geometry"]
        lines = g["coordinates"] if g["type"] == "MultiLineString" else [g["coordinates"]]
        for line in lines:
            for p in line:
                if not pts or abs(pts[-1][0] - p[0]) > 1e-9 or abs(pts[-1][1] - p[1]) > 1e-9:
                    pts.append(p[:3])
    P = np.array(pts)
    xy = np.column_stack([np.radians(P[:, 0]) * R, np.radians(P[:, 1]) * R])
    keep = douglas_peucker(xy, a.tol)

    wps = [f["properties"] for f in fetch("MSL_waypoints.json")["features"]]
    os.makedirs(a.out, exist_ok=True)
    with open(os.path.join(a.out, "MSL_ruta.csv"), "w", newline="\n") as o:
        o.write("lon_E,lat,elev_m\n")
        for p in P[keep]:
            o.write(f"{p[0]:.7f},{p[1]:.7f},{p[2]:.2f}\n")
    with open(os.path.join(a.out, "MSL_waypoints.csv"), "w", newline="\n") as o:
        o.write("sol,site,drive,lon_E,lat,elev_m,dist_km\n")
        for w in wps:
            o.write(f"{w['sol']},{w['site']},{w['drive']},{w['lon']:.7f},{w['lat']:.7f},{w['elev_geoid']:.2f},{w['dist_km']:.3f}\n")
    length = np.hypot(*np.diff(xy, axis=0).T).sum() / 1000
    print(f"[ruta] {len(P)} puntos -> {keep.sum()} (tol {a.tol} m), {length:.2f} km; "
          f"{len(wps)} waypoints, sol {wps[0]['sol']}-{wps[-1]['sol']}")


if __name__ == "__main__":
    main()
