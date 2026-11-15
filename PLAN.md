# PLAN — Módulo de navegación de ARES Map (ruta óptima A→B)

Estado: **fases 0–4 implementadas y probadas; fase 5 en parte** (ver §12) · Fecha: 2026-11-15 · Rama: `feature/navegacion-ruta-optima` (sale de `dev`)

## 1. Objetivo

Con el minimapa abierto (M / R3) y el cursor libre, un clic elige el **destino**; el **origen** es la posición
del rover. El simulador calcula la ruta óptima usando todos los datos que ya tiene (relieve, geología, material
del suelo, clima, radiación, hora marciana, rutas conocidas), la dibuja en el minimapa y sobre el terreno, y
deja cancelarla, modificarla y seguirla. En la vista aérea (Tab) la ruta se ve trazarse sobre el relieve, pegada
al suelo.

Qué significa "óptima" aquí: la de menor **coste generalizado**, una suma ponderada de tiempo, energía, riesgo
y dosis de radiación. Los pesos son un perfil que el usuario elige (equilibrada, rápida, segura, menor energía).

## 2. Lo que ya existe y se reutiliza

| Pieza | Dónde | Para qué sirve al módulo |
|---|---|---|
| Clic en el mapa → punto del mundo | `MinimapHUD.MapClicked` (`MinimapHUD.cs:62`, se lanza en `:237`), `MapToWorld` (`:301`), `WorldToMap` (`:283`) | Entrada del destino; ya está pensado para esto |
| Cursor del mapa (ratón o virtual con mando) | `MinimapHUD.MapCursor`, `InMap`, `MapRect` | Arrastrar pines, inspeccionar bajo el cursor |
| Líneas y marcadores en el mapa | `MinimapHUD.Line`, `Marker`, `Label` (IMGUI) | Dibujar la ruta y los pines |
| Clase geológica, suelo e índice termofísico bajo un punto | `GeoLayers.ClassAt` (`GeoLayers.cs:158`), `ThermoAt` (`:167`), `ZoneAt` (`:188`) | Coste por material; los mapas son de 3823 × 2003 px a 162,8 m |
| Capa temática pintada sobre el terreno y con leyenda | `GeoLayers` + `Gale/GeoOverlay` + `GeoLegendHUD` | Nueva capa "Transitabilidad" sin escribir otro shader |
| Clima y radiación | `ClimateDataset`, `McdPoint.At(utc)`, `MarsTime` (`ClimateDataset.cs`) | Hora local, luz, temperatura, polvo, dosis. **Hoy nadie lo carga en la escena**: falta un servicio |
| Cinta apoyada en el terreno | `CuriosityRoute.BuildRibbon` (`CuriosityRoute.cs:140`) + `Gale/RouteLine` | Misma técnica para la ruta en la vista aérea |
| Ruta real de Curiosity y recorrido grabado | `CuriosityRoute.Points`, `RoverTrackRecorder.Samples` | "Terreno probado" (descuento de riesgo) y destinos rápidos |
| Exportar CSV / GeoJSON | `RoverTrackRecorder.Export` (`:70`) | Mismo formato para exportar la ruta planificada |
| Georreferencia | `MarsGeo` (1 u = 50 m reales, X este, Z norte) | Todo el módulo trabaja en metros reales y convierte al dibujar |
| Instalación en la escena | `GaleSceneBuilder.Build` → `GeoLayersBuilder.Install()` (`GaleSceneBuilder.cs:185`) | Mismo patrón: `NavigationBuilder.Install()` |
| Acciones de entrada | `Tools/build_input_actions.py` (mapa `Map`, `:112`) → `GaleControls` (generado) | Atajos nuevos del mapa |

## 3. Decisiones que necesito de ti

Cada una tiene un valor por defecto con el que el plan sigue adelante si no dices otra cosa.

1. **Para quién es la ruta.** El rover de la simulación está agrandado respecto al terreno: recorre 12 u/s, que
   en el mapa son 600 m/s. Si la dosis y el clima se calcularan con ese tiempo, un viaje de 100 km duraría
   3 minutos y la radiación no pesaría nada. Propuesta: la ruta se calcula con un **tiempo de misión** a
   velocidad real de un vehículo (perfil "Rover tripulado", 10 km/h en llano firme; perfil "EVA a pie",
   2,5 km/h), y el panel muestra aparte el **tiempo de simulación** (lo que tarda el jugador).
2. **Límites de seguridad.** Uso los del equipo de datos (`gale_bradbury_info.json`): hasta 15° transitable,
   15–25° a evitar, más de 25° o rugosidad mayor de 0,35 m prohibido. Son umbrales del equipo, no norma NASA.
