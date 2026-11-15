# Clima (MCD) y radiación (RAD): pruebas de las APIs

Pruebas hechas el 2026-11-15 con `python -I Tools/build_climate_dataset.py probe`. El dataset del proyecto
se genera con `python -I Tools/build_climate_dataset.py` → `Assets/Gale/Data/climate_dataset.json`
(lo lee `Assets/Gale/Scripts/ClimateDataset.cs`). **Se regenera antes de cada demo; nunca se consulta en vivo.**

## Mars Climate Database v6.2 (LMD/ESA)

`https://www-mars.lmd.jussieu.fr/mcd_python/cgi-bin/mcdcgi.py`, GET sin clave. **Es un modelo climático:**
da el clima típico de esa estación (Ls), hora y lugar. No mide ni pronostica el tiempo real de 2026:
“mañana” es el clima esperado del sol siguiente, no un parte meteorológico.

### Dos formas de pedir la fecha

| Modo | Parámetros | Respuesta |
|---|---|---|
| Fecha terrestre | `datekeyhtml=0`, `year…seconds` y **`julian` obligatorio** (JD = unix/86400 + 2440587.5) | HTML con `<li>Etiqueta (unidad) ..... valor</li>`, un valor instantáneo |
| Fecha marciana | `datekeyhtml=1`, `ls=<grados>`, `localtime=all` (o `"h1 h2"`) | HTML con enlace `../txt/<hash>.txt`: ciclo diario, hora local 0..24, un bloque por variable |

Con fecha marciana basta **1 página + 1 txt por cada 4 variables y día**, en vez de 24 consultas.
El ciclo coincide con el valor puntual (Bradbury, LTST 9,07 h: tsurf 232,24 K en el ciclo frente a 232,59 K puntual).

Sin `julian`, la página no da error claro: toma JD 1.0 y dice “lies outside of the [jdate_min:jdate_max] range”.

### Parámetros fijos que usamos
`altitude=2&zkey=3` (2 m sobre el suelo), `hrkey=1` (alta resolución con relieve MOLA: hace falta para que la
presión cambie con la cota), `spacecraft=none`, `averaging=off`, `animation=off`, `proj=cyl`.
`var1..var4`: hasta 4 variables. **Si una no existe, la página se corta ahí con “field reference not found”.**
La lista completa está en `https://www-mars.lmd.jussieu.fr/mcd_python/listvar.js`.

### Variables probadas (Bradbury, 15 nov 2026 12:00 UTC, LTST 9,07 h)

| Variable | Qué es | Valor |
|---|---|---|
| `tsurf` | temperatura del suelo (K) | 232,6 |
| `t` | temperatura del aire (K) | 216,3 |
| `ps` | presión en superficie (Pa) | 895,5 |
| `rho` | densidad (kg/m³) | 0,0219 |
| `tauref` / `dod` | polvo, profundidad óptica diaria / mensual | 0,42 / 0,47 |
| `fluxsurf_dn_sw` | sol que llega al suelo horizontal (W/m²) | 334 |
| `fluxtop_dn_sw` | sol en el techo de la atmósfera (W/m²) | 368 |
| `fluxsurf_up_sw` / `fluxsurf_lw` | sol reflejado / infrarrojo hacia el suelo (W/m²) | 79 / 22,9 |
| `wind`, `u`, `v`, `w_l` | viento horizontal, componentes O-E, S-N, vertical (m/s) | 9,1 / -1,06 / 9,04 / ~0 |
| `ls`, `loctime`, `lmeantime`, `solzenang`, `marsau` | Ls, hora solar verdadera, hora media, ángulo cenital, distancia al Sol (UA) | 22,23° / 9,07 h / 9,47 h / 45,9° / 1,609 |
| `col_h2ovapor`, `col_h2oice` | columna de vapor / hielo de agua (kg/m²) | 0,0113 / 2,5e-5 |
| `co2ice`, `surf_h2o_ice` | escarcha de CO₂ / agua en superficie (kg/m²) | 0 / 6e-6 |
| `zmax`, `wstar_up` | altura de la capa límite (m), corriente ascendente máxima (m/s) | 808 / 5,0 |
| `rmstsurf`, `rmsps`, `rmst`, `rmsu` | variación de un día a otro (RMS) | 0,11 K / 0,79 Pa / 0,17 K / 0,42 m/s |
| `thermal_inertia`, `ground_albedo`, `oroheight`, `theta_s` | inercia térmica, albedo, cota (m), pendiente (°) | 232 / 0,24 / -4501 / 0,26 |

Las variaciones de un día a otro son de décimas: hoy y mañana salen casi iguales, y eso es correcto en un modelo climático.

