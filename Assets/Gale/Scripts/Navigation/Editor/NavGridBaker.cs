using System.Diagnostics;
using System.IO;
using Gale.Navigation;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Hornea la malla de navegación (PLAN.md §5.2) leyendo la cota de los MeshCollider del terreno:
//  - L0: la rejilla de los mapas geológicos (geo_dataset.json → region), un rayo por píxel.
//  - L1: las celdas L0 que caben enteras dentro de GaleRuta_TIN (relieve de 8 m), subdivididas en DetailSub.
// Rayos por lotes con RaycastCommand. Escribe Assets/Gale/Data/Nav/nav_grid.bytes (GZip) con la huella de
// las mallas, y nav_profiles.json si no existe. Menú Gale/Bake de navegación; lo llama GaleSceneBuilder.
public static class NavGridBaker
{
    public const string Dir = "Assets/Gale/Data/Nav";
    public const string GridPath = Dir + "/nav_grid.bytes";
    public const string ProfilesPath = Dir + "/nav_profiles.json";
    const string GeoPath = "Assets/Gale/Data/geo_dataset.json";
    const string DetailBlock = "GaleRuta_TIN";
    public const int DetailSub = 20;   // 162,8 m / 20 = 8,14 m
    const int Batch = 1 << 20;

    [MenuItem("Gale/Bake de navegación")]
    public static void BakeMenu()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[Nav] sal de Play Mode antes de hornear"); return; }
        Bake();
    }

    public static bool Bake()
    {
        var sw = Stopwatch.StartNew();
        var json = AssetDatabase.LoadAssetAtPath<TextAsset>(GeoPath);
        if (!json) { Debug.LogError($"[Nav] falta {GeoPath}"); return false; }
        var region = GeoDataset.Parse(json.text).region;
        var root = NavUnity.TerrainRoot();
        var colliders = NavUnity.TerrainColliders(root);
        if (colliders.Count == 0) { Debug.LogError("[Nav] no hay colliders bajo 'Gale_Terrain'"); return false; }
        Physics.SyncTransforms();

        float s = MarsGeo.WorldScale;
        Vector3 sw0 = MarsGeo.LonLatToUnity(region.lonOeste, region.latSur), ne = MarsGeo.LonLatToUnity(region.lonEste, region.latNorte);
        float cw = (ne.x - sw0.x) / region.ancho, ch = (ne.z - sw0.z) / region.alto;   // u por celda
        var grid = new NavGrid
        {
            L0 = new NavLevel(region.ancho, region.alto, sw0.x / s, sw0.z / s, cw / s, ch / s),
            Fingerprint = NavUnity.Fingerprint(root),
        };

        // Extensión del terreno: fuera no se lanzan rayos.
        Bounds all = colliders[0].bounds;
        MeshCollider detail = null;
        foreach (var mc in colliders)
        {
            all.Encapsulate(mc.bounds);
            if (mc.name == DetailBlock) detail = mc;
        }
        float top = all.max.y + 50f, len = all.size.y + 100f;
        int mask = ~(1 << 2);

        long rays = CastLevel(grid.L0, s, all, top, len, mask);
        if (detail)
        {
            var b = detail.bounds;
            int x0 = Mathf.CeilToInt((b.min.x - sw0.x) / cw), x1 = Mathf.FloorToInt((b.max.x - sw0.x) / cw);
            int y0 = Mathf.CeilToInt((b.min.z - sw0.z) / ch), y1 = Mathf.FloorToInt((b.max.z - sw0.z) / ch);
            if (x1 > x0 && y1 > y0)
            {
                grid.CreateDetail(x0, y0, x1 - x0, y1 - y0, DetailSub);
                rays += CastLevel(grid.L1, s, b, top, len, mask);
                Debug.Log($"[Nav] detalle {DetailBlock}: celdas L0 x {x0}..{x1 - 1}, y {y0}..{y1 - 1} → L1 {grid.L1.W}×{grid.L1.H} de {grid.L1.CellX:0.00} m");
            }
        }
        else Debug.LogWarning($"[Nav] no hay {DetailBlock}: la malla no tendrá nivel de detalle");

        Directory.CreateDirectory(Dir);
        using (var f = File.Create(GridPath)) grid.Write(f);
        if (!File.Exists(ProfilesPath)) WriteProfiles();
        AssetDatabase.ImportAsset(GridPath);
        AssetDatabase.ImportAsset(ProfilesPath);

        int valid0 = 0;
        foreach (float h in grid.L0.Height) if (!float.IsNaN(h)) valid0++;
        Debug.Log($"[Nav] malla horneada en {sw.Elapsed.TotalSeconds:0.0} s: L0 {grid.L0.W}×{grid.L0.H} ({valid0} con relieve), " +
                  $"{rays} rayos, {new FileInfo(GridPath).Length / 1048576f:0.0} MB, huella {grid.Fingerprint}");
        return true;
    }

    // Un rayo vertical por centro de celda dentro de los límites; el resto queda sin relieve (NaN).
    static long CastLevel(NavLevel lv, float s, Bounds b, float top, float len, int mask)
    {
        for (int i = 0; i < lv.Count; i++) lv.Height[i] = float.NaN;
        int xa = Mathf.Max(0, Mathf.FloorToInt((b.min.x / s - lv.X0) / lv.CellX));
        int xb = Mathf.Min(lv.W - 1, Mathf.CeilToInt((b.max.x / s - lv.X0) / lv.CellX));
        int ya = Mathf.Max(0, Mathf.FloorToInt((b.min.z / s - lv.Z0) / lv.CellZ));
        int yb = Mathf.Min(lv.H - 1, Mathf.CeilToInt((b.max.z / s - lv.Z0) / lv.CellZ));
        if (xb < xa || yb < ya) return 0;
        int w = xb - xa + 1;
        long total = (long)w * (yb - ya + 1);
        var qp = new QueryParameters(mask, false, QueryTriggerInteraction.Ignore, false);
        var cmds = new NativeArray<RaycastCommand>(Batch, Allocator.TempJob);
        var hits = new NativeArray<RaycastHit>(Batch, Allocator.TempJob);
        try
        {
            for (long start = 0; start < total; start += Batch)
            {
                int n = (int)System.Math.Min(Batch, total - start);
                for (int k = 0; k < n; k++)
                {
                    long c = start + k;
                    int x = xa + (int)(c % w), y = ya + (int)(c / w);
                    cmds[k] = new RaycastCommand(new Vector3(lv.CenterX(x) * s, top, lv.CenterZ(y) * s), Vector3.down, qp, len);
                }
                RaycastCommand.ScheduleBatch(cmds.GetSubArray(0, n), hits.GetSubArray(0, n), 256).Complete();
                for (int k = 0; k < n; k++)
                {
                    if (hits[k].distance <= 0f) continue;   // sin impacto
                    long c = start + k;
                    int x = xa + (int)(c % w), y = ya + (int)(c / w);
                    lv.Height[y * lv.W + x] = hits[k].point.y / s;
                }
                EditorUtility.DisplayProgressBar("Bake de navegación", $"{start + n:N0} / {total:N0} rayos", (float)(start + n) / total);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            cmds.Dispose();
            hits.Dispose();
        }
        return total;
    }

    public static void WriteProfiles()
    {
        var file = new NavProfilesFile
        {
            aviso = "Valores iniciales del equipo, no datos medidos: calibrar con el investigador (PLAN.md §5.3). " +
                    "Suelo: 0 sin dato, 1 polvo/arena fina, 2 regolito, 3 consolidado, 4 roca. " +
                    "Geología: 0 sin dato, 1 mNh, 2 lNh, 3 HNt, 4 eHt, 5 Htu, 6 AHi.",
            vehiculos = new[] { NavProfile.Rover(), NavProfile.Eva() },
            pesos = NavWeights.Defaults(),
        };
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ProfilesPath, JsonUtility.ToJson(file, true) + "\n");
    }
}
