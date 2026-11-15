using System;
using UnityEngine;

// Datos geológicos importados del zip "gale_ARES_v3_zonas" (Tools/import_geo_dataset.py los
// escribe en Assets/Gale/Data/geo_dataset.json). Las capas son mapas de clases: la textura
// guarda el número de clase por píxel y aquí están su color, su nombre y su descripción, de modo
// que el mapa, la leyenda y el HUD leen el mismo dato.
[Serializable]
public class GeoDataset
{
    public string fuente;
    public GeoRegion region;
    public GeoLayerDef[] capas;
    public string termofisico;
    public GeoZone[] zonas;
    public GeoSite[] mineralogia;

    public static GeoDataset Parse(string json) => JsonUtility.FromJson<GeoDataset>(json);
}

/// <summary>Rectángulo lon/lat (grados, Este positivo) que cubren las texturas, de borde a borde.</summary>
[Serializable]
public class GeoRegion
{
    public double lonOeste, lonEste, latSur, latNorte;
    public int ancho, alto;
    public float metrosPorPixel;
}

[Serializable]
public class GeoLayerDef
{
    public string id;         // "geologia", "suelo"
    public string nombre;     // para el menú
    public string titulo;     // para la leyenda
    public string fuente;
    public string aviso;      // límite de uso del dato
    public string textura;    // nombre del PNG de índices
    public bool contactos;    // dibujar la línea de contacto entre clases
    public GeoClass[] clases;

    /// <summary>Clase con ese valor de píxel, o null (0 = sin dato).</summary>
    public GeoClass Class(int indice)
    {
        if (clases != null)
            foreach (var c in clases)
                if (c.indice == indice) return c;
        return null;
    }
}

[Serializable]
public class GeoClass
{
    public int indice;        // valor del píxel
    public string codigo;     // p. ej. "AHi" (vacío en el material del suelo)
    public string nombre;     // corto, para la leyenda
    public string nombreLargo;
    public string descripcion;
    public string nota;
    public string color;      // "#RRGGBB"
    public string grupo, nombreEn, caracteristicasEn, interpretacionEn;

    public Color32 Color32 => ColorUtility.TryParseHtmlString(color, out var c) ? (Color32)c : new Color32(255, 0, 255, 255);
    public string Label => string.IsNullOrEmpty(codigo) ? nombre : $"{codigo} · {nombre}";
}

/// <summary>Zona de trabajo del equipo (recuadro lon/lat) con el reparto de clases dentro de ella.</summary>
[Serializable]
public class GeoZone
{
    public string id, nombre;
    public double lonOeste, lonEste, latSur, latNorte;
    public float anchoKm, altoKm;
    public float[] sueloPct;      // por clase de suelo 1..4
    public float[] geologiaPct;   // por unidad 1..6
    public GeoIndex[] tes;

    public bool Contains(double lon, double lat) => lon >= lonOeste && lon <= lonEste && lat >= latSur && lat <= latNorte;
    public float AreaKm2 => anchoKm * altoKm;
}

[Serializable]
public class GeoIndex
{
    public string nombre;
    public float valor;   // índice relativo 0..1
}

/// <summary>Mineralogía medida en un sitio (Curiosity u órbita), con la nota para el astronauta.</summary>
[Serializable]
public class GeoSite
{
    public string[] zonas;
    public string sitio, tipo, mineralogia, notas, instrumento, fuente;

    public bool InZone(string zoneId) => zonas != null && Array.IndexOf(zonas, zoneId) >= 0;
}
