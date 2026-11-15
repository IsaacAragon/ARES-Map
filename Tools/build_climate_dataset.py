"""
Clima y radiación de Gale para ARES Map, siempre con el dato más actual:

  MCD  Mars Climate Database v6.2 (LMD/OU/IAA/ESA/CNES), interfaz web mcdcgi.py.
       Es un MODELO climático: da el clima típico de esa estación, hora y lugar, no el tiempo medido.
       Se piden el sol de hoy y el de mañana de Curiosity, hora local por hora local.
  RAD  Dosimetría de Curiosity (MSL RAD) en el servidor HAPI de NASA CCMC/ISWA. Son medidas reales,
       tomadas donde está el rover, y llegan con ~1 día de retraso. Se guardan las últimas 48 h.

Salida: Assets/Gale/Data/climate_dataset.json (lo lee Assets/Gale/Scripts/ClimateDataset.cs).
Nada se consulta en vivo durante la demo: se vuelve a ejecutar este script antes de cada una.
El LMD pide uso moderado de su web: hay caché en disco y una pausa entre llamadas no cacheadas.

Cómo funciona el MCD aquí (ver Tools/climate_api_notes.md):
  - Con fecha marciana (datekeyhtml=1, ls=<Ls>, localtime=all) la página no trae valores sino un
    enlace a ../txt/<hash>.txt con el ciclo diario (hora local 0..24) de cada variable pedida.
    Son 2 peticiones por cada 4 variables y día, en vez de 24.
  - Con fecha terrestre (datekeyhtml=0) hay que mandar julian; los valores salen en <li>.
    Se usa para sacar Ls y la hora solar verdadera en el mediodía de cada sol.

Uso (solo biblioteca estándar):
  python -I Tools/build_climate_dataset.py [--cache DIR] [--now 2026-11-15T12:00:00Z]
  python -I Tools/build_climate_dataset.py probe [--cache DIR]     # batería de pruebas, imprime resultados
"""
import argparse, datetime as dt, hashlib, json, math, os, re, sys, time, urllib.error, urllib.parse, urllib.request

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
OUT = os.path.join(ROOT, "Assets", "Gale", "Data", "climate_dataset.json")
WAYPOINTS = os.path.join(ROOT, "Assets", "Gale", "Route", "MSL_waypoints.csv")
DEFAULT_CACHE = os.path.join(os.path.expanduser("~"), ".cache", "ares-map", "climate")

MCD_CGI = "https://www-mars.lmd.jussieu.fr/mcd_python/cgi-bin/mcdcgi.py"
MCD_BASE = "https://www-mars.lmd.jussieu.fr/mcd_python/"
HAPI = "https://iswa.ccmc.gsfc.nasa.gov/hapi"
RAD_ID = "msl_rad_dosimetry"
UA = "ares-map-climate-tools"
MCD_PAUSE_S = 2.0

BRADBURY = ("Bradbury (aterrizaje)", -4.5895, 137.4416)

# Escenarios de polvo del formulario (select "dust").
DUST = {1: "climatología, sol medio", 2: "climatología, sol mínimo", 3: "climatología, sol máximo",
        4: "tormenta de polvo, sol mínimo", 5: "tormenta de polvo, sol medio", 6: "tormenta de polvo, sol máximo",
        7: "cálido (con polvo, sol máximo)", 8: "frío (poco polvo, sol mínimo)"}
DUST_NORMAL, DUST_POLVO, DUST_TORMENTA = 1, 7, 5
# Los escenarios de tormenta (4-6) solo existen en la estación de tormentas, 180 < Ls < 360: fuera de ella
# el MCD los rechaza y se usa el 7 (cálido, con polvo) como "día con más polvo".

# ---------------------------------------------------------------- tiempo marciano
# Mars Sol Date (Allison y McEwen 2000, Mars24). TT-UTC = 69,184 s desde 2017.
TT_UTC_S = 69.184
MSL_SOL0_MSD = 49269  # sol 0 de Curiosity: floor(MSD + lon/360) en el aterrizaje (2012-08-06 05:17:57 UTC)
MSL_LON = 137.4417


def jd_utc(t):
    return t.timestamp() / 86400.0 + 2440587.5


