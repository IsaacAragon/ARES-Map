using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

// Preferencias de la interfaz que duran la sesión (mientras el juego esté abierto; no se guardan en disco).
public static class ExplorerPrefs
{
    public static bool? minimapVisible, curiosityRibbon, curiosityOnMap, pathOnMap, dustScenario;
    public static float? layerOpacity;
    public static int lastSection = 1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset()
    {
        minimapVisible = curiosityRibbon = curiosityOnMap = pathOnMap = dustScenario = null;
        layerOpacity = null;
        lastSection = 1;
    }
}

// Menú de exploración (UI Toolkit), inspirado en Google Earth: barra de iconos flotante a la izquierda y un
// panel translúcido con
//   Capas      imagen real / geología / material del suelo / transitabilidad (GeoLayers), opacidad, leyenda,
//              ruta de Curiosity y recorrido
//   Geodesia   posición, cota, pendiente, rumbo, distancias geodésicas, hora marciana y sistema de referencia
//   Rocas      unidad geológica, material del suelo e índice termofísico bajo el rover, y la ficha de la zona
//   Radiación  dosis medidas por RAD (últimas 48 h) y la atmósfera del MCD ahora
//   Ajustes    mostrar u ocultar el minimapa y las superposiciones (se recuerda durante la sesión)
// Todo sale de los datos que ya carga la escena. G (o clic en un icono con el cursor libre) abre y cierra
// el panel; abrirlo libera el cursor. En pausa se oculta.
[DefaultExecutionOrder(-40)]
[RequireComponent(typeof(UIDocument))]
public class ExplorerMenu : MonoBehaviour
{
    public static ExplorerMenu Instance { get; private set; }

    public StyleSheet styleSheet;
    public ChaseCamera chase;
    public MinimapHUD minimap;
    [Tooltip("Segundos entre actualizaciones de los datos del panel.")]
    public float refreshInterval = 0.25f;

    public enum Section { None = 0, Layers = 1, Geodesy = 2, Rocks = 3, Radiation = 4, Settings = 5 }
    static readonly string[] SectionNames = { "", "Capas", "Geodesia", "Rocas", "Radiación", "Ajustes" };

    public Section Current { get; private set; } = Section.None;
    public bool PanelOpen => Current != Section.None;
    /// <summary>Con el panel abierto el cursor queda libre (MinimapHUD.ApplyCursor lo consulta).</summary>
    public static bool WantsCursor => Instance && Instance.isActiveAndEnabled && Instance.PanelOpen;

    VisualElement root, rail, panel;
    Label title;
    ScrollView scroll;
    readonly Dictionary<Section, VisualElement> items = new();
    readonly Dictionary<Section, VisualElement> pages = new();
    float nextRefresh, nextSlowRefresh;
    CuriosityRoute curiosity;

    void Awake() => Instance = this;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void OnEnable()
    {
        if (!chase) chase = FindAnyObjectByType<ChaseCamera>();
        if (!minimap && chase) minimap = chase.GetComponent<MinimapHUD>();
        curiosity = FindAnyObjectByType<CuriosityRoute>();

        var docRoot = GetComponent<UIDocument>().rootVisualElement;
        // Comparte panel con el menú de pausa y la leyenda: fuera del flujo para no repartirse la pantalla.
        docRoot.style.position = Position.Absolute;
        docRoot.style.left = docRoot.style.top = docRoot.style.right = docRoot.style.bottom = 0;
        docRoot.pickingMode = PickingMode.Ignore;
        if (styleSheet && !docRoot.styleSheets.Contains(styleSheet)) docRoot.styleSheets.Add(styleSheet);
        docRoot.Clear();
        root = new VisualElement { name = "explorer", pickingMode = PickingMode.Ignore };
        root.AddToClassList("ex-root");
        docRoot.Add(root);

        BuildRail();
        BuildPanel();
        SimPause.Changed += OnPause;
        OnPause(SimPause.IsPaused);
        if (GeoLayers.Instance) GeoLayers.Instance.Changed += OnLayersChanged;
        ApplyPrefs();
    }

    void OnDisable()
    {
        SimPause.Changed -= OnPause;
        if (GeoLayers.Instance) GeoLayers.Instance.Changed -= OnLayersChanged;
        if (PanelOpen) { Current = Section.None; ApplyCursor(); }
        SetLegendHidden(false);
    }

    void OnPause(bool paused)
    {
        if (root != null) root.style.display = paused ? DisplayStyle.None : DisplayStyle.Flex;
    }

    // ---------- Estructura ----------

    void BuildRail()
    {
        rail = new VisualElement();
        rail.AddToClassList("ex-rail");
        root.Add(rail);
        AddItem(Section.Layers, ExplorerIcon.Kind.Layers);
        AddItem(Section.Geodesy, ExplorerIcon.Kind.Globe);
        AddItem(Section.Rocks, ExplorerIcon.Kind.Rock);
        AddItem(Section.Radiation, ExplorerIcon.Kind.Radiation);
        var sep = new VisualElement(); sep.AddToClassList("ex-sep"); rail.Add(sep);
        AddItem(Section.Settings, ExplorerIcon.Kind.Gear);
        var key = new Label("G menú"); key.AddToClassList("ex-key"); rail.Add(key);
    }