3. **Radiación por lugar.** RAD mide en un solo punto, así que la tasa es la misma en todo el mapa y minimizar
   dosis equivale a minimizar tiempo de exposición. Hay un modelo opcional (menos dosis a menor cota por la
   columna de atmósfera, y junto a paredes altas) que dejo **apagado** hasta que el investigador dé
   coeficientes citables (candidatos a revisar: Rafkin et al. 2014, Ehresmann et al. 2021).
4. **Detalle de 1 m de Bradbury** (rugosidad, 5 clases de suelo, riesgo). Está en el zip v3 pero no importado.
   Lo dejo en la fase 7 (opcional); sin él la zona de la ruta usa el relieve de 8 m que ya está en la escena.

## 4. Variables del simulador y cómo entran en la ruta

"Todas las variables" queda así. Donde un dato no tiene resolución o base física para desviar la ruta, se dice
y se usa solo en el informe, en vez de inventarle un efecto.

| Dato | Fuente en el proyecto | Resolución | Efecto en el coste | En el informe |
|---|---|---|---|---|
| Cota del terreno | Mallas TIN (`Gale_Terrain`), por raycast | 8 m en la zona de la ruta; ~400–463 m fuera | Pendiente a favor / en contra → velocidad y energía; pendiente máxima → riesgo y bloqueo | Perfil de elevación, desnivel + y −, pendiente máx. |
| Calidad del relieve | Bloque de procedencia | por bloque | Penalización de incertidumbre fuera de la zona de 8 m | Aviso "precisión regional" |
| Material del suelo (4 clases) | `Geo_SueloIndice` (THEMIS) | 163 m | Resistencia a la rodadura, velocidad, riesgo de hundimiento (clase 1) | Km por material |
| Índice termofísico (continuo) | `Geo_Termofisico16` | 163 m | Ajuste fino dentro de cada clase (más bajo = más blando) | — |
| Unidad geológica (6) | `Geo_GeologiaIndice` (USGS SIM 3292) | 1:20 M | Factor de riesgo débil por unidad (Htu: mesas y yardangs; AHi: bordes y eyecta) | Unidades que cruza |
| Zonas e índices TES / MOLA | `geo_dataset.json` → `zonas[].tes` | zona (píxel TES ~21 km) | Solo "Rugosidad MOLA" como factor débil; el resto no (el propio zip dice "no para rutas") | Contexto mineralógico de las zonas cruzadas |
| Mineralogía medida (10 sitios) | `geo_dataset.json` → `mineralogia` | sin coordenadas | Ninguno | Sitios y avisos de las zonas cruzadas (p. ej. dunas Bagnold: hundimiento) |
| Hora local y sol | `MarsTime` | — | Luz u oscuridad en cada tramo según la hora estimada de paso | Hora de salida y de llegada, soles |
| Flujo solar | `McdHour.flujoSolar` | por hora, 2 puntos | Noche → menos velocidad y más riesgo; balance de energía solar | Ventana de luz, "llegas de noche" |
| Polvo (τ) | `McdHour.polvo`, escenario `hoyPolvo` | por hora | Visibilidad (velocidad) y energía solar disponible | Escenario usado |
| Temperatura de suelo y aire | `McdHour.tSuelo`, `tAire` | por hora | Energía de calefacción por hora de viaje; aviso bajo el límite | Mín. y máx. en ruta |
| Presión | `McdHour.presion` (cambia con la cota) | 2 puntos | Solo en el modelo opcional de dosis | Presión en origen y destino |
| Viento y dirección | `McdHour.viento`, `vientoDesde` | por hora | Ninguno (a ~800 Pa el empuje es despreciable); aviso si supera un umbral | Viento máx. en ruta |
| Radiación | `RadData.ultimas24h.media` (dosis E, µGy/día) | 1 punto | Dosis = tasa × tiempo de misión | Dosis acumulada, antigüedad de la lectura |
| Ruta real de Curiosity | `CuriosityRoute.Points` | 5 m | Descuento de riesgo a menos de ~50 m (terreno ya recorrido) | Km sobre la ruta de Curiosity |
| Recorrido propio | `RoverTrackRecorder.Samples` | 5 m | Mismo descuento; opción "volver por donde vine" | — |
| Gravedad | `RoverController.gravity` (3,71) | — | Energía en pendiente | — |

## 5. Diseño

### 5.1 Arquitectura

Tres capas, de abajo arriba. El núcleo no usa `UnityEngine` salvo `Mathf`/`Vector3`, para poder probarlo y
ejecutarlo en un hilo.

