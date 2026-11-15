using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

// Instala el menú de exploración (barra lateral flotante) en la escena. Lo llama GaleSceneBuilder;
// también se puede ejecutar solo (menú Gale/Instalar menú de exploración): borra y recrea el objeto.
public static class ExplorerMenuBuilder
{
    const string StylePath = "Assets/Gale/UI/ExplorerMenu.uss";

    [MenuItem("Gale/Instalar menú de exploración")]
    public static void InstallAndSave()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[Menú] sal de Play Mode antes de instalar"); return; }
        if (!Install()) return;
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[Menú] menú de exploración instalado y escena guardada: {scene.path}");
    }

    /// <summary>Crea el objeto ExplorerMenu en la escena activa (no la guarda).</summary>
    public static ExplorerMenu Install()
    {
        var old = GameObject.Find("ExplorerMenu");
        if (old) Object.DestroyImmediate(old);
        var cam = Camera.main;
        var go = new GameObject("ExplorerMenu");
        var doc = go.AddComponent<UIDocument>();
        doc.panelSettings = GaleSceneBuilder.GalePanelSettings();
        doc.sortingOrder = 60;   // sobre la leyenda (50), bajo el menú de pausa (100)
        var menu = go.AddComponent<ExplorerMenu>();
        menu.styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
        if (!menu.styleSheet) Debug.LogError($"[Menú] falta {StylePath}");
        menu.chase = cam ? cam.GetComponent<ChaseCamera>() : null;
        var router = cam ? cam.GetComponent<InputRouter>() : null;
        menu.minimap = router && router.minimap ? router.minimap : cam ? cam.GetComponent<MinimapHUD>() : null;
        return menu;
    }
}
