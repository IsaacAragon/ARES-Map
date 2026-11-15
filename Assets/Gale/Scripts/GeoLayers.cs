using System;
using System.Collections.Generic;
using UnityEngine;

// Capas temáticas del mapa (geología USGS, material del suelo THEMIS) sobre el terreno.
//  - Se eligen en el menú de pausa (Capas del mapa) o con L / cruceta ← (acción Global.CycleLayer).
//  - La capa activa se pinta con Gale/GeoOverlay volviendo a dibujar las mallas del terreno
//    (Graphics.RenderMesh): no se tocan materiales ni UV de los bloques, y la ve también el minimapa.
//  - La textura de color se genera al activar la capa a partir del mapa de clases y de los colores
//    de geo_dataset.json; con mipmaps, para que no parpadee vista de lejos.
//  - También responde qué hay bajo un punto (clase de cada capa, índice termofísico, zona).
[DefaultExecutionOrder(-60)]   // antes que PauseMenu (-50), que lo consulta
public class GeoLayers : MonoBehaviour
{
    public static GeoLayers Instance { get; private set; }

    [Serializable]
    public struct LayerTexture
    {
        public string id;          // id de la capa en geo_dataset.json
        public Texture2D index;    // R8, valor = clase (Read/Write activado)
    }

    [Tooltip("Assets/Gale/Data/geo_dataset.json (Tools/import_geo_dataset.py).")]
    public TextAsset dataset;
    public LayerTexture[] textures;
    [Tooltip("Índice termofísico continuo, 16 bit (0 = fino/polvo, 1 = roca).")]
    public Texture2D thermophysical;
    [Tooltip("Material con Gale/GeoOverlay (lo asigna el builder para que el shader entre en la build).")]
    public Material overlayMaterial;
    [Tooltip("Raíz de los bloques del terreno (Gale_Terrain).")]
    public Transform terrainRoot;
    [Range(0f, 1f)] public float opacity = 0.72f;
    [Tooltip("Cuánto oscurece el relieve el color de la capa (0 = color plano).")]
    [Range(0f, 1f)] public float shading = 0.85f;

    /// <summary>Cambió la capa activa.</summary>
    public event Action Changed;

    public GeoDataset Data { get; private set; }
    /// <summary>Índice de la capa activa en Data.capas, o -1 si se ve la imagen real.</summary>
    public int Current { get; private set; } = -1;
    public GeoLayerDef CurrentLayer => Current >= 0 ? Data.capas[Current] : null;
    public int LayerCount => Data?.capas?.Length ?? 0;

    Material mat;
    MeshFilter[] terrain = Array.Empty<MeshFilter>();
    byte[][] indexData;       // por capa, fila 0 = sur
    Texture2D[] colorTex;     // por capa, generada al usarla
    ushort[] thermoData;
    int thermoW, thermoH;
    Vector2 rectMin, rectInvSize;

    static readonly int GeoTexId = Shader.PropertyToID("_GeoTex"), GeoRectId = Shader.PropertyToID("_GeoRect"),
        OpacityId = Shader.PropertyToID("_Opacity"), ShadeId = Shader.PropertyToID("_Shade");

    void Awake()
    {
        Instance = this;
        if (!dataset) { Debug.LogError("[Geo] falta geo_dataset.json en GeoLayers"); enabled = false; return; }
        Data = GeoDataset.Parse(dataset.text);
        indexData = new byte[LayerCount][];
        colorTex = new Texture2D[LayerCount];

        // Rectángulo de las imágenes en unidades Unity: X y Z son lineales en lon y lat (MarsGeo).
        var r = Data.region;
        Vector3 sw = MarsGeo.LonLatToUnity(r.lonOeste, r.latSur), ne = MarsGeo.LonLatToUnity(r.lonEste, r.latNorte);
        rectMin = new Vector2(sw.x, sw.z);
        rectInvSize = new Vector2(1f / (ne.x - sw.x), 1f / (ne.z - sw.z));

        var shader = overlayMaterial ? overlayMaterial.shader : Shader.Find("Gale/GeoOverlay");
        if (!shader) { Debug.LogError("[Geo] no se encuentra el shader Gale/GeoOverlay"); enabled = false; return; }
        mat = new Material(shader) { hideFlags = HideFlags.DontSave };
        mat.SetVector(GeoRectId, new Vector4(rectMin.x, rectMin.y, rectInvSize.x, rectInvSize.y));
    }

