"""
Prepara los puntos de un sector DXF (Civil 3D, coordenadas locales) para mallarlo como un
segundo bloque de terreno pegado al TIN de Gale:

1. Aplica la georreferencia ajustada por fit_sector_offset.py (traslación XY + desfase Z),
   dejando los puntos en Equirectangular Marte (R = 3 396 190 m), igual que el DXF de Gale.
2. Recorta todo lo que cae dentro del terreno de Gale (al sur de su borde norte) más un
   margen, para que no haya dos superficies superpuestas.
3. Añade los puntos del borde norte de Gale (mismas coordenadas exactas): ambos TIN
   comparten esos vértices y la unión queda sin rendijas.

Escribe un DXF de POINT (capa SECTOR) listo para Tools/blender_dxf_points_to_tin.py --origin.

Uso (venv de TIFFdetallado: numpy, ezdxf):
  python Tools/prepare_sector_points.py --sector <sector.dxf> --gale <gale.dxf>
         --georef Assets/Gale/Models/Babati_georef.json --out <puntos.dxf> [--margin 150]
"""
import argparse, json, math
import numpy as np
import ezdxf

R = 3396190.0


def points(path, layer=None):
    doc = ezdxf.readfile(path)
    return np.array([tuple(e.dxf.location) for e in doc.modelspace().query("POINT")
                     if layer is None or e.dxf.layer == layer])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sector", required=True)
    ap.add_argument("--gale", required=True)
    ap.add_argument("--georef", required=True)
    ap.add_argument("--layer", default="COTAS_DEM_200M")
    ap.add_argument("--margin", type=float, default=150.0,
                    help="Distancia (m) al norte del borde de Gale por debajo de la cual se descartan puntos del sector.")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    g = json.load(open(a.georef))
    off = np.array(g["fitted_offset"])
    dz = g["fitted_score"]["dz_median"]

    sec = points(a.sector, a.layer)
    sec[:, :2] += off
    sec[:, 2] -= dz

    gale = points(a.gale)
    g_ymax = gale[:, 1].max()
    keep = sec[:, 1] > g_ymax + a.margin
    sec = sec[keep]

    xmin, xmax = sec[:, 0].min(), sec[:, 0].max()
    edge = gale[(np.abs(gale[:, 1] - g_ymax) < 1e-3)]
    # Puntos del borde que cubren el ancho del sector (más el primero de cada lado).
    order = np.argsort(edge[:, 0]); edge = edge[order]
    i0 = max(0, np.searchsorted(edge[:, 0], xmin) - 1)
    i1 = min(len(edge), np.searchsorted(edge[:, 0], xmax) + 1)
    edge = edge[i0:i1]

    allp = np.vstack([sec, edge])
    doc = ezdxf.new("R2010")
    doc.header["$INSUNITS"] = 6
    msp = doc.modelspace()
    for x, y, z in sec:
        msp.add_point((x, y, z), dxfattribs={"layer": "SECTOR"})
    for x, y, z in edge:
        msp.add_point((x, y, z), dxfattribs={"layer": "BORDE_GALE"})
    doc.saveas(a.out)

    lon = np.degrees(allp[:, 0] / R); lat = np.degrees(allp[:, 1] / R)
    rep = {
        "out": a.out, "sector_points_kept": int(len(sec)), "sector_points_dropped": int((~keep).sum()),
        "gale_edge_points": int(len(edge)), "gale_edge_y": float(g_ymax), "margin_m": a.margin,
        "offset_applied": off.tolist(), "dz_applied": dz,
        "x_range": [float(allp[:, 0].min()), float(allp[:, 0].max())],
        "y_range": [float(allp[:, 1].min()), float(allp[:, 1].max())],
        "z_range": [float(allp[:, 2].min()), float(allp[:, 2].max())],
        "bbox_lon": [float(lon.min()), float(lon.max())], "bbox_lat": [float(lat.min()), float(lat.max())],
    }
    print(json.dumps(rep, indent=2))
    json.dump(rep, open(a.out.rsplit(".", 1)[0] + "_prep.json", "w"), indent=2)


if __name__ == "__main__":
    main()
