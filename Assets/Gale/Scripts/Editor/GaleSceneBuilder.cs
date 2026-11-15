using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Construye la prueba de Gale en la escena activa (SampleScene):
//  - quita las pelotas y el plano Ground,
//  - coloca el TIN de Gale (FBX desde el DXF) con MeshCollider,
//  - crea el rover (modelo Curiosity de la NASA) con RoverController y pone la cámara a seguirlo,
//  - aplica textura real (Viking + THEMIS), cielo, niebla, sol y postprocesado marcianos.
// Se puede volver a ejecutar: borra y recrea Gale_Terrain y Rover.
public static class GaleSceneBuilder
{
    const string FbxPath = "Assets/Gale/Models/Gale_TIN.fbx";
    const string MatDir = "Assets/Gale/Materials";

    // Bloques del mosaico alrededor de Gale (Tools/mosaic_blocks.json). Todos comparten el origen XY
    // de Gale_TIN y bordes con los mismos vértices, así que se colocan sin desplazamiento.
    // nombre, FBX, material, textura (UV 0..1 = rectángulo del bloque).
    static readonly (string name, string fbx, string mat, string albedo)[] Blocks =
    {
        // Franja norte (borde de Gale -> Babati): DXF Civil 3D + MOLA a los lados.
        ("GaleNorte_TIN", "Assets/Gale/Models/GaleNorte_TIN.fbx", "GaleNorte_Terrain.mat", "Assets/Gale/Textures/GaleNorte_Albedo.jpg"),
        // Franja sur (borde sur de Gale -> fin del sector 6, Farah Vallis).
        ("GaleSur_TIN", "Assets/Gale/Models/GaleSur_TIN.fbx", "GaleSur_Terrain.mat", "Assets/Gale/Textures/GaleSur_Albedo.jpg"),
        // Franja este (sector 7 a ~400 m + MOLA al sur), de la franja sur a la franja norte.
        ("GaleEste_TIN", "Assets/Gale/Models/GaleEste_TIN.fbx", "GaleEste_Terrain.mat", "Assets/Gale/Textures/GaleEste_Albedo.jpg"),
        // Franja oeste con el cráter Robert Sharp (sector 9 a ~400 m + MOLA al sur).
        ("GaleOeste_TIN", "Assets/Gale/Models/GaleOeste_TIN.fbx", "GaleOeste_Terrain.mat", "Assets/Gale/Textures/GaleOeste_Albedo.jpg"),
        // Zona de la ruta de Curiosity, dentro de Gale (hueco en Gale_TIN): DEM MSL de 1 m (a 8 m, quadtree)
        // y textura de color HiRISE a 2 m/px.
        ("GaleRuta_TIN", "Assets/Gale/Models/GaleRuta_TIN.fbx", "GaleRuta_Terrain.mat", "Assets/Gale/Textures/GaleRuta_Albedo.jpg"),
    };

    // 1 m Unity = 50 m reales. Escala uniforme: las pendientes son las reales.
    const float WorldScale = 0.02f;
    // Centro XY eliminado en Blender (ver Gale_TIN_conversion.json).
    const double OffsetX = 8168043.885866, OffsetY = -319997.637211;
    const double MarsRadius = 3396190.0;
    // Bradbury Landing (aterrizaje de Curiosity), 137.4417° E, 4.5895° S.
    const double SpawnLon = 137.4417, SpawnLat = -4.5895;