    void AddItem(Section s, ExplorerIcon.Kind icon)
    {
        var item = new VisualElement { tooltip = SectionNames[(int)s] };
        item.AddToClassList("ex-item");
        item.Add(new ExplorerIcon(icon));
        var l = new Label(SectionNames[(int)s]) { pickingMode = PickingMode.Ignore };
        l.AddToClassList("ex-item-label");
        item.Add(l);
        item.RegisterCallback<ClickEvent>(_ => Toggle(s));
        rail.Add(item);
        items[s] = item;
    }

    void BuildPanel()
    {
        panel = new VisualElement();
        panel.AddToClassList("ex-panel");
        var header = new VisualElement(); header.AddToClassList("ex-header");
        title = new Label(); title.AddToClassList("ex-title");
        var close = new Button(() => Open(Section.None)) { text = "×", tooltip = "Cerrar (G)" };
        close.AddToClassList("ex-close");
        header.Add(title); header.Add(close);
        panel.Add(header);
        scroll = new ScrollView(ScrollViewMode.Vertical);
        scroll.AddToClassList("ex-scroll");
        panel.Add(scroll);
        root.Add(panel);

        pages[Section.Layers] = BuildLayersPage();
        pages[Section.Geodesy] = BuildGeodesyPage();
        pages[Section.Rocks] = BuildRocksPage();
        pages[Section.Radiation] = BuildRadiationPage();
        pages[Section.Settings] = BuildSettingsPage();
        foreach (var p in pages.Values) scroll.Add(p);
        Open(Section.None);
    }

    // ---------- Abrir y cerrar ----------

    public void Toggle(Section s) => Open(Current == s ? Section.None : s);

    public void Open(Section s)
    {
        Current = s;
        if (s != Section.None) ExplorerPrefs.lastSection = (int)s;
        panel.style.display = s == Section.None ? DisplayStyle.None : DisplayStyle.Flex;
        foreach (var kv in items) kv.Value.EnableInClassList("selected", kv.Key == s);
        foreach (var kv in pages) kv.Value.style.display = kv.Key == s ? DisplayStyle.Flex : DisplayStyle.None;
        title.text = SectionNames[(int)s];
        if (s == Section.Layers) RebuildLayers();
        SetLegendHidden(s != Section.None);   // el panel ocupa el sitio de la leyenda (y Capas la incluye)
        nextRefresh = nextSlowRefresh = 0f;
        Refresh(true);
        ApplyCursor();
    }

    void ApplyCursor()
    {
        if (minimap) minimap.ApplyCursor();
    }

    void SetLegendHidden(bool hidden)
    {
        var legend = FindAnyObjectByType<GeoLegendHUD>();
        var e = legend ? legend.GetComponent<UIDocument>().rootVisualElement?.Q("legend") : null;
        if (e != null) e.style.visibility = hidden ? Visibility.Hidden : Visibility.Visible;
    }

    void Update()
    {
        if (SimPause.IsPaused) return;
        if (GaleInput.Controls.Global.ExplorerMenu.WasPressedThisFrame())
            Open(PanelOpen ? Section.None : (Section)Mathf.Clamp(ExplorerPrefs.lastSection, 1, 5));
        if (PanelOpen && Time.unscaledTime >= nextRefresh) Refresh(false);
    }

    void Refresh(bool force)
    {
        nextRefresh = Time.unscaledTime + refreshInterval;
        bool slow = force || Time.unscaledTime >= nextSlowRefresh;
        if (slow) nextSlowRefresh = Time.unscaledTime + 2f;
        switch (Current)
        {
            case Section.Layers: RefreshLayers(); break;
            case Section.Geodesy: RefreshGeodesy(); break;
            case Section.Rocks: RefreshRocks(slow); break;
            case Section.Radiation: if (slow) RefreshRadiation(); break;
            case Section.Settings: RefreshSettings(); break;
        }
    }

    Vector3 RoverPos => chase && chase.target ? chase.target.transform.position : Vector3.zero;

    // ---------- Ayudas de construcción ----------

    static VisualElement Page()
    {
        var p = new VisualElement();
        p.AddToClassList("ex-section");
        return p;
    }

    static Label Heading(VisualElement parent, string text)
    {
        var l = new Label(text.ToUpperInvariant()); l.AddToClassList("ex-heading"); parent.Add(l); return l;
    }

    static Label Text(VisualElement parent, string text, string cls = "ex-text")
    {
        var l = new Label(text); l.AddToClassList(cls); parent.Add(l); return l;
    }

