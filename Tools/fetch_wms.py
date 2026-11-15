"""
Descarga las imágenes orbitales de un bloque del mosaico desde el WMS de USGS Astrogeology
(planetarymaps.usgs.gov, mars_simp_cyl.map, dominio público), para Tools/build_gale_textures.py:
  MDIM21_color.png (Viking MDIM 2.1 color) y THEMIS.png (THEMIS día IR).

El bloque se da en Equirectangular Marte (m, R = 3 396 190 m) como en Tools/mosaic_blocks.json.
La resolución por defecto es la de Gale_Albedo.png (2048 px para el ancho de Gale), y se pide
un margen de --margin píxeles por lado para que el desenfoque tenga contexto
(build_gale_textures.py --crop-margin lo recorta).

Uso:
  python Tools/fetch_wms.py --config Tools/mosaic_blocks.json --block GaleSur --margin 32 --out <dir>
"""
import argparse, json, math, os, urllib.request

R = 3396190.0
URL = ("https://planetarymaps.usgs.gov/cgi-bin/mapserv?map=/maps/mars/mars_simp_cyl.map&SERVICE=WMS&VERSION=1.1.1"
       "&REQUEST=GetMap&LAYERS={layer}&STYLES=&SRS=EPSG:4326&BBOX={bbox}&WIDTH={w}&HEIGHT={h}&FORMAT=image/png")
# Gale_Albedo.png: 2048 px entre lon 136.11621087545228 y 139.48347084890932
GALE_PX_DEG = (139.48347084890932 - 136.11621087545228) / 2048


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True)
    ap.add_argument("--block", required=True)
    ap.add_argument("--px-deg", type=float, default=GALE_PX_DEG)
    ap.add_argument("--margin", type=int, default=32)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    x0, x1, y0, y1 = json.load(open(a.config, encoding="utf-8"))["blocks"][a.block]["rect"]
    lon0, lon1 = math.degrees(x0 / R), math.degrees(x1 / R)
    lat0, lat1 = math.degrees(y0 / R), math.degrees(y1 / R)
    w = max(1, round((lon1 - lon0) / a.px_deg)); h = max(1, round((lat1 - lat0) / a.px_deg))
    pdx = (lon1 - lon0) / w; pdy = (lat1 - lat0) / h
    m = a.margin
    # MapServer (WMS 1.1.1) toma el BBOX como los CENTROS de los píxeles del borde (paso = ancho/(n-1)),
    # comprobado contra Gale_Albedo: con BBOX en bordes la imagen sale corrida ~1 px. El UV de Unity pone
    # el texel i en lon0 + (i + 0.5) * pdx, así que se piden esos centros.
    bbox = (f"{lon0 + (0.5 - m) * pdx!r},{lat0 + (0.5 - m) * pdy!r},"
            f"{lon1 - (0.5 - m) * pdx!r},{lat1 - (0.5 - m) * pdy!r}")
    os.makedirs(a.out, exist_ok=True)
    for layer in ("MDIM21_color", "THEMIS"):
        url = URL.format(layer=layer, bbox=bbox, w=w + 2 * m, h=h + 2 * m)
        req = urllib.request.Request(url, headers={"User-Agent": "ares-map-terrain-tools"})
        with urllib.request.urlopen(req, timeout=300) as r:
            data = r.read()
        if not data.startswith(b"\x89PNG"):
            raise SystemExit(f"{layer}: el WMS no devolvió PNG: {data[:300]!r}")
        open(os.path.join(a.out, layer + ".png"), "wb").write(data)
        print(f"[wms] {layer}.png {w + 2 * m}x{h + 2 * m} ({len(data) / 1e6:.1f} MB)")
    info = {"block": a.block, "rect": [x0, x1, y0, y1], "lon": [lon0, lon1], "lat": [lat0, lat1],
            "texture_px": [w, h], "margin_px": m, "bbox_requested": bbox}
    json.dump(info, open(os.path.join(a.out, "wms_info.json"), "w"), indent=2)
    print(json.dumps(info, indent=2))


if __name__ == "__main__":
    main()
