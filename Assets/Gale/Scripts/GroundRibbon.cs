using System.Collections.Generic;
using UnityEngine;

// Cinta apoyada en el terreno a lo largo de una polilínea (ruta de Curiosity, ruta planificada).
// Cada borde de la cinta se apoya en el suelo con su propio rayo (pendientes laterales). Para el shader
// Gale/RouteLine: normal = dirección lateral hacia fuera, U = distancia acumulada (u), V = 0..1 a lo ancho,
// color por vértice opcional.
public static class GroundRibbon
{
    public static Mesh Build(IReadOnlyList<Vector3> points, float width, float lift, IReadOnlyList<Color> colors = null, string name = "Ribbon")
    {
        int n = points.Count;
        if (n < 2) return null;
        var verts = new List<Vector3>(n * 2);
        var normals = new List<Vector3>(n * 2);
        var uvs = new List<Vector2>(n * 2);
        var cols = colors != null ? new List<Color>(n * 2) : null;
        var tris = new List<int>((n - 1) * 6);
        float u = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 prev = points[Mathf.Max(0, i - 1)], next = points[Mathf.Min(n - 1, i + 1)];
            Vector3 dir = next - prev; dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f) dir = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, dir.normalized) * (width * 0.5f);
            if (i > 0) u += Vector3.Distance(points[i - 1], points[i]);
            Vector3 l = OnGround(points[i] - right) + Vector3.up * lift;
            Vector3 r = OnGround(points[i] + right) + Vector3.up * lift;
            int k = verts.Count;
            Vector3 side = right.normalized;
            verts.Add(l); uvs.Add(new Vector2(u, 0f)); normals.Add(-side);
            verts.Add(r); uvs.Add(new Vector2(u, 1f)); normals.Add(side);
            if (cols != null) { var c = colors[Mathf.Min(i, colors.Count - 1)]; cols.Add(c); cols.Add(c); }
            if (i > 0) { tris.Add(k - 2); tris.Add(k); tris.Add(k - 1); tris.Add(k - 1); tris.Add(k); tris.Add(k + 1); }
        }
        var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts); mesh.SetNormals(normals); mesh.SetUVs(0, uvs);
        if (cols != null) mesh.SetColors(cols);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        mesh.bounds = new Bounds(mesh.bounds.center, mesh.bounds.size + Vector3.one * 50f);   // margen por el ensanche
        return mesh;
    }

    /// <summary>Longitud total de la polilínea (u), la misma escala que la U de la cinta.</summary>
    public static float Length(IReadOnlyList<Vector3> points)
    {
        float u = 0f;
        for (int i = 1; i < points.Count; i++) u += Vector3.Distance(points[i - 1], points[i]);
        return u;
    }

    public static Vector3 OnGround(Vector3 p)
    {
        float? y = MarsGeo.GroundY(p);
        p.y = y ?? p.y;
        return p;
    }

    /// <summary>Objeto con la cinta en la capa 2 (el rover no la pisa y la cámara del minimapa no la pinta).</summary>
    public static GameObject Spawn(string name, Transform parent, Mesh mesh, Material material, float halfWidth)
    {
        var go = new GameObject(name) { hideFlags = HideFlags.DontSave, layer = 2 };
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = material;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        var mpb = new MaterialPropertyBlock();
        mpb.SetFloat("_HalfWidth", halfWidth);
        mr.SetPropertyBlock(mpb);
        return go;
    }
}
