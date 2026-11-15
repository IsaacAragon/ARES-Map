using System;
using UnityEngine;
using UnityEngine.Rendering;

// Minimapa circular arriba a la derecha: vista cenital centrada en el rover (el aro y los datos
// del borde los dibuja NavInstrumentHUD). Una cámara ortográfica
// propia, creada en ejecución y sin guardarse en la escena, pinta a una RenderTexture que
// se dibuja con IMGUI.
//
// Durante la simulación el cursor está oculto y bloqueado. Con M (R3 en el mando) se entra al mapa:
// se agranda, aparece el cursor y se navega sin mover el rover:
//   clic derecho + arrastrar / stick izq. = desplazar · rueda / gatillos = zoom sobre el cursor
//   WASD = desplazar · Q/E / LB-RB = girar · F / X = rover · C / Y = cráter · M / Esc / B = salir
//   Con mando, el stick derecho mueve un cursor virtual y A selecciona (acciones del mapa Map).
// Fuera del mapa, - / + siguen acercando y alejando.
// Navegación A→B (NavMapOverlay): MapToWorld / WorldToMap, el evento MapClicked (clic izquierdo) y
// OverlayGUI, que dibuja encima del mapa (después del recorrido y antes del rover) con Line / Marker / ClipLine.
[RequireComponent(typeof(ChaseCamera))]
public class MinimapHUD : MonoBehaviour
{
    [Header("Minimapa (círculo arriba a la derecha)")]
    public bool show = true;
    [Tooltip("Diámetro del minimapa en píxeles a 1080 p (escala con el alto de la pantalla).")]
    public float diameterAt1080 = 236f;
    [Tooltip("Espacio a la derecha y arriba del círculo para los datos del borde (px a 1080 p).")]
    public float rightSpace = 128f, topSpace = 48f;
    public int margin = 12;
    [Tooltip("Material con Hidden/Gale/CircleMask (lo asigna el builder; si falta se crea en ejecución).")]
    public Material circleMaterial;
    [Tooltip("Mitad del lado visible, en metros reales.")]
    public float radiusMeters = 10000f;
    public float minRadiusMeters = 500f;
    public float maxRadiusMeters = 60000f;
    [Tooltip("Girar el mapa con el rumbo del rover en vez de mantener el giro elegido.")]
    public bool rotateWithRover = false;
    [Tooltip("Pintar sin niebla (el polvo taparía el suelo visto desde arriba).")]
    public bool disableFog = true;

    [System.Serializable]
    public struct Place
    {
        public string name;
        public double lon, lat;   // grados, Este positivo
        public Place(string n, double lon, double lat) { name = n; this.lon = lon; this.lat = lat; }
    }

    [Header("Lugares (IAU, planetocéntricos)")]
    public Place[] places =
    {
        new Place("Gale", 137.80, -5.40),
        new Place("Aeolis Mons", 137.85, -5.08),
        new Place("Babati", 137.93, -3.16),
        new Place("Robert Sharp", 133.59, -4.12),
    };

    [Header("Mapa interactivo")]
    [Tooltip("Lado del mapa interactivo como fracción del alto de la pantalla.")]
    [Range(0.3f, 0.95f)] public float interactiveFraction = 0.8f;
    [Tooltip("Ocultar y bloquear el cursor fuera del mapa.")]
    public bool hideCursorInSimulation = true;

    /// <summary>Clic izquierdo dentro del mapa interactivo, en coordenadas de mundo (sobre el terreno).</summary>
    public event Action<Vector3> MapClicked;
    /// <summary>Dibujo extra sobre el mapa (IMGUI, Rect del mapa), tras el recorrido y antes del rover.</summary>
    public event Action<Rect> OverlayGUI;
    /// <summary>Texto extra para la línea de ayuda del mapa interactivo (lo pone la navegación).</summary>
    public string extraHint;
    /// <summary>Mientras es true (menú de un punto abierto, arrastre de un pin), el mapa no lee clic,
    /// desplazamiento ni zoom.</summary>
    [NonSerialized] public bool suspendInput;

    public bool Interactive { get; private set; }
    public float MapYaw => rotateWithRover && !Interactive && chase && chase.target
        ? chase.target.transform.eulerAngles.y : mapYaw;
    public Vector3 MapCenter => Interactive || !(chase && chase.target) ? focus : chase.target.transform.position;

