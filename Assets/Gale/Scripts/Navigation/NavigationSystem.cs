using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Gale.Navigation;
using UnityEngine;

// Módulo de navegación A→B (PLAN.md). Carga la malla horneada (nav_grid.bytes) y la completa con el
// suelo, la geología y el termofísico de GeoLayers y con el terreno probado de la ruta de Curiosity;
// publica la capa "Transitabilidad". Con el minimapa abierto (M):
//   clic = destino · Mayús + clic = parada al final · arrastrar un pin = moverlo · Supr sobre un pin = quitarlo
//   Intro = iniciar el guiado · Retroceso = cancelar · Ctrl+Z / Ctrl+Y · P = perfil · V = ver en vista aérea
// El cálculo va en un hilo aparte (Task.Run) y uno nuevo cancela el anterior. El origen es el rover.
//   Sin ruta ─clic─▶ Calculando ─▶ Vista previa ─Intro─▶ Activa ─llegada─▶ Completada
public class NavigationSystem : MonoBehaviour
{
    public static NavigationSystem Instance { get; private set; }

    public enum State { Loading, Disabled, Idle, Calculating, Preview, Active, Completed }

    [Header("Datos (los asigna NavigationBuilder)")]
    public TextAsset gridAsset;
    public TextAsset profilesAsset;
    public MinimapHUD minimap;
    public ChaseCamera chase;

    [Header("Perfil")]
    public int vehicle;
    public int weights;
    [Tooltip("Si no hay ruta segura, calcular una con los límites relajados y marcarla como no segura.")]
    public bool allowRelax = true;
    [Tooltip("Factor de la heurística de A*. 1 = óptimo exacto; 1,3 = hasta varias veces más rápido con un coste " +
             "garantizado ≤ 1,3 × el óptimo (en las rutas de prueba, < 1 % peor).")]
    [Range(1f, 2f)] public float heuristicWeight = 1.3f;

    [Header("Guiado")]
    public bool rerouteOnDeviation = true;
    [Tooltip("Distancia a la ruta (u) a partir de la que se considera fuera de ruta.")]
    public float offRouteUnits = 3f;
    public float offRouteSeconds = 3f;
    [Tooltip("Radio de llegada a una parada o al destino (u; 2 u = 100 m).")]
    public float arrivalUnits = 2f;
    public float completedSeconds = 5f;

    [Header("Terreno probado")]
    [Tooltip("Distancia a la ruta de Curiosity (m reales) dentro de la que el riesgo se descuenta.")]
    public float provenMeters = 50f;

    public event Action Changed;          // estado, ruta o mensaje
    public event Action<NavRoute> RouteReady;

    public State Current { get; private set; } = State.Loading;
    public NavRoute Route { get; private set; }
    public IReadOnlyList<Vector3> RouteWorld => routeWorld;
    public IReadOnlyList<Vector3> Stops => stops;     // paradas y destino (el último); el origen es el rover
    public string Message { get; private set; }
    public string ClimateStatus { get; private set; }
    public NavProfile Vehicle => profiles.vehiculos[Mathf.Clamp(vehicle, 0, profiles.vehiculos.Length - 1)];
    public NavWeights Weights => profiles.pesos[Mathf.Clamp(weights, 0, profiles.pesos.Length - 1)];
    public NavProfilesFile Profiles => profiles;
    public NavGrid Grid { get; private set; }
    public int ProgressIndex { get; private set; }
    public int NextStop { get; private set; }
    public bool Guiding => Current == State.Active;
    public float RemainingMeters => Route != null && ProgressIndex < Route.Points.Count ? Route.DistanceM - Route.Points[ProgressIndex].dist : 0f;
    public float RemainingSeconds => Route != null && ProgressIndex < Route.Points.Count ? Route.TimeS - Route.Points[ProgressIndex].time : 0f;
    /// <summary>Tiempo que tarda el jugador a la velocidad del rover de la simulación (12 u/s).</summary>
    public float SimSeconds => Route != null && chase && chase.target ? Route.DistanceM * MarsGeo.WorldScale / Mathf.Max(0.1f, chase.target.maxSpeed) : 0f;

