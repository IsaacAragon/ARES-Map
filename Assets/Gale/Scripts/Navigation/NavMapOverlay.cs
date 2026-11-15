using System.Text;
using Gale.Navigation;
using UnityEngine;

// La navegación en el minimapa (MinimapHUD.OverlayGUI): ruta violeta con borde oscuro, tramos de
// precaución en ámbar y no seguros en rojo con trazo discontinuo, parte recorrida atenuada, pines
// A / paradas / B, flecha en el aro hacia el siguiente punto en el minimapa circular, y en el mapa
// interactivo el resumen de la ruta, la línea recta mientras se arrastra un pin y los datos del terreno
// bajo el cursor (pendiente, material, unidad geológica, transitabilidad).
[RequireComponent(typeof(NavigationSystem))]
public class NavMapOverlay : MonoBehaviour
{
    public Color routeColor = new Color(0.72f, 0.45f, 1f, 1f);
    public Color cautionColor = new Color(1f, 0.72f, 0.2f, 1f);
    public Color unsafeColor = new Color(1f, 0.25f, 0.25f, 1f);
    public Color outlineColor = new Color(0.08f, 0.03f, 0.12f, 0.9f);
    [Tooltip("Segmentos máximos dibujados (la ruta se diezma si tiene más).")]
    public int maxSegments = 1500;

    NavigationSystem nav;
    MinimapHUD map;
    GUIStyle box, small;
    readonly StringBuilder sb = new StringBuilder();

    void Awake() => nav = GetComponent<NavigationSystem>();

    void OnEnable()
    {
        map = nav.minimap ? nav.minimap : FindAnyObjectByType<MinimapHUD>();
        if (map) map.OverlayGUI += Draw;
    }

    void OnDisable()
    {
        if (map) map.OverlayGUI -= Draw;
    }

