using System.Collections.Generic;
using UnityEngine;

// Rodadas persistentes del rover: una cinta por lado (las 3 ruedas de cada lado van casi por la
// misma línea: ±1,06 u delante y detrás, ±1,19 u en medio, así que una cinta de 0,5 u las cubre).
//  - Usa los contactos de RoverController (sin lanzar rayos nuevos).
//  - Añade una sección cada `step` u recorridas; parado no añade nada; si ninguna rueda de un lado
//    toca el suelo, esa cinta se corta.
//  - Malla por bloques de `sectionsPerChunk` secciones: solo se actualiza el bloque activo;
//    los cerrados quedan estáticos. Al pasar de `maxChunks` por lado se descartan los más viejos.
//  - Sin basura de memoria por frame: listas y mallas reutilizadas.
[DefaultExecutionOrder(100)]
public class RoverTracks : MonoBehaviour
{
    public RoverController rover;
    public Material material;
    [Tooltip("Ancho de cada cinta (u).")]
    public float width = 0.5f;
    [Tooltip("Distancia entre secciones (u).")]
    public float step = 0.25f;
    [Tooltip("Altura sobre el suelo, siguiendo la normal (u).")]
    public float lift = 0.02f;
    [Tooltip("Longitud de la textura a lo largo de la cinta (u).")]
    public float textureLength = 1.5f;
    public int sectionsPerChunk = 512;
    [Tooltip("Bloques por lado antes de descartar los más viejos (512 secciones × 0,25 u = 128 u por bloque).")]
    public int maxChunks = 400;

    class Chunk
    {
        public GameObject go;
        public Mesh mesh;
        public readonly List<Vector3> verts = new List<Vector3>();
        public readonly List<Vector2> uvs = new List<Vector2>();
        public readonly List<int> tris = new List<int>();
        public int sections;
    }

    class Ribbon
    {
        public readonly Queue<Chunk> chunks = new Queue<Chunk>();
        public Chunk active;
        public bool hasLast, continuing;
        public Vector3 lastPoint, lastNormal;
        public float u;
        public int totalSections;
    }

    readonly Ribbon[] ribbons = { new Ribbon(), new Ribbon() };   // 0 = izquierda, 1 = derecha
    readonly Stack<Chunk> pool = new Stack<Chunk>();
    Transform root;

    public int TotalSections => ribbons[0].totalSections + ribbons[1].totalSections;
    public int ChunkCount => ribbons[0].chunks.Count + ribbons[1].chunks.Count;

    void Awake()
    {
        root = new GameObject("RoverTracks_Meshes") { hideFlags = HideFlags.DontSave }.transform;
    }

    void OnDestroy()
    {
        if (root) Destroy(root.gameObject);
    }

    void LateUpdate()
    {
        if (!rover || SimPause.IsPaused) return;
        var contacts = rover.Contacts;
        if (contacts == null || contacts.Length == 0) return;
        for (int side = 0; side < 2; side++)
        {
            bool left = side == 0;
            Vector3 p = Vector3.zero, n = Vector3.zero;
            int k = 0;
            foreach (var c in contacts)
            {
                if (c.left != left || !c.grounded) continue;
                p += c.point; n += c.normal; k++;
            }
            if (k == 0) { Break(side); continue; }
            AddSample(side, p / k, (n / k).normalized);
        }
    }

    /// <summary>Corta la cinta de un lado (rueda en el aire, teletransporte...).</summary>
    public void Break(int side)
    {
        ribbons[side].hasLast = false;
        ribbons[side].continuing = false;
    }

    public void BreakAll() { Break(0); Break(1); }

