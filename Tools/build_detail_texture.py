"""
Textura de un bloque de detalle (p. ej. GaleRuta, la zona de la ruta de Curiosity) desde el mosaico
de color HiRISE de la misión MSL (USGS, Parker & Calef: MSL_Gale_HiRISE-LRGB_78quads_sharp_cog.tif,
COG JPEG en Equirectangular Marte, 25 cm/px), leído por ventana y a la resolución pedida.

Para que la unión con el bloque que lo rodea no se note:
  1. Ganancia por canal (en lineal) para que el color medio del bloque sea el de la textura vecina
     en la misma zona (recorte de --parent-albedo sobre --parent-rect).
  2. En los últimos --feather metros hacia el borde se funde con esa textura vecina (smoothstep),
     así en el borde mismo la imagen es la del bloque que lo rodea.

Uso (venv de TIFFdetallado: numpy, rasterio, Pillow):
  python Tools/build_detail_texture.py --config Tools/mosaic_blocks.json --block GaleRuta
         --cog <url o ruta> --res 2 --parent-albedo Assets/Gale/Textures/Gale_Albedo.png --out <png/jpg>
"""
import argparse, json, os
import numpy as np
import rasterio
from rasterio.windows import from_bounds
from rasterio.enums import Resampling
from PIL import Image

Image.MAX_IMAGE_PIXELS = None
COG = "/vsicurl/https://asc-pds-services.s3.us-west-2.amazonaws.com/mosaic/Mars/MSL/MSL_Gale_HiRISE-LRGB_78quads_sharp_cog.tif"


def to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True)
    ap.add_argument("--block", required=True)
    ap.add_argument("--cog", default=COG)
    ap.add_argument("--res", type=float, default=2.0, help="m/px de la textura")
    ap.add_argument("--parent-albedo", required=True)
    ap.add_argument("--feather", type=float, default=600.0, help="m de fundido hacia la textura vecina")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    cfg = json.load(open(a.config, encoding="utf-8"))
    blk = cfg["blocks"][a.block]
    x0, x1, y0, y1 = blk["rect"]
    px0, px1, py0, py1 = cfg["blocks"][blk["inside"]]["rect"]
    W = int(round((x1 - x0) / a.res)); H = int(round((y1 - y0) / a.res))

    env = dict(GDAL_DISABLE_READDIR_ON_OPEN="EMPTY_DIR", GDAL_HTTP_USERAGENT="ares-map-terrain-tools",
               CPL_VSIL_CURL_ALLOWED_EXTENSIONS=".tif")
    with rasterio.Env(**env), rasterio.open(a.cog) as ds:
        win = from_bounds(x0, y0, x1, y1, ds.transform)
        # Sin boundless: con él rasterio lee a través de un VRT que no usa los overviews del COG
        # (leería a 25 cm/px). El bloque cae dentro del mosaico.
        rgb = ds.read([1, 2, 3], window=win, out_shape=(3, H, W), resampling=Resampling.average)
    img = np.moveaxis(rgb, 0, -1).astype(np.float32) / 255.0
    valid = img.max(-1) > 0.004
    print(f"[detalle] {a.block}: {W}x{H} px a {a.res} m/px, píxeles con imagen {valid.mean() * 100:.1f} %")

    # Textura vecina (la del bloque que lo contiene) remuestreada a la misma rejilla.
    par = Image.open(a.parent_albedo).convert("RGB")
    PW, PH = par.size
    # UV del padre: u = (x - px0)/(px1 - px0); v = 0 abajo (y = py0) -> fila de imagen desde arriba.
    box = ((x0 - px0) / (px1 - px0) * PW, (py1 - y1) / (py1 - py0) * PH,
           (x1 - px0) / (px1 - px0) * PW, (py1 - y0) / (py1 - py0) * PH)
    parent = np.asarray(par.resize((W, H), Image.BICUBIC, box=box), dtype=np.float32) / 255.0

    # Todo en float32 y por franjas: la imagen a 2 m/px tiene ~40 M de píxeles.
    gs, gp = np.zeros(3), np.zeros(3)
    for r0 in range(0, H, 1024):
        sl = slice(r0, min(H, r0 + 1024)); v = valid[sl]
        gs += to_linear(img[sl])[v].sum(0); gp += to_linear(parent[sl])[v].sum(0)
    gain = (gp / np.maximum(gs, 1e-4)).astype(np.float32)
    print("[detalle] ganancia de color (lineal RGB):", gain.round(3))

    # Fundido en los bordes (y donde falte imagen).
    xs = (np.arange(W, dtype=np.float32) + 0.5) * a.res
    dx = np.minimum(xs, W * a.res - xs)
    out8 = np.empty((H, W, 3), dtype=np.uint8)
    seam = []
    for r0 in range(0, H, 1024):
        r1 = min(H, r0 + 1024)
        ys = (np.arange(r0, r1, dtype=np.float32) + 0.5) * a.res
        d = np.minimum(dx[None, :], np.minimum(ys, H * a.res - ys)[:, None])
        t = np.clip(d / a.feather, 0, 1)
        w = (t * t * (3 - 2 * t) * valid[r0:r1]).astype(np.float32)[..., None]
        lin = to_linear(img[r0:r1]).astype(np.float32) * gain
        plin = to_linear(parent[r0:r1]).astype(np.float32)
        # Zonas solo con HiRISE RED (gris, sin color): luminancia de HiRISE con el tono de la textura vecina.
        src = img[r0:r1]
        chroma = src.max(-1) - src.min(-1)
        gray = np.clip((0.045 - chroma) / 0.03, 0, 1)[..., None].astype(np.float32)
        Y = np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
        tint = plin / np.maximum(plin @ Y, 1e-4)[..., None]
        lin = lin * (1 - gray) + (lin @ Y)[..., None] * tint * gray
        out = lin * w + plin * (1 - w)
        if r0 == 0: seam.append(np.abs(out[0] - plin[0]).mean())
        if r1 == H: seam.append(np.abs(out[-1] - plin[-1]).mean())
        out8[r0:r1] = (to_srgb(out) * 255 + 0.5).astype(np.uint8)
    im = Image.fromarray(out8, "RGB")
    if a.out.lower().endswith((".jpg", ".jpeg")):
        im.save(a.out, quality=92, subsampling=0)
    else:
        im.save(a.out)
    print(f"[detalle] escrito {a.out} ({os.path.getsize(a.out) / 1e6:.1f} MB); "
          f"diferencia con el vecino en el borde: {float(np.mean(seam)):.4f}")


if __name__ == "__main__":
    main()
