using UnityEngine;

// Instrumento de navegación alrededor del minimapa circular (arriba a la derecha):
//   aro con marcas cada 30° y N/E/S/O que giran con el mapa, y los datos en el borde:
//   arriba el rumbo · derecha la velocidad · izquierda la elevación · abajo lon/lat, escala y g.
// En el modo mapa (M) los datos pasan a una barra inferior. Las ayudas de controles se muestran
// unos segundos al empezar y con H.
[RequireComponent(typeof(MinimapHUD))]
public class NavInstrumentHUD : MonoBehaviour
{
    public bool show = true;
    [Tooltip("Segundos que se ven las ayudas al empezar.")]
    public float helpSecondsAtStart = 8f;

    [Header("Colores")]
    public Color ringColor = new Color(0.10f, 0.06f, 0.05f, 0.92f);
    public Color tickColor = new Color(0.95f, 0.80f, 0.66f, 0.95f);
    public Color northColor = new Color(1f, 0.42f, 0.25f);
    public Color textColor = new Color(1f, 0.93f, 0.84f);
    public Color dimTextColor = new Color(0.86f, 0.72f, 0.60f);

    MinimapHUD map;
    ChaseCamera chase;
    Texture2D ringTex;
    bool helpPinned;
    float helpUntil;
    GUIStyle big, small, center, help;