    void Start()
    {
        if (!terrainRoot)
        {
            var go = GameObject.Find("Gale_Terrain");
            if (go) terrainRoot = go.transform;
        }
        if (terrainRoot) terrain = terrainRoot.GetComponentsInChildren<MeshFilter>();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (mat) Destroy(mat);
        if (colorTex != null)
            foreach (var t in colorTex)
                if (t) Destroy(t);
    }

    void Update()
    {
        if (!SimPause.IsPaused && GaleInput.Controls.Global.CycleLayer.WasPressedThisFrame()) Cycle();
    }

    void LateUpdate()
    {
        if (Current < 0 || !mat) return;
        mat.SetFloat(OpacityId, opacity);
        mat.SetFloat(ShadeId, shading);
        var rp = new RenderParams(mat)
        {
            shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off,
            receiveShadows = false,
        };
        foreach (var mf in terrain)
        {
            if (!mf || !mf.gameObject.activeInHierarchy || !mf.sharedMesh) continue;
            rp.layer = mf.gameObject.layer;
            // La malla que se está pintando (con el realce de relieve de ReliefShading, si lo hay).
            Graphics.RenderMesh(rp, mf.sharedMesh, 0, mf.transform.localToWorldMatrix);
        }
    }

    // ---------- Selección ----------

    /// <summary>Activa la capa i de Data.capas; -1 vuelve a la imagen real.</summary>
    public void Set(int layer)
    {
        layer = Mathf.Clamp(layer, -1, LayerCount - 1);
        if (layer == Current) return;
        if (layer >= 0)
        {
            var tex = ColorTexture(layer);
            if (!tex) { Debug.LogWarning($"[Geo] la capa '{Data.capas[layer].id}' no tiene textura de índices"); return; }
            mat.SetTexture(GeoTexId, tex);
        }
        Current = layer;
        Changed?.Invoke();
    }

    /// <summary>Imagen real → capa 0 → capa 1 → … → imagen real.</summary>
    public void Cycle() => Set(Current + 1 >= LayerCount ? -1 : Current + 1);

    public int LayerIndex(string id)
    {
        for (int i = 0; i < LayerCount; i++)
            if (Data.capas[i].id == id) return i;
        return -1;
    }

    // ---------- Consulta bajo un punto ----------

    bool TryPixel(Vector3 world, int w, int h, out int x, out int y)
    {
        float u = (world.x - rectMin.x) * rectInvSize.x, v = (world.z - rectMin.y) * rectInvSize.y;
        x = Mathf.Min((int)(u * w), w - 1);
        y = Mathf.Min((int)(v * h), h - 1);
        return u >= 0f && u <= 1f && v >= 0f && v <= 1f;
    }

    /// <summary>Clase de la capa bajo un punto del mundo; 0 = sin dato o fuera de la región.</summary>
    public int ClassAt(int layer, Vector3 world)
    {
        var data = Index(layer);
        var r = Data.region;
        if (data == null || !TryPixel(world, r.ancho, r.alto, out int x, out int y)) return 0;
        return data[y * r.ancho + x];
    }

    /// <summary>Índice termofísico 0..1 (0 = fino/polvo, 1 = roca), o null si no hay dato.</summary>
    public float? ThermoAt(Vector3 world)
    {
        if (thermoData == null)
        {
            if (!thermophysical) return null;
            thermoW = thermophysical.width;
            thermoH = thermophysical.height;
            if (thermophysical.format == TextureFormat.R16)
                thermoData = thermophysical.GetPixelData<ushort>(0).ToArray();
            else
            {
                var px = thermophysical.GetPixels();
                thermoData = new ushort[px.Length];
                for (int i = 0; i < px.Length; i++) thermoData[i] = (ushort)(px[i].r * 65535f);
            }
        }
        if (!TryPixel(world, thermoW, thermoH, out int x, out int y)) return null;
        return thermoData[y * thermoW + x] / 65535f;
    }