```
Assets/Gale/Scripts/Navigation/
  Core/                      (asmdef Gale.Navigation.Core, C# puro)
    NavGrid.cs               Malla de dos niveles: cota, pendiente, orientación, clases. Mundo ↔ celda.
    NavCostModel.cs          Coste de un tramo a partir de los datos de la celda, el perfil y el clima.
    NavProfile.cs            Pesos, velocidades, límites y coeficientes (serializable).
    NavPathfinder.cs         A* con cancelación y progreso; suavizado posterior.
    NavRoute.cs              Resultado: puntos, datos por tramo, totales, avisos.
  NavigationSystem.cs        MonoBehaviour: estados, paradas, deshacer, recálculo, guiado. Instance + eventos.
  ClimateService.cs          Carga climate_dataset.json; clima y tasa de dosis para una hora dada.
  NavMapOverlay.cs           Pines, línea, arrastre e inspección en el minimapa (se engancha a MinimapHUD).
  NavRouteRibbon.cs          Cinta sobre el terreno, trazado animado y marcadores en la vista aérea.
  NavPanelHUD.cs             Panel de ruta (UI Toolkit): resumen, perfil de elevación, botones.
  Editor/NavGridBaker.cs     Lee la cota de los colliders y escribe los .bytes de la malla.
  Editor/NavigationBuilder.cs  Instala todo en la escena (lo llama GaleSceneBuilder).
Assets/Gale/Data/Nav/        nav_L0.bytes, nav_L1.bytes, nav_profiles.json
Assets/Gale/UI/              NavPanel.uxml, NavPanel.uss
```

### 5.2 Malla de navegación (dos niveles)

- **L0 regional**: la misma rejilla que los mapas geológicos (3823 × 2003 celdas de 162,8 m). Así suelo,
  geología y termofísico se leen por índice, sin remuestrear, y la capa "Transitabilidad" encaja en `GeoLayers`.
  El terreno cubre unos 5,6 M de esas celdas; el resto queda sin dato (no transitable).
- **L1 detalle**: celdas de 8 m sobre el bloque `GaleRuta_TIN` (8,8 × 18,5 km, ~1100 × 2316 celdas), que es el
  único con relieve fino. Dentro de ese rectángulo sustituye a L0.
- **Cota**: `NavGridBaker` (menú `Gale/Bake de navegación`, y automático al final de `GaleSceneBuilder.Build`)
  lanza rayos contra los `MeshCollider` con `RaycastCommand` por lotes y guarda la cota en 16 bit. La ruta se
  calcula sobre la misma superficie que pisa el rover. Tamaño: ~20 MB sin comprimir; si pesa en git, GZip.
- **Caducidad**: la cabecera guarda una huella de las mallas (nombre, vértices, límites). Si no coincide al
  arrancar, error visible en consola y navegación desactivada, en vez de rutas sobre un relieve viejo.
- **Derivados al cargar**: pendiente máxima (½° por byte) y orientación por celda, con diferencias centrales.
- Límite que hay que decir: fuera de la zona de 8 m el relieve es MOLA de ~463 m. Ahí la ruta es un corredor
  regional fiable en pendientes medias, no una traza que esquive rocas.

### 5.3 Modelo de coste

Para un tramo entre celdas vecinas, con distancia horizontal `d`, desnivel `Δh`, pendiente en el sentido de la
marcha `θ` y pendiente máxima de la celda `s`:

```
bloqueado   si  no hay relieve  ·  s > s_max (25°)  ·  rugosidad > 0,35 m (solo donde haya dato de 1 m)
velocidad   v = v_ref · k_pend(θ) · k_suelo(clase, termofísico) · k_luz(hora) · k_polvo(τ)
tiempo      t = d₃D / v
energía     E = d · m·g · max(0, C_rr(clase)·cos θ + sin θ) / η  +  (P_base + P_calef(T)) · t
riesgo      r = r_pend(s) + r_hund(clase, termofísico) + r_geo(unidad) + r_rug(zona) + r_incert(nivel)
            r = r · (1 − descuento) cerca de la ruta de Curiosity o del recorrido propio
dosis       D = Ḋ · t / 86400            (Ḋ en µGy/día, media de 24 h de RAD)
coste       J = w_t · t/t₀ + w_E · E/E₀ + w_R · r · d + w_D · D/D₀
```

`t₀`, `E₀`, `D₀` son los valores por metro en llano firme, de modo que el coste se mide en "metros
equivalentes de terreno fácil" y los pesos son comparables. `r_pend` vale 0 hasta 15° y sube hasta 25°.

Valores iniciales (todos en `nav_profiles.json`, a calibrar con el investigador; no son datos medidos):