    static Label KV(VisualElement parent, string key)
    {
        var row = new VisualElement(); row.AddToClassList("ex-kv");
        var k = new Label(key); k.AddToClassList("ex-k");
        var v = new Label("—"); v.AddToClassList("ex-v");
        row.Add(k); row.Add(v); parent.Add(row);
        return v;
    }

    /// <summary>Interruptor que funciona de verdad: get lee el estado actual, set lo aplica.</summary>
    sealed class Switch
    {
        public readonly VisualElement row, sw;
        readonly Func<bool> get;
        readonly Action<bool> set;

        public Switch(VisualElement parent, string text, string note, Func<bool> get, Action<bool> set)
        {
            this.get = get; this.set = set;
            row = new VisualElement(); row.AddToClassList("ex-toggle");
            var col = new VisualElement(); col.AddToClassList("ex-toggle-text");
            Text(col, text);
            if (!string.IsNullOrEmpty(note)) Text(col, note, "ex-note");
            sw = new VisualElement(); sw.AddToClassList("ex-switch");
            var knob = new VisualElement(); knob.AddToClassList("ex-knob"); sw.Add(knob);
            row.Add(col); row.Add(sw);
            row.RegisterCallback<ClickEvent>(_ => { set(!get()); Sync(); });
            parent.Add(row);
            Sync();
        }

        public void Sync() => sw.EnableInClassList("on", get());
    }

    // ---------- Capas ----------

    VisualElement layerList, classList;
    Label layerNote;
    Slider opacity;
    readonly List<(int layer, VisualElement row)> layerRows = new();
    readonly List<(GeoClass cls, VisualElement row)> classRows = new();
    readonly List<Switch> layerSwitches = new();
    int builtLayerCount = -2;

    VisualElement BuildLayersPage()
    {
        var p = Page();
        Heading(p, "Capa del terreno");
        layerList = new VisualElement(); p.Add(layerList);
        opacity = new Slider("Opacidad", 0.1f, 1f) { value = GeoLayers.Instance ? GeoLayers.Instance.opacity : 0.72f };
        opacity.AddToClassList("ex-slider");
        opacity.RegisterValueChangedCallback(e =>
        {
            if (GeoLayers.Instance) GeoLayers.Instance.opacity = e.newValue;
            ExplorerPrefs.layerOpacity = e.newValue;
        });
        p.Add(opacity);
        Heading(p, "Leyenda");
        layerNote = Text(p, "", "ex-note");
        classList = new VisualElement(); p.Add(classList);
        Heading(p, "Superposiciones");
        layerSwitches.Add(new Switch(p, "Ruta real de Curiosity sobre el terreno", "JPL MMGIS, del sol 0 al último publicado",
            () => curiosity && curiosity.showRibbon, SetCuriosityRibbon));
        layerSwitches.Add(new Switch(p, "Ruta de Curiosity en el mapa", null,
            () => minimap && minimap.showCuriosityRoute, SetCuriosityOnMap));
        layerSwitches.Add(new Switch(p, "Recorrido del rover en el mapa", null,
            () => minimap && minimap.showPath, SetPathOnMap));
        Text(p, "L cambia de capa sin abrir el menú.", "ex-note");
        return p;
    }

    void OnLayersChanged()
    {
        if (Current == Section.Layers) RebuildLayers();
    }

    void RebuildLayers()
    {
        var geo = GeoLayers.Instance;
        if (!geo) { layerList.Clear(); Text(layerList, "Las capas geológicas no están instaladas en la escena.", "ex-note"); return; }
        if (builtLayerCount != geo.LayerCount)
        {
            builtLayerCount = geo.LayerCount;
            layerList.Clear();
            layerRows.Clear();
            for (int i = -1; i < geo.LayerCount; i++)
            {
                int layer = i;
                var def = i >= 0 ? geo.Data.capas[i] : null;
                var row = new VisualElement(); row.AddToClassList("ex-layer");
                var radio = new VisualElement(); radio.AddToClassList("ex-radio"); row.Add(radio);
                var col = new VisualElement(); col.AddToClassList("ex-layer-text");
                Text(col, def != null ? def.nombre : "Imagen real", "ex-layer-name");
                Text(col, def != null ? def.fuente : "Mosaico CTX / HiRISE y relieve del terreno", "ex-layer-src");
                if (def?.clases != null)
                {
                    var sw = new VisualElement(); sw.AddToClassList("ex-swatches");
                    foreach (var c in def.clases)
                    {
                        var s = new VisualElement(); s.AddToClassList("ex-swatch");
                        s.style.backgroundColor = (Color)c.Color32;
                        sw.Add(s);
                    }
                    col.Add(sw);
                }
                row.Add(col);
                row.RegisterCallback<ClickEvent>(_ => { geo.Set(layer); RebuildLayers(); });
                layerList.Add(row);
                layerRows.Add((layer, row));
            }
        }
        foreach (var (layer, row) in layerRows) row.EnableInClassList("selected", layer == geo.Current);

        classList.Clear();
        classRows.Clear();
        var cur = geo.CurrentLayer;
        layerNote.text = cur != null ? $"{cur.titulo}. Límite del dato: {cur.aviso}." : "Sin capa temática: se ve la imagen real del terreno.";
        if (cur?.clases != null)
            foreach (var c in cur.clases)
            {
                var row = new VisualElement(); row.AddToClassList("ex-class");
                var s = new VisualElement(); s.AddToClassList("ex-class-swatch");
                s.style.backgroundColor = (Color)c.Color32;
                row.Add(s);
                var t = Text(row, c.Label); t.style.flexShrink = 1;
                classList.Add(row);
                classRows.Add((c, row));
            }
        if (opacity != null) opacity.SetValueWithoutNotify(geo.opacity);
    }

