"""
Amplía hacia el este las capas geológicas del conjunto "gale_ARES_v3_zonas" (Tools/import_geo_dataset.py).

El recuadro regional del zip cubre lon 131.50-141.00 E, pero el mosaico de terreno llega hasta 142.0 E: la franja
de ~1 grado (~59 km) del borde este quedaba sin capa. Los datos de origen son globales, así que la franja se
reconstruye con EXACTAMENTE el método del equipo (zip v3, scripts_python/procesar.py, FUENTES.md §3 y §7):

  - Material del suelo: THEMIS infrarrojo noche y día (NASA Mars Trek, teselas z8, ~163 m/px).
        T = gauss(z(noche) - 0.5 z(día), 1.2 px); clases por los percentiles 15/55/85 de T; mediana de 3 px.
    La normalización z() y los cortes se toman de la región ORIGINAL (no de la ampliada), para que las clases del
    resto del mapa no cambien y la franja use la misma vara.
  - Geología: polígonos de unidades de Tanaka et al. (2014), SIM 3292 (servicio ArcGIS de USGS), rasterizados con
    el mismo orden (grandes primero, cráteres encima) y los mismos índices 1..6.
  - Índice termofísico 16 bit: T con los mismos percentiles 2/98 de la región original.

Antes de escribir nada se comprueba que el método reproduce las imágenes del zip en la región original (coincidencia
de píxeles); si no coincide, se avisa. Las imágenes ampliadas conservan la rejilla de píxeles del zip (z8, 162.8 m/px).

Salida: reemplaza en Assets/Gale/Textures/Geo las tres imágenes por versiones más anchas, y actualiza en
Assets/Gale/Data/geo_dataset.json el borde este y el tamaño de la región, y los porcentajes de la región completa.
Hay que correrlo DESPUÉS de import_geo_dataset.py (que vuelve a copiar las imágenes sin la franja).

Uso (venv de TIFFdetallado: numpy, Pillow):
  python Tools/extend_geo_region.py <carpeta gale_ARES_v3_zonas> --cache <dir de teselas> [--east 142.0] [--dry-run]
"""
import argparse, json, math, os, sys, time, urllib.parse, urllib.request
from concurrent.futures import ThreadPoolExecutor
import numpy as np
from PIL import Image, ImageDraw

Image.MAX_IMAGE_PIXELS = None
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
TEX_DIR = os.path.join(ROOT, "Assets", "Gale", "Textures", "Geo")
JSON_PATH = os.path.join(ROOT, "Assets", "Gale", "Data", "geo_dataset.json")
TREK = "https://trek.nasa.gov/tiles/Mars/EQ/{layer}/1.0.0/default/default028mm/8/{r}/{c}.png"
NIGHT = "THEMIS_NightIR_ControlledMosaics_100m_v2_oct2018"
DAY = "THEMIS_DayIR_ControlledMosaics_100m_v2_oct2018"
TANAKA = ("https://services.arcgis.com/v01gqwM5QqNysAAi/arcgis/rest/services/"
          "Tanaka_et_al_2014_Mars_Global_Map_Features/FeatureServer/3/query")
UNITS = ["mNh", "lNh", "HNt", "eHt", "Htu", "AHi"]       # índices 1..6 (orden de geologia_indice.png)
TILE = 256
DEG = 180.0 / TILE / 2 ** 8                               # grados por píxel en z8 = 0.703125 / 256


def http(url, tries=5):
    for k in range(tries):
        try:
            return urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "ares-map-terrain-tools"}),
                                          timeout=60).read()
        except Exception:
            time.sleep(1 + k)
    raise RuntimeError("no se pudo descargar " + url)


