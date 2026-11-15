"""
Importa el conjunto de datos geológicos "gale_ARES_v3_zonas" (equipo Salomonsters) al proyecto:

  Assets/Gale/Textures/Geo/Geo_GeologiaIndice.png   unidades USGS (Tanaka et al. 2014), valor = unidad 1..6
  Assets/Gale/Textures/Geo/Geo_SueloIndice.png      material del suelo (THEMIS), valor = clase 1..4
  Assets/Gale/Textures/Geo/Geo_Termofisico16.png    índice termofísico continuo, 16 bit
  Assets/Gale/Data/geo_dataset.json                 capas (clases, colores, textos), zonas y mineralogía

Solo se usa la carpeta zona_0_regional_completa: las demás zonas son recortes de la misma rejilla
(162,8 m/px), así que una textura por capa cubre todo el mosaico. De cada zona se guardan sus
límites y porcentajes, que el HUD muestra según dónde esté el rover.

Las imágenes están en equirectangular lon/lat (teselas de Mars Trek), igual que el terreno del
proyecto (MarsGeo: X = R·lon). Por eso se georreferencian con lon/lat y NO con los unity_X/Z del
zip, que llevan el factor cos(lat) de otro sistema (0,9968: ~1,8 km de error en el ancho total).

Los colores de la geología son los del zip. Los del suelo se cambian por una rampa con cambio de
tono (arena -> naranja -> ladrillo -> azul pizarra): la del zip es toda marrón y sobre el terreno
sombreado no se distinguía. Se editan aquí o en el JSON; la leyenda y el mapa leen el mismo dato.

Uso (solo biblioteca estándar):
  python -I Tools/import_geo_dataset.py <carpeta gale_ARES_v3_zonas>
"""
import csv, filecmp, json, os, shutil, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
TEX_DIR = os.path.join(ROOT, "Assets", "Gale", "Textures", "Geo")
DATA_DIR = os.path.join(ROOT, "Assets", "Gale", "Data")
REGIONAL = "zona_0_regional_completa"

TEXTURES = {
    "geologia_indice.png": "Geo_GeologiaIndice.png",
    "material_suelo_indice.png": "Geo_SueloIndice.png",
    "indice_termofisico_16bit.png": "Geo_Termofisico16.png",
}

# Nombre corto para la leyenda (el largo del CSV va en la descripción) y color del zip.
GEOLOGIA = {
    "mNh": ("Tierras altas del Noeico medio", "#C78C5A"),
    "lNh": ("Tierras altas del Noeico tardío", "#DEB478"),
    "HNt": ("Transición Hespérico-Noeico", "#A6AC8C"),
    "eHt": ("Transición del Hespérico temprano", "#96BEAA"),
    "Htu": ("Hespérico indiferenciado (monte Sharp)", "#F0D678"),
    "AHi": ("Impacto Amazónico-Hespérico (cráteres)", "#C878A0"),
}

# Clases de material del suelo: nombre, nota para el recorrido (de CapaInfoZona.cs del zip) y color.
SUELO = [
    ("Polvo / arena fina", "Inercia térmica baja: terreno blando.", "#EBD98A"),
    ("Regolito / arena mezclada", "", "#D99A4E"),
    ("Superficie consolidada / costra / grava", "", "#A8503C"),
    ("Roca expuesta / afloramiento", "Inercia térmica alta.", "#3F4A66"),
]

# En el zip "Albedo TES" vale 1,0 en todas las zonas (rampa de color saturada): no aporta nada.
TES_OMITIR = {"Albedo TES"}
# Los nombres de los índices vienen sin tildes en resumen_zonas.json.
TES_NOMBRES = {
    "Inercia termica TES": "Inercia térmica TES",
    "Indice de polvo TES": "Índice de polvo TES",
}

# Nombre de cada zona para el HUD (tabla de LEEME_contenido.md).
ZONAS = {
    "zona_0_regional_completa": "Región completa",
    "zona_1_bradbury": "Bradbury Landing",
    "zona_2_gale_completo": "Gale completo",
    "zona_4_babati_norte": "Babati – sector norte",
    "zona_6_sur_farah_vallis": "Sector sur – Farah Vallis",
    "zona_7_este_noreste": "Franja este-noreste",
    "zona_9_oeste_robert_sharp": "Franja oeste – Robert Sharp",
}