    void RefreshLayers()
    {
        var geo = GeoLayers.Instance;
        if (geo && builtLayerCount != geo.LayerCount) RebuildLayers();
        foreach (var s in layerSwitches) s.Sync();
        // Clase bajo el rover resaltada en la leyenda.
        if (geo && geo.Current >= 0)
        {
            int here = geo.ClassAt(geo.Current, RoverPos);
            foreach (var (cls, row) in classRows) row.EnableInClassList("current", cls.indice == here);
        }
    }

    // ---------- Geodesia ----------

    Label gLat, gLon, gElev, gSlope, gHeading, gBradbury, gCuriosity, gSol, gTime, gCursor, gCursorKey;

    VisualElement BuildGeodesyPage()
    {
        var p = Page();
        Heading(p, "Rover");
        gLat = KV(p, "Latitud");
        gLon = KV(p, "Longitud");
        gElev = KV(p, "Cota");
        gSlope = KV(p, "Pendiente del terreno");
        gHeading = KV(p, "Rumbo");
        gBradbury = KV(p, "A Bradbury Landing");
        gCuriosity = KV(p, "A la ruta de Curiosity");
        Heading(p, "Tiempo marciano");
        gSol = KV(p, "Sol de Curiosity");
        gTime = KV(p, "Hora solar media local");
        Heading(p, "Cursor del mapa");
        gCursor = KV(p, "Punto");
        gCursorKey = Text(p, "Abre el mapa (M) y pasa el cursor para medir cualquier punto.", "ex-note");
        Heading(p, "Sistema de referencia");
        KV(p, "Cuerpo").text = $"Marte, esfera de radio {MarsGeo.Radius / 1000.0:0.00} km";
        KV(p, "Coordenadas").text = "latitud planetocéntrica, longitud este";
        KV(p, "Proyección").text = "equirectangular";
        KV(p, "Escala de la escena").text = $"1 u = {1f / MarsGeo.WorldScale:0} m (1:{1f / MarsGeo.WorldScale:0})";
        KV(p, "Gravedad").text = chase && chase.target ? $"{chase.target.gravity:0.00} m/s²" : "3,71 m/s²";
        Text(p, "Cotas del modelo de terreno de la escena (DEM MSL de 1 m remuestreado a 8 m en la ruta de Curiosity; " +
                "relieve regional fuera). Distancias por círculo máximo sobre la esfera.", "ex-note");
        return p;
    }

    void RefreshGeodesy()
    {
        Vector3 p = RoverPos;
        var ll = MarsGeo.UnityToLonLat(p);
        gLat.text = Lat(ll.lat);
        gLon.text = Lon(ll.lon);
        gElev.text = $"{MarsGeo.Elevation(p.y):0} m";
        var nav = NavigationSystem.Instance;
        if (nav && nav.Grid != null)
        {
            var (x, z) = NavUnity.ToMeters(p);
            int n = nav.Grid.NodeAt(x, z);
            var c = n >= 0 ? nav.Grid.Cell(n) : default;
            gSlope.text = n >= 0 && !float.IsNaN(c.slopeDeg) ? $"{c.slopeDeg:0.0}° (celda de {(c.level == 1 ? "8 m" : "163 m")})" : "sin dato";
        }
        else gSlope.text = "malla de navegación no cargada";
        float heading = chase && chase.target ? Mathf.Repeat(chase.target.transform.eulerAngles.y, 360f) : 0f;
        gHeading.text = $"{heading:000}°";
        gBradbury.text = Km(GreatCircle(ll.lon, ll.lat, 137.4417, -4.5895));
        gCuriosity.text = curiosity && curiosity.Nearest(p, out float m, out int sol) ? $"{Km(m)} · sol {sol}" : "—";
        var now = DateTime.UtcNow;
        gSol.text = $"{MarsTime.CuriositySol(now)}";
        float lmst = MarsTime.LocalMeanSolarTime(now, ll.lon);
        gTime.text = $"{(int)lmst:00}:{(int)(lmst % 1f * 60f):00} (en la posición del rover)";
        if (minimap && minimap.Interactive && minimap.InMap(minimap.MapCursor, minimap.MapRect()))
        {
            Vector3 w = minimap.MapToWorld(minimap.MapCursor);
            var cl = MarsGeo.UnityToLonLat(w);
            gCursor.text = $"{Lat(cl.lat)} · {Lon(cl.lon)}\ncota {MarsGeo.Elevation(w.y):0} m · a {Km(GreatCircle(ll.lon, ll.lat, cl.lon, cl.lat))} del rover";
            gCursorKey.style.display = DisplayStyle.None;
        }
        else
        {
            gCursor.text = "—";
            gCursorKey.style.display = DisplayStyle.Flex;
        }
    }

