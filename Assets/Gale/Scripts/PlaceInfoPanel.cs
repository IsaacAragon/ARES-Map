using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

// Ventana emergente a la derecha con la ficha del lugar elegido (UI Toolkit, mismo estilo que el menú).
[RequireComponent(typeof(UIDocument))]
public class PlaceInfoPanel : MonoBehaviour
{
    public StyleSheet styleSheet;
    VisualElement root, card, body;
    Image photo;
    Label title, subtitle, photoNote;

    void OnEnable()
    {
        var doc = GetComponent<UIDocument>().rootVisualElement;
        doc.style.position = Position.Absolute;
        doc.style.left = doc.style.top = doc.style.right = doc.style.bottom = 0;
        doc.pickingMode = PickingMode.Ignore;
        if (styleSheet && !doc.styleSheets.Contains(styleSheet)) doc.styleSheets.Add(styleSheet);
        doc.Clear();
        root = new VisualElement { pickingMode = PickingMode.Ignore };
        root.AddToClassList("ex-root");
        doc.Add(root);

        card = new VisualElement();
        card.AddToClassList("ex-popup");
        var header = new VisualElement(); header.AddToClassList("ex-header");
        var col = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };
        title = new Label(); title.AddToClassList("ex-title");
        subtitle = new Label(); subtitle.AddToClassList("ex-note");
        col.Add(title); col.Add(subtitle);
        var close = new Button(() => PlaceAtlas.Instance?.ClearSelection()) { text = "×", tooltip = "Cerrar" };
        close.AddToClassList("ex-close");
        header.Add(col); header.Add(close);
        card.Add(header);
        var scroll = new ScrollView(ScrollViewMode.Vertical);
        scroll.AddToClassList("ex-scroll");
        card.Add(scroll);
        var inner = new VisualElement(); inner.AddToClassList("ex-section");
        scroll.Add(inner);
        photo = new Image { scaleMode = ScaleMode.ScaleAndCrop };
        photo.AddToClassList("ex-photo");
        inner.Add(photo);
        photoNote = new Label(); photoNote.AddToClassList("ex-note");
        inner.Add(photoNote);
        body = new VisualElement();
        inner.Add(body);
        root.Add(card);
        card.style.display = DisplayStyle.None;