| Parámetro | Rover tripulado | EVA a pie |
|---|---|---|
| `v_ref` en llano firme | 10 km/h | 2,5 km/h |
| `s_max` / inicio de precaución | 25° / 15° | 25° / 15° |
| `C_rr` roca · consolidado · regolito · arena fina | 0,05 · 0,08 · 0,12 · 0,25 | factor de marcha 1,0 · 1,1 · 1,3 · 1,8 |
| `k_luz` de noche | 0,5 | 0,3 |

Perfiles de pesos: **Equilibrada** (por defecto), **Más rápida** (sube `w_t`), **Más segura** (sube `w_R`),
**Menor energía** (sube `w_E`), **Menor exposición** (sube `w_D` y penaliza los tramos nocturnos).

Clima en el tiempo: en la fase 2 se usan las condiciones de la hora de salida. En la fase 6 cada nodo guarda
el tiempo transcurrido y el coste usa la hora estimada de paso. El dataset cubre hoy y mañana; para viajes más
largos se repite el ciclo diario del último sol, con aviso (es un modelo climático, el ciclo apenas cambia).
Si el dataset ya no cubre la fecha, los términos de clima se apagan y el panel pide regenerarlo.

### 5.4 Búsqueda

- **A\*** sobre la malla, 8 vecinos, con montículo binario y arrays reutilizados (sin basura por cálculo).
- Heurística admisible: distancia en línea recta × coste mínimo por metro del perfil. Garantiza el óptimo
  sobre la malla.
- **Dos niveles en un solo grafo**: las celdas L0 dentro del rectángulo de detalle no son nodos; las del borde
  de L1 enlazan con la celda L0 contigua. No hay costuras ni replanificación por tramos.
- **Suavizado**: se acortan esquinas solo si el tramo recto no cruza celdas bloqueadas y su coste integrado no
  empeora más de un 2 %. Después, curva suave para el dibujo, vuelta a apoyar en el suelo.
- **Paradas intermedias**: una búsqueda por tramo (origen → parada 1 → … → destino), se concatenan.
- **Hilo aparte** (`Task.Run` con `CancellationToken`): la UI no se congela y un cálculo nuevo cancela el
  anterior. Objetivo a medir en la fase 2: menos de 1 s en rutas de hasta 50 km, menos de 5 s en el caso peor.
- **Casos límite**: destino en celda bloqueada → se ajusta a la transitable más cercana (hasta 500 m) y se
  dice cuánto; rover en celda bloqueada → se le deja salir; sin ruta → mensaje con la causa y la opción de
  relajar los límites, con la ruta marcada como **no segura**.

### 5.5 Estados e interacción en el minimapa

```
Sin ruta ──clic──▶ Calculando ──▶ Vista previa ──Iniciar──▶ Activa ──llegada──▶ Completada
    ▲                                  │ │                    │
    └──────────── Cancelar ────────────┘ └─ editar ─▶ Calculando (recalcula)
```

| Acción | Teclado y ratón | Mando |
|---|---|---|
| Elegir o cambiar el destino | Clic izquierdo en el mapa | A en el mapa |
| Mover un pin | Arrastrarlo | A sobre el pin (cogerlo), A otra vez (soltarlo) |
| Añadir una parada | Mayús + clic (al final) · clic sobre la línea (en ese punto) | A → menú del punto → "Añadir parada" |
| Quitar una parada | Supr con el cursor sobre el pin | A sobre el pin → "Quitar" |
| Iniciar el guiado | Intro o botón del panel | A → "Iniciar" |
| Cancelar la ruta | Retroceso o botón del panel | A → "Cancelar ruta" |
| Deshacer / rehacer | Ctrl+Z / Ctrl+Y | — |
| Cambiar de perfil | P o botones del panel | menú del punto |
| Ver en vista aérea | V | menú del punto |
| Salir del mapa (la ruta se conserva) | M / Esc | B |

El mando no tiene botones libres en el mapa `Map`, por eso usa un menú pequeño junto al cursor. Mientras está
abierto, `MinimapHUD` no lee desplazamiento ni zoom.

Mientras se arrastra un pin se dibuja una línea recta discontinua y se recalcula como mucho 5 veces por
segundo; al soltar, el cálculo definitivo.

### 5.6 Dibujo en el minimapa

- `MinimapHUD` gana un evento `OverlayGUI(Rect)` que se invoca después del recorrido grabado y antes del
  marcador del rover; `Line`, `Marker` e `InMap` pasan a ser públicos. `NavMapOverlay` dibuja ahí.
- La línea se recorta contra el rectángulo (o el círculo en el minimapa pequeño). El código actual descarta el
  segmento entero si un extremo cae fuera, y una ruta simplificada tiene segmentos largos.