    static string Lat(double v) => v < 0 ? $"{-v:0.00000}° S" : $"{v:0.00000}° N";
    static string Lon(double v) => v < 0 ? $"{-v:0.00000}° O" : $"{v:0.00000}° E";
    static string Km(double m) => m >= 1000 ? $"{m / 1000:0.00} km" : $"{m:0} m";

    /// <summary>Distancia por círculo máximo (haversine) sobre la esfera de MarsGeo, en metros.</summary>
    public static double GreatCircle(double lon1, double lat1, double lon2, double lat2)
    {
        double r = Math.PI / 180.0, dLat = (lat2 - lat1) * r, dLon = (lon2 - lon1) * r;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * r) * Math.Cos(lat2 * r) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * MarsGeo.Radius * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    // ---------- Rocas ----------

    Label rUnit, rUnitDesc, rSoil, rSoilDesc, rThermo, rZone, rNoData;

    VisualElement BuildRocksPage()
    {
        var p = Page();
        rNoData = Text(p, "", "ex-note");
        Heading(p, "Unidad geológica bajo el rover");
        rUnit = Text(p, "—", "ex-big");
        rUnitDesc = Text(p, "", "ex-text");
        Heading(p, "Material del suelo");
        rSoil = Text(p, "—", "ex-text");
        rSoilDesc = Text(p, "", "ex-note");
        rThermo = KV(p, "Índice termofísico");
        Text(p, "0 = grano fino o polvo, 1 = roca (THEMIS, relativo a la región).", "ex-note");
        Heading(p, "Zona y mineralogía medida");
        rZone = Text(p, "", "ex-text");
        rZone.enableRichText = true;
        return p;
    }

    string lastZoneId = "\0";

    void RefreshRocks(bool slow)
    {
        var geo = GeoLayers.Instance;
        if (!geo) { rNoData.text = "Las capas geológicas no están instaladas en la escena."; return; }
        rNoData.text = "";
        Vector3 p = RoverPos;
        var unit = Class(geo, "geologia", p);
        rUnit.text = unit != null ? unit.Label : "sin dato";
        rUnitDesc.text = unit != null ? Join(unit.nombreLargo, unit.descripcion, unit.nota) : "";
        var soil = Class(geo, "suelo", p);
        rSoil.text = soil != null ? soil.nombre : "sin dato";
        rSoilDesc.text = soil != null ? Join(soil.descripcion, soil.nota) : "";
        float? th = geo.ThermoAt(p);
        rThermo.text = th.HasValue ? $"{th.Value:0.00}" : "sin dato";
        if (slow)
        {
            var z = geo.ZoneAt(p);
            string id = z?.id ?? "";
            if (id != lastZoneId) { lastZoneId = id; rZone.text = geo.ZoneReport(p); }
        }
    }

    static GeoClass Class(GeoLayers geo, string id, Vector3 p)
    {
        int i = geo.LayerIndex(id);
        return i >= 0 ? geo.Data.capas[i].Class(geo.ClassAt(i, p)) : null;
    }

    static string Join(params string[] parts)
    {
        var sb = new StringBuilder();
        foreach (var s in parts)
            if (!string.IsNullOrWhiteSpace(s)) { if (sb.Length > 0) sb.Append("\n"); sb.Append(s); }
        return sb.ToString();
    }

    // ---------- Radiación ----------

    Label radLast, radB, radMean, radPrev, radTrend, radYear, radAge, radRoute, radSource, radNo;
    Label atT, atTs, atP, atTau, atFlux, atWind, atHour, atSource;
    Sparkline spark;

    VisualElement BuildRadiationPage()
    {
        var p = Page();
        radNo = Text(p, "", "ex-note");
        Heading(p, "Dosis medida por RAD (Curiosity)");
        radLast = Text(p, "—", "ex-big");
        Text(p, "dosis E (total) de la última lectura, µGy/día", "ex-note");
        spark = new Sparkline(); spark.AddToClassList("ex-spark"); p.Add(spark);
        Text(p, "Lecturas de las últimas 48 h (dosis E)", "ex-note");
        radB = KV(p, "Dosis B (sin el fondo del RTG)");
        radMean = KV(p, "Media de las últimas 24 h");
        radPrev = KV(p, "Media de las 24 h previas");
        radTrend = KV(p, "Tendencia");
        radYear = KV(p, "A esa tasa, en un año terrestre");
        radAge = KV(p, "Última lectura");
        radRoute = KV(p, "Dosis de la ruta planificada");
        radSource = Text(p, "", "ex-note");
        Heading(p, "Atmósfera ahora en Gale");
        atHour = KV(p, "Hora local (MCD)");
        atT = KV(p, "Temperatura del aire");
        atTs = KV(p, "Temperatura del suelo");
        atP = KV(p, "Presión");
        atTau = KV(p, "Polvo (profundidad óptica τ)");
        atFlux = KV(p, "Sol en el suelo");
        atWind = KV(p, "Viento");
        atSource = Text(p, "", "ex-note");
        return p;
    }