    ChaseCamera chase;
    Camera mapCam;
    RenderTexture rt;
    bool fogWas;
    bool dragging;
    float mapYaw;
    Vector3 focus;
    static GUIStyle markerStyle, hintStyle;

    float WorldScale => chase ? chase.worldScale : 0.02f;
    float Half => radiusMeters * WorldScale;   // mitad del lado visible, en unidades Unity

    void Awake()
    {
        chase = GetComponent<ChaseCamera>();
    }

    void OnEnable()
    {
        rt = new RenderTexture(1024, 1024, 24) { name = "Minimap_RT", antiAliasing = 2 };
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
        ApplyCursor();
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RenderPipelineManager.endCameraRendering -= OnEndCamera;
        if (mapCam) Destroy(mapCam.gameObject);
        if (rt) { rt.Release(); Destroy(rt); }
        if (Interactive) SetInteractive(false);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus) ApplyCursor();
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

    // ---------- Modo interactivo ----------

    public void SetInteractive(bool on)
    {
        if (on == Interactive) return;
        Interactive = on;
        if (on)
        {
            if (chase && chase.target) focus = chase.target.transform.position;
            if (rotateWithRover && chase && chase.target) mapYaw = chase.target.transform.eulerAngles.y;
        }
        dragging = false;
        if (chase)
        {
            chase.inputBlocked = on;
            if (chase.target) chase.target.inputEnabled = !on && !chase.Aerial;
        }
        ApplyCursor();
    }

    /// <summary>Cursor libre en el mapa interactivo (o en pausa); oculto y bloqueado en la simulación.</summary>
    public void ApplyCursor()
    {
        bool free = Interactive || !hideCursorInSimulation || SimPause.IsPaused;
        Cursor.lockState = free ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = free;
    }

    void Update()
    {
        if (!show || !chase || !chase.target || SimPause.IsPaused) return;
        var k = GaleInput.Controls;

        if (k.Global.ToggleMap.WasPressedThisFrame()) SetInteractive(!Interactive);
        else if (Interactive && k.Map.Exit.WasPressedThisFrame()) SetInteractive(false);

        // Zoom por pasos: −/+ fuera del mapa (Global) y también −/+ y cruceta dentro (Map).
        var step = Interactive ? k.Map.ZoomStep : k.Global.MinimapZoom;
        if (step.WasPressedThisFrame())
        {
            float v = step.ReadValue<float>();
            if (v < 0f) radiusMeters = Mathf.Min(radiusMeters * 2f, maxRadiusMeters);
            else if (v > 0f) radiusMeters = Mathf.Max(radiusMeters * 0.5f, minRadiusMeters);
        }

        if (Interactive) UpdateInteractive(k.Map);
    }

    [Header("Cursor virtual (mando)")]
    [Tooltip("Velocidad del cursor del mapa con el stick derecho, en fracción del lado del mapa por segundo.")]
    public float virtualCursorSpeed = 0.6f;
    Vector2 virtualCursor;
    bool useVirtualCursor;

    /// <summary>Cursor del mapa en coordenadas de GUI: el del ratón o, con mando, uno virtual.</summary>
    public Vector2 MapCursor => useVirtualCursor ? virtualCursor : MousePointGui();
    public bool UsingVirtualCursor => useVirtualCursor;

    static Vector2 MousePointGui()
    {
        Vector2 sp = GaleInput.Controls.Map.Point.ReadValue<Vector2>();
        return new Vector2(sp.x, Screen.height - sp.y);
    }