    NavProfilesFile profiles;
    NavPlanner planner;
    readonly object planLock = new object();
    CancellationTokenSource cts;
    Task<NavPlanResult> task;
    int requestId;
    bool resumeActive;
    readonly List<Vector3> stops = new List<Vector3>();
    readonly List<Vector3> routeWorld = new List<Vector3>();
    readonly List<List<Vector3>> undo = new List<List<Vector3>>(), redo = new List<List<Vector3>>();
    float offRouteTime, completedAt;
    int transLayer = -1;

    // Arrastre de pines.
    int dragIndex = -1;
    float nextDragCalc;
    const float PinPixels = 14f;

    void Awake()
    {
        Instance = this;
        profiles = LoadProfiles();
    }

    void OnEnable()
    {
        if (!minimap) minimap = FindAnyObjectByType<MinimapHUD>();
        if (!chase && minimap) chase = minimap.GetComponent<ChaseCamera>();
        if (minimap) minimap.MapClicked += OnMapClicked;
    }

    void OnDisable()
    {
        if (minimap) { minimap.MapClicked -= OnMapClicked; minimap.suspendInput = false; minimap.extraHint = null; }
        cts?.Cancel();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    NavProfilesFile LoadProfiles()
    {
        NavProfilesFile f = null;
        if (profilesAsset)
            try { f = JsonUtility.FromJson<NavProfilesFile>(profilesAsset.text); }
            catch (Exception e) { Debug.LogError("[Nav] nav_profiles.json no válido: " + e.Message); }
        f ??= new NavProfilesFile();
        if (f.vehiculos == null || f.vehiculos.Length == 0) f.vehiculos = new[] { NavProfile.Rover(), NavProfile.Eva() };
        if (f.pesos == null || f.pesos.Length == 0) f.pesos = NavWeights.Defaults();
        return f;
    }

    // ---------- Carga ----------

    System.Collections.IEnumerator Start()
    {
        if (!gridAsset) { Disable("Falta la malla de navegación: menú Gale/Bake de navegación."); yield break; }
        yield return null;   // CuriosityRoute y GeoLayers ya han cargado sus datos (Start)

        string expected = NavUnity.Fingerprint(NavUnity.TerrainRoot());
        byte[] bytes = gridAsset.bytes;
        var geo = GeoLayers.Instance;
        byte[] soil = null, unit = null; ushort[] thermo = null; int tw = 0, th = 0;
        if (geo)
        {
            soil = geo.IndexData(geo.LayerIndex("suelo"));
            unit = geo.IndexData(geo.LayerIndex("geologia"));
            thermo = geo.ThermoData(out tw, out th);
        }
        var route = FindAnyObjectByType<CuriosityRoute>();
        var proven = new List<(float, float)>();
        if (route) foreach (var p in route.Points) proven.Add(NavUnity.ToMeters(p));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        float provenM = provenMeters;
        var load = Task.Run(() => LoadGrid(bytes, soil, unit, thermo, tw, th, proven, provenM));
        while (!load.IsCompleted) yield return null;
        if (load.IsFaulted) { Disable("No se pudo leer la malla de navegación: " + load.Exception?.GetBaseException().Message); yield break; }
        var (grid, trans) = load.Result;
        if (grid.Fingerprint != expected)
        {
            Debug.LogError($"[Nav] la malla de navegación está caducada (huella {grid.Fingerprint}, el terreno es {expected}): " +
                           "vuelve a hornearla con Gale/Bake de navegación.");
            Disable("Malla de navegación caducada: el terreno ha cambiado. Gale/Bake de navegación.");
            yield break;
        }
        Grid = grid;
        planner = new NavPlanner(grid) { HeuristicWeight = heuristicWeight };
        // Componentes conexas en segundo plano (la primera ruta las espera si aún no están).
        var prof = Vehicle;
        var pl0 = planner;
        Task.Run(() => { lock (planLock) pl0.PrepareComponents(prof); })
            .ContinueWith(t => Debug.Log($"[Nav] {pl0.ComponentCount} zonas transitables conexas en {pl0.ComponentsMs:0} ms"));
        if (geo && trans != null) transLayer = geo.AddRuntimeLayer(TransitabilityDef(), trans);
        Debug.Log($"[Nav] malla cargada en {sw.Elapsed.TotalMilliseconds:0} ms: L0 {grid.L0.W}×{grid.L0.H}" +
                  (grid.HasDetail ? $", L1 {grid.L1.W}×{grid.L1.H}" : "") + $", {proven.Count} puntos de terreno probado");
        SetState(State.Idle);
    }

    static (NavGrid, byte[]) LoadGrid(byte[] bytes, byte[] soil, byte[] unit, ushort[] thermo, int tw, int th,
        List<(float x, float z)> proven, float provenM)
    {
        NavGrid g;
        using (var ms = new MemoryStream(bytes)) g = NavGrid.Read(ms);
        g.ComputeSlopes();
        int w = g.L0.W, h = g.L0.H, n = w * h;
        if (soil != null && soil.Length == n) g.Soil = soil;
        if (unit != null && unit.Length == n) g.Geo = unit;
        if (thermo != null)
        {
            // Mismo rectángulo que los mapas de clases; si mide distinto, vecino más cercano.
            g.Thermo = new byte[n];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(tw - 1, x * tw / w), sy = Math.Min(th - 1, y * th / h);
                    g.Thermo[y * w + x] = (byte)(thermo[sy * tw + sx] >> 8);
                }
        }
        MarkProven(g, proven, provenM);
        return (g, Transitability(g));
    }

