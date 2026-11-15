using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

// Instala en la escena las capas geológicas (GeoLayers + leyenda) a partir de lo que importa
// Tools/import_geo_dataset.py. Lo llama GaleSceneBuilder al reconstruir la escena, y también se
// puede ejecutar solo: borra y recrea el objeto GeoLayers sin tocar el resto.
public static class GeoLayersBuilder
{
    const string TexDir = "Assets/Gale/Textures/Geo/";
    const string DataPath = "Assets/Gale/Data/geo_dataset.json";
    const string MatPath = "Assets/Gale/Materials/Geo_Overlay.mat";
    const string LegendPath = "Assets/Gale/UI/GeoLegend.uxml";

    [MenuItem("Gale/Instalar capas geológicas")]
    public static void InstallAndSave()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[Geo] Sal de Play Mode antes de instalar las capas.");
            return;
        }
        if (!Install()) return;
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[Geo] capas instaladas y escena guardada: {scene.path}");
    }

    /// <summary>Crea el objeto GeoLayers en la escena activa (no la guarda).</summary>
    public static GeoLayers Install()
    {
        var old = GameObject.Find("GeoLayers");
        if (old) Object.DestroyImmediate(old);

        var json = AssetDatabase.LoadAssetAtPath<TextAsset>(DataPath);
        if (!json)
        {
            Debug.LogError($"[Geo] falta {DataPath}: ejecuta  python -I Tools/import_geo_dataset.py <carpeta gale_ARES_v3_zonas>");
            return null;
        }
        var data = GeoDataset.Parse(json.text);

        var go = new GameObject("GeoLayers");
        var geo = go.AddComponent<GeoLayers>();
        geo.dataset = json;
        geo.textures = new GeoLayers.LayerTexture[data.capas.Length];
        for (int i = 0; i < data.capas.Length; i++)
            geo.textures[i] = new GeoLayers.LayerTexture
            {
                id = data.capas[i].id,
                index = DataTexture(TexDir + data.capas[i].textura, TextureImporterFormat.R8),
            };
        geo.thermophysical = DataTexture(TexDir + data.termofisico, TextureImporterFormat.R16);
        geo.overlayMaterial = OverlayMaterial();
        var terrain = GameObject.Find("Gale_Terrain");
        if (terrain) geo.terrainRoot = terrain.transform;
        else Debug.LogWarning("[Geo] no hay 'Gale_Terrain' en la escena: la capa no tendrá dónde pintarse");

        // Leyenda: mismo panel que el menú de pausa, por debajo de él.
        var doc = go.AddComponent<UIDocument>();
        doc.panelSettings = GaleSceneBuilder.GalePanelSettings();
        doc.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LegendPath);
        doc.sortingOrder = 50;
        var legend = go.AddComponent<GeoLegendHUD>();
        legend.layers = geo;
        var cam = Camera.main;
        legend.chase = cam ? cam.GetComponent<ChaseCamera>() : null;
        return geo;
    }

    // Texturas de datos: un canal, sin compresión ni mipmaps ni cambio de tamaño, legibles desde script
    // (el valor del píxel es la clase, no un color).
    static Texture2D DataTexture(string path, TextureImporterFormat format)
    {
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (!ti)
        {
            Debug.LogError($"[Geo] falta la textura {path}");
            return null;
        }
        var ps = ti.GetDefaultPlatformTextureSettings();
        var st = new TextureImporterSettings();
        ti.ReadTextureSettings(st);
        bool ok = ti.textureType == TextureImporterType.SingleChannel
                  && st.singleChannelComponent == TextureImporterSingleChannelComponent.Red
                  && !ti.sRGBTexture && ti.isReadable && !ti.mipmapEnabled
                  && ti.npotScale == TextureImporterNPOTScale.None && ti.filterMode == FilterMode.Point
                  && ti.wrapMode == TextureWrapMode.Clamp
                  && ps.format == format && ps.maxTextureSize == 4096;
        if (!ok)
        {
            ti.textureType = TextureImporterType.SingleChannel;
            ti.ReadTextureSettings(st);
            st.singleChannelComponent = TextureImporterSingleChannelComponent.Red;
            st.sRGBTexture = false;
            st.readable = true;
            st.mipmapEnabled = false;
            st.npotScale = TextureImporterNPOTScale.None;
            st.filterMode = FilterMode.Point;
            st.wrapMode = TextureWrapMode.Clamp;
            ti.SetTextureSettings(st);
            ps.format = format;
            ps.textureCompression = TextureImporterCompression.Uncompressed;
            ps.maxTextureSize = 4096;
            ti.SetPlatformTextureSettings(ps);
            ti.SaveAndReimport();
        }
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        Debug.Log($"[Geo] {tex.name}: {tex.width}×{tex.height} {tex.format} legible={tex.isReadable}");
        return tex;
    }

    // Material asset, para que el shader entre en la build.
    static Material OverlayMaterial()
    {
        var shader = Shader.Find("Gale/GeoOverlay");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        if (!mat)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, MatPath);
        }
        else if (mat.shader != shader)
        {
            mat.shader = shader;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
        }
        return mat;
    }
}