        SimPause.Changed += OnPause;
        if (PlaceAtlas.Instance) PlaceAtlas.Instance.SelectionChanged += Show;
    }

    void Start()
    {
        if (PlaceAtlas.Instance) { PlaceAtlas.Instance.SelectionChanged -= Show; PlaceAtlas.Instance.SelectionChanged += Show; }
    }

    void OnDisable()
    {
        SimPause.Changed -= OnPause;
        if (PlaceAtlas.Instance) PlaceAtlas.Instance.SelectionChanged -= Show;
    }

    void OnPause(bool p) { if (root != null) root.style.display = p ? DisplayStyle.None : DisplayStyle.Flex; }

    IauFeature shown;

    void Show(IauFeature f)
    {
        if (f == null) { card.style.display = DisplayStyle.None; shown = null; return; }
        card.style.display = DisplayStyle.Flex;
        // Debajo del minimapa y su instrumento; arriba del todo si el minimapa está oculto.
        var map = PlaceAtlas.Instance.minimap;
        card.style.top = map && map.circleVisible ? 430 : 16;
        var atlas = PlaceAtlas.Instance;
        photo.image = atlas.Photo;
        photo.MarkDirtyRepaint();
        photoNote.text = atlas.PhotoNote ?? "";
        if (shown == f) return;   // solo se actualizó la foto
        shown = f;
        title.text = f.nombre;
        subtitle.text = $"{f.TipoEs} · nombre oficial IAU (n.º {f.id})";
        body.Clear();

        Heading("Ficha IAU");
        KV("Centro", $"{PlaceAtlas.FormatLat(f.lat)}, {PlaceAtlas.FormatLon(f.lon)}");
        if (f.diametroKm > 0) KV(f.IsCrater ? "Diámetro" : "Extensión", $"{f.diametroKm:0.##} km");
        KV("Recuadro", $"{f.latSur:0.##}° a {f.latNorte:0.##}° lat · {f.lonOeste:0.##}° a {f.lonEste:0.##}° E");
        KV("Origen del nombre", string.IsNullOrEmpty(f.origen) ? "—" : f.origen);
        KV("Aprobado", string.IsNullOrEmpty(f.aprobado) ? "—" : f.aprobado);
        KV("Cuadrángulo", f.cuadrangulo == "MC23" ? "MC-23 Aeolis" : f.cuadrangulo == "MC22" ? "MC-22 Tyrrhenum" : f.cuadrangulo);
        var chase = atlas.chase;
        if (chase && chase.target)
        {
            var ll = MarsGeo.UnityToLonLat(chase.target.transform.position);
            KV("Distancia desde el rover", $"{PlaceAtlas.GreatCircleKm(ll.lon, ll.lat, f.lon, f.lat):0.#} km al centro");
        }

        var s = atlas.Measure(f);
        Heading("Lo que miden los datos de la escena en el área");
        if (s.cells == 0) Note("El área cae fuera de los mapas de la escena.");
        else
        {
            if (!float.IsNaN(s.centerH)) KV("Cota en el centro", $"{s.centerH:0} m");
            if (s.maxH > s.minH) KV("Cota mínima y máxima", $"{s.minH:0} m / {s.maxH:0} m (desnivel {s.maxH - s.minH:0} m)");
            var gl = GeoLayers.Instance;
            Shares("Geología", gl, "geologia", s.geo);
            Shares("Material del suelo", gl, "suelo", s.soil);
            Note($"{s.cells} celdas de 163 m dentro del área. Geología USGS SIM 3292 (1:20 M), suelo THEMIS, relieve de la malla de navegación.");
        }

        var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 12 } };
        Btn(buttons, "Ver en vista aérea", () => atlas.Select(f, true));
        if (NavigationSystem.Instance)
            Btn(buttons, "Ruta hasta aquí", () => NavigationSystem.Instance.SetDestination(GroundRibbon.OnGround(f.Center)));
        body.Add(buttons);
        Note(PlaceAtlasSource(atlas));
    }

    static string PlaceAtlasSource(PlaceAtlas a) => a.Data != null ? $"Nomenclatura: {a.Data.fuente}; descargado {a.Data.descargado}." : "";

    void Shares(string name, GeoLayers gl, string id, float[] frac)
    {
        if (!gl) return;
        int li = gl.LayerIndex(id);
        if (li < 0) return;
        var def = gl.Data.capas[li];
        var sb = new StringBuilder();
        var order = new List<int>();
        for (int i = 1; i < frac.Length; i++) if (frac[i] >= 0.005f) order.Add(i);
        order.Sort((a, b) => frac[b].CompareTo(frac[a]));
        foreach (int i in order)
        {
            var c = def.Class(i);
            if (sb.Length > 0) sb.Append('\n');
            sb.Append($"{(c != null ? c.Label : "clase " + i)}  {frac[i] * 100f:0} %");
        }
        if (frac[0] > 0.005f) sb.Append($"\nsin dato  {frac[0] * 100f:0} %");
        KV(name, sb.Length > 0 ? sb.ToString() : "sin dato");
    }

    void Heading(string t) { var l = new Label(t.ToUpperInvariant()); l.AddToClassList("ex-heading"); body.Add(l); }
    void Note(string t) { var l = new Label(t); l.AddToClassList("ex-note"); body.Add(l); }

    void KV(string k, string v)
    {
        var row = new VisualElement(); row.AddToClassList("ex-kv");
        var kl = new Label(k); kl.AddToClassList("ex-k");
        var vl = new Label(v); vl.AddToClassList("ex-v");
        row.Add(kl); row.Add(vl); body.Add(row);
    }

    static void Btn(VisualElement parent, string text, Action a)
    {
        var b = new Button(a) { text = text };
        b.AddToClassList("ex-action");
        parent.Add(b);
    }
}
