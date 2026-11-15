using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

// Leyenda de la capa activa (UI Toolkit, arriba a la izquierda): qué color es cada unidad o
// material, su porcentaje en la zona en la que está el rover, y qué hay justo bajo el rover
// (clase resaltada en la lista, descripción, y el dato de las otras capas).
// Sin capa activa no se muestra.
[RequireComponent(typeof(UIDocument))]
public class GeoLegendHUD : MonoBehaviour
{
    public GeoLayers layers;
    public ChaseCamera chase;
    [Tooltip("Segundos entre lecturas del dato bajo el rover.")]
    public float refreshInterval = 0.25f;

    VisualElement legend, rows, here;
    Label title, source, zone, hereClass, hereDescription, hereOther, hint;
    readonly List<(GeoClass cls, VisualElement row, Label pct)> items = new();
    float nextRefresh;
    bool hintGamepad;

    void OnEnable()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;
        // Comparte panel con el menú de pausa: fuera del flujo para no repartirse la pantalla con él.
        root.style.position = Position.Absolute;
        root.style.left = root.style.top = root.style.right = root.style.bottom = 0;
        root.pickingMode = PickingMode.Ignore;

        legend = root.Q("legend");
        rows = root.Q("legend-rows");
        here = root.Q("legend-here");
        title = root.Q<Label>("legend-title");
        source = root.Q<Label>("legend-source");
        zone = root.Q<Label>("legend-zone");
        hereClass = root.Q<Label>("here-class");
        hereDescription = root.Q<Label>("here-description");
        hereOther = root.Q<Label>("here-other");
        hint = root.Q<Label>("legend-hint");

        if (!layers) layers = GeoLayers.Instance;
        if (layers) layers.Changed += Rebuild;
        Rebuild();
    }

    void OnDisable()
    {
        if (layers) layers.Changed -= Rebuild;
    }

    // Filas de la leyenda: una por clase de la capa activa.
    void Rebuild()
    {
        var layer = layers ? layers.CurrentLayer : null;
        legend.style.display = layer != null ? DisplayStyle.Flex : DisplayStyle.None;
        rows.Clear();
        items.Clear();
        if (layer == null) return;

        title.text = layer.titulo;
        source.text = layer.fuente;
        foreach (var c in layer.clases)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList("legend-row");
            var swatch = new VisualElement { pickingMode = PickingMode.Ignore };
            swatch.AddToClassList("legend-swatch");
            swatch.style.backgroundColor = (Color)c.Color32;   // el color es dato (geo_dataset.json), no estilo
            row.Add(swatch);
            if (!string.IsNullOrEmpty(c.codigo)) row.Add(Text(c.codigo, "legend-code"));
            row.Add(Text(c.nombre, "legend-name"));
            var pct = Text("", "legend-pct");
            row.Add(pct);
            rows.Add(row);
            items.Add((c, row, pct));
        }
        SetHint();
        nextRefresh = 0f;
        Refresh();
    }

    static Label Text(string text, string cls)
    {
        var l = new Label(text) { enableRichText = false, pickingMode = PickingMode.Ignore };
        l.AddToClassList(cls);
        return l;
    }

    void SetHint()
    {
        hintGamepad = GaleInput.UsingGamepad;
        hint.text = (hintGamepad ? "Cruceta ← cambia de capa" : "L cambia de capa") + " · " + layers.CurrentLayer.aviso;
    }

    void Update()
    {
        if (!layers || layers.Current < 0 || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + refreshInterval;
        if (hintGamepad != GaleInput.UsingGamepad) SetHint();
        Refresh();
    }

    void Refresh()
    {
        bool hasRover = chase && chase.target;
        here.style.display = hasRover ? DisplayStyle.Flex : DisplayStyle.None;
        if (!hasRover) return;
        Vector3 p = chase.target.transform.position;
        int layer = layers.Current;

        // Reparto de clases en la zona del rover.
        var z = layers.ZoneAt(p);
        float[] pct = layers.ZonePercent(layer, z);
        zone.text = z != null ? $"% del área en la zona: {z.nombre} ({z.anchoKm:0} × {z.altoKm:0} km)" : "Fuera de las zonas con datos";

        int cur = layers.ClassAt(layer, p);
        foreach (var (cls, row, label) in items)
        {
            row.EnableInClassList("current", cls.indice == cur);
            float v = pct != null && cls.indice - 1 < pct.Length ? pct[cls.indice - 1] : -1f;
            label.text = v < 0f ? "" : v == 0f ? "–" : v < 0.05f ? "<0,1 %" : $"{v:0.#} %";
        }

        // Clase de la capa activa bajo el rover.
        var c = layers.CurrentLayer.Class(cur);
        hereClass.text = c == null ? "Sin dato en este punto"
            : string.IsNullOrEmpty(c.codigo) ? c.nombre : $"{c.codigo} · {c.nombreLargo}";
        string desc = c == null ? "" : Join(" ", c.descripcion, c.nota);
        hereDescription.text = desc;
        hereDescription.style.display = desc.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;

        // El dato de las demás capas en el mismo punto, y el índice termofísico.
        var sb = new StringBuilder();
        for (int i = 0; i < layers.LayerCount; i++)
        {
            if (i == layer) continue;
            var def = layers.Data.capas[i];
            var oc = def.Class(layers.ClassAt(i, p));
            sb.Append(def.nombre).Append(": ").Append(oc != null ? oc.Label : "sin dato").Append('\n');
        }
        float? ti = layers.ThermoAt(p);
        if (ti.HasValue) sb.Append($"Índice termofísico: {ti.Value:0.00} (0 = fino/polvo · 1 = roca)");
        hereOther.text = sb.ToString().TrimEnd('\n');
    }

    static string Join(string sep, params string[] parts)
    {
        var sb = new StringBuilder();
        foreach (var s in parts)
        {
            if (string.IsNullOrEmpty(s)) continue;
            if (sb.Length > 0) sb.Append(sep);
            sb.Append(s);
        }
        return sb.ToString();
    }
}
