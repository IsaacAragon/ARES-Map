"""
Descarga del Gazetteer of Planetary Nomenclature (IAU / USGS Astrogeology, dominio público) las formaciones con
nombre oficial de Marte que caen en la región de los mapas de ARES Map (la de geo_dataset.json), y escribe
Assets/Gale/Data/iau_nomenclatura.json para el buscador y los nodos del menú de exploración (PlaceAtlas).

Por cada formación: id del Gazetteer, nombre, tipo, diámetro (km; para cráteres, el del borde), centro y recuadro
lat/lon planetocéntricos con longitud este 0–360, origen del nombre, fecha de aprobación y cuadrángulo.

Uso:  python -I Tools/fetch_iau_names.py
"""
import datetime as dt
import html
import json
import os
import re
import urllib.parse
import urllib.request

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
GEO = os.path.join(ROOT, "Assets", "Gale", "Data", "geo_dataset.json")
OUT = os.path.join(ROOT, "Assets", "Gale", "Data", "iau_nomenclatura.json")
URL = "https://planetarynames.wr.usgs.gov/SearchResults"


def fetch(region):
    q = {
        "Target": "20_Mars", "is_positive_east": "true", "is_0_360": "true", "is_planetographic": "false",
        "displayCoordinates": "Planetocentric", "coordinateSystem": "PE360", "lat_lon_format": "decimal",
        "northernLatitude": region["latNorte"] + 0.1, "southernLatitude": region["latSur"] - 0.1,
        "westernLongitude": region["lonOeste"] - 0.1, "easternLongitude": region["lonEste"] + 0.1,
        "sort_asc": "true", "sort_column": "name",
    }
    req = urllib.request.Request(URL + "?" + urllib.parse.urlencode(q), headers={"User-Agent": "ares-map-tools"})
    with urllib.request.urlopen(req, timeout=180) as r:
        return r.read().decode("utf-8", "replace")


def rows(page):
    for r in re.findall(r'<tr\s+class="hover-highlight[^"]*">(.*?)</tr>', page, re.S):
        cells = {}
        for cls, val in re.findall(r'<td class="(\w+)Column[^"]*"[^>]*>(.*?)</td>', r, re.S):
            cells.setdefault(cls, []).append(html.unescape(re.sub(r"<[^>]+>", "", val)).strip())
        yield cells


def num(s):
    return float(s) if re.match(r"^-?\d", s or "") else None


def main():
    region = json.load(open(GEO, encoding="utf-8"))["region"]
    out = []
    for c in rows(fetch(region)):
        g = lambda k: (c.get(k) or [""])[0]
        ll = c.get("centerLatLon") or []
        if len(ll) < 2 or num(ll[0]) is None:
            continue
        lat, lon = num(ll[0]), num(ll[1])
        # La búsqueda del Gazetteer no siempre aplica el recuadro: se filtra aquí por el centro.
        if not (region["latSur"] <= lat <= region["latNorte"] and region["lonOeste"] <= lon <= region["lonEste"]):
            continue
        b = [num(x) for x in c.get("latLon", []) if num(x) is not None]
        n, s, e, w = (b + [lat, lat, lon, lon])[:4] if len(b) == 4 else (lat, lat, lon, lon)
        out.append({
            "id": int(g("featureID")), "nombre": g("cleanFeatureName") or g("featureName"), "tipo": g("featureType"),
            "diametroKm": num(g("diameter")) or 0.0, "lat": lat, "lon": lon,
            "latNorte": n, "latSur": s, "lonEste": e, "lonOeste": w,
            "origen": g("origin"), "aprobado": g("approvalDate"), "cuadrangulo": g("quad").upper(),
        })
    out.sort(key=lambda f: (-f["diametroKm"], f["nombre"]))
    doc = {
        "fuente": "Gazetteer of Planetary Nomenclature, IAU WGPSN / USGS Astrogeology Science Center "
                  "(planetarynames.wr.usgs.gov), dominio público",
        "descargado": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "coordenadas": "planetocéntricas, longitud este 0–360",
        "formaciones": out,
    }
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, ensure_ascii=False, indent=1)
        f.write("\n")
    for x in out:
        print(f'{x["tipo"]:18} {x["nombre"]:15} {x["diametroKm"]:7.2f} km  {x["lat"]:6.2f} {x["lon"]:7.2f}')
    print(len(out), "formaciones ->", os.path.relpath(OUT, ROOT))


if __name__ == "__main__":
    main()