    void UpdateInteractive(GaleControls.MapActions a)
    {
        float dt = Time.unscaledDeltaTime;
        Rect r = MapRect();

        Vector2 pan = Vector2.ClampMagnitude(a.Pan.ReadValue<Vector2>(), 1f);
        float speed = Half * (a.Fast.IsPressed() ? 3f : 1f);   // medio mapa por segundo
        focus += Quaternion.Euler(0f, mapYaw, 0f) * new Vector3(pan.x, 0f, pan.y) * speed * dt;
        mapYaw += a.Rotate.ReadValue<float>() * 60f * dt;
        if (a.FocusRover.WasPressedThisFrame()) focus = chase.target.transform.position;
        if (a.FocusCrater.WasPressedThisFrame()) { focus = chase.craterCenter; mapYaw = 0f; }

        // Cursor: el ratón manda en cuanto se mueve; el stick derecho activa el cursor virtual.
        Vector2 stick = a.CursorMove.ReadValue<Vector2>();
        if (stick.sqrMagnitude > 0.02f)
        {
            if (!useVirtualCursor) { useVirtualCursor = true; virtualCursor = r.center; }
            virtualCursor += new Vector2(stick.x, -stick.y) * r.width * virtualCursorSpeed * dt;
            Vector2 d = virtualCursor - r.center;
            virtualCursor = r.center + Vector2.ClampMagnitude(new Vector2(
                Mathf.Clamp(d.x, -r.width * 0.5f, r.width * 0.5f), Mathf.Clamp(d.y, -r.height * 0.5f, r.height * 0.5f)), r.width);
        }
        else if (a.PointerDelta.ReadValue<Vector2>().sqrMagnitude > 0.5f)
        {
            useVirtualCursor = false;
        }
        Vector2 gui = MapCursor;
        bool inside = InMap(gui, r);
        if (suspendInput) { dragging = false; return; }

        // Rueda / gatillos: zoom manteniendo fijo el punto del terreno bajo el cursor.
        float scroll = GaleInput.Delta(a.Zoom, 700f);
        if (inside && Mathf.Abs(scroll) > 0.01f) ZoomAt(gui, Mathf.Pow(0.999f, scroll));

        // Clic derecho + arrastrar: el terreno sigue al cursor.
        if (a.Drag.WasPressedThisFrame() && inside) dragging = true;
        if (!a.Drag.IsPressed()) dragging = false;
        if (dragging) Pan(a.PointerDelta.ReadValue<Vector2>(), r);

        // Clic izquierdo / A: punto en el mundo (destino de la navegación A→B).
        if (a.Select.WasPressedThisFrame() && inside) MapClicked?.Invoke(MapToWorld(gui));
    }

    /// <summary>Acerca (factor &lt; 1) o aleja (factor &gt; 1) manteniendo fijo el punto del mapa en guiPoint.</summary>
    public void ZoomAt(Vector2 guiPoint, float factor)
    {
        Rect r = MapRect();
        Vector3 before = MapToWorldFlat(guiPoint, r);
        radiusMeters = Mathf.Clamp(radiusMeters * factor, minRadiusMeters, maxRadiusMeters);
        Vector3 after = MapToWorldFlat(guiPoint, r);
        focus += before - after;
    }

    /// <summary>Desplaza el mapa como si se arrastrara deltaPixels (Input System: y hacia arriba).</summary>
    public void Pan(Vector2 deltaPixels, Rect r)
    {
        float unitsPerPixel = 2f * Half / r.width;
        Vector3 d = new Vector3(deltaPixels.x, 0f, deltaPixels.y) * unitsPerPixel;
        focus -= Quaternion.Euler(0f, mapYaw, 0f) * d;
    }

    // ---------- Conversiones mapa <-> mundo ----------

    /// <summary>Escala de la UI respecto a 1080 p.</summary>
    public static float UiScale => Mathf.Max(0.6f, Screen.height / 1080f);

    /// <summary>Rectángulo del mapa en coordenadas de GUI (origen arriba a la izquierda).
    /// Fuera del modo interactivo es el cuadrado que circunscribe el círculo.</summary>
    public Rect MapRect()
    {
        if (Interactive)
        {
            float s = Mathf.Min(Screen.height * interactiveFraction, Screen.width - 2f * margin);
            return new Rect((Screen.width - s) * 0.5f, (Screen.height - s) * 0.5f - 20f * UiScale, s, s);
        }
        float d = diameterAt1080 * UiScale;
        return new Rect(Screen.width - d - rightSpace * UiScale, topSpace * UiScale, d, d);
    }

    /// <summary>¿Cae un punto de GUI dentro del mapa visible? (círculo, o cuadrado en modo interactivo).</summary>
    public bool InMap(Vector2 gui, Rect r, float inset = 0f)
    {
        if (Interactive) return r.Contains(gui);
        return (gui - r.center).magnitude <= r.width * 0.5f - inset;
    }

    public Vector2 WorldToMap(Vector3 world) => WorldToMap(world, MapRect());

    public Vector2 WorldToMap(Vector3 world, Rect r)
    {
        Vector3 d = Quaternion.Euler(0f, -MapYaw, 0f) * (world - MapCenter);
        return new Vector2(r.center.x + d.x / Half * r.width * 0.5f,
                           r.center.y - d.z / Half * r.height * 0.5f);
    }