    /// <summary>Punto de contacto de un lado (0 izquierda, 1 derecha). Añade una sección si se recorrió `step`.</summary>
    public void AddSample(int side, Vector3 point, Vector3 normal)
    {
        var rb = ribbons[side];
        if (!rb.hasLast)
        {
            rb.hasLast = true;
            rb.lastPoint = point; rb.lastNormal = normal;
            rb.u = 0f;
            return;   // el primer punto solo fija el inicio
        }
        Vector3 d = point - rb.lastPoint;
        float dist = d.magnitude;
        if (dist < step) return;
        // Un salto grande (reinicio, teletransporte) no se une con una cinta.
        if (dist > step * 40f) { rb.lastPoint = point; rb.lastNormal = normal; rb.u = 0f; rb.continuing = false; return; }

        Vector3 dir = d / dist;
        bool startNew = rb.active == null || rb.active.sections == 0 || !rb.continuing;
        var ch = Active(rb, side);
        if (ch.sections == 0 || startNew)
            AppendSection(ch, rb.lastPoint, rb.lastNormal, dir, rb.u, connect: false);
        rb.u += dist / textureLength;
        AppendSection(ch, point, normal, dir, rb.u, connect: true);
        rb.lastPoint = point; rb.lastNormal = normal;
        rb.totalSections++;
        rb.continuing = true;
        Upload(ch);
    }

    Chunk Active(Ribbon rb, int side)
    {
        if (rb.active != null && rb.active.sections < sectionsPerChunk) return rb.active;
        // Bloque nuevo; si la cinta seguía, empieza repitiendo la última sección para no dejar hueco.
        var ch = pool.Count > 0 ? pool.Pop() : NewChunk();
        ch.verts.Clear(); ch.uvs.Clear(); ch.tris.Clear(); ch.sections = 0; ch.mesh.Clear();
        ch.go.name = side == 0 ? "Track_L" : "Track_R";
        ch.go.SetActive(true);
        rb.chunks.Enqueue(ch);
        rb.active = ch;
        rb.continuing = false;
        while (rb.chunks.Count > maxChunks)
        {
            var old = rb.chunks.Dequeue();
            rb.totalSections -= Mathf.Max(0, old.sections - 1);
            old.go.SetActive(false);
            pool.Push(old);
        }
        return ch;
    }

    Chunk NewChunk()
    {
        var go = new GameObject("Track") { hideFlags = HideFlags.DontSave };
        go.transform.SetParent(root, false);
        go.layer = 2;   // Ignore Raycast: no interfiere con la suspensión ni con el minimapa
        var mesh = new Mesh { name = "RoverTrack" };
        mesh.MarkDynamic();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = material;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return new Chunk { go = go, mesh = mesh };
    }

    void AppendSection(Chunk ch, Vector3 p, Vector3 n, Vector3 dir, float u, bool connect)
    {
        Vector3 right = Vector3.Cross(n, dir).normalized * (width * 0.5f);
        Vector3 c = p + n * lift;
        int i = ch.verts.Count;
        ch.verts.Add(c - right); ch.uvs.Add(new Vector2(u, 0f));
        ch.verts.Add(c + right); ch.uvs.Add(new Vector2(u, 1f));
        if (connect && i >= 2)
        {
            ch.tris.Add(i - 2); ch.tris.Add(i); ch.tris.Add(i - 1);
            ch.tris.Add(i - 1); ch.tris.Add(i); ch.tris.Add(i + 1);
        }
        ch.sections++;
    }

    void Upload(Chunk ch)
    {
        ch.mesh.SetVertices(ch.verts);
        ch.mesh.SetUVs(0, ch.uvs);
        ch.mesh.SetTriangles(ch.tris, 0, false);
        ch.mesh.RecalculateBounds();
    }

    /// <summary>Borra todas las rodadas.</summary>
    public void Clear()
    {
        foreach (var rb in ribbons)
        {
            while (rb.chunks.Count > 0)
            {
                var c = rb.chunks.Dequeue();
                c.go.SetActive(false);
                pool.Push(c);
            }
            rb.active = null; rb.hasLast = false; rb.totalSections = 0; rb.continuing = false;
        }
    }
}
