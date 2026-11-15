using System.IO;
using UnityEditor;
using UnityEngine;

// Tools/Colocar Babati: pone el cráter Babati en su posición real respecto a Gale.
//
// Ejes: X = Este, Z = Norte, Y = altura. Origen = centro del DXF de Gale (= centro de Gale).
// Escala (unidades Unity por km):
//   - escalaManual > 0 → se usa tal cual;
//   - si no, la de la georreferencia de la escena (MarsGeo: 1:50 → 20 u/km).
//   Se registra también la que saldría de los bounds del terreno / 154 km, sólo como
//   comparación: el recorte MOLA mide 200 km de lado, no el diámetro de Gale, así que ese
//   cálculo sobrestima la escala.
// Posición: lon/lat IAU de Babati → Equirectangular Marte → Unity (misma fórmula que el HUD).
// Altura: raycast hacia abajo; si no toca suelo, la Y de la base de Gale. El anillo marcador se
// sube a la cota media del borde (si no, el reborde del cráter lo taparía).
// Objeto: si existe "Babati" se mueve y se escala (diámetro XZ = 22 km × escala, Y proporcional);
// si no, se crea un anillo rojo semitransparente con ese diámetro.
public static class BabatiPlacer
{
    // Datos IAU (planetocéntricos, longitud Este).
    public const double GaleLon = 137.80, GaleLat = -5.40, GaleDiameterKm = 154.0;
    public const double BabatiLon = 137.93, BabatiLat = -3.16, BabatiDiameterKm = 22.0;

    /// <summary>Si es &gt; 0, unidades Unity por km que se usan en vez de la georreferencia.</summary>
    public static float escalaManual = 0f;

    const string RingMeshPath = "Assets/Gale/Models/Babati_Ring.asset";
    const string RingMatPath = "Assets/Gale/Materials/Babati_Marker.mat";

    [MenuItem("Tools/Colocar Babati")]
    public static void Place() => PlaceAndReport();