- Color violeta con borde oscuro: no se confunde con la ruta de Curiosity (naranja), el recorrido (cian) ni el
  terreno. Los tramos de precaución y los no seguros se marcan encima en ámbar y rojo, con trazo distinto para
  no depender solo del color.
- Pines: A (rover), paradas numeradas, B (destino) con distancia y tiempo. La parte ya recorrida se atenúa.
- En el minimapa circular, además: flecha en el aro hacia el siguiente punto cuando queda fuera del círculo.
- Bajo el cursor (modo mapa): pendiente, material, unidad geológica y nivel de transitabilidad de ese punto.
- Capa **Transitabilidad** en `GeoLayers` (favorable · precaución · evitar · no transitable), generada desde L0.
  Entra en el ciclo de L y en la leyenda, y explica de un vistazo por qué la ruta rodea una zona.

### 5.7 Vista aérea: ver cómo se traza

- **Cinta sobre el terreno** con la técnica de `CuriosityRoute`: la ruta se remuestrea cada 8 m reales en la
  zona de detalle y cada medio paso de celda fuera, y cada borde de la cinta se apoya en el suelo con su propio
  rayo. La construcción de la malla se saca a una utilidad común (`GroundRibbon`) que usan las dos rutas.
- **Trazado animado**: `Gale/RouteLine` recibe `_Reveal` (distancia revelada) y color por vértice. Al terminar
  el cálculo la cinta se dibuja del rover al destino en 1,5–3 s; con los valores por defecto la ruta de
  Curiosity no cambia. Rayas que avanzan indican el sentido de marcha.
- **Encuadre**: `ChaseCamera.FrameBounds(Bounds)` coloca el foco y la altura para que quepa la ruta entera.
  El atajo V cierra el mapa, pasa a vista aérea, encuadra y repite el trazado. Opción "seguir el trazado": la
  cámara acompaña la cabeza de la línea a baja altura.
- **Marcadores en el mundo**: pines A, paradas y B, y una marca de distancia cada N km, dibujados como el
  marcador "Rover" de `ChaseCamera.OnGUI`.
- La cinta va en la capa 2, como la de Curiosity: el rover no la pisa y la cámara del minimapa no la pinta dos
  veces.

### 5.8 Panel de ruta e instrumento

- **Panel** (UI Toolkit, a la derecha del mapa interactivo, mismo `PanelSettings` que la leyenda):
  distancia · tiempo de misión · tiempo de simulación · desnivel + / − · pendiente máxima · energía ·
  dosis acumulada · hora local de salida y de llegada · km por material · unidades geológicas cruzadas.
- **Por qué esta ruta**: reparto del coste (tiempo, energía, riesgo, dosis) y lista de avisos, por ejemplo
  "1,2 km de arena fina", "llegada de noche, −85 °C", "0,8 km con relieve de 463 m", "dato RAD de hace 21 h".
- **Perfil de elevación** dibujado con `generateVisualContent`, coloreado por dificultad; al pasar el cursor
  marca el punto en el mapa.
- **Hora de salida**: "ahora" por defecto, o elegir la hora local; indica la ventana con luz.
- **Avisos del dato**, siempre visibles: el MCD es un modelo climático, no un pronóstico; RAD llega con ~1 día
  de retraso; los umbrales son del equipo.
- **Instrumento** (`NavInstrumentHUD`), con ruta activa: distancia restante, tiempo, rumbo al siguiente punto.

### 5.9 Guiado

- Progreso: punto de la ruta más cercano buscado en una ventana desde el último índice, no en toda la lista.
- Fuera de ruta (más de 2 celdas o 3 u durante 3 s) → recalcula desde la posición actual conservando las
  paradas. Se puede desactivar.
- Llegada a una parada → pasa a la siguiente. Llegada al destino (radio de 2 u, configurable) → "Has llegado",
  la ruta pasa a Completada y se retira a los 5 s.
- "Reiniciar en Bradbury" del menú de pausa → recalcula desde la nueva posición.
- No hay piloto automático: el jugador conduce. Queda anotado como posible ampliación.

### 5.10 Entrada

Acciones nuevas en el mapa `Map` de `Tools/build_input_actions.py` (luego Unity regenera `GaleControls.cs`):
`AddStop` (Mayús + clic), `RouteCancel` (Retroceso), `RemovePoint` (Supr), `RouteConfirm` (Intro), `Undo` /
`Redo` (Ctrl+Z / Ctrl+Y), `CycleProfile` (P), `ViewAerial` (V). `Select` se sigue usando para clic y arrastre
(`IsPressed`). Hay que actualizar las ayudas de `MinimapHUD`, `NavInstrumentHUD` y `PauseMenu`.

## 6. Calidad de vida

