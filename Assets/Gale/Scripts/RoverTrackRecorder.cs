using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// Recorrido del rover como polilínea georreferenciada: un punto cada `everyMeters` metros reales
// (X, Y, Z reales en Equirectangular Marte, lon, lat y tiempo de simulación). Sirve para la ruta
// del astronauta y el navegador A→B; el minimapa lo dibuja como una línea.
// Se exporta a CSV y GeoJSON (MultiLineString: un tramo por cada salto o reinicio) al salir,
// o con Export(). Carpeta: Application.persistentDataPath/Recorridos.
public class RoverTrackRecorder : MonoBehaviour
{
    public RoverController rover;
    [Tooltip("Distancia real entre puntos guardados (m).")]
    public float everyMeters = 5f;
    [Tooltip("Exportar automáticamente al salir de la simulación.")]
    public bool exportOnQuit = true;

    public struct Sample
    {
        public Vector3 unity;
        public double x, y, z, lon, lat;
        public float time;
        public int segment;
    }

    readonly List<Sample> samples = new List<Sample>();
    int segment;
    bool breakNext = true;

    public IReadOnlyList<Sample> Samples => samples;
    public string LastExportPath { get; private set; }

    /// <summary>El siguiente punto empieza un tramo nuevo (teletransporte, reinicio).</summary>
    public void BreakSegment() => breakNext = true;

    void LateUpdate()
    {
        if (!rover || SimPause.IsPaused || !rover.Grounded) return;
        Vector3 p = rover.transform.position;
        if (samples.Count > 0 && !breakNext)
        {
            float realDist = (p - samples[samples.Count - 1].unity).magnitude / MarsGeo.WorldScale;
            if (realDist < everyMeters) return;
            // Un salto de más de 1 km real en un frame es un teletransporte: tramo nuevo.
            if (realDist > 1000f) breakNext = true;
        }
        if (breakNext && samples.Count > 0) segment++;
        breakNext = false;
        var ll = MarsGeo.UnityToLonLat(p);
        samples.Add(new Sample
        {
            unity = p,
            x = MarsGeo.OffsetX + p.x / MarsGeo.WorldScale,
            y = MarsGeo.OffsetY + p.z / MarsGeo.WorldScale,
            z = MarsGeo.Elevation(p.y),
            lon = ll.lon, lat = ll.lat,
            time = Time.time,
            segment = segment,
        });
    }

    void OnApplicationQuit()
    {
        if (exportOnQuit && samples.Count > 1) Export();
    }

    /// <summary>Escribe el recorrido en CSV y GeoJSON. Devuelve la ruta base (sin extensión).</summary>
    public string Export(string directory = null)
    {
        directory ??= Path.Combine(Application.persistentDataPath, "Recorridos");
        Directory.CreateDirectory(directory);
        string name = "recorrido_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string basePath = Path.Combine(directory, name);
        var ci = CultureInfo.InvariantCulture;

        var csv = new StringBuilder("tramo,tiempo_s,lon_E,lat,x_m,y_m,elev_m\n");
        foreach (var s in samples)
            csv.Append(string.Format(ci, "{0},{1:0.00},{2:0.000000},{3:0.000000},{4:0.0},{5:0.0},{6:0.0}\n",
                s.segment, s.time, s.lon, s.lat, s.x, s.y, s.z));
        File.WriteAllText(basePath + ".csv", csv.ToString());

        // GeoJSON en lon/lat (grados) de Marte; la cota va como tercera coordenada.
        var gj = new StringBuilder();
        gj.Append("{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",");
        gj.Append("\"properties\":{\"body\":\"Mars\",\"crs\":\"IAU_2015:49900 (lon/lat planetocéntricas, Este+)\",\"source\":\"ARES Map\"},");
        gj.Append("\"geometry\":{\"type\":\"MultiLineString\",\"coordinates\":[");
        int cur = -1;
        bool firstPt = true;
        foreach (var s in samples)
        {
            if (s.segment != cur)
            {
                if (cur != -1) gj.Append("]");
                if (cur != -1) gj.Append(",");
                gj.Append("[");
                cur = s.segment;
                firstPt = true;
            }
            if (!firstPt) gj.Append(",");
            gj.Append(string.Format(ci, "[{0:0.000000},{1:0.000000},{2:0.0}]", s.lon, s.lat, s.z));
            firstPt = false;
        }
        if (cur != -1) gj.Append("]");
        gj.Append("]}}]}");
        File.WriteAllText(basePath + ".geojson", gj.ToString());

        LastExportPath = basePath;
        Debug.Log($"[Recorrido] {samples.Count} puntos exportados a {basePath}.csv / .geojson");
        return basePath;
    }

    /// <summary>Longitud real recorrida (m), sumando los tramos.</summary>
    public double LengthMeters()
    {
        double d = 0;
        for (int i = 1; i < samples.Count; i++)
            if (samples[i].segment == samples[i - 1].segment)
                d += System.Math.Sqrt(System.Math.Pow(samples[i].x - samples[i - 1].x, 2) +
                                      System.Math.Pow(samples[i].y - samples[i - 1].y, 2) +
                                      System.Math.Pow(samples[i].z - samples[i - 1].z, 2));
        return d;
    }
}
