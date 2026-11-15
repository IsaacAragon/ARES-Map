using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

// Menú de pausa (UI Toolkit). Esc lo abre y lo cierra; si el minimapa interactivo está abierto,
// Esc primero cierra el minimapa (por eso este script corre antes que MinimapHUD).
//   Reanudar · Controles · Capas del mapa · Reiniciar en Bradbury · Salir (con confirmación)
// Capas del mapa: elige la capa temática de GeoLayers (geología, material del suelo) y muestra la
// ficha de la zona en la que está el rover (reparto de materiales y mineralogía medida).
[DefaultExecutionOrder(-50)]
[RequireComponent(typeof(UIDocument))]
public class PauseMenu : MonoBehaviour
{
    public ChaseCamera chase;
    public MinimapHUD minimap;
    [Tooltip("Punto de reinicio (Bradbury Landing).")]
    public double resetLon = 137.4417, resetLat = -4.5895;
    public float resetYaw = 180f;

    VisualElement root, mainPanel, controlsPanel, confirmPanel, layersPanel;
    readonly List<Button> layerButtons = new();   // [0] = imagen real, [i + 1] = capa i

    public bool Open => SimPause.IsPaused;

    void OnEnable()
    {
        root = GetComponent<UIDocument>().rootVisualElement;
        mainPanel = root.Q("main-panel");
        controlsPanel = root.Q("controls-panel");
        confirmPanel = root.Q("confirm-panel");
        layersPanel = root.Q("layers-panel");
        layerButtons.Clear();

        root.Q<Button>("resume").clicked += () => SetOpen(false);
        root.Q<Button>("controls").clicked += () => Show(controlsPanel);
        root.Q<Button>("layers").clicked += OpenLayers;
        root.Q<Button>("layers-back").clicked += () => Show(mainPanel);
        root.Q<Button>("reset").clicked += ResetRover;
        root.Q<Button>("quit").clicked += () => Show(confirmPanel);
        root.Q<Button>("controls-back").clicked += () => Show(mainPanel);
        root.Q<Button>("confirm-no").clicked += () => Show(mainPanel);
        root.Q<Button>("confirm-yes").clicked += Quit;

        SimPause.Changed += OnPauseChanged;
        OnPauseChanged(SimPause.IsPaused);
    }

    void OnDisable()
    {
        SimPause.Changed -= OnPauseChanged;
        if (SimPause.IsPaused) SimPause.Set(false);
    }

    void Update()
    {
        // Acción Global.Pause: Esc / Start. En el mapa interactivo, Esc lo cierra MinimapHUD (Map.Exit).
        if (GaleInput.Controls.Global.Pause.WasPressedThisFrame())
        {
            if (SimPause.IsPaused)
            {
                // Esc dentro de un submenú vuelve al principal; en el principal, reanuda.
                if (mainPanel.style.display == DisplayStyle.None) Show(mainPanel);
                else SetOpen(false);
            }
            else if (!(minimap && minimap.Interactive))
            {
                SetOpen(true);
            }
        }
    }

    public void SetOpen(bool open) => SimPause.Set(open);

    // Ayuda según el último dispositivo usado (GaleInput.UsingGamepad).
    const string KeyboardHelp =
        "<b>Rover</b>\nW/S avanzar y retroceder · A/D girar · Shift rápido · Espacio freno · R enderezar\n\n" +
        "<b>Cámara</b>\nRueda acercar/alejar · Tab vista aérea\n\n" +
        "<b>Vista aérea</b>\nWASD mover · Q/E girar · rueda altura · clic derecho + arrastrar inclinar\n" +
        "F centrar en el rover · C centrar en el cráter\n\n" +
        "<b>Mapa (M)</b>\nClic derecho + arrastrar mover · rueda zoom · WASD · Q/E girar · F rover · C cráter · M/Esc salir\n" +
        "− / + zoom del minimapa · H ayuda\n\n" +
        "<b>Ruta A→B (en el mapa)</b>\nClic destino · Mayús + clic parada · arrastrar un pin moverlo · Supr quitarlo\n" +
        "Intro iniciar el guiado · Retroceso cancelar · P perfil · V verla en vista aérea · Ctrl+Z / Ctrl+Y\n\n" +
        "<b>Capas del mapa</b>\nL cambia entre imagen real, geología, material del suelo y transitabilidad\n\n" +
        "<b>Esc</b> pausa";

