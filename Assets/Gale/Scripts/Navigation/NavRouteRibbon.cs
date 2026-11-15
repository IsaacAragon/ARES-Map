using System.Collections.Generic;
using Gale.Navigation;
using UnityEngine;

// La ruta planificada sobre el terreno (vista aérea y de seguimiento): cinta con GroundRibbon, que se
// traza del rover al destino al terminar el cálculo (_Reveal), color por tramo (violeta; ámbar con
// pendiente de precaución; rojo si no es segura), rayas que avanzan en el sentido de marcha, y en la
// vista aérea los pines A, paradas y B y una marca cada N km.
[RequireComponent(typeof(NavigationSystem))]
public class NavRouteRibbon : MonoBehaviour
{
    public Material material;
    public float width = 0.6f;
    public float lift = 0.04f;
    [Tooltip("Separación de las muestras de la cinta (m reales).")]
    public float sampleMeters = 8f;
    public Color routeColor = new Color(0.72f, 0.45f, 1f, 0.85f);
    public Color cautionColor = new Color(1f, 0.72f, 0.2f, 0.9f);
    public Color unsafeColor = new Color(1f, 0.25f, 0.25f, 0.95f);
    public float revealMinSeconds = 1.5f, revealMaxSeconds = 3f;
    [Tooltip("Marca de distancia en la vista aérea cada tantos km.")]
    public float kmMarkEvery = 1f;

    NavigationSystem nav;
    GameObject go;
    Material mat;
    readonly List<Vector3> pts = new List<Vector3>();
    readonly List<float> ptDist = new List<float>();   // m reales desde el origen
    float length, revealStart, revealDuration;
    static GUIStyle style;
    static readonly int RevealId = Shader.PropertyToID("_Reveal");

    void Awake() => nav = GetComponent<NavigationSystem>();

    void OnEnable() => nav.RouteReady += OnRoute;

    void OnDisable()
    {
        nav.RouteReady -= OnRoute;
        Clear();
    }

    void OnDestroy()
    {
        if (mat) Destroy(mat);
    }

    void Clear()
    {
        if (go) { Destroy(go.GetComponent<MeshFilter>().sharedMesh); Destroy(go); }
        go = null;
        pts.Clear();
        ptDist.Clear();
    }

    void OnRoute(NavRoute r)
    {
        Clear();
        if (r == null || r.Points.Count < 2 || !material) return;
        if (!mat)
        {
            mat = new Material(material) { hideFlags = HideFlags.DontSave };
            mat.SetFloat("_UseVertexColor", 1f);
            mat.SetColor("_Color", Color.white);
            mat.SetFloat("_Dash", 0.35f);
            mat.SetFloat("_DashSpeed", 0.8f);
        }

        // Remuestreo cada sampleMeters en el detalle; los tramos regionales ya vienen cada medio paso de celda.
        var colors = new List<Color>();
        float step = sampleMeters;
        NavPoint prev = r.Points[0];
        Add(prev, colors);
        for (int i = 1; i < r.Points.Count; i++)
        {
            NavPoint p = r.Points[i];
            float d = p.dist - prev.dist;
            int k = p.level == 1 ? Mathf.Max(1, Mathf.CeilToInt(d / step)) : 1;
            for (int s = 1; s <= k; s++)
            {
                float t = (float)s / k;
                var q = p;
                q.x = Mathf.Lerp(prev.x, p.x, t); q.z = Mathf.Lerp(prev.z, p.z, t); q.h = Mathf.Lerp(prev.h, p.h, t);
                q.dist = Mathf.Lerp(prev.dist, p.dist, t);
                Add(q, colors);
            }
            prev = p;
        }
        var mesh = GroundRibbon.Build(pts, width, lift, colors, "NavRoute");
        go = GroundRibbon.Spawn("NavRoute_Cinta", transform, mesh, mat, width * 0.5f);
        length = GroundRibbon.Length(pts);
        Replay();
    }

    void Add(in NavPoint p, List<Color> colors)
    {
        pts.Add(NavUnity.ToWorld(p));
        ptDist.Add(p.dist);
        colors.Add(p.unsafeStep ? unsafeColor : p.caution ? cautionColor : routeColor);
    }

    /// <summary>Vuelve a trazar la cinta del rover al destino.</summary>
    public void Replay()
    {
        revealStart = Time.unscaledTime;
        revealDuration = Mathf.Clamp(length / 600f, revealMinSeconds, revealMaxSeconds);
        if (mat) mat.SetFloat(RevealId, 0f);
    }

    void Update()
    {
        if (!mat || !go) return;
        float t = revealDuration > 0f ? Mathf.Clamp01((Time.unscaledTime - revealStart) / revealDuration) : 1f;
        float e = 1f - (1f - t) * (1f - t);   // sale rápido y frena al llegar
        mat.SetFloat(RevealId, t >= 1f ? 1e6f : e * length + 0.01f);
    }

    /// <summary>Punto de la cabeza del trazado (para que la cámara lo siga).</summary>
    public Vector3 Head
    {
        get
        {
            if (pts.Count == 0) return Vector3.zero;
            float t = revealDuration > 0f ? Mathf.Clamp01((Time.unscaledTime - revealStart) / revealDuration) : 1f;
            return pts[Mathf.Clamp(Mathf.RoundToInt((1f - (1f - t) * (1f - t)) * (pts.Count - 1)), 0, pts.Count - 1)];
        }
    }

    // Pines y marcas de km en la vista aérea, como el marcador "Rover" de ChaseCamera.
    void OnGUI()
    {
        if (pts.Count < 2 || SimPause.IsPaused || !nav.chase || !nav.chase.Aerial) return;
        var cam = nav.chase.GetComponent<Camera>();
        if (!cam) return;
        style ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        Pin(cam, pts[0], "A", new Color(0.4f, 1f, 1f));
        var stops = nav.Stops;
        for (int i = 0; i < stops.Count; i++)
            Pin(cam, GroundRibbon.OnGround(stops[i]), i == stops.Count - 1 ? "B" : (i + 1).ToString(), routeColor);
        if (kmMarkEvery > 0f)
        {
            float next = kmMarkEvery * 1000f;
            for (int i = 0; i < pts.Count; i++)
                if (ptDist[i] >= next && ptDist[i] < ptDist[ptDist.Count - 1] - 300f)
                {
                    Pin(cam, pts[i], $"{next / 1000f:0} km", new Color(0.85f, 0.75f, 1f, 0.85f), "·");
                    next += kmMarkEvery * 1000f;
                }
        }
    }

    static void Pin(Camera cam, Vector3 world, string label, Color c, string glyph = "◆")
    {
        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z <= 0f) return;
        var r = new Rect(sp.x - 9f, Screen.height - sp.y - 9f, 18f, 18f);
        var old = GUI.color;
        GUI.color = Color.black;
        GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), glyph);
        GUI.Label(new Rect(r.xMax + 1, r.y + 1, 80f, 20f), label, style);
        GUI.color = c;
        GUI.Label(r, glyph);
        GUI.Label(new Rect(r.xMax, r.y, 80f, 20f), label, style);
        GUI.color = old;
    }
}
