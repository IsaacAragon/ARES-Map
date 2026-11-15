"""
Builds the Gale terrain textures from real orbital imagery (USGS Astrogeology WMS, public domain):
  - MDIM21_color : Viking MDIM 2.1 colorized mosaic (~232 m/px)  -> regional colour
  - THEMIS       : THEMIS day IR controlled mosaic (~100 m/px)   -> fine detail
Both are requested for exactly the DXF point extent, so UV 0..1 of the TIN matches the image.

Albedo = Mars palette driven by Viking brightness (+30 % of Viking's own chroma)
         x THEMIS high-pass detail (compressed, because THEMIS has the sun shading baked in
           and Unity adds its own lighting).

Also converts an ambientCG CC0 material (Rocks015) into a URP Lit detail map:
  grey albedo with mean 0.5 (URP multiplies detail x2) and the OpenGL normal map.

Usage (venv with numpy + Pillow):
  python -I Tools/build_gale_textures.py <wms_dir> <ambientcg_dir> <out_dir>
"""
import sys, os
import numpy as np
from PIL import Image, ImageFilter

wms, acg, out = sys.argv[1:4]
os.makedirs(out, exist_ok=True)


def load(path, mode):
    return np.asarray(Image.open(path).convert(mode), dtype=np.float32) / 255.0


def blur(arr, r):
    if arr.ndim == 2:
        im = Image.fromarray(np.clip(arr * 255, 0, 255).astype(np.uint8))
        return np.asarray(im.filter(ImageFilter.GaussianBlur(r)), dtype=np.float32) / 255.0
    return np.stack([blur(arr[..., i], r) for i in range(arr.shape[2])], -1)


def to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


viking = load(os.path.join(wms, "MDIM21_color.png"), "RGB")
themis = load(os.path.join(wms, "THEMIS.png"), "L")
assert viking.shape[:2] == themis.shape, (viking.shape, themis.shape)

# Regional colour: blur Viking to drop its baked hillshade, take brightness.
vk = to_linear(blur(viking, 10))
lum = vk @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
lo, hi = np.percentile(lum, [2, 98])
t = np.clip((lum - lo) / (hi - lo), 0, 1)

# Mars palette (sRGB), from rover/orbiter true-colour images: dark basaltic sand -> ochre dust -> pale layered rock.
stops = np.array([0.0, 0.45, 1.0], dtype=np.float32)
cols = to_linear(np.array([[0.30, 0.20, 0.15], [0.60, 0.40, 0.27], [0.80, 0.63, 0.47]], dtype=np.float32))
pal = np.stack([np.interp(t, stops, cols[:, i]) for i in range(3)], -1)

# Keep a bit of Viking's own chroma (dark dunes, bright mound), normalised to the palette brightness.
vk_norm = vk / np.maximum(lum[..., None], 1e-4) * (pal @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32))[..., None]
vk_norm[..., 2] *= 0.8  # Viking colorisation leaves a blue cast on the crater floor
base = pal * 0.7 + vk_norm * 0.3

# THEMIS detail: ratio to its own low-pass, compressed.
th_low = blur(themis, 14)
detail = np.clip(themis / np.maximum(th_low, 1e-3), 0.3, 2.5) ** 0.55
# Large-scale THEMIS brightness also helps (albedo units on the mound); mix gently.
th_reg = th_low / max(th_low.mean(), 1e-3)
albedo = base * detail[..., None] * (0.85 + 0.15 * th_reg[..., None])

img = Image.fromarray((to_srgb(albedo) * 255 + 0.5).astype(np.uint8), "RGB")
img.save(os.path.join(out, "Gale_Albedo.png"))
print("[tex] Gale_Albedo.png", img.size, "mean sRGB", np.asarray(img).reshape(-1, 3).mean(0).round(1))

# ---- detail map from ambientCG Rocks015 (CC0)
name = "Rocks015_2K-JPG"
col = load(os.path.join(acg, f"{name}_Color.jpg"), "RGB")
g = to_linear(col) @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
g = to_srgb(g)
g = np.clip(0.5 + (g - g.mean()) * 0.9, 0, 1)   # mean 0.5 -> neutral in URP's x2 detail blend
Image.fromarray((g * 255 + 0.5).astype(np.uint8), "L").convert("RGB").save(os.path.join(out, "Detail_Rocks015_Albedo.png"))
Image.open(os.path.join(acg, f"{name}_NormalGL.jpg")).save(os.path.join(out, "Detail_Rocks015_Normal.png"))
print("[tex] detail maps written, grey mean", round(float(g.mean()), 3))

with open(os.path.join(out, "SOURCES.txt"), "w", encoding="utf-8") as f:
    f.write(
        "Gale_Albedo.png: derived from USGS Astrogeology WMS (planetarymaps.usgs.gov, mars_simp_cyl.map),\n"
        "  layers MDIM21_color (Viking MDIM 2.1 colorized, NASA/USGS, public domain) and THEMIS (THEMIS day IR, ASU/NASA).\n"
        "  BBOX lon 136.11621087545228..139.48347084890932, lat -7.082183679637354..-3.7149237061972213 (DXF point extent, R=3396190 m).\n"
        "Detail_Rocks015_*: ambientCG Rocks015 (CC0), https://ambientcg.com/view?id=Rocks015\n")