def themis(layer, gx0, gx1, gy0, gy1, cache):
    """Mosaico de teselas z8 recortado a los píxeles globales [gx0,gx1) x [gy0,gy1) (canal rojo, 0..255)."""
    os.makedirs(cache, exist_ok=True)
    tcs = range(gx0 // TILE, (gx1 - 1) // TILE + 1); trs = range(gy0 // TILE, (gy1 - 1) // TILE + 1)

    def get(rc):
        r, c = rc
        p = os.path.join(cache, f"{layer}_8_{r}_{c}.png")
        if not os.path.exists(p):
            data = http(TREK.format(layer=layer, r=r, c=c))
            open(p, "wb").write(data)
        return rc, np.asarray(Image.open(p).convert("RGB"))[..., 0]

    out = np.zeros((len(trs) * TILE, len(tcs) * TILE), np.float32)
    with ThreadPoolExecutor(8) as ex:
        for (r, c), a in ex.map(get, [(r, c) for r in trs for c in tcs]):
            out[(r - trs[0]) * TILE:(r - trs[0] + 1) * TILE, (c - tcs[0]) * TILE:(c - tcs[0] + 1) * TILE] = a
    ox, oy = gx0 - tcs[0] * TILE, gy0 - trs[0] * TILE
    return out[oy:oy + (gy1 - gy0), ox:ox + (gx1 - gx0)]


def tanaka(lon0, lon1, lat0, lat1, cache):
    p = os.path.join(cache, f"tanaka_{lon0:.3f}_{lon1:.3f}_{lat0:.3f}_{lat1:.3f}.json")
    if os.path.exists(p):
        return json.load(open(p, encoding="utf-8"))
    feats, off = [], 0
    while True:
        q = dict(where="1=1", geometry=f"{lon0},{lat0},{lon1},{lat1}", geometryType="esriGeometryEnvelope", inSR="4326",
                 spatialRel="esriSpatialRelIntersects", outFields="Unit,SphArea_km", returnGeometry="true", outSR="4326",
                 resultOffset=off, f="json")
        d = json.loads(http(TANAKA + "?" + urllib.parse.urlencode(q)))
        if "error" in d:
            raise RuntimeError(d["error"])
        feats += d["features"]
        if not d.get("exceededTransferLimit"):
            break
        off += len(d["features"])
    json.dump(feats, open(p, "w", encoding="utf-8"))
    return feats


def gaussian_filter(a, sigma):
    """Gaussiano por FFT con bordes reflejados (equivale al de scipy.ndimage a efectos de este método)."""
    pad = int(4 * sigma) + 1
    b = np.pad(a, pad, mode="symmetric")
    fy = np.fft.fftfreq(b.shape[0])[:, None]; fx = np.fft.rfftfreq(b.shape[1])[None, :]
    k = np.exp(-2 * (math.pi * sigma) ** 2 * (fx ** 2 + fy ** 2))
    return np.fft.irfft2(np.fft.rfft2(b) * k, s=b.shape)[pad:-pad, pad:-pad].astype(np.float32)


def median_filter(a, n=3):
    """Mediana n x n (bordes reflejados)."""
    r = n // 2
    b = np.pad(a, r, mode="symmetric")
    st = np.stack([b[i:i + a.shape[0], j:j + a.shape[1]] for i in range(n) for j in range(n)])
    return np.median(st, axis=0).astype(a.dtype)


def fill_nearest(arrays, hole, max_iter=200):
    """Rellena los píxeles de 'hole' con el valor del vecino válido más cercano (dilatación por capas)."""
    hole = hole.copy()
    for _ in range(max_iter):
        if not hole.any():
            break
        new = hole.copy()
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            src = np.roll(~hole, (dy, dx), (0, 1))
            if dy > 0: src[:dy] = False
            if dy < 0: src[dy:] = False
            if dx > 0: src[:, :dx] = False
            if dx < 0: src[:, dx:] = False
            take = new & src
            for a in arrays:
                a[take] = np.roll(a, (dy, dx), (0, 1))[take]
            new &= ~take
        hole = new
    return arrays


def classify(night, day, ref):
    """Índice de material (1..4, 0 = sin dato) e índice termofísico T. 'ref' = máscara de la región original,
    de donde salen la normalización z() y los percentiles."""
    valid = (night > 2) & (day > 2)
    vr = valid & ref

    def z(a):
        m = a[vr]
        return (a - np.median(m)) / (np.percentile(m, 84) - np.percentile(m, 16) + 1e-6)

    T = gaussian_filter(z(night) - 0.5 * z(day), 1.2)
    q = np.percentile(T[vr], [15, 55, 85])
    mat = np.ones(night.shape, np.uint8)
    mat[T >= q[0]] = 2; mat[T >= q[1]] = 3; mat[T >= q[2]] = 4; mat[~valid] = 0
    return median_filter(mat, 3), T, vr


def rasterize(feats, gx0, gy0, w, h):
    img = Image.new("L", (w, h), 0); dr = ImageDraw.Draw(img)
    pix = lambda lon, lat: ((lon + 180) / DEG - gx0, (90 - lat) / DEG - gy0)
    for f in sorted(feats, key=lambda f: -f["attributes"]["SphArea_km"]):
        u = f["attributes"]["Unit"]
        if u not in UNITS:
            print("  AVISO: unidad fuera del conjunto del equipo:", u)
            continue
        for ring in f["geometry"]["rings"]:
            dr.polygon([pix(*p) for p in ring], fill=UNITS.index(u) + 1)
    return np.asarray(img)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("zip_dir"); ap.add_argument("--cache", required=True)
    ap.add_argument("--east", type=float, default=142.0, help="lon este mínima de la región ampliada")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()

    reg0 = os.path.join(a.zip_dir, "zona_0_regional_completa")
    info = json.load(open(os.path.join(reg0, "info_zona.json"), encoding="utf-8"))
    lim = info["limites"]; W0, H0 = info["pixeles"]
    gx0 = round((lim["lon_oeste"] + 180) / DEG); gy0 = round((90 - lim["lat_norte"]) / DEG)
    assert abs(gx0 * DEG - 180 - lim["lon_oeste"]) < 1e-3 and abs(90 - gy0 * DEG - lim["lat_norte"]) < 1e-3, "rejilla distinta"
    W1 = math.ceil((a.east + 180) / DEG) - gx0
    print(f"[geo] región original {W0}x{H0} px (lon {lim['lon_oeste']}..{lim['lon_este']}); ampliada a {W1} px "
          f"(hasta lon {gx0 * DEG - 180 + W1 * DEG:.4f})")

    night = themis(NIGHT, gx0, gx0 + W1, gy0, gy0 + H0, a.cache)
    day = themis(DAY, gx0, gx0 + W1, gy0, gy0 + H0, a.cache)
    ref = np.zeros(night.shape, bool); ref[:, :W0] = True
    # la normalización de la región original es la de las imágenes del zip: se comprueba contra ellas
    zn = np.asarray(Image.open(os.path.join(reg0, "themis_ir_noche.png")).convert("RGB"))[..., 0]
    same = float((night[:, :W0] == zn).mean())
    print(f"[geo] THEMIS noche reconstruida vs zip: {same * 100:.2f} % de píxeles iguales")
    if same < 0.99:
        sys.exit("La reconstrucción no coincide con el zip: revisar la rejilla (no se escribe nada).")

    mat, T, vr = classify(night, day, ref)
    zm = np.asarray(Image.open(os.path.join(reg0, "material_suelo_indice.png")))
    sm = float((mat[:, :W0 - 3] == zm[:, :W0 - 3]).mean())
    print(f"[geo] material del suelo reconstruido vs zip: {sm * 100:.2f} % de píxeles iguales")

    feats = tanaka(lim["lon_oeste"] - 0.1, gx0 * DEG - 180 + W1 * DEG + 0.1, lim["lat_sur"] - 0.1, lim["lat_norte"] + 0.1, a.cache)
    geo = rasterize(feats, gx0, gy0, W1, H0)
    zg = np.asarray(Image.open(os.path.join(reg0, "geologia_indice.png")))
    sg = float((geo[:, :W0] == zg).mean())
    print(f"[geo] geología reconstruida vs zip: {sg * 100:.2f} % de píxeles iguales; "
          f"unidades en la franja nueva: {sorted(set(np.unique(geo[:, W0:]).tolist()) - {0})}")

    # Índice termofísico con los percentiles 2/98 de la región original.
    lo, hi = np.percentile(T[vr], [2, 98])
    T16 = (np.clip((T - lo) / (hi - lo), 0, 1) * 65535).astype(np.uint16)
    zt = np.asarray(Image.open(os.path.join(reg0, "indice_termofisico_16bit.png"))).astype(np.int32)
    print(f"[geo] termofísico: diferencia media con el zip {np.abs(T16[:, :W0 - 3].astype(np.int32) - zt[:, :W0 - 3]).mean():.1f} / 65535")

    strip = slice(W0, W1)
    print("[geo] franja nueva - material (%):", [round(float((mat[:, strip] == k).sum() / max((mat[:, strip] > 0).sum(), 1) * 100), 1) for k in (1, 2, 3, 4)],
          " geología (%):", [round(float((geo[:, strip] == k).sum() / max((geo[:, strip] > 0).sum(), 1) * 100), 1) for k in range(1, 7)])
    # Huecos sin dato de THEMIS en la franja nueva (el zip tiene los suyos en la región original: no se tocan).
    hole = np.zeros(mat.shape, bool); hole[:, strip] = mat[:, strip] == 0
    if hole.any():
        print(f"[geo] relleno {int(hole.sum())} píxeles sin dato de THEMIS en la franja con el vecino más cercano")
        fill_nearest([mat, T16], hole)
    sinsuelo = float((mat[:, strip] == 0).mean()); singeo = float((geo[:, strip] == 0).mean())
    print(f"[geo] píxeles sin dato en la franja: material {sinsuelo * 100:.2f} %, geología {singeo * 100:.2f} %")
    if a.dry_run:
        return

    Image.fromarray(mat).save(os.path.join(TEX_DIR, "Geo_SueloIndice.png"))
    Image.fromarray(geo).save(os.path.join(TEX_DIR, "Geo_GeologiaIndice.png"))
    Image.fromarray(T16).save(os.path.join(TEX_DIR, "Geo_Termofisico16.png"))
    east = gx0 * DEG - 180 + W1 * DEG
    d = json.load(open(JSON_PATH, encoding="utf-8"))
    d["region"].update(lonEste=round(east, 5), ancho=W1)
    z0 = next(z for z in d["zonas"] if z["id"] == "zona_0_regional_completa")
    z0["lonEste"] = round(east, 5)
    z0["anchoKm"] = round((east - lim["lon_oeste"]) * math.pi / 180 * 3396.19, 1)
    tot = max((mat > 0).sum(), 1); totg = max((geo > 0).sum(), 1)
    z0["sueloPct"] = [round(float((mat == k).sum() / tot * 100), 1) for k in (1, 2, 3, 4)]
    z0["geologiaPct"] = [round(float((geo == k).sum() / totg * 100), 1) for k in range(1, 7)]
    with open(JSON_PATH, "w", encoding="utf-8", newline="\n") as f:
        json.dump(d, f, indent=1, ensure_ascii=False); f.write("\n")
    print(f"[geo] escrito: imágenes {W1}x{H0} y región hasta lon {east:.5f}")


if __name__ == "__main__":
    main()
