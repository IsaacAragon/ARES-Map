"""
Textura de un bloque del mosaico de terreno con el detalle real del mosaico CTX (MRO, ~5-10 m/px) sobre el color
regional de Viking/THEMIS que ya usaba el bloque (~97 m/px).

  color  = textura base del bloque (Viking MDIM 2.1 + THEMIS, Tools/build_gale_textures.py), remuestreada a la
           resolución de salida con bicúbica: da el tono y el color regional
  detalle = luminancia CTX / su propio filtro paso bajo (sigma --sigma m), elevada a --gamma: el contraste de las
           estructuras de decenas a cientos de metros (capas, dunas, ondulaciones, cantos de cráter, rocas grandes);
           queda 1 donde el CTX no tiene imagen
  albedo = color * detalle (en lineal), como la receta de la textura base pero con 6-8x más resolución.

El CTX se toma de las teselas descargadas por Tools/fetch_ctx.py (nivel 11 = 10.2 m/px). Se pide un margen de
--margin px alrededor del bloque para que el filtro paso bajo tenga contexto real en los bordes: así los bloques
vecinos, que comparten el borde, no muestran juntura.

La resolución de salida es la del lado largo del bloque / --max-px (máx. 16384). UV 0..1 = el rectángulo del bloque,
v = 0 al sur (fila inferior de la imagen), igual que las texturas anteriores.

Uso (venv de TIFFdetallado: numpy, Pillow):
  python Tools/build_ctx_texture.py --config Tools/mosaic_blocks.json --block Gale --cache <CTX_cache>
         --base Tools/data/albedo_base/Gale_Albedo.png --max-px 12288 --out Assets/Gale/Textures/Gale_Albedo.jpg
Crédito: NASA/JPL/MSSS/Caltech Murray Lab (mosaico CTX global, Dickson et al.); servicio teselado de Esri.
"""
import argparse, json, math, os, time
import numpy as np
from PIL import Image, ImageFile

Image.MAX_IMAGE_PIXELS = None
ImageFile.LOAD_TRUNCATED_IMAGES = True   # el servicio entrega ~1 % de teselas truncadas por el final: se usa lo que traen
R = 3396190.0
TILE = 512


def to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


