using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Instala la navegación A→B en la escena: objeto "Navigation" con NavigationSystem, ClimateService,
// NavMapOverlay y NavRouteRibbon. Lo llama GaleSceneBuilder (después del bake); también se puede
// ejecutar solo (menú Gale/Instalar navegación), que borra y recrea el objeto sin tocar el resto.
public static class NavigationBuilder
{
    const string ClimatePath = "Assets/Gale/Data/climate_dataset.json";
    const string MatPath = "Assets/Gale/Materials/Nav_Route.mat";

    [MenuItem("Gale/Instalar navegación")]
    public static void InstallAndSave()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[Nav] sal de Play Mode antes de instalar"); return; }
        if (!AssetDatabase.LoadAssetAtPath<TextAsset>(NavGridBaker.GridPath) && !NavGridBaker.Bake()) return;
        if (!Install()) return;
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[Nav] navegación instalada y escena guardada: {scene.path}");
    }

    /// <summary>Crea el objeto Navigation en la escena activa (no la guarda).</summary>
    public static NavigationSystem Install()
    {
        var old = GameObject.Find("Navigation");
        if (old) Object.DestroyImmediate(old);

        var cam = Camera.main;
        // El que usan InputRouter y el menú de pausa (el último que añadió GaleSceneBuilder).
        var router = cam ? cam.GetComponent<InputRouter>() : null;
        var minimap = router && router.minimap ? router.minimap : cam ? cam.GetComponent<MinimapHUD>() : null;
        if (!minimap) { Debug.LogError("[Nav] no hay MinimapHUD en la cámara principal"); return null; }
        // Con dos MinimapHUD (escenas hechas antes de que GaleSceneBuilder los borrara todos), M abre dos
        // mapas superpuestos y el segundo tapa la ruta: se deja solo ese.
        foreach (var extra in cam.GetComponents<MinimapHUD>())
            if (extra != minimap) { Debug.LogWarning("[Nav] MinimapHUD duplicado en la cámara: se quita"); Object.DestroyImmediate(extra); }

        var go = new GameObject("Navigation");
        var climate = go.AddComponent<ClimateService>();
        climate.dataset = AssetDatabase.LoadAssetAtPath<TextAsset>(ClimatePath);
        if (!climate.dataset) Debug.LogWarning($"[Nav] falta {ClimatePath}: la ruta irá sin clima ni radiación");

        var nav = go.AddComponent<NavigationSystem>();
        nav.gridAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(NavGridBaker.GridPath);
        if (!AssetDatabase.LoadAssetAtPath<TextAsset>(NavGridBaker.ProfilesPath)) NavGridBaker.WriteProfiles();
        AssetDatabase.ImportAsset(NavGridBaker.ProfilesPath);
        nav.profilesAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(NavGridBaker.ProfilesPath);
        nav.minimap = minimap;
        nav.chase = cam.GetComponent<ChaseCamera>();
        if (!nav.gridAsset) Debug.LogWarning($"[Nav] falta {NavGridBaker.GridPath}: menú Gale/Bake de navegación");

        go.AddComponent<NavMapOverlay>();
        var ribbon = go.AddComponent<NavRouteRibbon>();
        ribbon.material = RouteMaterial();
        return nav;
    }

    static Material RouteMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        if (!mat)
        {
            mat = new Material(Shader.Find("Gale/RouteLine"));
            AssetDatabase.CreateAsset(mat, MatPath);
        }
        mat.SetColor("_Color", Color.white);
        mat.SetFloat("_UseVertexColor", 1f);
        mat.SetFloat("_Dash", 0.35f);
        mat.SetFloat("_DashSpeed", 0.8f);
        EditorUtility.SetDirty(mat);
        return mat;
    }
}