    void RefreshRadiation()
    {
        var cs = ClimateService.Instance;
        var data = cs ? cs.Data : null;
        if (data?.rad == null)
        {
            radNo.text = "No hay datos de radiación: falta climate_dataset.json (Tools/build_climate_dataset.py).";
            return;
        }
        radNo.text = "";
        var rad = data.rad;
        var now = DateTime.UtcNow;
        radLast.text = $"{rad.ultimaDosisE:0.0} µGy/día";
        radB.text = $"{rad.ultimaDosisB:0.0} µGy/día";
        radMean.text = rad.ultimas24h != null && rad.ultimas24h.n > 0
            ? $"{rad.ultimas24h.media:0.0} µGy/día ({rad.ultimas24h.min:0}–{rad.ultimas24h.max:0}, {rad.ultimas24h.n} lecturas)" : "—";
        radPrev.text = rad.previas24h != null && rad.previas24h.n > 0 ? $"{rad.previas24h.media:0.0} µGy/día" : "—";
        radTrend.text = $"{(rad.tendencia >= 0 ? "+" : "")}{rad.tendencia:0.00} µGy/día";
        radYear.text = rad.ultimas24h != null && rad.ultimas24h.n > 0 ? $"{rad.ultimas24h.media * 365.25f / 1000f:0} mGy" : "—";
        double age = cs.RadAgeHours(now);
        radAge.text = double.IsNaN(age) ? "—" : $"hace {age:0} h ({rad.ultimaLectura.Replace('T', ' ').TrimEnd('Z')} UTC)";
        var nav = NavigationSystem.Instance;
        radRoute.text = nav && nav.Route != null ? $"{nav.Route.DoseUGy:0.0} µGy en {NavMapOverlay.Hours(nav.Route.TimeS)} de misión" : "sin ruta (elige un destino en el mapa)";
        radSource.text = Join(data.fuentes?.rad, data.fuentes?.radAviso, "RAD mide en un solo punto: la tasa es la misma en todo el mapa.");
        if (rad.lecturas != null)
        {
            var v = new float[rad.lecturas.Length];
            for (int i = 0; i < v.Length; i++) v[i] = rad.lecturas[i].dosisE;
            spark.SetValues(v, "µGy/día");
        }

        // Atmósfera (MCD): modelo climático para esta hora.
        var pt = data.NearestPoint(MarsTime.CuriosityLon, -4.6);
        var h = pt?.At(now, cs.dustScenario);
        if (h == null)
        {
            atHour.text = "el dataset no cubre la fecha actual: regenéralo";
            atT.text = atTs.text = atP.text = atTau.text = atFlux.text = atWind.text = "—";
        }
        else
        {
            atHour.text = $"{(int)h.horaLocal:00}:{(int)(h.horaLocal % 1f * 60f):00} · {(h.flujoSolar > 20f ? "de día" : "de noche")}";
            atT.text = $"{h.TAireC:0} °C";
            atTs.text = $"{h.TSueloC:0} °C";
            atP.text = $"{h.presion:0} Pa";
            atTau.text = $"{h.polvo:0.00}{(cs.dustScenario ? " (escenario con polvo)" : "")}";
            atFlux.text = $"{h.flujoSolar:0} W/m²";
            atWind.text = $"{h.viento:0.0} m/s desde {h.vientoDesde:000}°";
        }
        atSource.text = Join(data.fuentes?.mcd, data.fuentes?.mcdAviso);
    }

    // ---------- Ajustes ----------

    readonly List<Switch> settingSwitches = new();

    VisualElement BuildSettingsPage()
    {
        var p = Page();
        Heading(p, "Mapa");
        settingSwitches.Add(new Switch(p, "Mostrar el minimapa",
            "Oculta el minimapa circular sin quitar el resto: los datos del rover pasan a la barra inferior y M sigue abriendo el mapa.",
            () => minimap && minimap.circleVisible, SetMinimapVisible));
        settingSwitches.Add(new Switch(p, "Ruta de Curiosity en el mapa", null,
            () => minimap && minimap.showCuriosityRoute, SetCuriosityOnMap));
        settingSwitches.Add(new Switch(p, "Recorrido del rover en el mapa", null,
            () => minimap && minimap.showPath, SetPathOnMap));
        Heading(p, "Terreno");
        settingSwitches.Add(new Switch(p, "Ruta de Curiosity sobre el terreno", null,
            () => curiosity && curiosity.showRibbon, SetCuriosityRibbon));
        Heading(p, "Clima");
        settingSwitches.Add(new Switch(p, "Escenario con polvo (MCD)",
            "Usa el escenario cálido con polvo del dataset en vez del climatológico (afecta al panel y a las rutas nuevas).",
            () => ClimateService.Instance && ClimateService.Instance.dustScenario, SetDust));
        Text(p, "Estas preferencias se recuerdan mientras dure la sesión.", "ex-note");
        return p;
    }