    public static string PlaceAndReport()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[Babati] Sal de Play Mode antes de colocar Babati.");
            return "play mode";
        }

        // ---- 1. Escala
        var gale = GameObject.Find("Gale_Terrain") ?? GameObject.Find("Gale");
        Bounds? galeBounds = null;
        if (gale)
        {
            var tin = gale.transform.Find("Gale_TIN");
            var rends = (tin ? tin.gameObject : gale).GetComponentsInChildren<Renderer>();
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                galeBounds = b;
            }
        }
        float escala = escalaManual > 0f ? escalaManual : MarsGeo.UnitsPerKm;
        string porBounds = galeBounds.HasValue
            ? $"{Mathf.Max(galeBounds.Value.size.x, galeBounds.Value.size.z) / (float)GaleDiameterKm:0.000} u/km (bounds/154, sólo referencia: el recorte mide 200 km)"
            : "sin terreno de Gale";
        Debug.Log($"[Babati] escala = {escala:0.000} u/km ({(escalaManual > 0f ? "manual" : "georreferencia 1:50")}); por bounds = {porBounds}");

        // ---- 2-3. Posición
        Vector3 galeCenter = MarsGeo.LonLatToUnity(GaleLon, GaleLat);
        Vector3 pos;
        if (escalaManual > 0f)
        {
            // Con escala manual: desplazamiento en km desde el centro de Gale (proyección equirectangular).
            double kmE = (BabatiLon - GaleLon) * System.Math.PI / 180.0 * MarsGeo.Radius / 1000.0;
            double kmN = (BabatiLat - GaleLat) * System.Math.PI / 180.0 * MarsGeo.Radius / 1000.0;
            pos = galeCenter + new Vector3((float)(kmE * escala), 0f, (float)(kmN * escala));
        }
        else pos = MarsGeo.LonLatToUnity(BabatiLon, BabatiLat);

        Physics.SyncTransforms();
        float? ground = MarsGeo.GroundY(pos);
        pos.y = ground ?? (galeBounds.HasValue ? galeBounds.Value.min.y : 0f);

        Vector3 d = pos - galeCenter;
        float kmEast = d.x / escala, kmNorth = d.z / escala;
        float dist = Mathf.Sqrt(kmEast * kmEast + kmNorth * kmNorth);
        float bearing = Mathf.Atan2(kmEast, kmNorth) * Mathf.Rad2Deg;
        float rimGap = dist - (float)(GaleDiameterKm + BabatiDiameterKm) / 2f;

        // ---- 4. Objeto
        float diameter = (float)BabatiDiameterKm * escala;
        var babati = GameObject.Find("Babati");
        // El anillo marca el borde: a la cota del centro (el fondo del cráter) quedaría enterrado bajo
        // el reborde, así que se coloca a la altura media del terreno a lo largo del círculo.
        float sum = 0f; int hits = 0;
        for (int i = 0; i < 72; i++)
        {
            float a = i * Mathf.PI * 2f / 72f;
            float? y = MarsGeo.GroundY(pos + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * diameter * 0.5f);
            if (y.HasValue) { sum += y.Value; hits++; }
        }
        Vector3 ringPos = pos;
        if (hits > 0) ringPos.y = sum / hits + 1f;
        string accion;
        if (babati && !IsRingMarker(babati))
        {
            Undo.RecordObject(babati.transform, "Colocar Babati");
            babati.transform.position = pos;
            var rends = babati.GetComponentsInChildren<Renderer>();
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                float current = Mathf.Max(b.size.x, b.size.z);
                if (current > 1e-4f) babati.transform.localScale *= diameter / current;   // uniforme: Y en la misma proporción
            }
            accion = "movido y escalado el GameObject existente";
        }
        else
        {
            if (!babati)
            {
                babati = new GameObject("Babati");
                Undo.RegisterCreatedObjectUndo(babati, "Colocar Babati");
                babati.AddComponent<MeshFilter>().sharedMesh = RingMesh();
                var mr = babati.AddComponent<MeshRenderer>();
                mr.sharedMaterial = MarkerMaterial();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                babati.layer = 2;   // Ignore Raycast: no interfiere con la suspensión ni con el minimapa
                accion = "creado anillo marcador";
            }
            else
            {
                Undo.RecordObject(babati.transform, "Colocar Babati");
                accion = "actualizado anillo marcador";
            }
            babati.transform.position = ringPos;
            babati.transform.localScale = Vector3.one * diameter;
            accion += $" (a la cota media del borde, {MarsGeo.Elevation(ringPos.y):0} m)";
        }
        Selection.activeGameObject = babati;
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(babati.scene);

        var ll = MarsGeo.UnityToLonLat(pos);
        string rep = $"[Babati] {accion}: pos={pos} ({ll}) suelo={(ground.HasValue ? $"{MarsGeo.Elevation(ground.Value):0} m" : "sin impacto, base de Gale")} " +
                     $"diámetro={diameter:0.0} u | desde el centro de Gale: {kmEast:0.00} km E, {kmNorth:0.00} km N, " +
                     $"distancia {dist:0.0} km, rumbo N {bearing:0.0}° E, separación entre bordes {rimGap:0.0} km";
        Debug.Log(rep);
        return rep;
    }

    static bool IsRingMarker(GameObject go)
    {
        var mf = go.GetComponent<MeshFilter>();
        return mf && mf.sharedMesh && mf.sharedMesh.name == "Babati_Ring";
    }

    // Toro de diámetro mayor 1 (radio 0,5) y tubo fino; se escala con el transform.
    static Mesh RingMesh()
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(RingMeshPath);
        if (mesh) return mesh;
        const int seg = 128, tubeSeg = 12;
        const float R = 0.5f, r = 0.012f;
        var v = new Vector3[(seg + 1) * (tubeSeg + 1)];
        var n = new Vector3[v.Length];
        var tris = new int[seg * tubeSeg * 6];
        for (int i = 0, k = 0; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            var c = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            for (int j = 0; j <= tubeSeg; j++, k++)
            {
                float b = j * Mathf.PI * 2f / tubeSeg;
                var dir = c * Mathf.Cos(b) + Vector3.up * Mathf.Sin(b);
                v[k] = c * R + dir * r;
                n[k] = dir;
            }
        }
        for (int i = 0, t = 0; i < seg; i++)
            for (int j = 0; j < tubeSeg; j++)
            {
                int a0 = i * (tubeSeg + 1) + j, a1 = a0 + tubeSeg + 1;
                tris[t++] = a0; tris[t++] = a0 + 1; tris[t++] = a1;
                tris[t++] = a1; tris[t++] = a0 + 1; tris[t++] = a1 + 1;
            }
        mesh = new Mesh { name = "Babati_Ring", vertices = v, normals = n, triangles = tris };
        mesh.RecalculateBounds();
        AssetDatabase.CreateAsset(mesh, RingMeshPath);
        return mesh;
    }

    static Material MarkerMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(RingMatPath);
        if (mat) return mat;
        Directory.CreateDirectory(Path.GetDirectoryName(RingMatPath));
        mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        // Transparente (URP): Surface Type = Transparent, mezcla alfa.
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", 0f);
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 0f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        mat.SetColor("_BaseColor", new Color(1f, 0.1f, 0.05f, 0.55f));
        AssetDatabase.CreateAsset(mat, RingMatPath);
        return mat;
    }
}