    void Styles()
    {
        float s = MinimapHUD.UiScale;
        box ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, richText = true, wordWrap = true };
        small ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
        box.fontSize = Mathf.RoundToInt(13 * s);
        small.fontSize = Mathf.RoundToInt(12 * s);
        int p = Mathf.RoundToInt(8 * s);
        box.padding = new RectOffset(p, p, p, p);
    }

    void Draw(Rect r)
    {
        Styles();
        var w = nav.RouteWorld;
        bool repaint = Event.current.type == EventType.Repaint;
        if (repaint && w.Count >= 2) DrawRoute(r, w);

        // Mientras se arrastra un pin: recta discontinua desde el anterior.
        if (repaint && nav.Dragging && map.Interactive) DrawDragPreview(r);

        DrawPins(r);
        if (!map.Interactive) { DrawRingArrow(r); return; }
        DrawSummary(r);
        DrawInspector(r);
    }

    void DrawRoute(Rect r, System.Collections.Generic.IReadOnlyList<Vector3> w)
    {
        var route = nav.Route;
        int n = w.Count, stride = Mathf.Max(1, n / maxSegments), done = nav.Guiding ? nav.ProgressIndex : 0;
        float wide = map.Interactive ? 5f : 3.5f, thin = map.Interactive ? 3f : 2f;
        var old = GUI.color;
        for (int pass = 0; pass < 2; pass++)
        {
            Vector2 prev = map.WorldToMap(w[0], r);
            for (int i = stride; i < n + stride - 1; i += stride)
            {
                int k = Mathf.Min(i, n - 1);
                Vector2 cur = map.WorldToMap(w[k], r);
                Vector2 a = prev, b = cur;
                prev = cur;
                if (!map.ClipLine(ref a, ref b, r)) continue;
                var pt = route.Points[k];
                bool traveled = k <= done;
                if (pass == 0) { GUI.color = outlineColor; MinimapHUD.Line(a, b, wide); continue; }
                Color c = pt.unsafeStep ? unsafeColor : pt.caution ? cautionColor : routeColor;
                if (traveled) c = Color.Lerp(c, Color.gray, 0.6f) * new Color(1, 1, 1, 0.6f);
                GUI.color = c;
                // No depender solo del color: lo no seguro va discontinuo y lo de precaución más fino.
                if (pt.unsafeStep && (k / stride) % 2 == 1) continue;
                MinimapHUD.Line(a, b, pt.caution && !pt.unsafeStep ? thin * 0.7f : thin);
            }
        }
        GUI.color = old;
    }

    void DrawDragPreview(Rect r)
    {
        var stops = nav.Stops;
        var old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.8f);
        Vector3 from = nav.chase && nav.chase.target ? nav.chase.target.transform.position : stops[0];
        for (int i = 0; i < stops.Count; i++)
        {
            Vector2 a = map.WorldToMap(from, r), b = map.WorldToMap(stops[i], r);
            Vector2 d = b - a;
            int k = Mathf.Max(1, Mathf.RoundToInt(d.magnitude / 10f));
            for (int j = 0; j < k; j += 2)
            {
                Vector2 p = a + d * (j / (float)k), q = a + d * Mathf.Min(1f, (j + 1) / (float)k);
                if (map.ClipLine(ref p, ref q, r)) MinimapHUD.Line(p, q, 1.5f);
            }
            from = stops[i];
        }
        GUI.color = old;
    }

    void DrawPins(Rect r)
    {
        var stops = nav.Stops;
        if (stops.Count == 0) return;
        var w = nav.RouteWorld;
        if (w.Count > 0)
        {
            Vector2 a = map.WorldToMap(w[0], r);
            if (map.InMap(a, r, 6f)) Tag(a, "A", new Color(0.4f, 1f, 1f));
        }
        for (int i = 0; i < stops.Count; i++)
        {
            Vector2 p = map.WorldToMap(stops[i], r);
            if (!map.InMap(p, r, 6f)) continue;
            bool last = i == stops.Count - 1;
            bool passed = nav.Guiding && i < nav.NextStop;
            Tag(p, last ? "B" : (i + 1).ToString(), passed ? Color.gray : routeColor);
            if (last && nav.Route != null && map.Interactive)
            {
                var old = GUI.color;
                string t = $"{Km(nav.Route.DistanceM)} · {Hours(nav.Route.TimeS)}";
                GUI.color = Color.black; GUI.Label(new Rect(p.x + 13, p.y + 3, 200, 20), t, small);
                GUI.color = Color.white; GUI.Label(new Rect(p.x + 12, p.y + 2, 200, 20), t, small);
                GUI.color = old;
            }
        }
    }

    static void Tag(Vector2 p, string text, Color c)
    {
        MinimapHUD.Marker(p, "●", c, 0f);
        var old = GUI.color;
        GUI.color = Color.black;
        GUI.Label(new Rect(p.x - 4, p.y - 26, 40, 20), text);
        GUI.color = Color.white;
        GUI.Label(new Rect(p.x - 5, p.y - 27, 40, 20), text);
        GUI.color = old;
    }

    // Minimapa circular: flecha en el aro hacia el siguiente punto de la ruta cuando queda fuera del círculo.
    void DrawRingArrow(Rect r)
    {
        if (nav.Route == null || nav.Stops.Count == 0) return;
        Vector3 target = nav.Stops[Mathf.Min(nav.NextStop, nav.Stops.Count - 1)];
        Vector2 p = map.WorldToMap(target, r);
        if (map.InMap(p, r, 6f)) return;
        Vector2 d = (p - r.center).normalized;
        Vector2 at = r.center + d * (r.width * 0.5f - 10f);
        float ang = Mathf.Atan2(d.x, -d.y) * Mathf.Rad2Deg;
        MinimapHUD.Marker(at, "▲", routeColor, ang);
    }

    void DrawSummary(Rect r)
    {
        float s = MinimapHUD.UiScale;
        float wBox = 300f * s;
        bool right = r.xMax + 12f + wBox < Screen.width;
        var rect = right ? new Rect(r.xMax + 12f, r.y, wBox, r.height * 0.75f) : new Rect(r.x + 8, r.yMax - 230f * s, wBox, 220f * s);
        sb.Clear();
        var st = nav.Current;
        sb.Append("<b>RUTA</b>  ·  ").Append(nav.Vehicle.nombre).Append(" · <b>").Append(nav.Weights.nombre).Append("</b>  (P)\n");
        if (st == NavigationSystem.State.Loading) sb.Append("Cargando la malla de navegación…\n");
        else if (st == NavigationSystem.State.Disabled) sb.Append("<color=#ff8080>").Append(nav.Message).Append("</color>\n");
        else if (st == NavigationSystem.State.Calculating) sb.Append("<color=#d8c0ff>Calculando ruta…</color>  Retroceso cancela\n");
        else if (nav.Stops.Count == 0) sb.Append("Clic en el mapa para elegir el destino.\nEl origen es el rover.\n");

        var route = nav.Route;
        if (route != null)
        {
            if (route.Unsafe) sb.Append("<color=#ff6060><b>RUTA NO SEGURA</b></color>\n");
            sb.Append($"Distancia <b>{Km(route.DistanceM)}</b>   pendiente máx. <b>{route.MaxSlopeDeg:0}°</b>\n");
            sb.Append($"Tiempo de misión <b>{Hours(route.TimeS)}</b>   simulación {Hours(nav.SimSeconds)}\n");
            sb.Append($"Desnivel +{route.AscentM:0} m / −{route.DescentM:0} m\n");
            sb.Append($"Energía <b>{route.EnergyJ / 3.6e6f:0.0} kWh</b>   dosis <b>{route.DoseUGy:0.0} µGy</b>\n");
            float cost = Mathf.Max(1e-3f, route.Cost);
            sb.Append($"Coste: tiempo {100f * route.CostTime / cost:0}% · energía {100f * route.CostEnergy / cost:0}% · " +
                      $"riesgo {100f * route.CostRisk / cost:0}% · dosis {100f * route.CostDose / cost:0}%\n");
            sb.Append("Suelo: ");
            string[] soil = { "sin dato", "arena fina", "regolito", "consolidado", "roca" };
            bool first = true;
            for (int i = 4; i >= 0; i--)
                if (route.SoilM[i] > 50f) { sb.Append(first ? "" : " · ").Append($"{soil[i]} {Km(route.SoilM[i])}"); first = false; }
            sb.Append('\n');
            if (route.ProvenM > 50f) sb.Append($"{Km(route.ProvenM)} sobre la ruta de Curiosity\n");
            if (nav.Guiding) sb.Append($"<color=#c0ffc0>GUIADO · quedan {Km(nav.RemainingMeters)} · {Hours(nav.RemainingSeconds)}</color>\n");
            else if (st == NavigationSystem.State.Preview) sb.Append("<color=#c0ffc0>Intro inicia el guiado</color> · V vista aérea\n");
            foreach (var wmsg in route.Warnings) sb.Append("<color=#ffd080>• ").Append(wmsg).Append("</color>\n");
        }
        if (!string.IsNullOrEmpty(nav.Message) && st != NavigationSystem.State.Disabled)
            sb.Append("<color=#ffb080>").Append(nav.Message).Append("</color>\n");
        if (!string.IsNullOrEmpty(nav.ClimateStatus)) sb.Append("<size=").Append(Mathf.RoundToInt(11 * s)).Append(">").Append(nav.ClimateStatus).Append("</size>\n");
        if (route != null)
        {
            var cs = ClimateService.Instance;
            double age = cs ? cs.RadAgeHours(System.DateTime.UtcNow) : double.NaN;
            sb.Append("<size=").Append(Mathf.RoundToInt(11 * s)).Append("><color=#c8b8a8>");
            if (!double.IsNaN(age)) sb.Append($"Dosis con RAD de hace {age:0} h (tasa única para todo el mapa). ");
            sb.Append("MCD = modelo climático, no pronóstico. Umbrales y coeficientes del equipo, sin calibrar.</color></size>");
        }
        var old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.95f);
        float h = box.CalcHeight(new GUIContent(sb.ToString()), rect.width);
        rect.height = Mathf.Min(Mathf.Max(h, 40f), Screen.height - rect.y - 8f);
        GUI.Box(rect, sb.ToString(), box);
        GUI.color = old;
    }

    // Datos del terreno bajo el cursor.
    void DrawInspector(Rect r)
    {
        var grid = nav.Grid;
        Vector2 c = map.MapCursor;
        if (grid == null || !map.InMap(c, r) || nav.Dragging) return;
        Vector3 world = map.MapToWorld(c);
        var (x, z) = NavUnity.ToMeters(world);
        int n = grid.NodeAt(x, z);
        if (n < 0) return;
        var cell = grid.Cell(n);
        sb.Clear();
        if (float.IsNaN(cell.h)) sb.Append("sin relieve");
        else
        {
            sb.Append($"pendiente {cell.slopeDeg:0}° · ");
            string[] soil = { "sin dato", "arena fina", "regolito", "consolidado", "roca" };
            sb.Append(soil[cell.soil < 5 ? cell.soil : 0]);
            var geo = GeoLayers.Instance;
            if (geo)
            {
                int gi = geo.LayerIndex("geologia");
                var u = gi >= 0 ? geo.Data.capas[gi].Class(cell.geo) : null;
                if (u != null) sb.Append(" · ").Append(u.codigo);
                int tl = nav.TransitabilityLayer;
                if (tl >= 0)
                {
                    var t = geo.Data.capas[tl].Class(geo.ClassAt(tl, world));
                    if (t != null) sb.Append(" · ").Append(t.nombre.ToLowerInvariant());
                }
            }
            sb.Append(cell.level == 1 ? " · relieve 8 m" : " · relieve regional");
            if (cell.proven) sb.Append(" · ruta de Curiosity");
        }
        var old = GUI.color;
        var lr = new Rect(c.x + 16f, c.y + 10f, 420f, 22f);
        GUI.color = Color.black; GUI.Label(new Rect(lr.x + 1, lr.y + 1, lr.width, lr.height), sb.ToString(), small);
        GUI.color = new Color(0.95f, 0.9f, 1f); GUI.Label(lr, sb.ToString(), small);
        GUI.color = old;
    }

    public static string Km(float m) => m >= 1000f ? $"{m / 1000f:0.0} km" : $"{m:0} m";

    public static string Hours(float s)
    {
        if (s < 60f) return $"{s:0} s";
        int min = Mathf.RoundToInt(s / 60f);
        return min < 60 ? $"{min} min" : $"{min / 60} h {min % 60:00} min";
    }
}
