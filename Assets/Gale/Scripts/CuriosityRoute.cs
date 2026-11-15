using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// Ruta real del rover Curiosity (MSL) en Gale, del aterrizaje en Bradbury (sol 0) al último
// punto publicado. Datos de JPL (MMGIS: MSL_traverse.json y MSL_waypoints.json), simplificados a
// CSV en Assets/Gale/Route (lon, lat, cota; y los waypoints con su sol).
//  - Cinta apoyada en el terreno (raycast cada `sampleMeters` m reales, GroundRibbon), material Gale/RouteLine.
//  - Points/Waypoints para el minimapa (MinimapHUD) y Nearest() para el instrumento de navegación.
public class CuriosityRoute : MonoBehaviour
{
    public TextAsset routeCsv;
    public TextAsset waypointsCsv;
    public Material material;
    [Tooltip("Mostrar la cinta sobre el terreno.")]
    public bool showRibbon = true;
    [Tooltip("Ancho de la cinta (u Unity; 0,5 u = 25 m reales).")]
    public float width = 0.5f;
    [Tooltip("Separación de las muestras a lo largo de la ruta, en metros reales.")]
    public float sampleMeters = 5f;
    [Tooltip("Altura de la cinta sobre el suelo (u).")]
    public float lift = 0.03f;

    public struct Waypoint
    {
        public int sol;
        public Vector3 unity;
        public double lon, lat, elev, distKm;
    }

    /// <summary>Vértices de la ruta en coordenadas Unity (Y = suelo).</summary>
    public IReadOnlyList<Vector3> Points => points;
    /// <summary>Sol aproximado de cada vértice (por la distancia recorrida).</summary>
    public IReadOnlyList<int> PointSols => pointSols;
    public IReadOnlyList<Waypoint> Waypoints => waypoints;
    public int LastSol => waypoints.Count > 0 ? waypoints[waypoints.Count - 1].sol : 0;
    public double LengthKm { get; private set; }

    readonly List<Vector3> points = new List<Vector3>();
    readonly List<int> pointSols = new List<int>();
    readonly List<Waypoint> waypoints = new List<Waypoint>();
    GameObject ribbonGo;

    void Start()
    {
        Load();
        if (showRibbon) BuildRibbon();
    }

    void OnDestroy()
    {
        if (ribbonGo) Destroy(ribbonGo);
    }

    public void SetRibbonVisible(bool on)
    {
        showRibbon = on;
        if (on && !ribbonGo) BuildRibbon();
        if (ribbonGo) ribbonGo.SetActive(on);
    }

    void Load()
    {
        points.Clear(); pointSols.Clear(); waypoints.Clear();
        var ci = CultureInfo.InvariantCulture;
        if (waypointsCsv)
            foreach (var line in Lines(waypointsCsv.text))
            {
                var f = line.Split(',');
                var w = new Waypoint
                {
                    sol = int.Parse(f[0], ci),
                    lon = double.Parse(f[3], ci), lat = double.Parse(f[4], ci),
                    elev = double.Parse(f[5], ci), distKm = double.Parse(f[6], ci),
                };
                w.unity = OnGround(MarsGeo.LonLatToUnity(w.lon, w.lat));
                waypoints.Add(w);
            }

        var raw = new List<Vector3>();
        if (routeCsv)
            foreach (var line in Lines(routeCsv.text))
            {
                var f = line.Split(',');
                raw.Add(MarsGeo.LonLatToUnity(double.Parse(f[0], ci), double.Parse(f[1], ci)));
            }
        if (raw.Count < 2) return;

        // Remuestreo cada sampleMeters reales y apoyo en el suelo.
        float step = sampleMeters * MarsGeo.WorldScale;
        double total = 0;
        points.Add(OnGround(raw[0]));
        float carry = 0f;
        for (int i = 1; i < raw.Count; i++)
        {
            Vector3 a = raw[i - 1], b = raw[i];
            float len = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
            total += len;
            float t = step - carry;
            while (t <= len)
            {
                points.Add(OnGround(Vector3.Lerp(a, b, t / len)));
                t += step;
            }
            carry = len - (t - step);
        }
        points.Add(OnGround(raw[raw.Count - 1]));
        LengthKm = total / MarsGeo.WorldScale / 1000.0;

        // Sol aproximado por distancia recorrida (los waypoints traen dist_km acumulada).
        double scale = waypoints.Count > 0 && LengthKm > 0 ? waypoints[waypoints.Count - 1].distKm / LengthKm : 1.0;
        int wi = 0; double d = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (i > 0) d += Vector2.Distance(new Vector2(points[i - 1].x, points[i - 1].z), new Vector2(points[i].x, points[i].z)) / MarsGeo.WorldScale / 1000.0;
            while (wi + 1 < waypoints.Count && waypoints[wi + 1].distKm <= d * scale) wi++;
            pointSols.Add(waypoints.Count > 0 ? waypoints[wi].sol : 0);
        }
    }

    static IEnumerable<string> Lines(string text)
    {
        bool header = true;
        foreach (var l in text.Split('\n'))
        {
            var s = l.Trim();
            if (s.Length == 0) continue;
            if (header) { header = false; continue; }
            yield return s;
        }
    }

    static Vector3 OnGround(Vector3 p) => GroundRibbon.OnGround(p);

    void BuildRibbon()
    {
        if (points.Count < 2 || !material) return;
        var mesh = GroundRibbon.Build(points, width, lift, null, "CuriosityRoute");
        ribbonGo = GroundRibbon.Spawn("CuriosityRoute_Cinta", transform, mesh, material, width * 0.5f);
    }

    /// <summary>Punto de la ruta más cercano (en planta): distancia real en m y sol aproximado.</summary>
    public bool Nearest(Vector3 p, out float meters, out int sol)
    {
        meters = float.MaxValue; sol = 0;
        if (points.Count == 0) return false;
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            float dx = points[i].x - p.x, dz = points[i].z - p.z;
            float d = dx * dx + dz * dz;
            if (d < bd) { bd = d; best = i; }
        }
        meters = Mathf.Sqrt(bd) / MarsGeo.WorldScale;
        sol = pointSols[best];
        return true;
    }
}