    // Terreno probado: celdas a menos de provenMeters de la ruta de Curiosity (puntos cada 5 m).
    static void MarkProven(NavGrid g, List<(float x, float z)> pts, float meters)
    {
        foreach (var lv in new[] { g.L0, g.L1 })
        {
            if (lv == null) continue;
            int rx = Math.Max(0, (int)Math.Ceiling(meters / lv.CellX)), rz = Math.Max(0, (int)Math.Ceiling(meters / lv.CellZ));
            float m2 = meters * meters;
            for (int i = 0; i < pts.Count; i += lv == g.L0 ? 8 : 1)
            {
                var (px, pz) = pts[i];
                if (!lv.TryCell(px, pz, out int cx, out int cy) && lv == g.L1) continue;
                for (int y = cy - rz; y <= cy + rz; y++)
                    for (int x = cx - rx; x <= cx + rx; x++)
                    {
                        if (x < 0 || y < 0 || x >= lv.W || y >= lv.H) continue;
                        float dx = lv.CenterX(x) - px, dz = lv.CenterZ(y) - pz;
                        if (dx * dx + dz * dz <= m2 || lv == g.L0 && x == cx && y == cy) lv.Flags[y * lv.W + x] |= NavLevel.FlagProven;
                    }
            }
        }
    }

    // Capa Transitabilidad (rejilla L0 = la de los mapas): 1 favorable · 2 precaución · 3 evitar · 4 no transitable.
    static byte[] Transitability(NavGrid g)
    {
        var lv = g.L0;
        var c = new byte[lv.Count];
        for (int i = 0; i < c.Length; i++)
        {
            byte s = lv.Slope[i];
            if (s == 255) continue;
            float deg = s * 0.5f;
            bool sand = g.Soil != null && g.Soil[i] == 1;
            c[i] = deg > 25f ? (byte)4 : deg > 15f ? (byte)3 : deg > 10f || sand ? (byte)2 : (byte)1;
        }
        return c;
    }

    static GeoLayerDef TransitabilityDef() => new GeoLayerDef
    {
        id = "transitabilidad",
        nombre = "Transitabilidad",
        titulo = "Transitabilidad para el rover",
        fuente = "Pendiente del relieve del simulador (malla de navegación, celdas de 163 m) y material del suelo (THEMIS)",
        aviso = "umbrales del equipo (15° / 25°), no norma NASA; a 163 m no se ven obstáculos pequeños",
        clases = new[]
        {
            new GeoClass { indice = 1, nombre = "Favorable", color = "#3BA55C", descripcion = "Pendiente de hasta 10° sobre suelo firme." },
            new GeoClass { indice = 2, nombre = "Precaución", color = "#E3C33F", descripcion = "Pendiente de 10° a 15°, o polvo y arena fina (riesgo de hundimiento)." },
            new GeoClass { indice = 3, nombre = "Evitar", color = "#E07B2E", descripcion = "Pendiente de 15° a 25°: transitable con riesgo, la ruta la evita si puede." },
            new GeoClass { indice = 4, nombre = "No transitable", color = "#C42F3A", descripcion = "Pendiente de más de 25°: la ruta no pasa salvo con los límites relajados." },
        },
    };

