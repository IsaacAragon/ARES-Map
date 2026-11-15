using UnityEngine;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

// Realce del relieve SOLO en el sombreado.
//  - reliefIntensity exagera la pendiente aparente de las normales (equivale a sombrear el terreno como si
//    sus alturas fueran k veces mayores), pero sobre una copia de la malla que sólo se usa para pintar.
//  - La malla real (sourceMesh) sigue en el MeshCollider: elevaciones, colisiones, suspensión del rover,
//    rutas, HUD y sombras proyectadas siguen a escala real.
//  - También controla la luz principal rasante (acimut/elevación) y la luz ambiental que rellena el lado en sombra.
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter))]
public class ReliefShading : MonoBehaviour
{
    [Header("Relieve (solo sombreado; la geometría no cambia)")]
    [Tooltip("1 = sombreado real. 2-3 = pendientes más marcadas. No modifica elevaciones ni colisiones.")]
    [Range(1f, 5f)] public float reliefIntensity = 2f;
    [Tooltip("Malla real del terreno (la que usa el MeshCollider).")]
    public Mesh sourceMesh;

    [Header("Luz principal rasante")]
    public Light sun;
    [Tooltip("Desde dónde llega la luz, en grados desde el norte en sentido horario. 315 = noroeste.")]
    [Range(0f, 360f)] public float sunAzimuth = 315f;
    [Tooltip("Altura del sol sobre el horizonte, en grados.")]
    [Range(5f, 90f)] public float sunElevation = 30f;
    [Min(0f)] public float sunIntensity = 2.8f;

    [Header("Luz ambiental (lado en sombra)")]
    [Tooltip("Multiplica los tres colores ambientales. Más alto = el lado en sombra se lee mejor.")]
    [Range(0f, 3f)] public float ambientIntensity = 1.35f;
    public Color ambientSky = new Color(0.60f, 0.47f, 0.38f);
    public Color ambientEquator = new Color(0.52f, 0.39f, 0.30f);
    public Color ambientGround = new Color(0.30f, 0.21f, 0.15f);

    Mesh display;
    float appliedIntensity = -1f;
    bool dirty = true;

    void OnEnable()
    {
        dirty = true;
#if UNITY_EDITOR
        EditorSceneManager.sceneSaving += OnSceneSaving;
        EditorSceneManager.sceneSaved += OnSceneSaved;
#endif
    }

    void OnDisable()
    {
#if UNITY_EDITOR
        EditorSceneManager.sceneSaving -= OnSceneSaving;
        EditorSceneManager.sceneSaved -= OnSceneSaved;
#endif
        RestoreSource();
    }

    void OnValidate() => dirty = true;   // se aplica en Update (OnValidate no debe cambiar mallas)

    void Update()
    {
        if (dirty || !Mathf.Approximately(appliedIntensity, reliefIntensity) || !display)
            Apply();
    }

    public void Apply()
    {
        dirty = false;
        ApplyLighting();
        if (!sourceMesh) return;

        var mf = GetComponent<MeshFilter>();
        if (!display)
        {
            display = Instantiate(sourceMesh);
            display.name = sourceMesh.name + "_Relieve";
            display.hideFlags = HideFlags.DontSave;   // no se guarda en la escena: se regenera al cargar
        }

        // Dirección "arriba" del mundo expresada en el espacio local de la malla.
        Vector3 up = transform.InverseTransformDirection(Vector3.up).normalized;
        var src = sourceMesh.normals;
        var dst = new Vector3[src.Length];
        float k = Mathf.Max(1f, reliefIntensity);
        for (int i = 0; i < src.Length; i++)
        {
            Vector3 n = src[i];
            float a = Vector3.Dot(n, up);
            Vector3 h = n - a * up;                 // componente horizontal = pendiente
            dst[i] = (a * up + k * h).normalized;   // igual que sombrear con alturas ×k
        }
        display.normals = dst;
        display.RecalculateTangents();
        mf.sharedMesh = display;

        // La colisión siempre con la malla real.
        var mc = GetComponent<MeshCollider>();
        if (mc && mc.sharedMesh != sourceMesh) mc.sharedMesh = sourceMesh;

        appliedIntensity = reliefIntensity;
    }

    // Comprueba que el realce no toca la geometría: collider con la malla real, mismos vértices y bounds,
    // y la elevación medida por raycast (lo que usan la suspensión y las rutas del rover).
    [ContextMenu("Verificar escala real")]
    public string VerifyRealScale()
    {
        var mc = GetComponent<MeshCollider>();
        var mf = GetComponent<MeshFilter>();
        bool colliderReal = mc && mc.sharedMesh == sourceMesh;
        bool sameVerts = mf.sharedMesh.vertexCount == sourceMesh.vertexCount && mf.sharedMesh.bounds == sourceMesh.bounds;
        string probe = "sin impacto";
        if (Physics.Raycast(new Vector3(-424.58f, 5000f, 959.13f), Vector3.down, out var hit, 20000f))
            probe = $"y={hit.point.y:0.000} (real {hit.point.y / 0.02f:0} m)";
        string r = $"[Relief] k={reliefIntensity} colliderReal={colliderReal} mismosVertices/bounds={sameVerts} " +
                   $"raycast Bradbury {probe}";
        Debug.Log(r);
        return r;
    }

    void ApplyLighting()
    {
        if (sun)
        {
            // La luz viaja hacia el acimut opuesto, inclinada sunElevation grados hacia abajo.
            sun.transform.rotation = Quaternion.Euler(sunElevation, sunAzimuth + 180f, 0f);
            sun.intensity = sunIntensity;
        }
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = ambientSky * ambientIntensity;
        RenderSettings.ambientEquatorColor = ambientEquator * ambientIntensity;
        RenderSettings.ambientGroundColor = ambientGround * ambientIntensity;
    }

    void RestoreSource()
    {
        var mf = GetComponent<MeshFilter>();
        if (mf && sourceMesh) mf.sharedMesh = sourceMesh;
        if (display)
        {
            if (Application.isPlaying) Destroy(display); else DestroyImmediate(display);
            display = null;
        }
        appliedIntensity = -1f;
    }

#if UNITY_EDITOR
    // La escena guarda siempre la malla real; la copia sombreada se vuelve a crear justo después.
    void OnSceneSaving(UnityEngine.SceneManagement.Scene s, string path)
    {
        if (s == gameObject.scene) RestoreSource();
    }

    void OnSceneSaved(UnityEngine.SceneManagement.Scene s)
    {
        if (s == gameObject.scene) dirty = true;
    }
#endif
}
