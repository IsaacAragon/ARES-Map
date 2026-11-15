using System.Collections.Generic;
using System.Text;
using Gale.Navigation;
using UnityEngine;

// Puente entre el núcleo (metros reales, X este, Z norte) y la escena (unidades Unity, 1 u = 50 m).
public static class NavUnity
{
    public static float S => MarsGeo.WorldScale;

    public static (float x, float z) ToMeters(Vector3 u) => (u.x / S, u.z / S);

    public static Vector3 ToWorld(in NavPoint p) => new Vector3(p.x * S, p.h * S, p.z * S);

    public static Vector3 ToWorld(float x, float z, float h) => new Vector3(x * S, h * S, z * S);

    /// <summary>Mallas de terreno con collider bajo la raíz (Gale_Terrain), ordenadas por nombre.</summary>
    public static List<MeshCollider> TerrainColliders(Transform root)
    {
        var list = new List<MeshCollider>();
        if (root) list.AddRange(root.GetComponentsInChildren<MeshCollider>(true));
        list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return list;
    }

    /// <summary>Huella del relieve: nombre, vértices y límites de cada collider. Si cambia, la malla de
    /// navegación está caducada y hay que volver a hornearla.</summary>
    public static string Fingerprint(Transform root)
    {
        var sb = new StringBuilder();
        foreach (var mc in TerrainColliders(root))
        {
            if (!mc.sharedMesh) continue;
            var b = mc.bounds;
            sb.Append(mc.name).Append(':').Append(mc.sharedMesh.vertexCount).Append(':')
              .Append(Mathf.RoundToInt(b.min.x)).Append(',').Append(Mathf.RoundToInt(b.min.z)).Append(',')
              .Append(Mathf.RoundToInt(b.max.x)).Append(',').Append(Mathf.RoundToInt(b.max.z)).Append(';');
        }
        // FNV-1a 64 bits, para que la cabecera no crezca con el número de bloques.
        ulong h = 1469598103934665603UL;
        foreach (char c in sb.ToString()) { h ^= c; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    public static Transform TerrainRoot()
    {
        var go = GameObject.Find("Gale_Terrain");
        return go ? go.transform : null;
    }
}