    const string GamepadHelp =
        "<b>Rover</b>\nRT/LT avanzar y retroceder · stick izq. girar · A rápido · B freno · Y enderezar\n\n" +
        "<b>Cámara</b>\nCruceta ↑/↓ acercar/alejar · Select vista aérea\n\n" +
        "<b>Vista aérea</b>\nStick izq. mover · L3 rápido · LB/RB girar · stick dcho. Y altura\n" +
        "stick dcho. X + cruceta ↑/↓ mirar · X centrar en el rover · Y centrar en el cráter\n\n" +
        "<b>Mapa (R3)</b>\nStick izq. mover · gatillos zoom · LB/RB girar · stick dcho. cursor · A seleccionar\n" +
        "cruceta ↑/↓ zoom por pasos · X rover · Y cráter · B salir · cruceta → ayuda\n" +
        "Ruta A→B: A en el mapa elige el destino (el resto de la edición, de momento con teclado)\n\n" +
        "<b>Capas del mapa</b>\nCruceta ← cambia entre imagen real, geología, material del suelo y transitabilidad\n\n" +
        "<b>Start</b> pausa · en este menú: cruceta/stick para moverse, A para elegir";

    void OnPauseChanged(bool paused)
    {
        root.style.display = paused ? DisplayStyle.Flex : DisplayStyle.None;
        if (paused)
        {
            root.Q<Label>("controls-text").text = GaleInput.UsingGamepad ? GamepadHelp : KeyboardHelp;
            root.Q<Button>("layers").style.display = GeoLayers.Instance ? DisplayStyle.Flex : DisplayStyle.None;
            Show(mainPanel);
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            root.Q<Button>("resume").Focus();
        }
        else if (minimap)
        {
            minimap.ApplyCursor();
        }
    }

    void Show(VisualElement panel)
    {
        foreach (var p in new[] { mainPanel, controlsPanel, confirmPanel, layersPanel })
            p.style.display = p == panel ? DisplayStyle.Flex : DisplayStyle.None;
    }

    // ---------- Capas del mapa ----------

    void OpenLayers()
    {
        var geo = GeoLayers.Instance;
        if (!geo) return;
        if (layerButtons.Count != geo.LayerCount + 1)   // la navegación añade la capa Transitabilidad al cargar
        {
            var box = root.Q("layer-buttons");
            foreach (var old in layerButtons) old.RemoveFromHierarchy();
            layerButtons.Clear();
            for (int i = -1; i < geo.LayerCount; i++)
            {
                int layer = i;
                var b = new Button(() => { geo.Set(layer); MarkLayer(geo); })
                    { text = layer < 0 ? "Imagen real" : geo.Data.capas[layer].nombre };
                b.AddToClassList("menu-button");
                box.Add(b);
                layerButtons.Add(b);
            }
        }
        MarkLayer(geo);
        Vector3 at = chase && chase.target ? chase.target.transform.position : Vector3.zero;
        root.Q<Label>("zone-text").text = geo.ZoneReport(at);
        Show(layersPanel);
        layerButtons[geo.Current + 1].Focus();
    }

    void MarkLayer(GeoLayers geo)
    {
        for (int i = 0; i < layerButtons.Count; i++)
            layerButtons[i].EnableInClassList("selected", i == geo.Current + 1);
    }

    void ResetRover()
    {
        if (chase && chase.target)
        {
            chase.SetAerial(false);
            chase.target.ResetTo(MarsGeo.LonLatToUnity(resetLon, resetLat), resetYaw);
            // El salto no se une con una rodada ni con el tramo anterior del recorrido.
            if (chase.target.TryGetComponent<RoverTracks>(out var tracks)) tracks.BreakAll();
            if (chase.target.TryGetComponent<RoverTrackRecorder>(out var rec)) rec.BreakSegment();
            // La ruta planificada se recalcula desde la nueva posición.
            if (NavigationSystem.Instance) NavigationSystem.Instance.OnRoverReset();
        }
        SetOpen(false);
    }

    void Quit()
    {
        SimPause.Set(false);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
