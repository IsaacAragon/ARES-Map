using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

[Serializable]
public class IauFeature
{
    public int id;
    public string nombre, tipo;
    public float diametroKm;
    public double lat, lon, latNorte, latSur, lonEste, lonOeste;
    public string origen, aprobado, cuadrangulo;

    public bool IsCrater => tipo != null && tipo.StartsWith("Crater");
    /// <summary>Área de forma circular (cráter): se sombrea con su diámetro oficial.</summary>
    public bool Circular => IsCrater && diametroKm > 0f;

    public string TipoEs
    {
        get
        {
            if (tipo == null) return "";
            if (tipo.StartsWith("Crater")) return "Cráter";
            if (tipo.StartsWith("Mons")) return "Monte (mons)";
            if (tipo.StartsWith("Palus")) return "Llanura (palus)";
            if (tipo.StartsWith("Mensa")) return "Mesas (mensae)";
            if (tipo.StartsWith("Vallis")) return "Valle (vallis)";
            return tipo;
        }
    }

    public Vector3 Center => MarsGeo.LonLatToUnity(lon, lat);

    /// <summary>¿Está el punto dentro de la formación? Cráter: círculo del diámetro oficial; resto: recuadro IAU.</summary>
    public bool Contains(double qlon, double qlat)
    {
        if (Circular) return PlaceAtlas.GreatCircleKm(lon, lat, qlon, qlat) <= diametroKm * 0.5;
        return qlat >= latSur && qlat <= latNorte && qlon >= lonOeste && qlon <= lonEste;
    }
}

[Serializable]
public class IauFile
{
    public string fuente, descargado, coordenadas;
    public IauFeature[] formaciones;
}

// Lugares con nombre oficial (IAU / USGS Gazetteer, Tools/fetch_iau_names.py → iau_nomenclatura.json):
//  - Search: por coordenadas (decimal o grados/minutos/segundos, con o sin N/S/E/O) dice qué formación hay ahí;
//    por nombre, filtra la lista.
//  - Nodos: un punto con el nombre de cada formación en el minimapa (clic en el mapa interactivo = elegir) y en la
//    vista aérea (clic con el cursor libre).
//  - Select: sombrea en blanco el área del cráter sobre el relieve, encuadra la vista aérea si se pide y abre la
//    ventana de la derecha (PlaceInfoPanel) con la ficha IAU, la foto orbital del lugar y lo que miden los datos
//    de la escena dentro del área (geología, suelo, relieve).
[DefaultExecutionOrder(-45)]
public class PlaceAtlas : MonoBehaviour
{
    public static PlaceAtlas Instance { get; private set; }

    public TextAsset dataset;
    [Tooltip("Material con Gale/GeoOverlay (para que el shader entre en la build); se usa una copia.")]
    public Material highlightMaterial;
    public MinimapHUD minimap;
    public ChaseCamera chase;
    public Color highlightColor = new Color(1f, 1f, 1f, 0.3f);
    public Color rimColor = new Color(1f, 1f, 1f, 0.9f);
    public Color nodeColor = new Color(1f, 1f, 1f, 0.95f);

    public IauFile Data { get; private set; }
    public IReadOnlyList<IauFeature> Features => Data?.formaciones ?? Array.Empty<IauFeature>();
    public IauFeature Selected { get; private set; }
    public Texture Photo => photoRt;
    public string PhotoNote { get; private set; }
    public event Action<IauFeature> SelectionChanged;

    bool highlightOn, hideHighlight;
    Material overlayMat;
    Texture2D circleTex;
    MeshFilter[] terrain;
    RenderTexture photoRt;
    Camera photoCam;
    static GUIStyle nodeStyle, nodeButton;
    const float NodePixels = 14f;

    void Awake()
    {
        Instance = this;
        if (!dataset) { Debug.LogWarning("[Lugares] falta iau_nomenclatura.json"); return; }
        Data = JsonUtility.FromJson<IauFile>(dataset.text);
    }