    /// <summary>La zona más pequeña del equipo que contiene el punto (la regional si no hay otra).</summary>
    public GeoZone ZoneAt(Vector3 world)
    {
        var ll = MarsGeo.UnityToLonLat(world);
        GeoZone best = null;
        foreach (var z in Data.zonas)
            if (z.Contains(ll.lon, ll.lat) && (best == null || z.AreaKm2 < best.AreaKm2)) best = z;
        return best;
    }

    /// <summary>Porcentaje de cada clase de la capa dentro de una zona (índice = clase − 1).</summary>
    public float[] ZonePercent(int layer, GeoZone zone)
    {
        if (zone == null || layer < 0) return null;
        switch (Data.capas[layer].id)
        {
            case "geologia": return zone.geologiaPct;
            case "suelo": return zone.sueloPct;
            default: return null;
        }
    }

    /// <summary>Ficha de la zona bajo un punto, en texto enriquecido: reparto de materiales y unidades,
    /// índices regionales TES/MOLA y la mineralogía medida (primero la de esa zona, luego el resto).</summary>
    public string ZoneReport(Vector3 world)
    {
        var sb = new System.Text.StringBuilder();
        var z = ZoneAt(world);
        if (z == null) sb.Append("<b>Fuera de las zonas con datos</b>\n");
        else
        {
            sb.Append($"<b>Zona del rover: {Plain(z.nombre)}</b>  ·  {z.anchoKm:0} × {z.altoKm:0} km\n");
            foreach (var def in Data.capas)
            {
                float[] pct = ZonePercent(LayerIndex(def.id), z);
                if (pct == null) continue;
                sb.Append(def.nombre).Append(": ");
                bool first = true;
                foreach (var c in def.clases)
                {
                    float v = c.indice - 1 < pct.Length ? pct[c.indice - 1] : 0f;
                    if (v <= 0f) continue;
                    if (!first) sb.Append(" · ");
                    first = false;
                    sb.Append(string.IsNullOrEmpty(c.codigo) ? c.nombre : c.codigo).Append($" {v:0.#} %");
                }
                sb.Append('\n');
            }
            if (z.tes != null && z.tes.Length > 0)
            {
                sb.Append("Índices regionales relativos (0–1): ");
                for (int i = 0; i < z.tes.Length; i++)
                    sb.Append(i > 0 ? " · " : "").Append(Plain(z.tes[i].nombre)).Append($" {z.tes[i].valor:0.00}");
                sb.Append('\n');
            }
        }

        string zid = z?.id;
        sb.Append("\n<b>Mineralogía medida en esta zona</b>\n");
        int n = 0;
        foreach (var s in Data.mineralogia)
            if (zid != null && s.InZone(zid)) { AppendSite(sb, s); n++; }
        if (n == 0) sb.Append("No hay sitios medidos en esta zona.\n");
        if (n < Data.mineralogia.Length)
        {
            sb.Append("\n<b>Otras zonas</b>\n");
            foreach (var s in Data.mineralogia)
                if (zid == null || !s.InZone(zid)) AppendSite(sb, s);
        }
        return sb.ToString().TrimEnd('\n');
    }

    static void AppendSite(System.Text.StringBuilder sb, GeoSite s)
    {
        sb.Append($"\n<b>{Plain(s.sitio)}</b>\n{Plain(s.tipo)}\n");
        sb.Append($"Minerales: {Plain(s.mineralogia)}\n");
        sb.Append($"Para el recorrido: {Plain(s.notas)}\n");
        sb.Append($"<size=85%>{Plain(s.instrumento)} · {Plain(s.fuente)}</size>\n");
    }

    // Los textos del zip traen "<150 µm" y similares: que no se lean como etiquetas.
    static string Plain(string s) => string.IsNullOrEmpty(s) || s.IndexOf('<') < 0 ? s : "<noparse>" + s + "</noparse>";

    // ---------- Acceso a los datos (navegación) ----------

    /// <summary>Rectángulo de los mapas en unidades Unity (esquina SO y tamaño).</summary>
    public Rect RegionRect => new Rect(rectMin.x, rectMin.y, 1f / rectInvSize.x, 1f / rectInvSize.y);