    Vector3 MapToWorldFlat(Vector2 gui, Rect r)
    {
        float dx = (gui.x - r.center.x) / (r.width * 0.5f) * Half;
        float dz = -(gui.y - r.center.y) / (r.height * 0.5f) * Half;
        Vector3 c = MapCenter;
        return new Vector3(c.x, 0f, c.z) + Quaternion.Euler(0f, MapYaw, 0f) * new Vector3(dx, 0f, dz);
    }

    /// <summary>Punto del terreno bajo un píxel del mapa (coordenadas de GUI).</summary>
    public Vector3 MapToWorld(Vector2 gui)
    {
        Vector3 p = MapToWorldFlat(gui, MapRect());
        if (Physics.Raycast(new Vector3(p.x, 500f, p.z), Vector3.down, out var hit, 2000f, ~(1 << 2)))
            p.y = hit.point.y;
        return p;
    }

    // ---------- Cámara y dibujo ----------

    void LateUpdate()
    {
        if (!mapCam) return;
        mapCam.enabled = show && chase && chase.target;
        if (!mapCam.enabled) return;
        Vector3 c = MapCenter;
        // Por encima del punto más alto del terreno (Z máx. 1347 m → ~27 u a 1:50).
        mapCam.transform.SetPositionAndRotation(new Vector3(c.x, 200f, c.z), Quaternion.Euler(90f, MapYaw, 0f));
        mapCam.orthographicSize = Half;
    }