    [MenuItem("Gale/Build Rover Test Scene")]
    public static void Build()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[Gale] Sal de Play Mode antes de reconstruir la escena.");
            return;
        }
        var scene = EditorSceneManager.GetActiveScene();

        foreach (var n in new[] { "BallLeft", "BallRight", "Ground", "Gale_Terrain", "Rover", "PauseMenu", "CuriosityRoute" })
        {
            var go = GameObject.Find(n);
            if (go) Object.DestroyImmediate(go);
        }

        ConfigureImporter(FbxPath);
        foreach (var blk in Blocks) ConfigureImporter(blk.fbx);

        // ---------- terreno
        var root = new GameObject("Gale_Terrain");
        // FBX: X Blender (este) -> -X Unity, Y Blender (norte) -> -Z Unity. 180° en Y deja este=+X, norte=+Z.
        root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 180f, 0f));
        // La escala 1:50 se aplica en el importador (globalScale), no en el transform:
        // así el MeshCollider recibe triángulos de ~9 m en vez de 463 m (PhysX avisa por encima de 500).
        root.transform.localScale = Vector3.one;

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        var terrain = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
        terrain.name = "Gale_TIN";
        var mf = terrain.GetComponentInChildren<MeshFilter>();
        var mr = terrain.GetComponentInChildren<MeshRenderer>();
        mr.sharedMaterial = TerrainMaterial("Gale_Terrain.mat", AlbedoPath);
        var mc = mf.gameObject.AddComponent<MeshCollider>();
        mc.sharedMesh = mf.sharedMesh;

        var mesh = mf.sharedMesh;
        var b = mr.bounds;
        Debug.Log($"[Gale] mesh '{mesh.name}' verts={mesh.vertexCount} tris={mesh.triangles.Length / 3} " +
                  $"index={mesh.indexFormat} localBounds={mesh.bounds} worldBounds center={b.center} size={b.size}");
        var verts = mesh.vertices;
        var l2w = mf.transform.localToWorldMatrix;
        Vector3 hw = l2w.MultiplyPoint3x4(verts[0]);
        for (int i = 1; i < verts.Length; i++) { var w = l2w.MultiplyPoint3x4(verts[i]); if (w.y > hw.y) hw = w; }
        Debug.Log($"[Gale] highest vertex world={hw} -> real X={OffsetX + hw.x / WorldScale:0.0} " +
                  $"Y={OffsetY + hw.z / WorldScale:0.0} Z={hw.y / WorldScale:0.0}");

        // Bloques vecinos del mosaico: mismo sistema de coordenadas, bordes compartidos con Gale.
        var blockFilters = new System.Collections.Generic.List<MeshFilter>();
        foreach (var blk in Blocks)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(blk.fbx), root.transform);
            go.name = blk.name;
            var bmf = go.GetComponentInChildren<MeshFilter>();
            var bmr = go.GetComponentInChildren<MeshRenderer>();
            bmr.sharedMaterial = TerrainMaterial(blk.mat, blk.albedo);
            var bmc = bmf.gameObject.AddComponent<MeshCollider>();
            bmc.sharedMesh = bmf.sharedMesh;
            blockFilters.Add(bmf);
            Debug.Log($"[Gale] block {blk.name} verts={bmf.sharedMesh.vertexCount} worldBounds={bmr.bounds}");
        }

        // ---------- rover
        var rover = BuildRover();
        double sx = SpawnLon * Mathf.Deg2Rad * MarsRadius - OffsetX;
        double sy = SpawnLat * Mathf.Deg2Rad * MarsRadius - OffsetY;
        var spawn = new Vector3((float)(sx * WorldScale), 0f, (float)(sy * WorldScale));
        Physics.SyncTransforms();
        if (Physics.Raycast(new Vector3(spawn.x, 5000f, spawn.z), Vector3.down, out var hit, 20000f, ~(1 << 2)))
            spawn.y = hit.point.y + 1.5f;
        else
            Debug.LogWarning("[Gale] spawn raycast missed the terrain");
        // Mira al sur, hacia Aeolis Mons (Mount Sharp).
        rover.transform.SetPositionAndRotation(spawn, Quaternion.Euler(0f, 180f, 0f));
        Debug.Log($"[Gale] rover spawn {spawn} (real elev {spawn.y / WorldScale:0} m)");

        // ---------- cámara
        var cam = Camera.main;
        if (cam)
        {
            cam.farClipPlane = 8000f;
            cam.nearClipPlane = 0.1f;
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data)
            {
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                data.antialiasingQuality = AntialiasingQuality.High;
            }
            var chase = cam.GetComponent<ChaseCamera>() ?? cam.gameObject.AddComponent<ChaseCamera>();
            chase.target = rover.GetComponent<RoverController>();
            chase.worldScale = WorldScale;
            chase.offsetX = OffsetX;
            chase.offsetY = OffsetY;
            chase.marsRadius = MarsRadius;
            // Límites de la vista aérea = extensión del mosaico (u Unity desde el centro de Gale).
            chase.aerialNorthLimit = 3000f;
            chase.aerialSouthLimit = 2150f;
            chase.aerialEastLimit = 5000f;
            chase.aerialWestLimit = 6800f;
            // Se recrean para que tomen los valores del código (p. ej. rótulos nuevos) y no los guardados
            // en la escena. Primero el instrumento, que depende del minimapa.
            var oldNav = cam.GetComponent<NavInstrumentHUD>();
            if (oldNav) Object.DestroyImmediate(oldNav);
            var oldMap = cam.GetComponent<MinimapHUD>();
            if (oldMap) Object.DestroyImmediate(oldMap);
            var minimap = cam.gameObject.AddComponent<MinimapHUD>();
            minimap.circleMaterial = CircleMaskMaterial();
            cam.gameObject.AddComponent<NavInstrumentHUD>();
            var router = cam.GetComponent<InputRouter>() ?? cam.gameObject.AddComponent<InputRouter>();
            router.chase = chase;
            router.minimap = minimap;
            BuildPauseMenu(chase, minimap);
            BuildCuriosityRoute();
            cam.transform.position = spawn + new Vector3(0f, 6f, 10f);
            cam.transform.LookAt(spawn);
        }
        ApplyAtmosphere(cam);

        // Relieve solo en el sombreado + luz rasante desde el NO a 30°. La malla real queda en el MeshCollider.
        var relief = mf.gameObject.AddComponent<ReliefShading>();
        relief.sourceMesh = mf.sharedMesh;
        relief.sun = RenderSettings.sun;
        relief.reliefIntensity = 2f;
        relief.sunAzimuth = 315f;
        relief.sunElevation = 30f;
        relief.Apply();
        // Bloques vecinos: mismo realce de relieve; la luz la controla el de Gale.
        foreach (var bmf in blockFilters)
        {
            var brelief = bmf.gameObject.AddComponent<ReliefShading>();
            brelief.sourceMesh = bmf.sharedMesh;
            brelief.controlLighting = false;
            brelief.reliefIntensity = relief.reliefIntensity;
            brelief.Apply();
        }
        DynamicGI.UpdateEnvironment();
        Debug.Log($"[Gale] relief: k={relief.reliefIntensity} sun az={relief.sunAzimuth} el={relief.sunElevation} " +
                  $"collider='{mc.sharedMesh.name}' render='{mf.sharedMesh.name}'");

        // Capas temáticas (geología, material del suelo) y su leyenda.
        GeoLayersBuilder.Install();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[Gale] scene built and saved: {scene.path}");
    }

    // Material de la máscara circular del minimapa (asset, para que el shader entre en la build).
    static Material CircleMaskMaterial()
    {
        const string path = MatDir + "/Minimap_CircleMask.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!mat)
        {
            mat = new Material(Shader.Find("Hidden/Gale/CircleMask"));
            AssetDatabase.CreateAsset(mat, path);
        }
        return mat;
    }

    // ---------- Ruta real de Curiosity (JPL MMGIS, en Assets/Gale/Route) ----------
    static void BuildCuriosityRoute()
    {
        var go = new GameObject("CuriosityRoute");
        var route = go.AddComponent<CuriosityRoute>();
        route.routeCsv = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Gale/Route/MSL_ruta.csv");
        route.waypointsCsv = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Gale/Route/MSL_waypoints.csv");
        const string matPath = MatDir + "/Curiosity_Route.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (!mat)
        {
            mat = new Material(Shader.Find("Gale/RouteLine"));
            AssetDatabase.CreateAsset(mat, matPath);
        }
        mat.SetColor("_Color", new Color(1f, 0.6f, 0.15f, 0.7f));
        mat.SetFloat("_Dash", 0.5f);   // rayas de ~1,3 u: se distingue de las rodadas del rover
        EditorUtility.SetDirty(mat);
        route.material = mat;
        if (!route.routeCsv || !route.waypointsCsv) Debug.LogWarning("[Gale] faltan los CSV de la ruta de Curiosity");
    }

    // ---------- Menú de pausa (UI Toolkit) ----------
    const string UiDir = "Assets/Gale/UI";
    const string PanelSettingsPath = UiDir + "/GalePanelSettings.asset";

    static void BuildPauseMenu(ChaseCamera chase, MinimapHUD minimap)
    {
        var go = new GameObject("PauseMenu");
        var doc = go.AddComponent<UnityEngine.UIElements.UIDocument>();
        doc.panelSettings = GalePanelSettings();
        doc.visualTreeAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(UiDir + "/PauseMenu.uxml");
        doc.sortingOrder = 100;
        var menu = go.AddComponent<PauseMenu>();
        menu.chase = chase;
        menu.minimap = minimap;
        menu.resetLon = SpawnLon;
        menu.resetLat = SpawnLat;
    }

    /// <summary>PanelSettings común de la UI del simulador: escala con la pantalla (referencia 1920×1080).</summary>
    public static UnityEngine.UIElements.PanelSettings GalePanelSettings()
    {
        var ps = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(PanelSettingsPath);
        if (!ps)
        {
            ps = ScriptableObject.CreateInstance<UnityEngine.UIElements.PanelSettings>();
            AssetDatabase.CreateAsset(ps, PanelSettingsPath);
        }
        ps.themeStyleSheet = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.ThemeStyleSheet>(UiDir + "/GaleTheme.tss");
        ps.scaleMode = UnityEngine.UIElements.PanelScaleMode.ScaleWithScreenSize;
        ps.referenceResolution = new Vector2Int(1920, 1080);
        ps.screenMatchMode = UnityEngine.UIElements.PanelScreenMatchMode.MatchWidthOrHeight;
        ps.match = 0.5f;
        EditorUtility.SetDirty(ps);
        AssetDatabase.SaveAssets();
        return ps;
    }

    static void ConfigureImporter(string path)
    {
        var imp = (ModelImporter)AssetImporter.GetAtPath(path);
        bool dirty = false;
        if (imp.materialImportMode != ModelImporterMaterialImportMode.None) { imp.materialImportMode = ModelImporterMaterialImportMode.None; dirty = true; }
        if (!imp.isReadable) { imp.isReadable = true; dirty = true; }
        if (imp.meshCompression != ModelImporterMeshCompression.Off) { imp.meshCompression = ModelImporterMeshCompression.Off; dirty = true; }
        if (imp.meshOptimizationFlags != 0) { imp.meshOptimizationFlags = 0; dirty = true; }  // conserva el orden de vértices
        if (imp.indexFormat != ModelImporterIndexFormat.UInt32) { imp.indexFormat = ModelImporterIndexFormat.UInt32; dirty = true; }
        if (!Mathf.Approximately(imp.globalScale, WorldScale)) { imp.globalScale = WorldScale; dirty = true; }
        if (imp.importNormals != ModelImporterNormals.Import) { imp.importNormals = ModelImporterNormals.Import; dirty = true; }
        if (dirty) imp.SaveAndReimport();
    }

    // ------------------------------------------------------------------ terreno

    const string AlbedoPath = "Assets/Gale/Textures/Gale_Albedo.jpg";
    const string DetailAlbedoPath = "Assets/Gale/Textures/Detail_Rocks015_Albedo.png";
    const string DetailNormalPath = "Assets/Gale/Textures/Detail_Rocks015_Normal.png";
    // Un mosaico de la textura de detalle cubre ~4 m de Unity (el rover mide 3 m).
    const float DetailTiling = 4000f / 4f;

    static void ConfigureTexture(string path, bool srgb, bool normal, TextureWrapMode wrap)
    {
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        ti.sRGBTexture = srgb;
        ti.mipmapEnabled = true;
        ti.anisoLevel = 8;
        ti.filterMode = FilterMode.Trilinear;
        ti.wrapMode = wrap;
        // Tamaño máximo = el de la imagen (potencia de 2 por encima, hasta 16384): con 2048 fijo,
        // las texturas de los bloques grandes y la de detalle de la ruta se reducían al importarlas.
        ti.GetSourceTextureWidthAndHeight(out int tw, out int th);
        ti.maxTextureSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(tw, th, 32)), 32, 16384);
        ti.textureCompression = TextureImporterCompression.CompressedHQ;
        ti.SaveAndReimport();
    }

    static Material TerrainMaterial(string matName, string albedoPath)
    {
        Directory.CreateDirectory(MatDir);
        ConfigureTexture(albedoPath, true, false, TextureWrapMode.Clamp);
        // El detalle se multiplica x2 en URP: gris lineal 0,5 = neutro, por eso sRGB desactivado.
        ConfigureTexture(DetailAlbedoPath, false, false, TextureWrapMode.Repeat);
        ConfigureTexture(DetailNormalPath, false, true, TextureWrapMode.Repeat);

        string matPath = MatDir + "/" + matName;
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (!mat)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, matPath);
        }
        // Color real de Gale: Viking MDIM 2.1 (color) x THEMIS (detalle), recortado al encuadre del DXF.
        mat.SetColor("_BaseColor", Color.white);
        mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath));
        mat.SetTextureScale("_BaseMap", Vector2.one);
        mat.SetTextureOffset("_BaseMap", Vector2.zero);
        // Detalle de cerca: guijarros y arena (ambientCG Rocks015, CC0).
        mat.SetTexture("_DetailAlbedoMap", AssetDatabase.LoadAssetAtPath<Texture2D>(DetailAlbedoPath));
        mat.SetTexture("_DetailNormalMap", AssetDatabase.LoadAssetAtPath<Texture2D>(DetailNormalPath));
        mat.SetTextureScale("_DetailAlbedoMap", new Vector2(DetailTiling, DetailTiling));
        mat.SetFloat("_DetailAlbedoMapScale", 0.5f);
        mat.SetFloat("_DetailNormalMapScale", 0.6f);
        mat.EnableKeyword("_DETAIL_MULX2");
        // Suelo mate: sin brillos especulares ni reflejos del cielo.
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Smoothness", 0.05f);
        mat.SetFloat("_SpecularHighlights", 0f);
        mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
        mat.SetFloat("_EnvironmentReflections", 0f);
        mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
        mat.SetFloat("_Cull", 0f);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }

    // ------------------------------------------------------------------ atmósfera

    const string SettingsDir = "Assets/Gale/Settings";
    const string VolumePath = SettingsDir + "/Gale_Volume.asset";

    static void ApplyAtmosphere(Camera cam)
    {
        // Sol: más débil y cálido que en la Tierra, a media altura para marcar el relieve.
        Light sun = null;
        foreach (var l in Object.FindObjectsByType<Light>())
            if (l.type == LightType.Directional) { sun = l; break; }
        // Dirección, intensidad y luz ambiental las controla ReliefShading (en Gale_TIN), editable en el Inspector.
        if (sun)
        {
            sun.color = new Color(1f, 0.91f, 0.80f);
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.85f;
            RenderSettings.sun = sun;
        }

        // Cielo color caramelo (polvo en suspensión).
        string skyPath = MatDir + "/Gale_Sky.mat";
        var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
        if (!sky) { sky = new Material(Shader.Find("Skybox/Procedural")); AssetDatabase.CreateAsset(sky, skyPath); }
        sky.SetFloat("_SunDisk", 2f);
        sky.SetFloat("_SunSize", 0.03f);
        sky.SetFloat("_SunSizeConvergence", 6f);
        sky.SetFloat("_AtmosphereThickness", 1.3f);
        sky.SetColor("_SkyTint", new Color(1f, 0.55f, 0.28f));
        sky.SetColor("_GroundColor", new Color(0.42f, 0.28f, 0.2f));
        sky.SetFloat("_Exposure", 1.05f);
        EditorUtility.SetDirty(sky);
        RenderSettings.skybox = sky;

        RenderSettings.reflectionIntensity = 0.4f;

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogColor = new Color(0.76f, 0.57f, 0.42f);
        RenderSettings.fogDensity = 0.0005f;

        // Postprocesado: perfil propio (no se toca SampleSceneProfile).
        Directory.CreateDirectory(SettingsDir);
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumePath);
        if (!profile) { profile = ScriptableObject.CreateInstance<VolumeProfile>(); AssetDatabase.CreateAsset(profile, VolumePath); }
        T Get<T>() where T : VolumeComponent
        {
            if (!profile.TryGet<T>(out var c))
            {
                c = profile.Add<T>(false);
                c.name = typeof(T).Name;
                AssetDatabase.AddObjectToAsset(c, profile);
            }
            return c;
        }
        Get<Tonemapping>().mode.Override(TonemappingMode.ACES);
        var ca = Get<ColorAdjustments>();
        ca.postExposure.Override(0.45f);
        ca.contrast.Override(12f);
        ca.saturation.Override(-6f);
        Get<WhiteBalance>().temperature.Override(6f);
        var bloom = Get<Bloom>();
        bloom.threshold.Override(1.0f);
        bloom.intensity.Override(0.25f);
        bloom.scatter.Override(0.6f);
        var vig = Get<Vignette>();
        vig.intensity.Override(0.22f);
        vig.smoothness.Override(0.45f);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();

        var volGo = GameObject.Find("Global Volume");
        var vol = volGo ? volGo.GetComponent<Volume>() : null;
        if (vol) { vol.isGlobal = true; vol.sharedProfile = profile; }
        else Debug.LogWarning("[Gale] no 'Global Volume' in the scene");

        // Comprobaciones previas (skill urp-postprocessing).
        var urp = UniversalRenderPipeline.asset;
        var data = cam ? cam.GetComponent<UniversalAdditionalCameraData>() : null;
        if (data) data.renderPostProcessing = true;
        Debug.Log($"[Gale] post: URP={(urp ? urp.name : "none")} HDR={(urp && urp.supportsHDR)} " +
                  $"camPost={(data && data.renderPostProcessing)} volumeInMask={(data && vol && (data.volumeLayerMask & (1 << vol.gameObject.layer)) != 0)} " +
                  $"shadowDistance={(urp ? urp.shadowDistance : 0f)}");
        DynamicGI.UpdateEnvironment();
    }

    // ------------------------------------------------------------------ rover

    // Modelo de Curiosity de la NASA (science.nasa.gov, NASA/JPL-Caltech), convertido con Tools/blender_curiosity_glb_to_fbx.py.
    const string RoverFbxPath = "Assets/Gale/Models/Curiosity/Curiosity.fbx";
    static readonly string[] WheelNames =
        { "Wheel_L_Front", "Wheel_L_Mid", "Wheel_L_Rear", "Wheel_R_Front", "Wheel_R_Mid", "Wheel_R_Rear" };

    static void SetLayer(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform c in go.transform) SetLayer(c.gameObject, layer);
    }

    // Materiales URP del rover, construidos desde las texturas de la NASA y asignados por nombre al FBX.
    // (albedo, normal, metálico, suavidad, recorte alfa). Valores tomados de los materiales del GLB.
    static void RoverMaterials()
    {
        const string texDir = "Assets/Gale/Models/Curiosity/Textures/";
        const string matDir = "Assets/Gale/Models/Curiosity/Materials/";
        Directory.CreateDirectory(matDir);
        var specs = new (string name, string albedo, string normal, float metal, float smooth, bool cutout, Color tint)[]
        {
            ("internals", null, null, 0f, 0.45f, false, new Color(0.8f, 0.75f, 0.61f)),
            ("parts_AO", "parts_AO.jpg", null, 0.6f, 0.3f, false, Color.white),
            ("tex_01", "tex_01.jpg", null, 0f, 0.45f, false, Color.white),
            ("tex_02", "tex_02.jpg", null, 0f, 0.45f, false, Color.white),
            ("tex_03", "tex_03.png", null, 0.6f, 0.3f, false, Color.white),
            ("tex_03a", "tex_03.png", null, 0f, 0f, true, Color.white),
            ("tex_04", "tex_04.jpg", null, 0.6f, 0.28f, false, Color.white),
            ("tex_05", "tex_05.jpg", null, 0f, 0.45f, false, Color.white),
            ("wheels", "tex_03.png", "tex_03_n.jpg", 0.7f, 0.45f, false, Color.white),
        };
        var imp = (ModelImporter)AssetImporter.GetAtPath(RoverFbxPath);
        foreach (var sp in specs)
        {
            if (sp.normal != null) ConfigureTexture(texDir + sp.normal, false, true, TextureWrapMode.Repeat);
            string path = matDir + "Curiosity_" + sp.name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", sp.tint);
            m.SetTexture("_BaseMap", sp.albedo != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(texDir + sp.albedo) : null);
            if (sp.normal != null)
            {
                m.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texDir + sp.normal));
                m.EnableKeyword("_NORMALMAP");
            }
            m.SetFloat("_Metallic", sp.metal);
            m.SetFloat("_Smoothness", sp.smooth);
            m.SetFloat("_AlphaClip", sp.cutout ? 1f : 0f);
            m.SetFloat("_Cutoff", 0.5f);
            if (sp.cutout) { m.EnableKeyword("_ALPHATEST_ON"); m.SetFloat("_Cull", 0f); }
            else m.DisableKeyword("_ALPHATEST_ON");
            EditorUtility.SetDirty(m);
            imp.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), sp.name), m);
        }
        AssetDatabase.SaveAssets();
        imp.SaveAndReimport();
    }

    static GameObject BuildRover()
    {
        RoverMaterials();
        var rover = new GameObject("Rover");
        var rb = rover.AddComponent<Rigidbody>();
        rb.mass = 899f;  // masa real de Curiosity
        rb.linearDamping = 0.05f;
        rb.angularDamping = 2f;

        var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(RoverFbxPath), rover.transform);
        model.name = "Curiosity";
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        SetLayer(rover, 2);  // Ignore Raycast: los rayos de suspensión no se ven a sí mismos

        var all = model.GetComponentsInChildren<Transform>(true);
        var wheels = new Transform[WheelNames.Length];
        for (int i = 0; i < WheelNames.Length; i++)
        {
            foreach (var t in all) if (t.name == WheelNames[i]) { wheels[i] = t; break; }
            if (!wheels[i]) throw new System.Exception($"Wheel '{WheelNames[i]}' not found in {RoverFbxPath}");
        }
        var wr = wheels[0].GetComponent<Renderer>().bounds;
        Debug.Log($"[Gale] rover wheels: L_Front local={rover.transform.InverseTransformPoint(wheels[0].position)} " +
                  $"R_Rear local={rover.transform.InverseTransformPoint(wheels[5].position)} radius={wr.extents.y:0.000}");

        // Colisionador del chasis (caja principal, sin mástil ni brazo).
        var col = rover.AddComponent<BoxCollider>();
        col.center = new Vector3(0f, 0.95f, 0.1f);
        col.size = new Vector3(1.6f, 0.6f, 2.6f);

        var ctrl = rover.AddComponent<RoverController>();
        ctrl.wheels = wheels;
        ctrl.wheelRadius = wr.extents.y;
        ctrl.moveWheelVisuals = false;   // el rocker-bogie del modelo es rígido: las ruedas sólo giran
        ctrl.autoRestLength = true;      // en reposo las ruedas tocan el suelo
        ctrl.centerOfMass = new Vector3(0f, 0.7f, 0f);

        // Rodadas persistentes y recorrido georreferenciado.
        var tracks = rover.AddComponent<RoverTracks>();
        tracks.rover = ctrl;
        tracks.material = TrackMaterial();
        rover.AddComponent<RoverTrackRecorder>().rover = ctrl;
        return rover;
    }

    // ---------- Rodadas ----------
    const string TreadTexPath = "Assets/Gale/Textures/Rover_Tread.png";

    static Material TrackMaterial()
    {
        if (!File.Exists(TreadTexPath)) WriteTreadTexture(TreadTexPath);
        var imp = (TextureImporter)AssetImporter.GetAtPath(TreadTexPath);
        if (imp.sRGBTexture || imp.wrapModeU != TextureWrapMode.Repeat || imp.wrapModeV != TextureWrapMode.Clamp)
        {
            imp.sRGBTexture = false;
            imp.wrapModeU = TextureWrapMode.Repeat;
            imp.wrapModeV = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
        }
        string matPath = MatDir + "/Rover_Tracks.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (!mat)
        {
            mat = new Material(Shader.Find("Gale/RoverTrack"));
            AssetDatabase.CreateAsset(mat, matPath);
        }
        mat.shader = Shader.Find("Gale/RoverTrack");
        mat.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(TreadTexPath));
        mat.SetFloat("_Strength", 0.8f);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }

    // Dibujo de la banda de rodadura de Curiosity (gris, 1 = suelo sin tocar): huella general algo
    // más oscura, grousers en chevrón (10 por vuelta de textura de 1,5 u, para que se lean a distancia
    // de cámara aunque el real tiene 24 por rueda) y los agujeros de "JPL" en Morse
    // (·--- ·--· ·-··) a lo largo del centro.
    static void WriteTreadTexture(string path)
    {
        const int W = 512, H = 128;
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        var px = new Color[W * H];
        // Morse JPL: punto = 1 unidad, raya = 3, separación entre símbolos 1, entre letras 3.
        var morse = new System.Collections.Generic.List<(int start, int len)>();
        int pos = 0;
        foreach (var letter in new[] { ".---", ".--.", ".-.." })
        {
            foreach (char ch in letter) { int l = ch == '.' ? 1 : 3; morse.Add((pos, l)); pos += l + 1; }
            pos += 2;
        }
        float morseLen = pos;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float u = (float)x / W, v = (float)y / H;
                float g = 0.74f;                                   // suelo compactado
                float vc = Mathf.Abs(v - 0.5f) * 2f;               // 0 centro, 1 borde
                // Grousers en chevrón: 24 por vuelta, desplazados según la distancia al centro.
                float phase = Mathf.Repeat(u * 10f - vc * 0.35f, 1f);
                if (phase < 0.38f) g -= 0.34f * Mathf.Sin(phase / 0.38f * Mathf.PI);
                // Agujeros Morse: franja central, repetida dos veces por vuelta.
                if (vc < 0.18f)
                {
                    float m = Mathf.Repeat(u * 2f, 1f) * (morseLen + 4f);
                    foreach (var (s, l) in morse)
                        if (m >= s && m < s + l) { g += 0.18f; break; }
                }
                g += (Mathf.PerlinNoise(x * 0.11f, y * 0.11f) - 0.5f) * 0.10f;
                px[y * W + x] = new Color(g, g, g);
            }
        tex.SetPixels(px);
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);
    }
}
