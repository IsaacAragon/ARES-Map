using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

// Minimapa abajo a la derecha: vista cenital (norte arriba, como la vista aérea con yaw 0)
// centrada en el rover. Una cámara ortográfica propia, creada en tiempo de ejecución y sin
// guardarse en la escena, pinta a una RenderTexture que se dibuja con IMGUI.
// Teclas: - / + (o = ) para alejar/acercar.
// En vista aérea además marca dónde está mirando la cámara principal.
[RequireComponent(typeof(ChaseCamera))]
public class MinimapHUD : MonoBehaviour
{
    [Header("Minimapa")]
    public bool show = true;
    [Tooltip("Lado del minimapa en píxeles de pantalla.")]
    public int sizePixels = 260;
    public int margin = 12;
    [Tooltip("Mitad del lado visible, en metros reales.")]
    public float radiusMeters = 10000f;
    public float minRadiusMeters = 500f;
    public float maxRadiusMeters = 60000f;
    [Tooltip("Girar el mapa con el rumbo del rover en vez de mantener el norte arriba.")]
    public bool rotateWithRover = false;
    [Tooltip("Pintar sin niebla (el polvo taparía el suelo visto desde arriba).")]
    public bool disableFog = true;

    ChaseCamera chase;
    Camera mapCam;
    RenderTexture rt;
    bool fogWas;
    bool prevMinus, prevPlus;

    float WorldScale => chase ? chase.worldScale : 0.02f;

    void Awake()
    {
        chase = GetComponent<ChaseCamera>();
    }

    void OnEnable()
    {
        rt = new RenderTexture(512, 512, 24) { name = "Minimap_RT", antiAliasing = 2 };
        var go = new GameObject("Minimap Camera") { hideFlags = HideFlags.DontSave };
        mapCam = go.AddComponent<Camera>();
        mapCam.orthographic = true;
        mapCam.targetTexture = rt;
        mapCam.clearFlags = CameraClearFlags.SolidColor;
        mapCam.backgroundColor = new Color(0.08f, 0.05f, 0.04f);
        mapCam.nearClipPlane = 0.5f;
        mapCam.farClipPlane = 1000f;
        mapCam.depth = -10f;
        // El rover (capa 2) a esta escala es un punto: se dibuja con un marcador.
        mapCam.cullingMask = ~(1 << 2);
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        RenderPipelineManager.endCameraRendering += OnEndCamera;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RenderPipelineManager.endCameraRendering -= OnEndCamera;
        if (mapCam) Destroy(mapCam.gameObject);
        if (rt) { rt.Release(); Destroy(rt); }
    }

    void OnBeginCamera(ScriptableRenderContext ctx, Camera c)
    {
        if (c != mapCam || !disableFog) return;
        fogWas = RenderSettings.fog;
        RenderSettings.fog = false;
    }

    void OnEndCamera(ScriptableRenderContext ctx, Camera c)
    {
        if (c != mapCam || !disableFog) return;
        RenderSettings.fog = fogWas;
    }

    void LateUpdate()
    {
        if (!mapCam) return;
        mapCam.enabled = show && chase && chase.target;
        if (!mapCam.enabled) return;

        var kb = Keyboard.current;
        if (kb != null)
        {
            bool minus = kb.minusKey.isPressed || kb.numpadMinusKey.isPressed;
            bool plus = kb.equalsKey.isPressed || kb.numpadPlusKey.isPressed;
            if (minus && !prevMinus) radiusMeters = Mathf.Min(radiusMeters * 2f, maxRadiusMeters);
            if (plus && !prevPlus) radiusMeters = Mathf.Max(radiusMeters * 0.5f, minRadiusMeters);
            prevMinus = minus; prevPlus = plus;
        }

        Vector3 p = chase.target.transform.position;
        float yaw = rotateWithRover ? chase.target.transform.eulerAngles.y : 0f;
        // Por encima del punto más alto del terreno (Z máx. 1347 m → ~27 u a 1:50).
        mapCam.transform.SetPositionAndRotation(new Vector3(p.x, 200f, p.z), Quaternion.Euler(90f, yaw, 0f));
        mapCam.orthographicSize = radiusMeters * WorldScale;
    }

    void OnGUI()
    {
        if (!show || !mapCam || !mapCam.enabled || !rt) return;
        float s = sizePixels;
        var r = new Rect(Screen.width - s - margin, Screen.height - s - margin, s, s);

        var old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(r.x - 3, r.y - 3, s + 6, s + 6), Texture2D.whiteTexture);
        GUI.color = old;
        GUI.DrawTexture(r, rt, ScaleMode.StretchToFill, false);

        Transform t = chase.target.transform;
        float mapYaw = rotateWithRover ? t.eulerAngles.y : 0f;

        // En vista aérea: punto al que mira la cámara principal (proyección sobre el suelo).
        if (chase.Aerial)
        {
            Vector3 focus = AerialFocusOnGround();
            Vector2 fp = WorldToMap(focus, t.position, mapYaw, r);
            if (r.Contains(fp)) Marker(fp, "✚", Color.yellow, 0f);
        }

        // Rover en el centro con flecha de rumbo.
        Vector2 c = r.center;
        Marker(c, "▲", Color.cyan, t.eulerAngles.y - mapYaw);

        // Norte, escala y teclas.
        GUI.Label(new Rect(r.center.x - 6, r.y + 2, 20, 20), "N");
        float bar = s * 0.25f;
        float barMeters = radiusMeters * 2f * 0.25f;
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(r.x + 8, r.yMax - 10, bar, 3), Texture2D.whiteTexture);
        GUI.Label(new Rect(r.x + 8, r.yMax - 30, 200, 20),
            barMeters >= 1000f ? $"{barMeters / 1000f:0.#} km" : $"{barMeters:0} m");
        GUI.Label(new Rect(r.xMax - 60, r.yMax - 22, 60, 20), "− / +");
        GUI.color = old;
    }

    Vector3 AerialFocusOnGround()
    {
        var ray = new Ray(transform.position, transform.forward);
        if (Physics.Raycast(ray, out var hit, 20000f, ~(1 << 2))) return hit.point;
        return transform.position;
    }

    Vector2 WorldToMap(Vector3 world, Vector3 center, float mapYaw, Rect r)
    {
        Vector3 d = Quaternion.Euler(0f, -mapYaw, 0f) * (world - center);
        float half = radiusMeters * WorldScale;
        return new Vector2(r.center.x + d.x / half * r.width * 0.5f,
                           r.center.y - d.z / half * r.height * 0.5f);
    }

    static GUIStyle markerStyle;

    static void Marker(Vector2 pos, string glyph, Color color, float angleDeg)
    {
        var m = GUI.matrix;
        var old = GUI.color;
        GUIUtility.RotateAroundPivot(angleDeg, pos);
        markerStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16, padding = new RectOffset() };
        var rr = new Rect(pos.x - 12f, pos.y - 12f, 24f, 24f);
        GUI.color = Color.black; GUI.Label(new Rect(rr.x + 1, rr.y + 1, rr.width, rr.height), glyph, markerStyle);
        GUI.color = color; GUI.Label(rr, glyph, markerStyle);
        GUI.matrix = m;
        GUI.color = old;
    }
}