class CtxSource:
    """Mosaico CTX virtual sobre las teselas en caché; cada pedido de una franja de filas arma solo lo necesario."""

    def __init__(self, cache, lod):
        self.dir = os.path.join(cache, f"L{lod}")
        self.res = 0.3515625 / (2 ** lod)  # grados por píxel
        self.mpp = math.radians(self.res) * R
        self.lod = lod
        self.missing = 0

    def gx(self, x):  # X proyecto (m) -> columna continua del mosaico global
        return (math.degrees(x / R) + 180) / self.res

    def gy(self, y):  # Y proyecto (m) -> fila continua (0 = polo norte)
        return (90 - math.degrees(y / R)) / self.res

    def strip(self, c0, c1, r0, r1):
        """Píxeles globales [c0,c1) x [r0,r1) como uint8 (0 = sin imagen)."""
        out = np.zeros((r1 - r0, c1 - c0), dtype=np.uint8)
        for tr in range(r0 // TILE, (r1 - 1) // TILE + 1):
            for tc in range(c0 // TILE, (c1 - 1) // TILE + 1):
                p = os.path.join(self.dir, f"{tr}_{tc}.jpg")
                if not os.path.exists(p):
                    self.missing += 1
                    continue
                t = np.asarray(Image.open(p).convert("L"))
                a0, a1 = max(c0, tc * TILE), min(c1, (tc + 1) * TILE)
                b0, b1 = max(r0, tr * TILE), min(r1, (tr + 1) * TILE)
                out[b0 - r0:b1 - r0, a0 - c0:a1 - c0] = t[b0 - tr * TILE:b1 - tr * TILE, a0 - tc * TILE:a1 - tc * TILE]
        return out


def gauss_fft(a, sigma):
    """Filtro gaussiano por FFT (reflejando los bordes), para no depender de scipy."""
    pad = int(3 * sigma) + 1
    b = np.pad(a, pad, mode="reflect")
    fy = np.fft.fftfreq(b.shape[0])[:, None]; fx = np.fft.rfftfreq(b.shape[1])[None, :]
    k = np.exp(-2 * (math.pi * sigma) ** 2 * (fx ** 2 + fy ** 2))
    return np.fft.irfft2(np.fft.rfft2(b) * k, s=b.shape)[pad:-pad, pad:-pad].astype(np.float32)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True)
    ap.add_argument("--block", required=True)
    ap.add_argument("--cache", required=True)
    ap.add_argument("--lod", type=int, default=11)
    ap.add_argument("--base", required=True, help="textura base del bloque (Viking+THEMIS, UV = rectángulo del bloque)")
    ap.add_argument("--base-rect", type=float, nargs=4, metavar=("X0", "X1", "Y0", "Y1"),
                    help="rectángulo que cubre la textura base (por defecto, el del bloque); para pruebas")
    ap.add_argument("--max-px", type=int, default=12288)
    ap.add_argument("--res", type=float, help="m/px de salida (en vez de --max-px)")
    ap.add_argument("--size", type=int, nargs=2, metavar=("W", "H"),
                    help="tamaño exacto en px. Usar potencias de 2: Unity no comprime (DXT/BC7) las texturas NPOT sin reescalarlas")
    ap.add_argument("--sigma", type=float, default=250.0, help="m del paso bajo con el que se saca el detalle")
    ap.add_argument("--gamma", type=float, default=0.9)
    ap.add_argument("--clamp", type=float, nargs=2, default=(0.45, 2.0), help="límites del cociente de detalle")
    ap.add_argument("--margin", type=int, default=96, help="px de contexto alrededor del bloque")
    ap.add_argument("--quality", type=int, default=88)
    ap.add_argument("--crop", type=float, nargs=3, metavar=("X", "Y", "M"),
                    help="solo para revisar: guarda el recorte de M m de lado centrado en (X,Y) del proyecto")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    t0 = time.time()
    cfg = json.load(open(a.config, encoding="utf-8"))
    x0, x1, y0, y1 = cfg["blocks"][a.block]["rect"]
    wm, hm = x1 - x0, y1 - y0
    res = a.res if a.res else max(wm, hm) / a.max_px
    # Múltiplos de 4: las texturas comprimidas por bloques (DXT/BC7) lo exigen; el UV 0..1 sigue siendo el bloque entero.
    W, H = max(4, int(round(wm / res / 4)) * 4), max(4, int(round(hm / res / 4)) * 4)
    if a.size:
        W, H = a.size
    resx, resy = wm / W, hm / H
    m = a.margin
    EW, EH = W + 2 * m, H + 2 * m
    print(f"[ctx] {a.block}: {W}x{H} px a {resx:.1f} x {resy:.1f} m/px (CTX nivel {a.lod})", flush=True)

    src = CtxSource(a.cache, a.lod)
    # Luminancia CTX en la rejilla de salida ampliada con el margen (uint8).
    L = np.zeros((EH, EW), dtype=np.uint8)
    step = 1024
    ex0, ey1 = x0 - m * resx, y1 + m * resy     # esquina noroeste de la rejilla ampliada
    for r0 in range(0, EH, step):
        r1 = min(EH, r0 + step)
        ya, yb = ey1 - r0 * resy, ey1 - r1 * resy
        gl, gr = src.gx(ex0), src.gx(ex0 + EW * resx)
        gt, gb = src.gy(ya), src.gy(yb)
        c0, c1 = int(math.floor(gl)) - 2, int(math.ceil(gr)) + 2
        t0r, t1r = int(math.floor(gt)) - 2, int(math.ceil(gb)) + 2
        s = Image.fromarray(src.strip(c0, c1, t0r, t1r), "L")
        L[r0:r1] = np.asarray(s.resize((EW, r1 - r0), Image.LANCZOS, box=(gl - c0, gt - t0r, gr - c0, gb - t0r)))
        print(f"[ctx]   CTX filas {r1}/{EH} ({time.time() - t0:.0f}s)", flush=True)
    if src.missing:
        print(f"[ctx] AVISO: {src.missing} teselas ausentes (se tratan como sin imagen)")
    valid = L > 2
    print(f"[ctx] píxeles con imagen CTX: {valid.mean() * 100:.1f} %")

    # Paso bajo (sigma en px de salida) calculado a 1/8 de resolución.
    f = 8
    sm = np.asarray(Image.fromarray(L, "L").reduce(f), dtype=np.float32)
    vm = np.asarray(Image.fromarray((valid * 255).astype(np.uint8), "L").reduce(f), dtype=np.float32) / 255.0
    sig = a.sigma / (0.5 * (resx + resy)) / f
    low_s = gauss_fft(sm * vm, sig) / np.maximum(gauss_fft(vm, sig), 1e-3)   # media ponderada por la máscara
    low_img = Image.fromarray(low_s, "F")
    del sm, vm

    # Textura base remuestreada por franjas y producto con el detalle.
    base = Image.open(a.base).convert("RGB")
    BW, BH = base.size
    bx0, bx1, by0, by1 = a.base_rect or (x0, x1, y0, y1)
    out = np.empty((H, W, 3), dtype=np.uint8)
    lo, hi = a.clamp
    for r0 in range(0, H, 512):
        r1 = min(H, r0 + 512)
        ya, yb = y1 - r0 * resy, y1 - r1 * resy
        b = base.resize((W, r1 - r0), Image.BICUBIC, box=((x0 - bx0) / (bx1 - bx0) * BW, (by1 - ya) / (by1 - by0) * BH,
                                                          (x1 - bx0) / (bx1 - bx0) * BW, (by1 - yb) / (by1 - by0) * BH))
        lin = to_linear(np.asarray(b, dtype=np.float32) / 255.0)
        Ls = L[m + r0:m + r1, m:m + W].astype(np.float32)
        lw = np.asarray(low_img.resize((W, r1 - r0), Image.BICUBIC,
                                       box=(m / f, (m + r0) / f, (m + W) / f, (m + r1) / f)), dtype=np.float32)
        v = valid[m + r0:m + r1, m:m + W]
        ratio = np.where(v, Ls / np.maximum(lw, 1.0), 1.0)
        ratio = np.clip(ratio, lo, hi) ** a.gamma
        out[r0:r1] = (to_srgb(lin * ratio[..., None]) * 255 + 0.5).astype(np.uint8)
    del L, valid

    im = Image.frombuffer("RGB", (W, H), out, "raw", "RGB", 0, 1)   # comparte la memoria, sin copia
    if a.crop:
        cx, cy, cm = a.crop
        px, py = (cx - x0) / resx, (y1 - cy) / resy
        h = cm / resx / 2
        im.crop((int(px - h), int(py - h), int(px + h), int(py + h))).save(a.out)
        print("[ctx] recorte escrito", a.out)
        return
    if a.out.lower().endswith((".jpg", ".jpeg")):
        im.save(a.out, quality=a.quality, subsampling=2, optimize=True)
    else:
        im.save(a.out)
    mean = out.reshape(-1, 3)[::97].mean(0)
    print(f"[ctx] escrito {a.out} ({os.path.getsize(a.out) / 1e6:.1f} MB), sRGB medio {mean.round(1)}, {time.time() - t0:.0f}s")


if __name__ == "__main__":
    main()