    void OnGUI()
    {
        GUI.depth = 10;  // debajo del aro y los datos de NavInstrumentHUD
        if (!show || !mapCam || !mapCam.enabled || !rt || SimPause.IsPaused) return;
        markerStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16, padding = new RectOffset() };
        hintStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, wordWrap = true };

        Rect r = MapRect();
        var old = GUI.color;
        if (Interactive)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.DrawTexture(r, rt, ScaleMode.StretchToFill, false);
        }
        else if (Event.current.type == EventType.Repaint)
        {
            // Círculo: la RenderTexture con máscara circular (el aro lo dibuja NavInstrumentHUD).
            if (!circleMaterial)
            {
                var sh = Shader.Find("Hidden/Gale/CircleMask");
                if (sh) circleMaterial = new Material(sh) { hideFlags = HideFlags.DontSave };
            }
            if (circleMaterial) Graphics.DrawTexture(r, rt, circleMaterial);
            else GUI.DrawTexture(r, rt, ScaleMode.StretchToFill, false);
        }

        Transform t = chase.target.transform;
        float yaw = MapYaw;

        // En vista aérea: punto al que mira la cámara principal.
        if (chase.Aerial)
        {
            Vector2 fp = WorldToMap(AerialFocusOnGround(), r);
            if (InMap(fp, r, 8f)) Marker(fp, "✚", Color.yellow, 0f);
        }

        // Ruta real de Curiosity (CuriosityRoute) y recorrido del rover (RoverTrackRecorder), como líneas.
        DrawCuriosityRoute(r);
        if (Event.current.type == EventType.Repaint) DrawRecordedPath(r);
        OverlayGUI?.Invoke(r);

        // Lugares con nombre.
        if (places != null)
            foreach (var pl in places)
            {
                Vector2 pp = WorldToMap(MarsGeo.LonLatToUnity(pl.lon, pl.lat), r);
                if (!InMap(pp, r, 8f)) continue;
                Marker(pp, "•", new Color(1f, 0.85f, 0.55f), 0f);
                var oldc = GUI.color;
                GUI.color = Color.black; GUI.Label(new Rect(pp.x + 7, pp.y - 9, 120, 20), pl.name);
                GUI.color = new Color(1f, 0.92f, 0.75f); GUI.Label(new Rect(pp.x + 6, pp.y - 10, 120, 20), pl.name);
                GUI.color = oldc;
            }

        // Rover con flecha de rumbo (en el centro salvo que el mapa interactivo se haya desplazado).
        Vector2 rp = WorldToMap(t.position, r);
        if (InMap(rp, r, 6f)) Marker(rp, "▲", Color.cyan, t.eulerAngles.y - yaw);

        // En el modo interactivo (cuadrado) se dibujan aquí el norte, la escala y la ayuda;
        // en el círculo lo hace NavInstrumentHUD sobre el aro.
        if (Interactive)
        {
            Vector2 dirN = new Vector2(Mathf.Sin(-yaw * Mathf.Deg2Rad), -Mathf.Cos(-yaw * Mathf.Deg2Rad));
            Marker(r.center + dirN * (r.width * 0.5f - 12f), "N", Color.white, 0f);

            float bar = r.width * 0.25f;
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(r.x + 8, r.yMax - 10, bar, 3), Texture2D.whiteTexture);
            GUI.Label(new Rect(r.x + 8, r.yMax - 30, 200, 20), ScaleText(r));
            GUI.Label(new Rect(r.x + 8, r.y + 22, r.width - 16, 40),
                GaleInput.UsingGamepad
                    ? "MAPA · stick izq. mover · gatillos zoom · stick dcho. cursor · A seleccionar · LB/RB girar · X rover · Y cráter · B salir"
                    : "MAPA · clic dcho+arrastrar mover · rueda zoom · WASD · Q/E girar · F rover · C cráter · M/Esc salir",
                hintStyle);
            if (!string.IsNullOrEmpty(extraHint))
                GUI.Label(new Rect(r.x + 8, r.y + 22 + hintStyle.CalcHeight(new GUIContent(GaleInput.UsingGamepad ? "MAPA · stick izq. mover · gatillos zoom · stick dcho. cursor · A seleccionar · LB/RB girar · X rover · Y cráter · B salir" : "MAPA · clic dcho+arrastrar mover · rueda zoom · WASD · Q/E girar · F rover · C cráter · M/Esc salir"), r.width - 16), r.width - 16, 40), extraHint, hintStyle);
            if (useVirtualCursor) Marker(virtualCursor, "✛", new Color(1f, 0.95f, 0.4f), 0f);
        }
        GUI.color = old;
    }

    [Header("Ruta de Curiosity")]
    public bool showCuriosityRoute = true;
    public Color curiosityRouteColor = new Color(1f, 0.62f, 0.2f, 0.95f);
    [Tooltip("Marca un waypoint cada tantos soles (con el mapa acercado).")]
    public int solMarkEvery = 500;
    CuriosityRoute curiosity;
    bool curiositySearched;

    void DrawCuriosityRoute(Rect r)
    {
        if (!showCuriosityRoute) return;
        if (!curiositySearched) { curiosity = FindAnyObjectByType<CuriosityRoute>(); curiositySearched = true; }
        if (!curiosity || curiosity.Points.Count < 2) return;
        var old = GUI.color;
        if (Event.current.type == EventType.Repaint)
        {
            var pts = curiosity.Points;
            int stride = Mathf.Max(1, pts.Count / 900);
            GUI.color = curiosityRouteColor;
            Vector2 prev = WorldToMap(pts[0], r);
            for (int i = stride; i < pts.Count + stride - 1; i += stride)
            {
                Vector2 cur = WorldToMap(pts[Mathf.Min(i, pts.Count - 1)], r);
                Vector2 ca = prev, cb = cur;
                if (ClipLine(ref ca, ref cb, r)) Line(ca, cb, 2.5f);
                prev = cur;
            }
        }
        var wps = curiosity.Waypoints;
        if (wps.Count == 0) { GUI.color = old; return; }
        // Inicio, final y un hito cada solMarkEvery soles (solo si el mapa está acercado).
        Label(r, wps[0].unity, "Bradbury · sol 0", curiosityRouteColor);
        Label(r, wps[wps.Count - 1].unity, $"Curiosity · sol {wps[wps.Count - 1].sol}", curiosityRouteColor);
        if (radiusMeters <= 12000f && solMarkEvery > 0)
        {
            int next = solMarkEvery;
            foreach (var w in wps)
                if (w.sol >= next && w.sol < wps[wps.Count - 1].sol - solMarkEvery / 4)
                {
                    Label(r, w.unity, $"sol {w.sol}", new Color(1f, 0.8f, 0.55f, 0.95f));
                    next = (w.sol / solMarkEvery + 1) * solMarkEvery;
                }
        }
        GUI.color = old;
    }

    void Label(Rect r, Vector3 world, string text, Color c)
    {
        Vector2 p = WorldToMap(world, r);
        if (!InMap(p, r, 8f)) return;
        Marker(p, "●", c, 0f);
        var old = GUI.color;
        GUI.color = Color.black; GUI.Label(new Rect(p.x + 8, p.y - 9, 160, 20), text);
        GUI.color = c; GUI.Label(new Rect(p.x + 7, p.y - 10, 160, 20), text);
        GUI.color = old;
    }

    [Header("Recorrido")]
    public bool showPath = true;
    public Color pathColor = new Color(0.35f, 0.95f, 1f, 0.9f);
    RoverTrackRecorder recorder;

    void DrawRecordedPath(Rect r)
    {
        if (!showPath) return;
        if (!recorder && chase && chase.target) recorder = chase.target.GetComponent<RoverTrackRecorder>();
        if (!recorder) return;
        var s = recorder.Samples;
        int n = s.Count;
        if (n < 2) return;
        int stride = Mathf.Max(1, n / 600);
        var old = GUI.color;
        GUI.color = pathColor;
        Vector2 prev = WorldToMap(s[0].unity, r);
        int prevSeg = s[0].segment;
        for (int i = stride; i < n + stride - 1; i += stride)
        {
            int k = Mathf.Min(i, n - 1);
            Vector2 cur = WorldToMap(s[k].unity, r);
            Vector2 ca = prev, cb = cur;
            if (s[k].segment == prevSeg && ClipLine(ref ca, ref cb, r)) Line(ca, cb, 2f);
            prev = cur; prevSeg = s[k].segment;
        }
        GUI.color = old;
    }

    /// <summary>Recorta el segmento a–b al mapa visible (cuadrado en modo interactivo, círculo si no).
    /// Devuelve false si queda fuera entero.</summary>
    public bool ClipLine(ref Vector2 a, ref Vector2 b, Rect r, float inset = 2f)
    {
        Vector2 d = b - a;
        float t0 = 0f, t1 = 1f;
        if (Interactive)
        {
            // Liang–Barsky contra el rectángulo.
            float[] p = { -d.x, d.x, -d.y, d.y };
            float[] q = { a.x - r.xMin, r.xMax - a.x, a.y - r.yMin, r.yMax - a.y };
            for (int i = 0; i < 4; i++)
            {
                if (Mathf.Abs(p[i]) < 1e-6f) { if (q[i] < 0f) return false; continue; }
                float t = q[i] / p[i];
                if (p[i] < 0f) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
            }
        }
        else
        {
            // Intersección con el círculo.
            Vector2 c = r.center, f = a - c;
            float rad = r.width * 0.5f - inset;
            float A = Vector2.Dot(d, d), B = 2f * Vector2.Dot(f, d), C = Vector2.Dot(f, f) - rad * rad;
            if (A < 1e-6f) return C <= 0f;
            float disc = B * B - 4f * A * C;
            if (disc < 0f) return false;
            float sq = Mathf.Sqrt(disc);
            t0 = Mathf.Max(0f, (-B - sq) / (2f * A));
            t1 = Mathf.Min(1f, (-B + sq) / (2f * A));
            if (t0 > t1) return false;
        }
        Vector2 a0 = a;
        a = a0 + d * t0;
        b = a0 + d * t1;
        return true;
    }

    public static void Line(Vector2 a, Vector2 b, float w)
    {
        Vector2 d = b - a;
        float len = d.magnitude;
        if (len < 0.5f) return;
        var m = GUI.matrix;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
        GUI.DrawTexture(new Rect(a.x, a.y - w * 0.5f, len, w), Texture2D.whiteTexture);
        GUI.matrix = m;
    }

    /// <summary>Distancia real que abarca un cuarto del ancho del mapa (la barra de escala).</summary>
    public float ScaleBarMeters => radiusMeters * 2f * 0.25f;

    public string ScaleText(Rect r)
    {
        float m = ScaleBarMeters;
        return m >= 1000f ? $"{m / 1000f:0.#} km" : $"{m:0} m";
    }

    Vector3 AerialFocusOnGround()
    {
        var ray = new Ray(transform.position, transform.forward);
        if (Physics.Raycast(ray, out var hit, 20000f, ~(1 << 2))) return hit.point;
        return transform.position;
    }

    public static void Marker(Vector2 pos, string glyph, Color color, float angleDeg)
    {
        markerStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16, padding = new RectOffset() };
        var m = GUI.matrix;
        var old = GUI.color;
        GUIUtility.RotateAroundPivot(angleDeg, pos);
        var rr = new Rect(pos.x - 12f, pos.y - 12f, 24f, 24f);
        GUI.color = Color.black; GUI.Label(new Rect(rr.x + 1, rr.y + 1, rr.width, rr.height), glyph, markerStyle);
        GUI.color = color; GUI.Label(rr, glyph, markerStyle);
        GUI.matrix = m;
        GUI.color = old;
    }
}