    static readonly string[] Cardinal16 =
        { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSO", "SO", "OSO", "O", "ONO", "NO", "NNO" };

    void Awake()
    {
        map = GetComponent<MinimapHUD>();
        chase = GetComponent<ChaseCamera>();
    }

    void Start() => helpUntil = Time.unscaledTime + helpSecondsAtStart;

    void OnDestroy()
    {
        if (ringTex) Destroy(ringTex);
    }

    void Update()
    {
        if (SimPause.IsPaused) return;
        if (GaleInput.Controls.Global.Help.WasPressedThisFrame())   // H / cruceta →
        {
            if (HelpShown) { helpPinned = false; helpUntil = 0f; }
            else helpPinned = true;
        }

        // Ruta de Curiosity: punto más cercano (4 veces por segundo; son ~7700 puntos).
        if (Time.unscaledTime >= nextRouteCheck && chase && chase.target)
        {
            nextRouteCheck = Time.unscaledTime + 0.25f;
            if (!routeSearched) { route = FindAnyObjectByType<CuriosityRoute>(); routeSearched = true; }
            routeText = route && route.Nearest(chase.target.transform.position, out float m, out int sol)
                ? $"ruta de Curiosity a {(m >= 1000f ? $"{m / 1000f:0.0} km" : $"{m:0} m")} · sol {sol}"
                : null;
        }
    }

    CuriosityRoute route;
    bool routeSearched;
    float nextRouteCheck;
    string routeText;

    bool HelpShown => helpPinned || Time.unscaledTime < helpUntil;

    // Navegación A→B: distancia y tiempo restantes y rumbo al punto de la ruta que está por delante.
    string GuidanceText()
    {
        var nav = NavigationSystem.Instance;
        if (!nav || nav.Route == null) return null;
        if (nav.Current == NavigationSystem.State.Completed) return "<b>" + nav.Message + "</b>";
        if (nav.Current == NavigationSystem.State.Calculating) return "ruta: calculando…";
        if (!nav.Guiding) return $"ruta en vista previa · {NavMapOverlay.Km(nav.Route.DistanceM)} · Intro en el mapa (M) la inicia";
        nav.Bearing(out float brg, out _);
        string card = Cardinal16[Mathf.RoundToInt(brg / 22.5f) % 16];
        return $"<b>RUTA</b> quedan {NavMapOverlay.Km(nav.RemainingMeters)} · {NavMapOverlay.Hours(nav.RemainingSeconds)} · rumbo {brg:000}° {card}";
    }

    // Aro: anillo blanco con alfa suavizado; el color se aplica con GUI.color.
    Texture2D Ring()
    {
        if (ringTex) return ringTex;
        const int n = 512;
        ringTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        float inner = 0.855f, outer = 0.995f, aa = 2f / n;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01((d - inner) / aa + 0.5f) * Mathf.Clamp01((outer - d) / aa + 0.5f);
                px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        ringTex.SetPixels32(px);
        ringTex.Apply();
        return ringTex;
    }

    void Styles(float s)
    {
        big ??= new GUIStyle(GUI.skin.label) { richText = true };
        small ??= new GUIStyle(GUI.skin.label) { richText = true };
        center ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, richText = true, padding = new RectOffset() };
        help ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, richText = true, wordWrap = true };
        big.fontSize = Mathf.RoundToInt(20 * s);
        small.fontSize = Mathf.RoundToInt(13 * s);
        center.fontSize = Mathf.RoundToInt(14 * s);
        help.fontSize = Mathf.RoundToInt(14 * s);
        int p = Mathf.RoundToInt(10 * s);
        help.padding = new RectOffset(p, p, p, p);
    }

    void OnGUI()
    {
        GUI.depth = 0;   // encima del minimapa (MinimapHUD usa 10)
        if (!show || SimPause.IsPaused || !map || !map.show || !chase || !chase.target) return;
        float s = MinimapHUD.UiScale;
        Styles(s);

        Transform t = chase.target.transform;
        var ll = MarsGeo.UnityToLonLat(t.position);
        float heading = Mathf.Repeat(t.eulerAngles.y, 360f);
        string card = Cardinal16[Mathf.RoundToInt(heading / 22.5f) % 16];
        string speed = $"{chase.target.Speed:0.0}";
        string elev = $"{MarsGeo.Elevation(t.position.y):0}";
        string latS = ll.lat < 0 ? $"{-ll.lat:0.0000}° S" : $"{ll.lat:0.0000}° N";
        string lonS = ll.lon < 0 ? $"{-ll.lon:0.0000}° O" : $"{ll.lon:0.0000}° E";
        string g = $"g = {chase.target.gravity:0.00} m/s²";

        Rect r = map.MapRect();
        if (map.Interactive || !map.circleVisible)
        {
            // Barra inferior con los mismos datos (en el mapa, o con el minimapa oculto desde el menú).
            float h = 34f * s;
            var bar = new Rect(0, Screen.height - h, Screen.width, h);
            Fill(bar, new Color(0.08f, 0.05f, 0.04f, 0.85f));
            string extra = "";
            if (!map.Interactive)
            {
                string guide = GuidanceText();
                extra = (guide != null ? "     " + guide : routeText != null ? "     " + routeText : "") +
                        (GaleInput.UsingGamepad ? "     R3 mapa" : "     M mapa · G menú · H ayuda");
            }
            Text(new Rect(bar.x + 12 * s, bar.y, bar.width - 24 * s, h),
                $"Rumbo <b>{heading:000}° {card}</b>     Vel. <b>{speed} m/s</b>     Elev. <b>{elev} m</b>     " +
                $"{latS}  {lonS}     " + (map.Interactive ? $"Escala {map.ScaleText(r)}     " : "") + g + extra,
                small, textColor, TextAnchor.MiddleLeft);
        }
        else
        {
            DrawRing(r, s);
            Vector2 c = r.center;
            // Arriba: rumbo.
            Text(new Rect(c.x - 120 * s, r.y - 40 * s, 240 * s, 30 * s),
                $"<b>{heading:000}°</b> <size={Mathf.RoundToInt(15 * s)}>{card}</size>", big, textColor, TextAnchor.MiddleCenter);
            // Derecha: velocidad.
            Text(new Rect(r.xMax + 18 * s, c.y - 26 * s, 110 * s, 22 * s), "VELOCIDAD", small, dimTextColor, TextAnchor.LowerLeft);
            Text(new Rect(r.xMax + 18 * s, c.y - 4 * s, 110 * s, 30 * s), $"<b>{speed}</b> <size={Mathf.RoundToInt(13 * s)}>m/s</size>", big, textColor, TextAnchor.UpperLeft);
            // Izquierda: elevación.
            Text(new Rect(r.x - 138 * s, c.y - 26 * s, 120 * s, 22 * s), "ELEVACIÓN", small, dimTextColor, TextAnchor.LowerRight);
            Text(new Rect(r.x - 138 * s, c.y - 4 * s, 120 * s, 30 * s), $"<b>{elev}</b> <size={Mathf.RoundToInt(13 * s)}>m</size>", big, textColor, TextAnchor.UpperRight);
            // Abajo: coordenadas, escala y gravedad.
            Text(new Rect(c.x - 170 * s, r.yMax + 18 * s, 340 * s, 20 * s), $"{latS}   {lonS}", small, textColor, TextAnchor.MiddleCenter);
            Text(new Rect(c.x - 170 * s, r.yMax + 36 * s, 340 * s, 20 * s),
                $"escala {map.ScaleText(r)}  ·  {g}", small, dimTextColor, TextAnchor.MiddleCenter);
            Text(new Rect(c.x - 170 * s, r.yMax + 54 * s, 340 * s, 20 * s),
                GaleInput.UsingGamepad ? "R3 mapa · cruceta → ayuda" : "M mapa · G menú · − / + zoom · H ayuda",
                small, dimTextColor, TextAnchor.MiddleCenter);
            if (routeText != null)
                Text(new Rect(c.x - 190 * s, r.yMax + 72 * s, 380 * s, 20 * s), routeText, small,
                     new Color(1f, 0.72f, 0.4f), TextAnchor.MiddleCenter);
            string guide = GuidanceText();
            if (guide != null)
                Text(new Rect(c.x - 210 * s, r.yMax + 90 * s, 420 * s, 20 * s), guide, small,
                     new Color(0.8f, 0.65f, 1f), TextAnchor.MiddleCenter);
        }

        if (HelpShown && !map.Interactive)
        {
            string txt = GaleInput.UsingGamepad
                ? (chase.Aerial
                    ? "<b>VISTA AÉREA</b>\nStick izq. mover · L3 rápido · LB/RB girar · stick dcho. Y altura\nstick dcho. X + cruceta ↑/↓ mirar\nX centrar en el rover · Y centrar en el cráter\nSelect volver · R3 mapa · Start pausa · cruceta → ocultar"
                    : "<b>ROVER</b>\nRT/LT avanzar y retroceder · stick izq. girar · A rápido\nB freno · Y enderezar · cruceta ↑/↓ distancia\nSelect vista aérea · R3 mapa · Start pausa · cruceta → ocultar")
                : (chase.Aerial
                    ? "<b>VISTA AÉREA</b>\nWASD mover · Shift rápido · Q/E girar · rueda altura\nclic derecho + arrastrar inclinar\nF centrar en el rover · C centrar en el cráter\nTab volver · M mapa · Esc pausa · H ocultar"
                    : "<b>ROVER</b>\nW/S avanzar · A/D girar · Shift rápido\nEspacio freno · R enderezar · rueda distancia\nTab vista aérea · M mapa · Esc pausa · H ocultar");
            var box = new Rect(16 * s, Screen.height - 132 * s, 380 * s, 116 * s);
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.9f);
            GUI.Box(box, txt, help);
            GUI.color = old;
        }
    }

    void DrawRing(Rect r, float s)
    {
        float yaw = map.MapYaw;
        float pad = 9f * s;
        var ringRect = new Rect(r.x - pad, r.y - pad, r.width + 2 * pad, r.height + 2 * pad);
        var old = GUI.color;
        GUI.color = ringColor;
        GUI.DrawTexture(ringRect, Ring());
        float Rout = ringRect.width * 0.5f * 0.995f, Rin = ringRect.width * 0.5f * 0.855f;
        Vector2 c = r.center;

        // Marcas cada 30° (más largas en los cardinales) y letras N/E/S/O.
        for (int a = 0; a < 360; a += 30)
        {
            float th = (a - yaw) * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Sin(th), -Mathf.Cos(th));
            bool cardinal = a % 90 == 0;
            if (cardinal)
            {
                string l = a == 0 ? "N" : a == 90 ? "E" : a == 180 ? "S" : "O";
                Vector2 lp = c + dir * (Rin - 11f * s);   // dentro del mapa, junto al aro
                Text(new Rect(lp.x - 12 * s, lp.y - 11 * s, 24 * s, 22 * s), $"<b>{l}</b>", center, a == 0 ? northColor : tickColor, TextAnchor.MiddleCenter);
            }
            else
            {
                float len = (Rout - Rin) * 0.5f;
                Vector2 p = c + dir * (Rin + (Rout - Rin) * 0.5f);
                var m = GUI.matrix;
                GUIUtility.RotateAroundPivot(a - yaw, p);
                GUI.color = tickColor;
                GUI.DrawTexture(new Rect(p.x - 1f * s, p.y - len * 0.5f, 2f * s, len), Texture2D.whiteTexture);
                GUI.matrix = m;
            }
        }

        // Rumbo del rover: pequeño triángulo sobre el aro.
        float hd = (chase.target.transform.eulerAngles.y - yaw) * Mathf.Deg2Rad;
        Vector2 hp = c + new Vector2(Mathf.Sin(hd), -Mathf.Cos(hd)) * (Rout + 7f * s);
        var mm = GUI.matrix;
        GUIUtility.RotateAroundPivot(chase.target.transform.eulerAngles.y - yaw, hp);
        Text(new Rect(hp.x - 10 * s, hp.y - 10 * s, 20 * s, 20 * s), "▼", center, Color.cyan, TextAnchor.MiddleCenter);
        GUI.matrix = mm;
        GUI.color = old;
    }

    static void Fill(Rect r, Color c)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    // Texto con sombra para que se lea sobre el terreno.
    static void Text(Rect r, string s, GUIStyle st, Color c, TextAnchor anchor)
    {
        var old = GUI.color;
        var oa = st.alignment;
        st.alignment = anchor;
        GUI.color = new Color(0f, 0f, 0f, 0.85f);
        GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), s, st);
        GUI.color = c;
        GUI.Label(r, s, st);
        st.alignment = oa;
        GUI.color = old;
    }

}