    void RefreshSettings()
    {
        foreach (var s in settingSwitches) s.Sync();
    }

    // ---------- Acciones (se guardan en la sesión) ----------

    public void SetMinimapVisible(bool on)
    {
        ExplorerPrefs.minimapVisible = on;
        if (minimap) minimap.circleVisible = on;
    }

    void SetCuriosityRibbon(bool on)
    {
        ExplorerPrefs.curiosityRibbon = on;
        if (curiosity) curiosity.SetRibbonVisible(on);
    }

    void SetCuriosityOnMap(bool on)
    {
        ExplorerPrefs.curiosityOnMap = on;
        if (minimap) minimap.showCuriosityRoute = on;
    }

    void SetPathOnMap(bool on)
    {
        ExplorerPrefs.pathOnMap = on;
        if (minimap) minimap.showPath = on;
    }

    void SetDust(bool on)
    {
        ExplorerPrefs.dustScenario = on;
        if (ClimateService.Instance) ClimateService.Instance.dustScenario = on;
        nextSlowRefresh = 0f;
    }

    void ApplyPrefs()
    {
        if (ExplorerPrefs.minimapVisible.HasValue) SetMinimapVisible(ExplorerPrefs.minimapVisible.Value);
        if (ExplorerPrefs.curiosityOnMap.HasValue) SetCuriosityOnMap(ExplorerPrefs.curiosityOnMap.Value);
        if (ExplorerPrefs.pathOnMap.HasValue) SetPathOnMap(ExplorerPrefs.pathOnMap.Value);
        if (ExplorerPrefs.dustScenario.HasValue) SetDust(ExplorerPrefs.dustScenario.Value);
        if (ExplorerPrefs.layerOpacity.HasValue && GeoLayers.Instance) GeoLayers.Instance.opacity = ExplorerPrefs.layerOpacity.Value;
        // La cinta de Curiosity se crea en su Start: se aplica después.
        if (ExplorerPrefs.curiosityRibbon.HasValue) Invoke(nameof(ApplyRibbonPref), 0.1f);
    }

    void ApplyRibbonPref()
    {
        if (ExplorerPrefs.curiosityRibbon.HasValue) SetCuriosityRibbon(ExplorerPrefs.curiosityRibbon.Value);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}

/// <summary>Iconos de la barra lateral dibujados con vectores (no dependen de los glifos de la fuente).</summary>
public class ExplorerIcon : VisualElement
{
    public enum Kind { Layers, Globe, Rock, Radiation, Gear }
    readonly Kind kind;
    static readonly Color Ink = new Color(0.91f, 0.92f, 0.93f);

    public ExplorerIcon(Kind k)
    {
        kind = k;
        pickingMode = PickingMode.Ignore;
        AddToClassList("ex-icon");
        generateVisualContent += Draw;
    }