Imprescindible (fases 3–5):
- Vista previa antes de iniciar; cancelar en cualquier momento; la ruta sobrevive a cerrar el mapa.
- Mover el destino, añadir, mover y quitar paradas; deshacer y rehacer.
- Indicador "Calculando…" con cancelación; nunca se congela la pantalla.
- Ajuste del destino a terreno transitable; mensaje claro cuando no hay ruta.
- Distancia, tiempos y avisos visibles sin abrir nada más.
- Recalcular al desviarse; detección de llegada.

Recomendable (fase 5–6):
- Tres alternativas a la vez (equilibrada, rápida, segura) con tabla comparativa; se elige con un clic.
- Destinos rápidos: lugares con nombre (`MinimapHUD.places`), hitos de la ruta de Curiosity, "volver a Bradbury".
- Perfil de elevación interactivo e inspección bajo el cursor.
- Exportar la ruta a CSV y GeoJSON (mismo formato que el recorrido, con pendiente, material, unidad, riesgo,
  dosis acumulada y hora local por punto). Sirve como entregable de la guía de supervivencia.
- Guardar la última ruta y ofrecer recuperarla al arrancar.
- Botón "Cancelar ruta" en el menú de pausa.

Opcional (fase 7): zonas a evitar dibujadas por el usuario, elegir destino desde la vista aérea, "volver por
donde vine", piloto automático.

## 7. Fases

Cada fase deja algo que se puede ver y comprobar, y va en commits propios.

| Fase | Contenido | Se comprueba con |
|---|---|---|
| 0. Preparación | Rama. `ClimateService`. `GeoLayers`: acceso a los arrays de índices y al termofísico, y capas registradas en ejecución. `MinimapHUD`: `OverlayGUI` y utilidades públicas. Acciones de entrada. | Compila; el juego se comporta igual que antes |
| 1. Malla | `NavGridBaker`, `NavGrid`, capa "Transitabilidad" | La capa se ve con L: rojo en las paredes del cráter y del monte Sharp, verde en Aeolis Palus |
| 2. Búsqueda | `NavProfile`, `NavCostModel`, `NavPathfinder`, `NavRoute`; pruebas del núcleo | Pruebas en verde; tiempos de cálculo medidos y anotados aquí |
| 3. Minimapa | `NavigationSystem` (estados), `NavMapOverlay`: clic → ruta dibujada, cancelar, resumen mínimo | Clic en el mapa traza la ruta desde el rover; Retroceso la quita |
| 4. Vista aérea | `GroundRibbon`, `NavRouteRibbon`, `_Reveal`, `FrameBounds`, marcadores | Tab o V: la cinta sigue el relieve y se traza animada |
| 5. Edición y guiado | Paradas, arrastre, deshacer, perfiles, alternativas, panel completo, perfil de elevación, guiado, desvío, llegada, exportar, mando | Lista de aceptación del apartado 9 |
| 6. Clima en el tiempo | Hora estimada de paso por nodo, hora de salida, escenario de polvo, balance de energía | Salir de tarde cambia la ruta o los avisos frente a salir por la mañana |
| 7. Opcional | Detalle de 1 m de Bradbury, estructuras de Tanaka como peligro, zonas a evitar, destino desde la vista aérea | — |

Las fases 0–4 son el mínimo que cumple lo pedido; 5–6 lo completan.

## 8. Cambios en archivos existentes

| Archivo | Cambio |
|---|---|
| `MinimapHUD.cs` | Evento `OverlayGUI`; `Line`, `Marker`, `InMap`, `WorldToMap(Vector3, Rect)` públicos; bandera para suspender la entrada con el menú abierto; texto de ayuda |
| `GeoLayers.cs` | `IndexData(layer)`, `ThermoData`, rectángulo de la región; `AddRuntimeLayer(def, índices)` |
| `CuriosityRoute.cs` | Usa `GroundRibbon` (sin cambio visible) |
| `Gale/RouteLine.shader` | `_Reveal`, color por vértice, rayas animadas opcionales |
| `ChaseCamera.cs` | `FrameBounds(Bounds)`, `FocusOn(Vector3)` |
| `NavInstrumentHUD.cs` | Línea de guiado y flecha al siguiente punto |
| `PauseMenu.cs` / `.uxml` | Botón "Cancelar ruta", ayuda de controles; `ResetRover` avisa a la navegación |
| `GaleSceneBuilder.cs` | Llama a `NavGridBaker.Bake()` y `NavigationBuilder.Install()` |
| `Tools/build_input_actions.py` | Acciones nuevas del mapa `Map` |
| `Packages/manifest.json` | `com.unity.test-framework` (pruebas del núcleo) |

## 9. Criterios de aceptación