### Escenarios de polvo (`dust`)

| dust | Escenario | Hoy (Ls 22°) |
|---|---|---|
| 1, 2, 3 | climatología, sol medio / mínimo / máximo | iguales cerca del suelo: tau 0,41, tsurf 183–272 K, sol máx. 487 W/m² |
| 4, 5, 6 | tormenta de polvo | **rechazado**: solo existe en la estación de tormentas, 180° < Ls < 360° |
| 7 | cálido (con polvo, sol máximo) | tau 0,79, sol máx. 457 W/m² |
| 8 | frío (poco polvo, sol mínimo) | tau 0,15, sol máx. 504 W/m² |

El dataset guarda `hoyPolvo` con la tormenta (5) cuando la estación lo permite y, si no, con el 7.
(El “5 = tormenta” de la nota del equipo es correcto, pero solo en esa estación.)

### ¿Distingue puntos de la ruta? Sí, por la cota (presión), no por la temperatura

| Punto | ps a las 12 h | tsurf | cota MOLA |
|---|---|---|---|
| Bradbury | 862,5 Pa | 267,3 K | -4501 m |
| Yellowknife | 862,8 | 267,3 | -4504 |
| Pahrump | 857,7 | 267,3 | -4437 |
| Murray | 837,1 | 267,4 | -4162 |
| Curiosity hoy (sol 5073) | 784,8 | 267,4 | -3433 |

El dataset trae dos puntos: Curiosity (último punto de la ruta) y Bradbury.

### Altura del viento (14 h locales)
2 m: 1,84 m/s · 10 m: 2,11 · 100 m: 2,27 · 1000 m: 0,56. Usamos 2 m.

### Otros
- Latitud fuera de rango (95°): la página no da valores ni error. El script lo trata como error.
- El servidor cortó la conexión una vez (WinError 10054) tras ~60 peticiones seguidas: el script reintenta con
  espera creciente y hace una pausa de 2 s entre llamadas. Con caché, regenerar cuesta ~30 peticiones.

## Tiempo marciano

Fórmulas Mars24 (Allison y McEwen 2000) en el script y en `MarsTime` (C#). Comprobado:
- JD de 2026-11-15 12:00 UTC = 2461360,0 (como el ejemplo del equipo).
- Sol de Curiosity en el aterrizaje = 0; el 15 nov 2026 = **sol 5075** (el último waypoint de la ruta es el 5073).
- Hora media local calculada 9,474 h = `lmeantime` del MCD 9,4734 h. La hora del MCD (`loctime`) es la
  **verdadera**; la diferencia (ecuación del tiempo, hoy -0,40 h) se guarda por día como `ecuacionTiempoH`.
- En el MCD, `hours=12` es **hora UTC terrestre**, no la hora de Gale (12:00 UTC ≈ 9 h locales hoy).

## RAD de Curiosity (NASA CCMC, HAPI 2.0)

`https://iswa.ccmc.gsfc.nasa.gov/hapi`, sin clave. `/capabilities`: formatos `csv` y `json`.
**Sin `format=json` devuelve CSV.**

| Campo | Unidad | Qué es |
|---|---|---|
| `Time` | UTC ISO | instante de la lectura |
| `dose_b_raw` | µGy/día | detector B de silicio, sin corregir |
| `dose_b_rtg` | µGy/día | detector B sin la contribución del generador RTG del rover |
| `dose_e_total` | µGy/día | detector E, plástico equivalente a tejido (el más parecido a la dosis de una persona) |

- Rango: 2011-11-20 → `stopDate` (hoy 2026-11-14T17:20:25Z). **Retraso: ~21 h.** Se lee `stopDate` de
  `/info?id=msl_rad_dosimetry` y se piden las 48 h anteriores.
- Cadencia: una lectura cada ~17 min (580 en 7 días, huecos máximos de 36 min, sin nulls).
  En el último año, ~2 475 lecturas por cada 30 días, sin huecos grandes.
- Hoy: última lectura 188,16 µGy/día (E); media de 24 h 185,8 (178–201); tendencia -2,6 frente a las 24 h anteriores.
- Los valores son de donde está el rover, sin filtro por coordenadas.
- En el catálogo (`/catalog`, 327 conjuntos) también hay `CLEAR_RT_ambient_sw_Mars_P10M`, viento solar en Marte casi en tiempo real.

## Cómo citarlo
- Mars Climate Database v6.2 (c) LMD/OU/IAA/ESA/CNES — Millour et al.; http://www-mars.lmd.jussieu.fr
- MSL RAD (Hassler et al.), datos servidos por NASA CCMC ISWA, https://iswa.ccmc.gsfc.nasa.gov
