"""
Descarga las teselas del mosaico global CTX (MRO Context Camera, Murray Lab / Caltech, ~5 m/px, escala de grises)
que cubren los bloques de Tools/mosaic_blocks.json, desde el servicio de teselas de Esri
(https://astro.arcgis.com/arcgis/rest/services/OnMars/CTX1/MapServer).

Esquema del servicio: GCS Mars 2000 esfera (R = 3 396 190 m), origen (-180, 90), teselas de 512 px JPEG,
nivel L -> resolución 0.3515625 / 2^L grados/px (L=11: 10.2 m/px, L=12: 5.09 m/px, el máximo).
Como el mapa del proyecto es Equirectangular Marte (X = R*lon, Y = R*lat), un píxel CTX es cuadrado también
en metros del proyecto.

Las teselas se guardan sin tocar en <cache>/L<nivel>/<fila>_<col>.jpg y se reanuda donde se quedó.

Uso (venv de TIFFdetallado):
  python Tools/fetch_ctx.py --config Tools/mosaic_blocks.json --lod 11 --cache C:/Users/isaac/dev/TIFFdetallado/CTX_cache
         [--blocks Gale GaleNorte ...] [--margin-m 6000] [--threads 12]
Crédito: NASA/JPL/MSSS/Caltech Murray Lab (Dickson et al.); servicio teselado de Esri.
"""
import argparse, io, json, math, os, sys, threading, time, urllib.request, urllib.error
from concurrent.futures import ThreadPoolExecutor
from PIL import Image, ImageFile

ImageFile.LOAD_TRUNCATED_IMAGES = True

R = 3396190.0
SERVICE = "https://astro.arcgis.com/arcgis/rest/services/OnMars/CTX1/MapServer/tile/{L}/{r}/{c}"
TILE = 512


def deg_per_px(lod):
    return 0.3515625 / (2 ** lod)


def tile_range(rect, lod, margin_m):
    x0, x1, y0, y1 = rect
    x0 -= margin_m; x1 += margin_m; y0 -= margin_m; y1 += margin_m
    d = deg_per_px(lod) * TILE
    lon0, lon1 = math.degrees(x0 / R), math.degrees(x1 / R)
    lat0, lat1 = math.degrees(y0 / R), math.degrees(y1 / R)
    c0 = int(math.floor((lon0 + 180) / d)); c1 = int(math.floor((lon1 + 180) / d))
    r0 = int(math.floor((90 - lat1) / d)); r1 = int(math.floor((90 - lat0) / d))
    return r0, r1, c0, c1


def fetch(lod, r, c, cache, tries=5):
    path = os.path.join(cache, f"L{lod}", f"{r}_{c}.jpg")
    if os.path.exists(path) and os.path.getsize(path) > 0:
        return "cached"
    url = SERVICE.format(L=lod, r=r, c=c)
    for k in range(tries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "ares-map-terrain-tools"})
            data = urllib.request.urlopen(req, timeout=25).read()
            if data[:2] != b"\xff\xd8" and data[:4] != b"\x89PNG":  # el servicio mezcla teselas JPEG y PNG
                raise ValueError("ni JPEG ni PNG")
            Image.open(io.BytesIO(data)).load()  # descarta lo ilegible (el servicio sirve ~1 % de teselas truncadas por el final: se aceptan)
            tmp = path + ".part"
            with open(tmp, "wb") as f:
                f.write(data)
            os.replace(tmp, path)
            return "ok"
        except urllib.error.HTTPError as e:
            if e.code in (400, 404):
                return "missing"
            time.sleep(0.5 + k)
        except Exception:
            time.sleep(0.5 + k)
    return "failed"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True)
    ap.add_argument("--blocks", nargs="*", help="por defecto, todos")
    ap.add_argument("--lod", type=int, default=11)
    ap.add_argument("--cache", required=True)
    ap.add_argument("--margin-m", type=float, default=6000.0)
    ap.add_argument("--threads", type=int, default=12)
    ap.add_argument("--reverse", action="store_true", help="orden inverso (para lanzar un segundo proceso en paralelo)")
    a = ap.parse_args()

    cfg = json.load(open(a.config, encoding="utf-8"))
    names = a.blocks or list(cfg["blocks"].keys())
    need = set()
    for n in names:
        r0, r1, c0, c1 = tile_range(cfg["blocks"][n]["rect"], a.lod, a.margin_m)
        need.update((r, c) for r in range(r0, r1 + 1) for c in range(c0, c1 + 1))
    os.makedirs(os.path.join(a.cache, f"L{a.lod}"), exist_ok=True)
    need = sorted(need, reverse=a.reverse)
    print(f"[ctx] nivel {a.lod} ({deg_per_px(a.lod) * math.pi / 180 * R:.2f} m/px): {len(need)} teselas", flush=True)

    counts = {"ok": 0, "cached": 0, "missing": 0, "failed": 0}
    lock = threading.Lock(); t0 = time.time()

    def work(rc):
        res = fetch(a.lod, rc[0], rc[1], a.cache)
        with lock:
            counts[res] += 1
            done = sum(counts.values())
            if done % 250 == 0 or done == len(need):
                print(f"[ctx] {done}/{len(need)} {counts} {time.time() - t0:.0f}s", flush=True)

    with ThreadPoolExecutor(a.threads) as ex:
        list(ex.map(work, need))
    print("[ctx] fin", counts, flush=True)
    if counts["failed"]:
        sys.exit(1)


if __name__ == "__main__":
    main()