def read_csv(path, ncols):
    rows = []
    with open(path, encoding="utf-8-sig", newline="") as f:
        r = csv.reader(f)
        next(r)
        for row in r:
            if not row:
                continue
            if len(row) > ncols:   # comillas sin escapar en el último campo: se reúne
                row = row[:ncols - 1] + [",".join(row[ncols - 1:])]
            rows.append([" ".join(c.replace('"', "").split()) for c in row])
    return rows


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    src = os.path.abspath(sys.argv[1])
    os.makedirs(TEX_DIR, exist_ok=True)
    os.makedirs(DATA_DIR, exist_ok=True)
    for name, out in TEXTURES.items():
        a, b = os.path.join(src, REGIONAL, name), os.path.join(TEX_DIR, out)
        if os.path.exists(b) and filecmp.cmp(a, b, shallow=False):
            print("textura", out, "(sin cambios)")   # no se toca: Unity no la reimporta
            continue
        shutil.copyfile(a, b)
        print("textura", out)

    resumen = json.load(open(os.path.join(src, "resumen_zonas.json"), encoding="utf-8"))
    zonas_src = resumen["resumen"]
    suelo_keys = [resumen["material_clases"][str(i)] for i in range(1, 5)]
    geo_codes = [resumen["geologia_clases"][str(i)] for i in range(1, 7)]

    geo_rows = read_csv(os.path.join(src, "tabla_geologia_unidades.csv"), 8)
    geo_clases = []
    for idx, code, nombre_es, material, grupo, nombre_en, caract, interp in geo_rows:
        corto, color = GEOLOGIA[code]
        geo_clases.append({"indice": int(idx), "codigo": code, "nombre": corto, "nombreLargo": nombre_es,
                           "descripcion": material, "nota": "", "color": color,
                           "grupo": grupo, "nombreEn": nombre_en, "caracteristicasEn": caract,
                           "interpretacionEn": interp})
    assert [c["codigo"] for c in geo_clases] == geo_codes, "el orden del CSV no es el de geologia_indice.png"

    suelo_clases = [{"indice": i + 1, "codigo": "", "nombre": n, "nombreLargo": suelo_keys[i], "descripcion": "",
                     "nota": nota, "color": color, "grupo": "", "nombreEn": "", "caracteristicasEn": "",
                     "interpretacionEn": ""} for i, (n, nota, color) in enumerate(SUELO)]

    reg = zonas_src[REGIONAL]
    lim = reg["limites"]
    region = {"lonOeste": lim["lon_oeste"], "lonEste": lim["lon_este"], "latSur": lim["lat_sur"],
              "latNorte": lim["lat_norte"], "ancho": reg["pixeles"][0], "alto": reg["pixeles"][1],
              "metrosPorPixel": reg["metros_por_pixel"]}

    zonas = []
    for zid, z in zonas_src.items():
        l = z["limites"]
        zonas.append({
            "id": zid, "nombre": ZONAS.get(zid, z["nombre"]),
            "lonOeste": l["lon_oeste"], "lonEste": l["lon_este"], "latSur": l["lat_sur"], "latNorte": l["lat_norte"],
            "anchoKm": round(l["ancho_m"] / 1000.0, 1), "altoKm": round(l["alto_m"] / 1000.0, 1),
            "sueloPct": [z["material_pct"].get(k, 0.0) for k in suelo_keys],
            "geologiaPct": [z["geologia_pct"].get(k, 0.0) for k in geo_codes],
            "tes": [{"nombre": TES_NOMBRES.get(k, k), "valor": v}
                    for k, v in z["tes_indice_relativo_medio"].items() if k not in TES_OMITIR],
        })

    mineralogia = []
    for zonas_txt, sitio, tipo, minerales, notas, instrumento, fuente in read_csv(
            os.path.join(src, "tabla_mineralogia_curiosity_gale.csv"), 7):
        mineralogia.append({"zonas": [s.strip() for s in zonas_txt.split(";")], "sitio": sitio, "tipo": tipo,
                            "mineralogia": minerales, "notas": notas, "instrumento": instrumento, "fuente": fuente})

    data = {
        "fuente": "gale_ARES_v3_zonas (Salomonsters): NASA Mars Trek (THEMIS, TES, MOLA) y USGS SIM 3292",
        "region": region,
        "capas": [
            {"id": "geologia", "nombre": "Geología", "titulo": "Unidades geológicas",
             "fuente": "USGS SIM 3292 (Tanaka et al. 2014), escala 1:20 000 000",
             "aviso": "Mapa global: vale para la escala regional, no para detalle de metros.",
             "textura": TEXTURES["geologia_indice.png"], "contactos": True, "clases": geo_clases},
            {"id": "suelo", "nombre": "Material del suelo", "titulo": "Material del suelo",
             "fuente": "THEMIS infrarrojo día y noche (Mars Odyssey), ~163 m/px",
             "aviso": "Clases relativas dentro de la región (percentiles), no inercia térmica calibrada.",
             "textura": TEXTURES["material_suelo_indice.png"], "contactos": False, "clases": suelo_clases},
        ],
        "termofisico": TEXTURES["indice_termofisico_16bit.png"],
        "zonas": zonas,
        "mineralogia": mineralogia,
    }
    out = os.path.join(DATA_DIR, "geo_dataset.json")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=1, ensure_ascii=False)
        f.write("\n")
    print("datos", os.path.relpath(out, ROOT), "-", len(zonas), "zonas,", len(mineralogia), "sitios de mineralogía")
    for m in mineralogia:
        print("  ", m["zonas"], "|", m["sitio"], "|", m["fuente"][:70])


if __name__ == "__main__":
    main()