    void Draw(MeshGenerationContext ctx)
    {
        var p = ctx.painter2D;
        float w = contentRect.width, h = contentRect.height, cx = w / 2f, cy = h / 2f, r = Mathf.Min(w, h) / 2f - 1.5f;
        p.strokeColor = Ink;
        p.fillColor = Ink;
        p.lineWidth = 2f;
        p.lineJoin = LineJoin.Round;
        p.lineCap = LineCap.Round;
        switch (kind)
        {
            case Kind.Layers:
                for (int i = 0; i < 2; i++)
                {
                    float y = cy - 4f + i * 7f;
                    p.BeginPath();
                    p.MoveTo(new Vector2(cx, y - 6f)); p.LineTo(new Vector2(cx + r, y)); p.LineTo(new Vector2(cx, y + 6f));
                    p.LineTo(new Vector2(cx - r, y)); p.ClosePath();
                    if (i == 0) { p.fillColor = new Color(Ink.r, Ink.g, Ink.b, 0.35f); p.Fill(); }
                    p.Stroke();
                }
                break;
            case Kind.Globe:
                p.BeginPath(); p.Arc(new Vector2(cx, cy), r, 0f, 360f); p.Stroke();
                p.BeginPath(); p.MoveTo(new Vector2(cx - r, cy)); p.LineTo(new Vector2(cx + r, cy)); p.Stroke();
                p.BeginPath(); p.MoveTo(new Vector2(cx - r * 0.85f, cy - r * 0.5f)); p.LineTo(new Vector2(cx + r * 0.85f, cy - r * 0.5f)); p.Stroke();
                p.BeginPath(); p.MoveTo(new Vector2(cx - r * 0.85f, cy + r * 0.5f)); p.LineTo(new Vector2(cx + r * 0.85f, cy + r * 0.5f)); p.Stroke();
                p.BeginPath(); p.MoveTo(new Vector2(cx, cy - r));
                p.BezierCurveTo(new Vector2(cx + r * 0.75f, cy - r * 0.5f), new Vector2(cx + r * 0.75f, cy + r * 0.5f), new Vector2(cx, cy + r));
                p.BezierCurveTo(new Vector2(cx - r * 0.75f, cy + r * 0.5f), new Vector2(cx - r * 0.75f, cy - r * 0.5f), new Vector2(cx, cy - r));
                p.Stroke();
                break;
            case Kind.Rock:
                p.BeginPath();
                p.MoveTo(new Vector2(cx - r, cy + r * 0.7f)); p.LineTo(new Vector2(cx - r * 0.6f, cy - r * 0.3f));
                p.LineTo(new Vector2(cx - r * 0.1f, cy - r * 0.8f)); p.LineTo(new Vector2(cx + r * 0.5f, cy - r * 0.45f));
                p.LineTo(new Vector2(cx + r, cy + r * 0.7f)); p.ClosePath();
                p.fillColor = new Color(Ink.r, Ink.g, Ink.b, 0.3f); p.Fill(); p.Stroke();
                p.BeginPath(); p.MoveTo(new Vector2(cx - r * 0.1f, cy - r * 0.8f)); p.LineTo(new Vector2(cx, cy + r * 0.7f)); p.Stroke();
                break;
            case Kind.Radiation:
                p.BeginPath(); p.Arc(new Vector2(cx, cy), r * 0.22f, 0f, 360f); p.Fill();
                for (int i = 0; i < 3; i++)
                {
                    float a0 = -90f + i * 120f - 30f, a1 = a0 + 60f;
                    p.BeginPath();
                    p.Arc(new Vector2(cx, cy), r, a0, a1);
                    p.Arc(new Vector2(cx, cy), r * 0.38f, a1, a0, ArcDirection.CounterClockwise);
                    p.ClosePath();
                    p.Fill();
                }
                break;
            case Kind.Gear:
                p.BeginPath();
                for (int i = 0; i < 16; i++)
                {
                    float a = i * Mathf.PI * 2f / 16f, rr = i % 2 == 0 ? r : r * 0.72f;
                    var v = new Vector2(cx + Mathf.Cos(a) * rr, cy + Mathf.Sin(a) * rr);
                    if (i == 0) p.MoveTo(v); else p.LineTo(v);
                }
                p.ClosePath(); p.Stroke();
                p.BeginPath(); p.Arc(new Vector2(cx, cy), r * 0.3f, 0f, 360f); p.Stroke();
                break;
        }
    }
}

/// <summary>Gráfica de línea pequeña (lecturas de RAD), con el mínimo y el máximo.</summary>
public class Sparkline : VisualElement
{
    float[] values = Array.Empty<float>();
    readonly Label maxL, minL;
    static readonly Color Line = new Color(0.54f, 0.71f, 0.97f);

    public Sparkline()
    {
        generateVisualContent += Draw;
        maxL = new Label { pickingMode = PickingMode.Ignore };
        minL = new Label { pickingMode = PickingMode.Ignore };
        foreach (var l in new[] { maxL, minL })
        {
            l.AddToClassList("ex-note");
            l.style.position = Position.Absolute;
            l.style.right = 6;
            Add(l);
        }
        maxL.style.top = 2;
        minL.style.bottom = 2;
    }

    public void SetValues(float[] v, string unit)
    {
        values = v ?? Array.Empty<float>();
        if (values.Length > 0)
        {
            float mn = float.MaxValue, mx = float.MinValue;
            foreach (var x in values) { mn = Mathf.Min(mn, x); mx = Mathf.Max(mx, x); }
            maxL.text = $"máx {mx:0} {unit}";
            minL.text = $"mín {mn:0}";
        }
        MarkDirtyRepaint();
    }

    void Draw(MeshGenerationContext ctx)
    {
        if (values.Length < 2) return;
        float mn = float.MaxValue, mx = float.MinValue;
        foreach (var x in values) { mn = Mathf.Min(mn, x); mx = Mathf.Max(mx, x); }
        if (mx - mn < 1e-3f) { mx += 1f; mn -= 1f; }
        var r = contentRect;
        float padX = 8f, padY = 18f;
        var p = ctx.painter2D;
        p.strokeColor = Line;
        p.lineWidth = 1.5f;
        p.lineJoin = LineJoin.Round;
        p.BeginPath();
        for (int i = 0; i < values.Length; i++)
        {
            float x = r.x + padX + (r.width - 2 * padX) * i / (values.Length - 1);
            float y = r.y + padY + (r.height - 2 * padY) * (1f - (values[i] - mn) / (mx - mn));
            if (i == 0) p.MoveTo(new Vector2(x, y)); else p.LineTo(new Vector2(x, y));
        }
        p.Stroke();
    }
}