    void Disable(string why)
    {
        Message = why;
        Debug.LogWarning("[Nav] " + why);
        SetState(State.Disabled);
    }

    public int TransitabilityLayer => transLayer;

    // ---------- Edición ----------

    void OnMapClicked(Vector3 world)
    {
        if (!Ready) return;
        int pin = PinUnderCursor();
        if (pin >= 0)
        {
            dragIndex = pin;
            minimap.suspendInput = true;
            PushUndo();
            return;
        }
        PushUndo();
        if (GaleInput.Controls.Map.Fast.IsPressed() && stops.Count > 0) stops.Add(world);   // Mayús + clic: parada al final
        else if (stops.Count == 0) stops.Add(world);
        else stops[stops.Count - 1] = world;
        Recalculate();
    }

    bool Ready => Grid != null && Current != State.Disabled && Current != State.Loading;

    public void SetDestination(Vector3 world)
    {
        if (!Ready) return;
        PushUndo();
        stops.Clear();
        stops.Add(world);
        Recalculate();
    }

    public void AddStop(Vector3 world)
    {
        if (!Ready) return;
        PushUndo();
        stops.Add(world);
        Recalculate();
    }

    public void RemoveStop(int i)
    {
        if (i < 0 || i >= stops.Count) return;
        PushUndo();
        stops.RemoveAt(i);
        if (stops.Count == 0) Cancel(false);
        else Recalculate();
    }

    public void Cancel(bool keepUndo = true)
    {
        if (keepUndo && stops.Count > 0) PushUndo();
        cts?.Cancel();
        requestId++;
        stops.Clear();
        ClearRoute();
        Message = null;
        SetState(Ready ? State.Idle : Current);
    }

    void ClearRoute()
    {
        Route = null;
        routeWorld.Clear();
        ProgressIndex = 0;
        NextStop = 0;
        resumeActive = false;
        RouteReady?.Invoke(null);
    }

    public void StartGuidance()
    {
        if (Current != State.Preview || Route == null) return;
        ProgressIndex = 0;
        NextStop = 0;
        offRouteTime = 0f;
        SetState(State.Active);
        if (minimap && minimap.Interactive) minimap.SetInteractive(false);
    }

    public void CycleWeights(int step = 1)
    {
        weights = (weights + step + profiles.pesos.Length) % profiles.pesos.Length;
        if (stops.Count > 0) Recalculate();
        else Changed?.Invoke();
    }

    public void CycleVehicle()
    {
        vehicle = (vehicle + 1) % profiles.vehiculos.Length;
        if (stops.Count > 0) Recalculate();
        else Changed?.Invoke();
    }

    void PushUndo()
    {
        undo.Add(new List<Vector3>(stops));
        if (undo.Count > 50) undo.RemoveAt(0);
        redo.Clear();
    }

    public void Undo() => Swap(undo, redo);
    public void Redo() => Swap(redo, undo);

    void Swap(List<List<Vector3>> from, List<List<Vector3>> to)
    {
        if (from.Count == 0) return;
        to.Add(new List<Vector3>(stops));
        stops.Clear();
        stops.AddRange(from[from.Count - 1]);
        from.RemoveAt(from.Count - 1);
        if (stops.Count == 0) { cts?.Cancel(); requestId++; ClearRoute(); SetState(State.Idle); }
        else Recalculate();
    }