    void OnEnable()
    {
        if (!chase) chase = FindAnyObjectByType<ChaseCamera>();
        if (!minimap && chase) minimap = chase.GetComponent<MinimapHUD>();
        if (minimap)
        {
            minimap.OverlayGUI += DrawOnMap;
            minimap.clickFilter += OnMapClick;
            // Los lugares fijos del minimapa que tienen nombre IAU los dibuja el atlas (con las coordenadas oficiales).
            if (Data != null && minimap.places != null)
            {
                var keep = new List<MinimapHUD.Place>();
                foreach (var pl in minimap.places)
                    if (Array.Find(Data.formaciones, f => f.nombre == pl.name) == null) keep.Add(pl);
                minimap.places = keep.ToArray();
            }
        }
        RenderPipelineManager.beginCameraRendering += BeginCam;
        RenderPipelineManager.endCameraRendering += EndCam;
    }

    void OnDisable()
    {
        if (minimap) { minimap.OverlayGUI -= DrawOnMap; minimap.clickFilter -= OnMapClick; }
        RenderPipelineManager.beginCameraRendering -= BeginCam;
        RenderPipelineManager.endCameraRendering -= EndCam;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        ClearHighlight();
        if (overlayMat) Destroy(overlayMat);
        if (circleTex) Destroy(circleTex);
        if (photoCam) Destroy(photoCam.gameObject);
        if (photoRt) { photoRt.Release(); Destroy(photoRt); }
    }

    // ---------- Búsqueda ----------

    public struct Hit
    {
        public IauFeature feature;
        public double distanceKm;   // del punto buscado al centro
        public bool inside;
        public bool centerMatch;    // las coordenadas buscadas son las del centro oficial
    }

    static string Article(IauFeature f) => f.TipoEs.ToLowerInvariant().Split(' ')[0] switch
    {
        "cráter" => "el cráter", "monte" => "el monte", "llanura" => "la llanura", "valle" => "el valle", "mesas" => "las mesas", _ => "",
    };

    /// <summary>"del cráter", "de la llanura"...</summary>
    static string Of(IauFeature f)
    {
        string a = Article(f);
        return a.StartsWith("el ") ? "del " + a.Substring(3) : "de " + a;
    }

    public sealed class SearchResult
    {
        public bool isCoordinate;
        public double lat, lon;     // planetocéntricas, lon este 0–360
        public readonly List<Hit> hits = new List<Hit>();
        public string message;
    }

    public SearchResult Search(string query)
    {
        var r = new SearchResult();
        query = (query ?? "").Trim();
        if (query.Length == 0) { foreach (var f in Features) r.hits.Add(new Hit { feature = f }); return r; }

        if (TryParseCoordinates(query, out double lat, out double lon))
        {
            r.isCoordinate = true; r.lat = lat; r.lon = lon;
            var near = new List<Hit>();
            foreach (var f in Features)
            {
                double d = GreatCircleKm(f.lon, f.lat, lon, lat);
                // Coordenadas del centro oficial (con el redondeo de 2 decimales, ~0,3 km, o el 2 % del tamaño).
                bool center = d <= Math.Max(1.0, f.diametroKm * 0.02);
                var h = new Hit { feature = f, distanceKm = d, inside = center || f.Contains(lon, lat), centerMatch = center };
                if (h.inside) r.hits.Add(h); else near.Add(h);
            }
            // Orden: el lugar cuyas coordenadas oficiales son esas; luego los cráteres que lo contienen; luego el
            // resto (montes, llanuras y valles, por su recuadro). Dentro de cada grupo, lo más pequeño primero.
            int Rank(Hit h) => h.centerMatch ? 0 : h.feature.IsCrater ? 1 : 2;
            r.hits.Sort((a, b) => Rank(a) != Rank(b) ? Rank(a).CompareTo(Rank(b)) : a.feature.diametroKm.CompareTo(b.feature.diametroKm));
            near.Sort((a, b) => a.distanceKm.CompareTo(b.distanceKm));
            string where = $"{FormatLat(lat)}, {FormatLon(lon)}";
            if (r.hits.Count == 0)
                r.message = $"{where}: ninguna formación con nombre IAU contiene este punto. La más cercana es " +
                            (near.Count > 0 ? $"{near[0].feature.nombre}, a {near[0].distanceKm:0.#} km." : "—");
            else
            {
                var top = r.hits[0].feature;
                r.message = r.hits[0].centerMatch
                    ? $"{where}: coordenadas oficiales {Of(top)} {top.nombre}."
                    : $"{where} está en {Article(top)} {top.nombre}.";
                if (r.hits.Count > 1)
                {
                    var others = new List<string>();
                    for (int i = 1; i < r.hits.Count; i++) others.Add(r.hits[i].feature.nombre);
                    r.message += " También dentro de: " + string.Join(", ", others) + ".";
                }
            }
            for (int i = 0; i < near.Count && i < 3; i++) r.hits.Add(near[i]);
            return r;
        }

        string q = Fold(query);
        foreach (var f in Features)
            if (Fold(f.nombre).Contains(q) || Fold(f.TipoEs).Contains(q)) r.hits.Add(new Hit { feature = f });
        r.message = r.hits.Count == 0 ? "Ningún lugar con ese nombre en la región del mapa." : null;
        return r;
    }