    /// <summary>Clases de la capa por píxel (fila 0 = sur, Data.region.ancho × alto), o null. No modificar.</summary>
    public byte[] IndexData(int layer) => Index(layer);

    /// <summary>Índice termofísico por píxel (0..65535, fila 0 = sur), o null si no hay textura.</summary>
    public ushort[] ThermoData(out int w, out int h)
    {
        ThermoAt(new Vector3(rectMin.x, 0f, rectMin.y));   // carga perezosa
        w = thermoW; h = thermoH;
        return thermoData;
    }

    /// <summary>Añade una capa generada en ejecución (p. ej. Transitabilidad) con su mapa de clases
    /// en la rejilla de la región. Si ya existe una con ese id, la sustituye. Devuelve su índice.</summary>
    public int AddRuntimeLayer(GeoLayerDef def, byte[] classes)
    {
        var r = Data.region;
        if (classes == null || classes.Length != r.ancho * r.alto)
        {
            Debug.LogError($"[Geo] la capa '{def.id}' no tiene {r.ancho}×{r.alto} píxeles");
            return -1;
        }
        int i = LayerIndex(def.id);
        if (i < 0)
        {
            i = LayerCount;
            var capas = Data.capas;
            Array.Resize(ref capas, i + 1);
            Data.capas = capas;
            Array.Resize(ref indexData, i + 1);
            Array.Resize(ref colorTex, i + 1);
        }
        else
        {
            Data.capas[i] = def;
            if (colorTex[i]) Destroy(colorTex[i]);
            colorTex[i] = null;
        }
        Data.capas[i] = def;
        indexData[i] = classes;
        if (Current == i) { Current = -1; Set(i); }
        else Changed?.Invoke();
        return i;
    }

    // ---------- Datos y textura de color ----------

    byte[] Index(int layer)
    {
        if (layer < 0 || layer >= LayerCount) return null;
        if (indexData[layer] != null) return indexData[layer];
        Texture2D tex = null;
        string id = Data.capas[layer].id;
        if (textures != null)
            foreach (var t in textures)
                if (t.id == id) tex = t.index;
        if (!tex) return null;
        var r = Data.region;
        if (tex.width != r.ancho || tex.height != r.alto)
        {
            Debug.LogError($"[Geo] {tex.name} mide {tex.width}×{tex.height} y se esperaba {r.ancho}×{r.alto}: " +
                           "revisa la importación (Non-Power of 2 = None, Max Size ≥ 4096).");
            return null;
        }
        byte[] data;
        if (tex.format == TextureFormat.R8) data = tex.GetPixelData<byte>(0).ToArray();
        else
        {
            var px = tex.GetPixels32();
            data = new byte[px.Length];
            for (int i = 0; i < px.Length; i++) data[i] = px[i].r;
        }
        return indexData[layer] = data;
    }

    Texture2D ColorTexture(int layer)
    {
        if (colorTex[layer]) return colorTex[layer];
        var idx = Index(layer);
        if (idx == null) return null;
        var def = Data.capas[layer];
        int w = Data.region.ancho, h = Data.region.alto;

        var palette = new Color32[256];   // sin dato = transparente
        foreach (var c in def.clases) palette[c.indice & 255] = c.Color32;
        var px = new Color32[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = palette[idx[i]];

        if (def.contactos)
        {
            // Línea de contacto: píxeles cuyo vecino de la derecha o de arriba es de otra clase (2 px de ancho).
            var line = new List<int>();
            for (int y = 0; y < h - 1; y++)
                for (int x = 0, i = y * w; x < w - 1; x++, i++)
                {
                    byte a = idx[i];
                    if (a != idx[i + 1]) { line.Add(i); line.Add(i + 1); }
                    if (a != idx[i + w]) { line.Add(i); line.Add(i + w); }
                }
            foreach (int i in line)
                if (px[i].a != 0) px[i] = new Color32(38, 26, 22, 255);
        }

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, false)
        {
            name = "Geo_" + def.id + "_Color",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8,
            hideFlags = HideFlags.DontSave,
        };
        tex.SetPixels32(px);
        tex.Apply(true, true);
        return colorTex[layer] = tex;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}
