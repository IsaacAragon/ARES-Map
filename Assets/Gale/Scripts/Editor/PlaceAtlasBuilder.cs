using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

// Instala en la escena los lugares con nombre IAU (PlaceAtlas: buscador, nodos, sombreado) y su ventana
// (PlaceInfoPanel). Lo llama GaleSceneBuilder; también se puede ejecutar solo (Gale/Instalar lugares con nombre).
public static class PlaceAtlasBuilder
{
    const string DataPath = "Assets/Gale/Data/iau_nomenclatura.json";
    const string StylePath = "Assets/Gale/UI/ExplorerMenu.uss";
    const string MatPath = "Assets/Gale/Materials/Geo_Overlay.mat";

    [MenuItem("Gale/Instalar lugares con nombre")]
    public static void InstallAndSave()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[Lugares] sal de Play Mode antes de instalar"); return; }
        if (!Install()) return;
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[Lugares] instalados y escena guardada: {scene.path}");
    }

    /// <summary>Crea el objeto PlaceAtlas en la escena activa (no la guarda).</summary>
    public static PlaceAtlas Install()
    {
        var old = GameObject.Find("PlaceAtlas");
        if (old) Object.DestroyImmediate(old);
        var json = AssetDatabase.LoadAssetAtPath<TextAsset>(DataPath);
        if (!json) { Debug.LogError($"[Lugares] falta {DataPath}: python -I Tools/fetch_iau_names.py"); return null; }
        var cam = Camera.main;
        var go = new GameObject("PlaceAtlas");
        var atlas = go.AddComponent<PlaceAtlas>();
        atlas.dataset = json;
        atlas.highlightMaterial = HighlightMaterial();
        atlas.chase = cam ? cam.GetComponent<ChaseCamera>() : null;
        var router = cam ? cam.GetComponent<InputRouter>() : null;
        atlas.minimap = router && router.minimap ? router.minimap : cam ? cam.GetComponent<MinimapHUD>() : null;
        var doc = go.AddComponent<UIDocument>();
        doc.panelSettings = GaleSceneBuilder.GalePanelSettings();
        doc.sortingOrder = 61;   // sobre el menú de exploración (60), bajo el de pausa (100)
        var panel = go.AddComponent<PlaceInfoPanel>();
        panel.styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
        return atlas;
    }

    // El sombreado usa Gale/GeoOverlay (el mismo de las capas geológicas): se referencia su material para que
    // el shader entre en la build; PlaceAtlas trabaja con una copia.
    static Material HighlightMaterial() => AssetDatabase.LoadAssetAtPath<Material>(MatPath);
}