    static readonly Regex CoordToken = new Regex(
        @"(?<v>[-+−]?\d+(?:\.\d+)?)\s*(?:[°º]\s*(?:(?<m>\d+(?:\.\d+)?)\s*['′’]\s*(?:(?<s>\d+(?:\.\d+)?)\s*(?:""|″|''|”))?)?)?\s*(?<h>[NSEOW](?![a-z]))?",
        RegexOptions.IgnoreCase);

    /// <summary>Entiende "-5.37, 137.81", "5.37 S 137.81 E", "5°22' S, 137°48' E", "137.81E -5.37" y la coma decimal
    /// ("-5,37 137,81"). Sin letras, el orden es latitud, longitud (como Google Earth); una longitud oeste o negativa
    /// se pasa a 0–360 este.</summary>
    public static bool TryParseCoordinates(string text, out double lat, out double lon)
    {
        lat = lon = 0;
        string s = text.Trim();
        // Coma decimal: "5,37" (coma entre dígitos sin espacio) → "5.37"; el resto de comas separa.
        s = Regex.Replace(s, @"(?<=\d),(?=\d)", ".");
        if (Regex.IsMatch(s, @"[a-df-mp-rt-vx-z]", RegexOptions.IgnoreCase)) return false;   // texto: búsqueda por nombre
        var ms = CoordToken.Matches(s);
        var vals = new List<(double v, char h)>();
        foreach (Match m in ms)
        {
            if (!m.Success || m.Length == 0) continue;
            double v = double.Parse(m.Groups["v"].Value.Replace('−', '-'), CultureInfo.InvariantCulture);
            double sign = v < 0 ? -1 : 1;
            v = Math.Abs(v);
            if (m.Groups["m"].Success) v += double.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) / 60.0;
            if (m.Groups["s"].Success) v += double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) / 3600.0;
            char h = m.Groups["h"].Success ? char.ToUpperInvariant(m.Groups["h"].Value[0]) : '\0';
            if (h == 'S' || h == 'W' || h == 'O') sign = -1;
            vals.Add((sign * v, h));
        }
        if (vals.Count != 2) return false;
        var (a, ha) = vals[0];
        var (b, hb) = vals[1];
        bool aIsLon = ha == 'E' || ha == 'W' || ha == 'O' || hb == 'N' || hb == 'S' || (ha == '\0' && hb == '\0' && Math.Abs(a) > 90);
        lat = aIsLon ? b : a;
        lon = aIsLon ? a : b;
        if (Math.Abs(lat) > 90) return false;
        lon = ((lon % 360) + 360) % 360;
        return true;
    }

    static string Fold(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var n = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(n.Length);
        foreach (char c in n)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static string FormatLat(double v) => v < 0 ? $"{-v:0.###}° S" : $"{v:0.###}° N";
    public static string FormatLon(double v) => v > 180 ? $"{v:0.###}° E ({360 - v:0.###}° O)" : $"{v:0.###}° E";

    public static double GreatCircleKm(double lon1, double lat1, double lon2, double lat2) =>
        ExplorerMenu.GreatCircle(lon1, lat1, lon2, lat2) / 1000.0;

    // ---------- Selección ----------

    public void Select(IauFeature f, bool frame = true)
    {
        Selected = f;
        ClearHighlight();
        if (f == null) { SelectionChanged?.Invoke(null); return; }
        if (f.Circular) BuildHighlight(f);
        if (frame && chase)
        {
            float half = HalfExtentUnits(f);
            var c = GroundRibbon.OnGround(f.Center);
            chase.FrameBounds(new Bounds(c, new Vector3(half * 2.4f, 0f, half * 2.4f)));
            if (minimap && minimap.Interactive) minimap.SetInteractive(false);
        }
        StartCoroutine(TakePhoto(f));
        SelectionChanged?.Invoke(f);
    }

    public void ClearSelection() => Select(null, false);

    /// <summary>Mitad del lado del área en unidades Unity (radio del cráter o del recuadro).</summary>
    public static float HalfExtentUnits(IauFeature f)
    {
        float km = f.Circular ? f.diametroKm * 0.5f
            : (float)Math.Max((f.latNorte - f.latSur) * 0.5, (f.lonEste - f.lonOeste) * 0.5 * Math.Cos(f.lat * Math.PI / 180)) * 59.27f;
        return Mathf.Max(0.5f, km * 1000f * MarsGeo.WorldScale);
    }

    void ClearHighlight() => highlightOn = false;

    // Sombreado blanco del área del cráter: la propia malla del terreno se vuelve a dibujar con Gale/GeoOverlay y
    // una textura circular (relleno translúcido y borde más opaco) proyectada en planta sobre el recuadro del
    // cráter. Queda pegada al relieve sin huecos y se ve igual en la vista principal y en el minimapa.
    void BuildHighlight(IauFeature f)
    {
        if (!overlayMat)
        {
            var sh = highlightMaterial ? highlightMaterial.shader : Shader.Find("Gale/GeoOverlay");
            if (!sh) return;
            overlayMat = new Material(sh) { hideFlags = HideFlags.DontSave, renderQueue = 2901 };   // sobre la capa temática
            overlayMat.SetFloat("_Opacity", 1f);
            overlayMat.SetFloat("_Shade", 0.35f);
        }
        if (!circleTex) circleTex = CircleTexture(512);
        overlayMat.SetTexture("_GeoTex", circleTex);
        Vector3 c = f.Center;
        float rz = f.diametroKm * 500f * MarsGeo.WorldScale;                  // km → m → u (norte-sur)
        float rx = rz / Mathf.Cos((float)f.lat * Mathf.Deg2Rad);              // la longitud se estira con 1/cos(lat)
        overlayMat.SetVector("_GeoRect", new Vector4(c.x - rx, c.z - rz, 1f / (2f * rx), 1f / (2f * rz)));
        if (terrain == null || terrain.Length == 0)
        {
            var root = GameObject.Find("Gale_Terrain");
            terrain = root ? root.GetComponentsInChildren<MeshFilter>() : Array.Empty<MeshFilter>();
        }
        highlightOn = true;
    }

    Texture2D CircleTexture(int n)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true, false)
        {
            name = "Lugar_Circulo", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, hideFlags = HideFlags.DontSave,
        };
        var px = new Color32[n * n];
        float rim = 0.035f, aa = 2f / n;
        byte fill = (byte)(highlightColor.a * 255f), edge = (byte)(rimColor.a * 255f);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f, d = Mathf.Sqrt(dx * dx + dy * dy);
                float inside = Mathf.Clamp01((1f - d) / aa);
                float isRim = Mathf.Clamp01((d - (1f - rim)) / aa);
                byte a = (byte)(Mathf.Lerp(fill, edge, isRim) * inside);
                px[y * n + x] = new Color32(255, 255, 255, a);
            }
        t.SetPixels32(px);
        t.Apply(true, true);
        return t;
    }

    void LateUpdate()
    {
        if (!highlightOn || hideHighlight || !overlayMat || terrain == null) return;
        var rp = new RenderParams(overlayMat) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
        foreach (var mf in terrain)
        {
            if (!mf || !mf.gameObject.activeInHierarchy || !mf.sharedMesh) continue;
            rp.layer = mf.gameObject.layer;
            Graphics.RenderMesh(rp, mf.sharedMesh, 0, mf.transform.localToWorldMatrix);
        }
    }

    // ---------- Foto orbital ----------

    // Vista cenital del lugar con una cámara ortográfica sobre el terreno de la escena: es la imagen orbital real
    // con la que está texturizado (sin la capa temática, el sombreado ni la niebla).
    IEnumerator TakePhoto(IauFeature f)
    {
        if (!photoRt) photoRt = new RenderTexture(768, 768, 24) { name = "Lugar_Foto", antiAliasing = 2 };
        if (!photoCam)
        {
            var go = new GameObject("Lugar Foto Camera") { hideFlags = HideFlags.DontSave };
            photoCam = go.AddComponent<Camera>();
            photoCam.orthographic = true;
            photoCam.clearFlags = CameraClearFlags.SolidColor;
            photoCam.backgroundColor = new Color(0.08f, 0.05f, 0.04f);
            photoCam.nearClipPlane = 0.5f;
            photoCam.farClipPlane = 1000f;
            photoCam.depth = -20f;
            photoCam.cullingMask = ~(1 << 2);
            photoCam.targetTexture = photoRt;
            photoCam.enabled = false;
        }
        Vector3 c = f.Center;
        // Al menos 1 km de lado: en los cráteres diminutos (Yellowknife, 120 m) se ve el entorno.
        float photoHalf = Mathf.Max(HalfExtentUnits(f) * 1.15f, 500f * MarsGeo.WorldScale);
        photoCam.orthographicSize = photoHalf;
        photoCam.transform.SetPositionAndRotation(new Vector3(c.x, 200f, c.z), Quaternion.Euler(90f, 0f, 0f));
        var geo = GeoLayers.Instance;
        float op = geo ? geo.opacity : 0f;
        if (geo) geo.opacity = 0f;
        hideHighlight = true;
        photoCam.enabled = true;
        yield return null;
        yield return new WaitForEndOfFrame();
        photoCam.enabled = false;
        if (geo) geo.opacity = op;
        hideHighlight = false;
        float km = photoHalf * 2f / MarsGeo.WorldScale / 1000f;
        PhotoNote = $"Vista cenital de {km:0.#} km de lado, norte arriba. Imagen orbital del terreno: mosaico global CTX " +
                    "(MRO; NASA/JPL/MSSS/Caltech Murray Lab) con color Viking MDIM 2.1 y THEMIS; HiRISE en la ruta de Curiosity.";
        SelectionChanged?.Invoke(Selected);
    }

    bool fogWas;
    void BeginCam(ScriptableRenderContext ctx, Camera c) { if (c == photoCam) { fogWas = RenderSettings.fog; RenderSettings.fog = false; } }
    void EndCam(ScriptableRenderContext ctx, Camera c) { if (c == photoCam) RenderSettings.fog = fogWas; }

    // ---------- Datos de la escena dentro del área ----------

    public sealed class AreaStats
    {
        public int cells;
        public float minH = float.MaxValue, maxH = float.MinValue, centerH = float.NaN;
        public float[] geo = new float[7], soil = new float[5];   // fracción por clase
    }

    /// <summary>Recorre la rejilla de los mapas (163 m) dentro del área: clases de geología y suelo, y la cota
    /// mínima y máxima de la malla de navegación.</summary>
    public AreaStats Measure(IauFeature f)
    {
        var s = new AreaStats();
        var gl = GeoLayers.Instance;
        if (!gl) return s;
        var reg = gl.Data.region;
        byte[] geo = gl.IndexData(gl.LayerIndex("geologia")), soil = gl.IndexData(gl.LayerIndex("suelo"));
        var grid = NavigationSystem.Instance ? NavigationSystem.Instance.Grid : null;
        double dLon = (reg.lonEste - reg.lonOeste) / reg.ancho, dLat = (reg.latNorte - reg.latSur) / reg.alto;
        int x0 = Mathf.Max(0, (int)((f.lonOeste - reg.lonOeste) / dLon) - 1), x1 = Mathf.Min(reg.ancho - 1, (int)((f.lonEste - reg.lonOeste) / dLon) + 1);
        int y0 = Mathf.Max(0, (int)((f.latSur - reg.latSur) / dLat) - 1), y1 = Mathf.Min(reg.alto - 1, (int)((f.latNorte - reg.latSur) / dLat) + 1);
        if (f.Circular)
        {
            double rDeg = f.diametroKm * 0.5 / 59.27;
            double rLon = rDeg / Math.Cos(f.lat * Math.PI / 180);
            x0 = Mathf.Max(0, (int)((f.lon - rLon - reg.lonOeste) / dLon)); x1 = Mathf.Min(reg.ancho - 1, (int)((f.lon + rLon - reg.lonOeste) / dLon) + 1);
            y0 = Mathf.Max(0, (int)((f.lat - rDeg - reg.latSur) / dLat)); y1 = Mathf.Min(reg.alto - 1, (int)((f.lat + rDeg - reg.latSur) / dLat) + 1);
        }
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double lo = reg.lonOeste + (x + 0.5) * dLon, la = reg.latSur + (y + 0.5) * dLat;
                if (!f.Contains(lo, la)) continue;
                int i = y * reg.ancho + x;
                s.cells++;
                if (geo != null && geo[i] < 7) s.geo[geo[i]]++;
                if (soil != null && soil[i] < 5) s.soil[soil[i]]++;
                if (grid != null && grid.L0.W == reg.ancho)
                {
                    float h = grid.L0.Height[i];
                    if (!float.IsNaN(h)) { if (h < s.minH) s.minH = h; if (h > s.maxH) s.maxH = h; }
                }
            }
        if (s.cells > 0)
        {
            for (int i = 0; i < 7; i++) s.geo[i] /= s.cells;
            for (int i = 0; i < 5; i++) s.soil[i] /= s.cells;
        }
        float? cy = MarsGeo.GroundY(f.Center);
        if (cy.HasValue) s.centerH = (float)MarsGeo.Elevation(cy.Value);
        return s;
    }

    // ---------- Nodos ----------

    // Los muy pequeños (Yellowknife, 120 m) solo con el mapa acercado (lado visible < 16 km) o si están elegidos.
    bool Visible(IauFeature f, Rect r, float pxPerUnit) =>
        f == Selected || f.diametroKm * 1000f * MarsGeo.WorldScale * pxPerUnit > 3f || minimap.radiusMeters <= 8000f;

    void DrawOnMap(Rect r)
    {
        if (Data == null || !minimap) return;
        nodeStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
        float pxPerUnit = r.width / (2f * minimap.radiusMeters * MarsGeo.WorldScale);
        var old = GUI.color;
        // Contorno del cráter elegido.
        if (Selected != null && Selected.Circular && Event.current.type == EventType.Repaint)
        {
            float rad = Selected.diametroKm * 500f * MarsGeo.WorldScale;
            Vector3 c = Selected.Center;
            GUI.color = rimColor;
            Vector2 prev = minimap.WorldToMap(c + new Vector3(rad, 0f, 0f), r);
            for (int j = 1; j <= 96; j++)
            {
                float a = j * Mathf.PI * 2f / 96f;
                Vector2 cur = minimap.WorldToMap(c + new Vector3(Mathf.Cos(a) * rad, 0f, Mathf.Sin(a) * rad), r);
                Vector2 a0 = prev, b0 = cur;
                if (minimap.ClipLine(ref a0, ref b0, r)) MinimapHUD.Line(a0, b0, 2f);
                prev = cur;
            }
        }
        foreach (var f in Features)
        {
            Vector2 p = minimap.WorldToMap(f.Center, r);
            if (!minimap.InMap(p, r, 8f) || !Visible(f, r, pxPerUnit)) continue;
            bool sel = f == Selected;
            MinimapHUD.Marker(p, f.IsCrater ? "◎" : "◇", sel ? Color.white : nodeColor, 0f);
            if (!minimap.Interactive && !sel && f.diametroKm < 30f) continue;   // en el círculo, solo los nombres grandes
            string t = f.nombre;
            GUI.color = Color.black; GUI.Label(new Rect(p.x + 11, p.y - 7, 180, 20), t, nodeStyle);
            GUI.color = sel ? Color.white : new Color(0.93f, 0.95f, 1f); GUI.Label(new Rect(p.x + 10, p.y - 8, 180, 20), t, nodeStyle);
        }
        GUI.color = old;
    }

    /// <summary>Clic en el mapa interactivo: si cae sobre un nodo, lo elige (y no fija destino de ruta).</summary>
    bool OnMapClick(Vector2 gui)
    {
        if (Data == null || !minimap) return false;
        Rect r = minimap.MapRect();
        float pxPerUnit = r.width / (2f * minimap.radiusMeters * MarsGeo.WorldScale);
        IauFeature best = null;
        float bd = NodePixels * MinimapHUD.UiScale;
        foreach (var f in Features)
        {
            if (!Visible(f, r, pxPerUnit)) continue;
            float d = Vector2.Distance(minimap.WorldToMap(f.Center, r), gui);
            if (d < bd) { bd = d; best = f; }
        }
        if (best == null) return false;
        Select(best, frame: false);
        return true;
    }

    // Vista aérea: nodos con nombre sobre el terreno; con el cursor libre (menú abierto) se pueden pulsar.
    void OnGUI()
    {
        if (Data == null || SimPause.IsPaused || !chase || !chase.Aerial || (minimap && minimap.Interactive)) return;
        var cam = chase.GetComponent<Camera>();
        if (!cam) return;
        nodeButton ??= new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        bool clickable = UnityEngine.Cursor.visible;
        float span = chase.aerialHeight;
        foreach (var f in Features)
        {
            Vector3 w = GroundRibbon.OnGround(f.Center);
            Vector3 sp = cam.WorldToScreenPoint(w);
            if (sp.z <= 0f) continue;
            // Los pequeños, solo cuando la cámara está cerca.
            if (f.diametroKm * 1000f * MarsGeo.WorldScale < span * 0.004f && f != Selected) continue;
            var pos = new Vector2(sp.x, Screen.height - sp.y);
            bool sel = f == Selected;
            var old = GUI.color;
            GUI.color = Color.black; GUI.Label(new Rect(pos.x - 7, pos.y - 9, 20, 20), f.IsCrater ? "◎" : "◇");
            GUI.color = sel ? Color.white : nodeColor; GUI.Label(new Rect(pos.x - 8, pos.y - 10, 20, 20), f.IsCrater ? "◎" : "◇");
            var lr = new Rect(pos.x + 10, pos.y - 11, 220, 22);
            GUI.color = Color.black; GUI.Label(new Rect(lr.x + 1, lr.y + 1, lr.width, lr.height), f.nombre, nodeButton);
            GUI.color = sel ? Color.white : new Color(0.93f, 0.95f, 1f);
            if (clickable ? GUI.Button(lr, f.nombre, nodeButton) : false) Select(f, frame: true);
            else if (!clickable) GUI.Label(lr, f.nombre, nodeButton);
            GUI.color = old;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}