1. Con el mapa abierto, un clic izquierdo fija el destino y en menos de 5 s aparece la ruta desde el rover.
2. La ruta no cruza ninguna celda con pendiente mayor de 25° ni sin relieve (salvo con los límites relajados,
   y entonces se marca como no segura).
3. Cambiar de perfil cambia el trazado o, si no, los totales; el panel explica el reparto del coste.
4. Se puede cancelar, mover el destino, añadir, mover y quitar paradas, y deshacer, con ratón y con mando.
5. La ruta sigue visible al cerrar el mapa: en el minimapa circular, en el instrumento y sobre el terreno.
6. En la vista aérea la cinta va pegada al relieve en todo su recorrido, sin tramos enterrados ni flotando, y
   se ve trazarse del rover al destino.
7. El panel muestra distancia, tiempo de misión, tiempo de simulación, desnivel, energía, dosis acumulada,
   hora de llegada y avisos, con la procedencia y los límites de cada dato.
8. Al desviarse, la ruta se recalcula sola; al llegar, lo indica y se retira.
9. El cálculo no baja la tasa de fotogramas de forma apreciable ni bloquea la entrada.
10. Con `climate_dataset.json` caducado la navegación sigue funcionando sin clima y lo avisa.

## 10. Pruebas

- **Núcleo (EditMode, sin escena)**: malla plana → ruta recta; muro bloqueado → lo rodea; colina → la bordea
  según el peso de energía; arena frente a roca → elige roca si el rodeo compensa; el coste de A\* coincide con
  Dijkstra en mallas pequeñas (heurística admisible); cancelación; destino inalcanzable; paso L0 ↔ L1.
- **Datos**: la cota de la malla coincide con `MarsGeo.GroundY` en 200 puntos al azar (tolerancia 0,5 m en L1).
- **En juego** (lista manual; con `com.unity.pipeline` se pueden automatizar capturas): Bradbury → Yellowknife
  Bay; Bradbury → base del monte Sharp (comparar con la ruta real de Curiosity, debe ir por un corredor
  parecido); Gale → Babati (ruta larga entre bloques); destino en la pared del cráter; destino fuera del terreno.
- **Rendimiento**: tiempos de cálculo y memoria en las tres rutas anteriores, anotados en este archivo.

## 11. Riesgos y límites

- **Resolución**: fuera de la zona de 8 m, la ruta no ve obstáculos menores de ~400 m. Se dice en el panel.
- **Suelo y geología son mapas regionales** (163 m y 1:20 M); las clases de suelo son relativas (percentiles),
  no inercia térmica calibrada. Sirven para preferir terreno firme, no para garantizarlo.
- **Coeficientes sin calibrar**: velocidades, resistencia a la rodadura y pesos son valores iniciales. La ruta
  es óptima respecto a ese modelo; hay que revisarlo con el investigador antes de presentarla como recomendación.
- **Clima y radiación apenas varían en el espacio** con los datos actuales (2 puntos del MCD, 1 de RAD):
  influyen en cuándo salir, cuánto se tarda y qué dosis se acumula, más que en por dónde ir.
- **Memoria**: unos 70–100 MB extra entre malla y búsqueda. Aceptable en PC; si hiciera falta, la búsqueda se
  limita a un corredor alrededor de origen y destino.
- **Malla caducada** si cambia el terreno sin volver a hornear: lo cubre la huella de la cabecera.
- **Hilos**: en una build WebGL no hay `Task.Run`; habría que repartir la búsqueda entre fotogramas.
- **Otra sesión en la misma copia**: el árbol de trabajo tiene cambios sin confirmar de otras ramas. La rama
  nueva debe salir de `dev` sin mover el HEAD de esta copia, y no se guarda la escena sin avisar.

## 12. Estado de la implementación (2026-11-15)

### Hecho