    /// <summary>Pin (índice de parada) bajo el cursor del mapa, o -1.</summary>
    public int PinUnderCursor()
    {
        if (!minimap || !minimap.Interactive) return -1;
        Rect r = minimap.MapRect();
        Vector2 c = minimap.MapCursor;
        int best = -1;
        float bd = PinPixels * MinimapHUD.UiScale;
        for (int i = 0; i < stops.Count; i++)
        {
            float d = Vector2.Distance(minimap.WorldToMap(stops[i], r), c);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    // ---------- Cálculo ----------

    /// <summary>Recalcula desde la posición actual del rover por las paradas que quedan.</summary>
    public void Recalculate()
    {
        if (!Ready || stops.Count == 0 || !chase || !chase.target) return;
        resumeActive = Current == State.Active;
        cts?.Cancel();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        int id = ++requestId;

        var pts = new List<(float x, float z)> { NavUnity.ToMeters(chase.target.transform.position) };
        for (int i = resumeActive ? NextStop : 0; i < stops.Count; i++) pts.Add(NavUnity.ToMeters(stops[i]));
        if (resumeActive && NextStop > 0) { stops.RemoveRange(0, NextStop); NextStop = 0; }
        var p = Vehicle; var w = Weights;
        var cond = NavConditions.None;
        string st = "Sin servicio de clima: ruta calculada sin clima ni radiación.";
        if (ClimateService.Instance) cond = ClimateService.Instance.Sample(DateTime.UtcNow, out st);
        ClimateStatus = st;
        bool relax = allowRelax;
        float hw = heuristicWeight;
        var pl = planner;
        Message = null;
        task = Task.Run(() =>
        {
            lock (planLock)
            {
                if (token.IsCancellationRequested) return new NavPlanResult { status = NavStatus.Cancelled };
                pl.HeuristicWeight = hw;
                pl.PrepareComponents(p, token);
                return pl.Plan(pts, p, w, cond, relax, token);
            }
        }, token);
        pendingId = id;
        SetState(State.Calculating);
    }

    int pendingId;

    void ApplyResult(NavPlanResult res)
    {
        if (res.status == NavStatus.Cancelled) return;
        if (res.status != NavStatus.Ok)
        {
            Message = res.message;
            Debug.Log($"[Nav] sin ruta: {res.message}");
            // Se conserva la ruta anterior si la había (p. ej. al arrastrar a una pared).
            SetState(Route != null ? (resumeActive ? State.Active : State.Preview) : State.Idle);
            return;
        }
        Route = res.route;
        if (!string.IsNullOrEmpty(res.message) && Route.Unsafe) Message = res.message;
        routeWorld.Clear();
        foreach (var pt in Route.Points) routeWorld.Add(NavUnity.ToWorld(pt));
        ProgressIndex = 0;
        offRouteTime = 0f;
        Debug.Log($"[Nav] ruta {Route.DistanceM / 1000f:0.00} km, {Route.TimeS / 3600f:0.00} h de misión, coste {Route.Cost:0}, " +
                  $"{Route.Expanded} nodos en {Route.SearchMs:0} ms (total {Route.TotalMs:0} ms), perfil {Route.weightsId}" +
                  (Route.Unsafe ? " · NO SEGURA" : ""));
        SetState(resumeActive ? State.Active : State.Preview);
        RouteReady?.Invoke(Route);
    }

    void SetState(State s)
    {
        Current = s;
        UpdateHint();
        Changed?.Invoke();
    }

    void UpdateHint()
    {
        if (!minimap) return;
        minimap.extraHint = Current == State.Disabled || Current == State.Loading ? null
            : "RUTA · clic destino · Mayús+clic parada · arrastrar pin · Supr quitar · Intro iniciar · Retroceso cancelar · P perfil · V vista aérea · Ctrl+Z/Y";
    }

    // ---------- Bucle ----------

    void Update()
    {
        if (task != null && task.IsCompleted)
        {
            var t = task; task = null;
            if (pendingId == requestId && !t.IsCanceled && !t.IsFaulted) ApplyResult(t.Result);
            else if (t.IsFaulted) { Debug.LogException(t.Exception); Message = "Error al calcular la ruta."; SetState(Route != null ? State.Preview : State.Idle); }
        }
        if (!Ready || SimPause.IsPaused) return;

        if (minimap && minimap.Interactive) UpdateMapInput();
        else if (dragIndex >= 0) EndDrag();

        if (Current == State.Active) UpdateGuidance();
        if (Current == State.Completed && Time.unscaledTime > completedAt + completedSeconds)
        {
            stops.Clear();
            ClearRoute();
            Message = null;
            SetState(State.Idle);
        }
    }

    void UpdateMapInput()
    {
        var a = GaleInput.Controls.Map;
        if (dragIndex >= 0)
        {
            Vector3 w = minimap.MapToWorld(minimap.MapCursor);
            if (dragIndex < stops.Count) stops[dragIndex] = w;
            if (!a.Select.IsPressed()) { EndDrag(); return; }
            if (Time.unscaledTime >= nextDragCalc) { nextDragCalc = Time.unscaledTime + 0.2f; Recalculate(); }   // 5 por segundo
            return;
        }
        if (a.RouteCancel.WasPressedThisFrame()) Cancel();
        if (a.RouteConfirm.WasPressedThisFrame()) StartGuidance();
        if (a.CycleProfile.WasPressedThisFrame()) CycleWeights();
        if (a.Undo.WasPressedThisFrame()) Undo();
        if (a.Redo.WasPressedThisFrame()) Redo();
        if (a.RemovePoint.WasPressedThisFrame()) RemoveStop(PinUnderCursor());
        if (a.ViewAerial.WasPressedThisFrame()) ViewAerial();
    }

    void EndDrag()
    {
        dragIndex = -1;
        if (minimap) minimap.suspendInput = false;
        Recalculate();
    }

    public bool Dragging => dragIndex >= 0;

    /// <summary>Cierra el mapa, pasa a vista aérea, encuadra la ruta y repite el trazado.</summary>
    public void ViewAerial()
    {
        if (routeWorld.Count < 2 || !chase) return;
        if (minimap && minimap.Interactive) minimap.SetInteractive(false);
        var b = new Bounds(routeWorld[0], Vector3.zero);
        foreach (var p in routeWorld) b.Encapsulate(p);
        chase.FrameBounds(b);
        var ribbon = GetComponent<NavRouteRibbon>();
        if (ribbon) ribbon.Replay();
    }

    void UpdateGuidance()
    {
        if (!chase || !chase.target || routeWorld.Count < 2) return;
        Vector3 pos = chase.target.transform.position;

        // Punto más cercano en una ventana desde el último índice (no en toda la lista).
        int lo = Mathf.Max(0, ProgressIndex - 20), hi = Mathf.Min(routeWorld.Count - 1, ProgressIndex + 400);
        int best = ProgressIndex; float bd = float.MaxValue;
        for (int i = lo; i <= hi; i++)
        {
            float dx = routeWorld[i].x - pos.x, dz = routeWorld[i].z - pos.z, d = dx * dx + dz * dz;
            if (d < bd) { bd = d; best = i; }
        }
        ProgressIndex = Mathf.Max(ProgressIndex, best);

        // Llegada a la parada siguiente o al destino.
        if (NextStop < stops.Count)
        {
            Vector3 s = stops[NextStop];
            if (new Vector2(s.x - pos.x, s.z - pos.z).magnitude < arrivalUnits)
            {
                NextStop++;
                if (NextStop >= stops.Count)
                {
                    Message = "Has llegado al destino.";
                    completedAt = Time.unscaledTime;
                    ProgressIndex = routeWorld.Count - 1;
                    SetState(State.Completed);
                    return;
                }
                Message = $"Parada {NextStop} alcanzada.";
                Changed?.Invoke();
            }
        }

        // Fuera de ruta: recalcula desde aquí conservando las paradas que quedan.
        if (rerouteOnDeviation && task == null)
        {
            if (Mathf.Sqrt(bd) > offRouteUnits) offRouteTime += Time.deltaTime;
            else offRouteTime = 0f;
            if (offRouteTime > offRouteSeconds)
            {
                offRouteTime = 0f;
                Message = "Fuera de ruta: recalculando.";
                Recalculate();
            }
        }
    }

    /// <summary>El rover se ha teletransportado (Reiniciar en Bradbury): recalcular desde allí.</summary>
    public void OnRoverReset()
    {
        if (stops.Count > 0 && Ready) Recalculate();
    }

    /// <summary>Rumbo (grados desde el norte) y distancia (m reales) del rover al punto de la ruta a ~150 m por delante.</summary>
    public bool Bearing(out float headingDeg, out float meters)
    {
        headingDeg = 0f; meters = 0f;
        if (Route == null || routeWorld.Count < 2 || !chase || !chase.target) return false;
        Vector3 pos = chase.target.transform.position;
        float ahead = Route.Points[ProgressIndex].dist + 150f;
        int i = ProgressIndex;
        while (i < Route.Points.Count - 1 && Route.Points[i].dist < ahead) i++;
        Vector3 d = routeWorld[i] - pos;
        headingDeg = Mathf.Repeat(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 360f);
        meters = new Vector2(d.x, d.z).magnitude / MarsGeo.WorldScale;
        return true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}