def msd(t):
    jd_tt = jd_utc(t) + TT_UTC_S / 86400.0
    return (jd_tt - 2451549.5) / 1.0274912517 + 44796.0 - 0.0009626


def utc_from_msd(m):
    jd_tt = (m - 44796.0 + 0.0009626) * 1.0274912517 + 2451549.5
    unix = (jd_tt - TT_UTC_S / 86400.0 - 2440587.5) * 86400.0
    return dt.datetime.fromtimestamp(unix, dt.timezone.utc)


def curiosity_sol(t, lon=MSL_LON):
    return int(math.floor(msd(t) + lon / 360.0)) - MSL_SOL0_MSD


def lmst_hours(t, lon):
    """Hora solar media local (LMST) en horas marcianas."""
    return ((msd(t) + lon / 360.0) % 1.0) * 24.0


def utc_of_local(sol, hour, lon):
    """UTC en que la hora solar MEDIA local vale `hour` en ese sol de Curiosity."""
    return utc_from_msd(sol + MSL_SOL0_MSD + hour / 24.0 - lon / 360.0)


def iso(t):
    return t.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def parse_iso(s):
    return dt.datetime.strptime(s.strip(), "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=dt.timezone.utc)

# ---------------------------------------------------------------- HTTP con caché


class Http:
    def __init__(self, cache, offline=False):
        self.cache = cache
        self.offline = offline
        self.last_mcd = 0.0
        self.calls = 0
        os.makedirs(cache, exist_ok=True)

    def get(self, url, ttl_s=None, polite=False):
        """GET con caché por URL. ttl_s=None: el resultado no caduca (MCD es determinista)."""
        key = hashlib.sha1(url.encode()).hexdigest()[:20]
        path = os.path.join(self.cache, key)
        if os.path.exists(path) and (ttl_s is None or time.time() - os.path.getmtime(path) < ttl_s):
            with open(path, "rb") as f:
                return f.read().decode("utf-8", "replace")
        if self.offline:
            raise RuntimeError(f"sin caché y en modo offline: {url}")
        if polite:
            wait = MCD_PAUSE_S - (time.time() - self.last_mcd)
            if wait > 0:
                time.sleep(wait)
        t0 = time.time()
        req = urllib.request.Request(url, headers={"User-Agent": UA})
        for k in range(5):
            try:
                data = urllib.request.urlopen(req, timeout=120).read()
                break
            except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
                if isinstance(e, urllib.error.HTTPError) and e.code < 500:
                    raise
                if k == 4:
                    raise
                wait = 5 * 2 ** k
                print(f"  (reintento en {wait} s: {e})", file=sys.stderr)
                time.sleep(wait)
        self.calls += 1
        if polite:
            self.last_mcd = time.time()
        with open(path + ".part", "wb") as f:
            f.write(data)
        os.replace(path + ".part", path)
        with open(path + ".url", "w", encoding="utf-8") as f:
            f.write(f"{url}\n{time.time() - t0:.2f} s\n")
        return data.decode("utf-8", "replace")

# ---------------------------------------------------------------- MCD


MCD_FIXED = dict(hrkey=1, spacecraft="none", isfixedlt="off", averaging="off", animation="off",
                 animframes=12, dpi=80, islog="off", colorm="jet", proj="cyl")


def mcd_vars(vs):
    vs = list(vs) + ["none"] * (4 - len(vs))
    assert len(vs) == 4
    return {f"var{i + 1}": v for i, v in enumerate(vs)}


def mcd_url(params):
    q = dict(MCD_FIXED)
    q.update(params)
    return MCD_CGI + "?" + urllib.parse.urlencode(q)


LI_RE = re.compile(r"<li>\s*(.+?)\s*\.{3,}\s*([-+0-9.eE]+|nan)\s*</li>", re.S)


def mcd_point(http, vs, lat, lon, t, dust=DUST_NORMAL, alt=2.0, zkey=3):
    """Valores instantáneos para una fecha terrestre. Devuelve {var: (etiqueta, valor)} y la cabecera."""
    params = mcd_vars(vs)
    params.update(datekeyhtml=0, year=t.year, month=t.month, day=t.day, hours=t.hour, minutes=t.minute,
                  seconds=t.second, julian=f"{jd_utc(t):.6f}", latitude=lat, longitude=lon,
                  altitude=alt, zkey=zkey, dust=dust, localtime="0.", ls="")
    html = mcd_http(http, params)
    items = LI_RE.findall(html)
    if not items:
        raise RuntimeError("el MCD no devolvió valores (¿coordenadas fuera de rango?)")
    out = {}
    for v, (label, val) in zip([v for v in vs if v != "none"], items):
        out[v] = (label.strip(), float(val))
    return out, mcd_header(html)


def mcd_sweep(http, vs, lat, lon, ls, dust=DUST_NORMAL, alt=2.0, zkey=3):
    """Ciclo diario (hora solar verdadera 0..24) para un Ls. Devuelve {var: (etiqueta, [(h, v)...])}."""
    params = mcd_vars(vs)
    params.update(datekeyhtml=1, ls=f"{ls:.3f}", localtime="all", year="", month="", day="", hours="",
                  minutes="", seconds="", julian="", latitude=lat, longitude=lon, altitude=alt, zkey=zkey,
                  dust=dust)
    html = mcd_http(http, params)
    m = re.search(r"href='(\.\./txt/[0-9a-f]+\.txt)'", html)
    if not m:
        raise RuntimeError("el MCD no devolvió archivo de texto:\n" + strip_html(html)[-600:])
    txt = http.get(urllib.parse.urljoin(MCD_BASE + "cgi-bin/", m.group(1)), polite=True)
    return parse_sweep_txt(txt, [v for v in vs if v != "none"])


def parse_sweep_txt(txt, vs):
    blocks, cur, label = [], None, None
    for line in txt.splitlines():
        if line.startswith("### Column 2 is"):
            label = line.split("is", 1)[1].strip()
            cur = []
            blocks.append((label, cur))
        elif line.strip() and not line.startswith("#") and cur is not None:
            a, b = line.split()[:2]
            cur.append((float(a), float(b)))
    if len(blocks) != len(vs):
        raise RuntimeError(f"esperaba {len(vs)} bloques y llegaron {len(blocks)}")
    return {v: blk for v, blk in zip(vs, blocks)}


def mcd_http(http, params):
    html = http.get(mcd_url(params), polite=True)
    if "field reference not found" in html:
        raise RuntimeError("variable no reconocida por el MCD (la página se corta en la primera que falla)")
    if "<li>" not in html and "../txt/" not in html:
        raise RuntimeError("respuesta sin datos del MCD:\n" + strip_html(html)[-600:])
    return html


def mcd_header(html):
    m = re.search(r"<hr>\s*(MCD_v[^<]+)<hr>", html, re.S)
    return " ".join(m.group(1).split()) if m else ""


def strip_html(s):
    return re.sub(r"<[^>]+>", "", s)

# ---------------------------------------------------------------- RAD (HAPI)


def hapi_json(http, path, ttl_s=600, **q):
    url = f"{HAPI}/{path}" + ("?" + urllib.parse.urlencode(q) if q else "")
    d = json.loads(http.get(url, ttl_s=ttl_s))
    st = d.get("status", {})
    if st.get("code") != 1200:
        raise RuntimeError(f"HAPI {path}: {st}")
    return d


def rad_info(http):
    return hapi_json(http, "info", id=RAD_ID)


def rad_data(http, t0, t1, params=None, ttl_s=600):
    q = {"id": RAD_ID, "time.min": iso(t0), "time.max": iso(t1), "format": "json"}
    if params:
        q["parameters"] = ",".join(params)
    d = hapi_json(http, "data", ttl_s=ttl_s, **q)
    names = [p["name"] for p in d["parameters"]]
    return [dict(zip(names, row)) for row in d.get("data", [])]


def stats(xs):
    xs = [x for x in xs if x is not None]
    if not xs:
        return None
    return {"media": round(sum(xs) / len(xs), 2), "min": round(min(xs), 2), "max": round(max(xs), 2), "n": len(xs)}

# ---------------------------------------------------------------- ruta


def curiosity_now():
    """Último waypoint de la ruta del proyecto: (sol, lat, lon)."""
    with open(WAYPOINTS, encoding="utf-8") as f:
        rows = [l.strip().split(",") for l in f if l.strip()][1:]
    last = rows[-1]
    return int(last[0]), float(last[4]), float(last[3])

# ---------------------------------------------------------------- dataset


SWEEP_A = ["tsurf", "t", "ps", "fluxsurf_dn_sw"]
SWEEP_B = ["wind", "u", "v", "tauref"]


def build_day(http, sol, lat, lon, dust):
    """Un sol de Curiosity: Ls y hora verdadera al mediodía medio, y ciclo de 24 horas locales."""
    noon = utc_of_local(sol, 12.0, lon)
    pt, header = mcd_point(http, ["ls", "loctime", "marsau", "solzenang"], lat, lon, noon, dust)
    ls = pt["ls"][1]
    eot = pt["loctime"][1] - 12.0  # hora verdadera - hora media (ecuación del tiempo), en horas
    a = mcd_sweep(http, SWEEP_A, lat, lon, ls, dust)
    b = mcd_sweep(http, SWEEP_B, lat, lon, ls, dust)
    horas = []
    for i in range(24):
        h = a["tsurf"][1][i][0]
        wu, wv = b["u"][1][i][1], b["v"][1][i][1]
        horas.append({
            "horaLocal": h,
            "utc": iso(utc_of_local(sol, (h - eot) % 24.0, lon)),
            "tSuelo": round(a["tsurf"][1][i][1], 2),
            "tAire": round(a["t"][1][i][1], 2),
            "presion": round(a["ps"][1][i][1], 2),
            "flujoSolar": round(a["fluxsurf_dn_sw"][1][i][1], 2),
            "viento": round(b["wind"][1][i][1], 2),
            # De dónde viene el viento, grados desde el norte en sentido horario (convención meteorológica).
            "vientoDesde": round((math.degrees(math.atan2(-wu, -wv)) + 360.0) % 360.0, 1),
            "polvo": round(b["tauref"][1][i][1], 3),
        })
    tA = [x["tAire"] for x in horas]
    tS = [x["tSuelo"] for x in horas]
    return {
        "sol": sol,
        "inicioUtc": iso(utc_of_local(sol, 0.0, lon)),
        "finUtc": iso(utc_of_local(sol + 1, 0.0, lon)),
        "ls": round(ls, 3),
        "distanciaSolUA": round(pt["marsau"][1], 4),
        "ecuacionTiempoH": round(eot, 3),
        "escenarioPolvo": dust,
        "escenarioNombre": DUST[dust],
        "resumen": {
            "tAireMin": min(tA), "tAireMax": max(tA), "tSueloMin": min(tS), "tSueloMax": max(tS),
            "presionMedia": round(sum(x["presion"] for x in horas) / 24.0, 1),
            "flujoSolarMax": max(x["flujoSolar"] for x in horas),
            "vientoMax": max(x["viento"] for x in horas),
            "polvo": horas[12]["polvo"],
        },
        "horas": horas,
    }


def storm_or_dust(http, sol, lat, lon):
    """Tormenta de polvo si la estación lo permite (180 < Ls < 360); si no, el escenario cálido con polvo."""
    ls = mcd_point(http, ["ls"], lat, lon, utc_of_local(sol, 12.0, lon))[0]["ls"][1]
    return DUST_TORMENTA if 180.0 < ls < 360.0 else DUST_POLVO


def build(args):
    http = Http(args.cache, offline=args.offline)
    now = parse_iso(args.now) if args.now else dt.datetime.now(dt.timezone.utc).replace(microsecond=0)
    route_sol, lat, lon = curiosity_now()
    sol = curiosity_sol(now)
    print(f"Ahora {iso(now)} = sol {sol} de Curiosity (LMST {lmst_hours(now, MSL_LON):.2f} h); "
          f"último waypoint de la ruta: sol {route_sol}")

    puntos = []
    for nombre, plat, plon in [("Curiosity (último punto de la ruta)", lat, lon), BRADBURY]:
        print(f"MCD {nombre} ({plat:.4f}, {plon:.4f}): soles {sol} y {sol + 1}")
        puntos.append({
            "nombre": nombre, "lat": plat, "lon": plon,
            "hoy": build_day(http, sol, plat, plon, DUST_NORMAL),
            "manana": build_day(http, sol + 1, plat, plon, DUST_NORMAL),
            "hoyPolvo": build_day(http, sol, plat, plon, storm_or_dust(http, sol, plat, plon)),
        })

    info = rad_info(http)
    stop = parse_iso(info["stopDate"])
    rows = rad_data(http, stop - dt.timedelta(hours=48), stop + dt.timedelta(seconds=1))
    cut = stop - dt.timedelta(hours=24)
    last24 = [r for r in rows if parse_iso(r["Time"]) > cut]
    prev24 = [r for r in rows if parse_iso(r["Time"]) <= cut]
    s_now = stats([r["dose_e_total"] for r in last24])
    s_prev = stats([r["dose_e_total"] for r in prev24])
    ultima = rows[-1] if rows else None
    print(f"RAD: última lectura {ultima and ultima['Time']} ({(now - stop).total_seconds() / 3600:.1f} h de retraso), "
          f"{len(rows)} lecturas en 48 h")

    data = {
        "generado": iso(now),
        "solActual": sol,
        "fuentes": {
            "mcd": "Mars Climate Database v6.2 (c) LMD/OU/IAA/ESA/CNES, www-mars.lmd.jussieu.fr/mcd_python",
            "mcdAviso": "Modelo climático: valores típicos de esa estación, hora y lugar, no una medición ni un pronóstico del tiempo real.",
            "rad": "MSL RAD Dosimetry (msl_rad_dosimetry), NASA CCMC ISWA HAPI, iswa.ccmc.gsfc.nasa.gov/hapi",
            "radAviso": "Medidas reales del rover Curiosity en su posición; llegan con ~1 día de retraso.",
        },
        "unidades": {"temperatura": "K", "presion": "Pa", "flujoSolar": "W/m2", "viento": "m/s",
                     "polvo": "profundidad óptica visible (tau)", "dosis": "uGy/dia", "hora": "hora marciana (1/24 de sol)"},
        "escenarios": {"normal": DUST[DUST_NORMAL], "polvo": DUST[DUST_POLVO], "tormenta": DUST[DUST_TORMENTA]},
        "mcd": {"puntos": puntos},
        "rad": {
            "ultimaLectura": ultima["Time"] if ultima else "",
            "ultimaDosisE": ultima["dose_e_total"] if ultima else 0.0,
            "ultimaDosisB": ultima["dose_b_rtg"] if ultima else 0.0,
            "retrasoHoras": round((now - stop).total_seconds() / 3600.0, 1),
            "ultimas24h": s_now,
            "previas24h": s_prev,
            "tendencia": round(s_now["media"] - s_prev["media"], 2) if s_now and s_prev else 0.0,
            "lecturas": [{"utc": r["Time"], "dosisE": r["dose_e_total"], "dosisB": r["dose_b_rtg"],
                          "dosisBBruta": r["dose_b_raw"]} for r in rows],
        },
    }
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
        f.write("\n")
    print(f"Escrito {os.path.relpath(OUT, ROOT)} ({os.path.getsize(OUT) / 1024:.0f} KB), {http.calls} peticiones nuevas")
    for p in puntos:
        for k in ("hoy", "manana", "hoyPolvo"):
            r = p[k]["resumen"]
            print(f"  {p['nombre'][:28]:28s} {k:11s} sol {p[k]['sol']} Ls {p[k]['ls']:7.3f}  "
                  f"aire {r['tAireMin']:.0f}-{r['tAireMax']:.0f} K  suelo {r['tSueloMin']:.0f}-{r['tSueloMax']:.0f} K  "
                  f"p {r['presionMedia']:.0f} Pa  sol máx {r['flujoSolarMax']:.0f} W/m2  tau {r['polvo']:.2f}  "
                  f"viento máx {r['vientoMax']:.1f} m/s")
    if s_now:
        print(f"  RAD 24 h: E {s_now['media']} uGy/día ({s_now['min']}-{s_now['max']}), tendencia {data['rad']['tendencia']:+}")

# ---------------------------------------------------------------- pruebas


def probe(args):
    http = Http(args.cache)
    ok = lambda name, msg: print(f"[ok]   {name}: {msg}")
    bad = lambda name, msg: print(f"[fallo] {name}: {msg}")

    def check(name, fn):
        try:
            ok(name, fn())
        except Exception as e:  # noqa: BLE001 - queremos ver todos los fallos
            bad(name, f"{type(e).__name__}: {e}")

    lat, lon = BRADBURY[1], BRADBURY[2]
    ex = dt.datetime(2026, 11, 15, 12, tzinfo=dt.timezone.utc)

    print("== Tiempo marciano")
    print(f"  JD(2026-11-15 12:00 UTC) = {jd_utc(ex):.4f} (el ejemplo usa 2461360.0)")
    print(f"  sol de Curiosity en el aterrizaje = {curiosity_sol(dt.datetime(2012, 8, 6, 5, 17, 57, tzinfo=dt.timezone.utc))} (debe ser 0)")
    print(f"  sol de Curiosity el 2026-11-15 12:00 UTC = {curiosity_sol(ex)}")

    print("== MCD, fecha terrestre (ejemplo del equipo)")
    check("ejemplo Bradbury", lambda: mcd_point(http, ["tsurf", "t", "ps", "tauref"], -4.59, 137.44, ex)[0])
    check("hora/estación", lambda: mcd_point(http, ["ls", "loctime", "lmeantime", "solzenang"], -4.59, 137.44, ex)[0])
    check("LMST propia vs MCD", lambda: f"LMST calculada {lmst_hours(ex, 137.44):.3f} h")

    print("== MCD, catálogo de variables útiles (valor instantáneo en el ejemplo)")
    groups = [["fluxsurf_dn_sw", "fluxsurf_lw", "fluxtop_dn_sw", "fluxsurf_up_sw"],
              ["wind", "u", "v", "w_l"], ["rho", "dod", "col_h2ovapor", "col_h2oice"],
              ["co2ice", "surf_h2o_ice", "zmax", "wstar_up"], ["rmstsurf", "rmsps", "rmst", "rmsu"],
              ["thermal_inertia", "ground_albedo", "oroheight", "theta_s"]]
    for g in groups:
        check(",".join(g), lambda g=g: {k: f"{v[1]:.4g}  [{v[0]}]" for k, v in mcd_point(http, g, lat, lon, ex)[0].items()})
    check("variable inexistente", lambda: mcd_point(http, ["tsurf", "nope", "ps"], lat, lon, ex)[0])

    print("== MCD, ciclo diario con fecha marciana (ls + localtime=all)")
    ls = mcd_point(http, ["ls"], lat, lon, ex)[0]["ls"][1]

    def sweep_vs_point():
        sw = mcd_sweep(http, ["tsurf", "t", "ps", "fluxsurf_dn_sw"], lat, lon, ls)
        pt = mcd_point(http, ["tsurf", "t", "loctime"], lat, lon, ex)[0]
        h = pt["loctime"][1]
        i = int(h)
        f = h - i
        interp = lambda v: sw[v][1][i][1] * (1 - f) + sw[v][1][i + 1][1] * f
        return (f"{len(sw['tsurf'][1])} filas por variable; a LTST {h:.2f} h: tsurf ciclo {interp('tsurf'):.2f} vs "
                f"puntual {pt['tsurf'][1]:.2f} K; t ciclo {interp('t'):.2f} vs puntual {pt['t'][1]:.2f} K")
    check("ciclo vs puntual", sweep_vs_point)

    print("== MCD, escenarios de polvo (tau, tsurf máx, flujo solar máx a mediodía)")
    for d in DUST:
        def run(d=d):
            sw = mcd_sweep(http, ["tauref", "tsurf", "fluxsurf_dn_sw", "t"], lat, lon, ls, dust=d)
            return (f"{DUST[d]}: tau {sw['tauref'][1][12][1]:.3f}, tsurf {min(x[1] for x in sw['tsurf'][1]):.0f}-"
                    f"{max(x[1] for x in sw['tsurf'][1]):.0f} K, sol máx {max(x[1] for x in sw['fluxsurf_dn_sw'][1]):.0f} W/m2")
        check(f"dust={d}", run)

    print("== MCD, ¿distingue puntos de la ruta? (ps y tsurf a las 12 h locales)")
    for name, plat, plon in [BRADBURY, ("Yellowknife", -4.59, 137.45), ("Pahrump", -4.66, 137.37),
                             ("Murray", -4.73, 137.38), ("Curiosity hoy", *curiosity_now()[1:])]:
        check(name, lambda plat=plat, plon=plon: (lambda s: f"ps {s['ps'][1][12][1]:.1f} Pa, tsurf {s['tsurf'][1][12][1]:.1f} K, "
                                                  f"orografía {s['oroheight'][1][12][1]:.0f} m")(
            mcd_sweep(http, ["ps", "tsurf", "oroheight"], plat, plon, ls)))

    print("== MCD, altura sobre el suelo (viento a las 14 h)")
    for alt in (2, 10, 100, 1000):
        check(f"{alt} m", lambda alt=alt: f"{mcd_sweep(http, ['wind', 't'], lat, lon, ls, alt=alt)['wind'][1][14][1]:.2f} m/s")

    print("== MCD, errores")
    def no_julian():
        p = mcd_vars(["tsurf"])
        p.update(datekeyhtml=0, year=2026, month=11, day=15, hours=12, minutes=0, seconds=0, julian="",
                 latitude=lat, longitude=lon, altitude=2, zkey=3, dust=1, localtime="0.", ls="")
        return strip_html(http.get(mcd_url(p), polite=True)).strip()[-300:]
    check("sin julian", no_julian)
    check("latitud 95", lambda: mcd_point(http, ["tsurf"], 95, lon, ex)[0])

    print("== RAD")
    check("capabilities", lambda: hapi_json(http, "capabilities")["outputFormats"])
    info = rad_info(http)
    print(f"  info: {info['startDate']} -> {info['stopDate']}; parámetros "
          f"{[(p['name'], p.get('units')) for p in info['parameters']]}")
    stop = parse_iso(info["stopDate"])
    check("retraso", lambda: f"{(dt.datetime.now(dt.timezone.utc) - stop).total_seconds() / 3600:.1f} h desde la última lectura")
    check("csv por defecto", lambda: http.get(f"{HAPI}/data?id={RAD_ID}&time.min={iso(stop - dt.timedelta(hours=1))}"
                                               f"&time.max={iso(stop + dt.timedelta(seconds=1))}", ttl_s=600).strip().splitlines()[-1])
    check("solo dose_e_total", lambda: rad_data(http, stop - dt.timedelta(hours=1), stop + dt.timedelta(seconds=1), ["dose_e_total"])[-1])

    def cadence():
        rows = rad_data(http, stop - dt.timedelta(days=7), stop + dt.timedelta(seconds=1))
        ts = [parse_iso(r["Time"]) for r in rows]
        gaps = sorted(((b - a).total_seconds() / 60 for a, b in zip(ts, ts[1:])), reverse=True)
        nulls = sum(1 for r in rows for k in ("dose_b_raw", "dose_b_rtg", "dose_e_total") if r[k] is None)
        return (f"7 días: {len(rows)} lecturas, paso mediano {gaps[len(gaps) // 2]:.1f} min, "
                f"huecos mayores {[round(g) for g in gaps[:3]]} min, nulls {nulls}")
    check("cadencia", cadence)

    def year_gaps():
        out = []
        for m in range(12, 0, -1):
            a = stop - dt.timedelta(days=30 * m)
            rows = rad_data(http, a, a + dt.timedelta(days=30), ["dose_e_total"], ttl_s=None)
            out.append(len(rows))
        return f"lecturas por bloque de 30 días en el último año: {out}"
    check("huecos en un año", year_gaps)
    print(f"\n{http.calls} peticiones nuevas; caché en {args.cache}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("mode", nargs="?", default="build", choices=["build", "probe"])
    ap.add_argument("--cache", default=DEFAULT_CACHE)
    ap.add_argument("--now", help="fecha UTC de referencia, p. ej. 2026-11-15T12:00:00Z (por defecto, ahora)")
    ap.add_argument("--offline", action="store_true", help="usar solo la caché")
    args = ap.parse_args()
    (probe if args.mode == "probe" else build)(args)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