| Fase | Qué hay | Cómo se comprobó |
|---|---|---|
| 0 | `GeoLayers` (IndexData, ThermoData, AddRuntimeLayer), `MinimapHUD` (OverlayGUI, ClipLine, suspendInput), `ClimateService`, acciones del mapa, test-framework | Compila sin avisos; el juego se comporta igual |
| 1 | `NavGridBaker` (menú *Gale/Bake de navegación* y en `GaleSceneBuilder`), capa **Transitabilidad** | 8,04 M rayos en 2,6 s; L0 3823 × 2003 (5,63 M con relieve), L1 1060 × 2260 de 8,14 m; `nav_grid.bytes` 13,5 MB (LFS). Cota frente a `MarsGeo.GroundY` en 200 puntos: error máx. **0,018 m (L1)** y **0,051 m (L0)**. Con L: rojo en las paredes del cráter y del monte Sharp, verde en Aeolis Palus |
| 2 | Núcleo `Gale.Navigation.Core` (C# puro) | **31 pruebas EditMode en verde** (§10 + heurística frente a 20 000 aristas, A* = Dijkstra con subida neta y con dos niveles, componentes, A* ponderado) |
| 3 | `NavigationSystem`, `NavMapOverlay` | En juego: clic → ruta en 0,1 s (Bradbury → Pahrump), resumen, pines, inspección bajo el cursor, Retroceso la quita |
| 4 | `GroundRibbon` (también la usa `CuriosityRoute`), `NavRouteRibbon`, `_Reveal`, `FrameBounds` | Cinta a 0,04 u del suelo en todos los vértices medidos (0 enterrados, 0 flotando); `_Reveal` 57 → 93 → 115 → 122 u en 1,5 s; V encuadra la ruta |
| 5 (parte) | Paradas (Mayús + clic), arrastre de pines, Supr, deshacer/rehacer, perfiles (P), guiado con desvío y llegada, línea de guiado en el instrumento, reinicio en Bradbury recalcula | Prueba en juego: parada 6,1 → 8,8 km, deshacer y rehacer, perfil cambia el trazado (8 810 → 8 858 m), desvío de 600 m recalcula en ~3 s, llegada → "Has llegado" y se retira a los 5 s, destino fuera del terreno → mensaje |

Cambios respecto al diseño, con su motivo:

- **Heurística más ajustada y consistente** (sigue siendo admisible): cota de tracción para todo el trayecto,
  `cos s · max(0, C_rr,mín · cos s · D + Δh)`, más el riesgo mínimo del tramo que seguro queda en el nivel regional.
  Para que valga, una arista más empinada que `s_max` (60° con los límites relajados) no se recorre.
- **Componentes conexas** del terreno transitable (521, 3,3 s en segundo plano al cargar): un destino aislado se
  detecta sin explorar toda la región.
- **A\* ponderado** (`heuristicWeight`, 1,3 por defecto en la escena): coste garantizado ≤ 1,3 × óptimo; en las
  rutas de prueba, igual o hasta un 1,6 % peor. Con 1,0 es el óptimo exacto.
- Si no hay ruta segura, se calcula **directamente** la relajada y se marca como NO SEGURA (en vez de preguntar).
- Se quitó un `MinimapHUD` **duplicado** que ya había en la escena de `dev` (M abría dos mapas y el segundo
  tapaba la ruta); `GaleSceneBuilder` borraba solo uno de los antiguos.

### Tiempos medidos (perfil Equilibrada, editor, i.e. el peor caso; memoria de búsqueda ~110–120 MB)

| Ruta | Distancia | Exacto (1,0) | Ponderado (1,3) |
|---|---|---|---|
| Bradbury → Yellowknife Bay | 0,95 km | 35 ms | 28 ms |
| Bradbury → base del monte Sharp (Pahrump) | 6,1 km | 0,36 s | 5 ms |
| Bradbury → último punto de Curiosity (+1 100 m) | 14,8 km | 2,9 s | 0,84 s |
| Bradbury → pared del cráter | 18,4 km | 0,71 s | 0,12 s |
| Bradbury → borde O de Gale | 26 km | 2,4 s | 1,6 s |
| Bradbury → Aeolis Mons | 45 km | 4,0 s | 4,0 s |
| Gale → Babati (no segura: 2 km > 25°) | 144 km | 5,3 s | 5,0 s |
| Bradbury → Robert Sharp | 268 km | 6,2 s | 5,5 s |
| Destino fuera del terreno | — | inmediato, con mensaje | |

Objetivo "< 1 s hasta 50 km": **se cumple hasta ~20 km**; de 25 a 50 km tarda 1,6–4 s. El cálculo va en un hilo
y no bloquea el juego. Lo que lo frena es la zona de detalle (8 m): A* la recorre casi entera cuando la ruta
sale de ella. Mejora candidata: búsqueda jerárquica (corredor en L0 y refinado en L1 solo dentro del corredor).

### Pendiente

- Fase 5: menú de punto para el **mando** (hoy con mando solo se elige el destino; el resto es con teclado),
  panel de UI Toolkit con **perfil de elevación** (hoy el resumen es IMGUI junto al mapa), tres alternativas a la
  vez, destinos rápidos, exportar CSV/GeoJSON, guardar la última ruta, "Cancelar ruta" en el menú de pausa.
- Fase 6: clima por hora estimada de paso (hoy se usan las condiciones de la hora de salida).
- Fase 7 (opcional) y la búsqueda jerárquica de arriba.
- Decisiones del §3: se aplicaron los valores por defecto; siguen pendientes de confirmar.
