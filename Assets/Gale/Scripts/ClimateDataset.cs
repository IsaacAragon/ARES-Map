using System;
using UnityEngine;

// Clima y radiación de Gale (Tools/build_climate_dataset.py los escribe en
// Assets/Gale/Data/climate_dataset.json; se regenera antes de cada demo, nunca se consulta en vivo).
//   mcd: Mars Climate Database v6.2, un MODELO: clima típico de ese sol, hora y lugar. Trae el sol de hoy,
//        el de mañana y hoy con más polvo, cada uno con sus 24 horas locales (hora solar verdadera).
//   rad: dosis medidas por RAD en Curiosity (NASA CCMC), las últimas 48 h; llegan con ~1 día de retraso.
// MarsTime convierte la hora del reloj (UTC) en sol de Curiosity y hora local, con las mismas fórmulas
// que el script (Mars24, Allison y McEwen 2000).
[Serializable]
public class ClimateDataset
{
    public string generado;
    public int solActual;
    public ClimateSources fuentes;
    public ClimateScenarios escenarios;
    public McdData mcd;
    public RadData rad;

    public static ClimateDataset Parse(string json) => JsonUtility.FromJson<ClimateDataset>(json);

    public DateTime Generado => MarsTime.ParseUtc(generado);

    /// <summary>Punto del MCD más cercano en planta a esa lon/lat (hoy: Curiosity y Bradbury).</summary>
    public McdPoint NearestPoint(double lon, double lat)
    {
        McdPoint best = null;
        double bd = double.MaxValue;
        if (mcd?.puntos != null)
            foreach (var p in mcd.puntos)
            {
                double dl = (p.lon - lon) * Math.Cos(lat * Math.PI / 180.0), dp = p.lat - lat;
                double d = dl * dl + dp * dp;
                if (d < bd) { bd = d; best = p; }
            }
        return best;
    }
}

[Serializable] public class ClimateSources { public string mcd, mcdAviso, rad, radAviso; }
[Serializable] public class ClimateScenarios { public string normal, polvo, tormenta; }
[Serializable] public class McdData { public McdPoint[] puntos; }

[Serializable]
public class McdPoint
{
    public string nombre;
    public double lat, lon;
    public McdDay hoy, manana, hoyPolvo;

    /// <summary>El día del dataset que contiene ese sol de Curiosity (hoy o mañana), o null.</summary>
    public McdDay DayForSol(int sol) => hoy != null && hoy.sol == sol ? hoy : manana != null && manana.sol == sol ? manana : null;

    /// <summary>Clima en ese instante UTC (null si el dataset ya no cubre ese sol: hay que regenerarlo).</summary>
    public McdHour At(DateTime utc, bool polvo = false)
    {
        int sol = MarsTime.CuriositySol(utc, lon);
        var day = polvo && hoy != null && sol == hoy.sol ? hoyPolvo : DayForSol(sol);
        return day?.Sample(MarsTime.LocalTrueSolarTime(utc, lon, day.ecuacionTiempoH));
    }
}

[Serializable]
public class McdDay
{
    public int sol;
    public string inicioUtc, finUtc;
    public float ls, distanciaSolUA, ecuacionTiempoH;
    public int escenarioPolvo;
    public string escenarioNombre;
    public McdSummary resumen;
    public McdHour[] horas;   // 24 horas locales (0..23, hora solar verdadera)

    /// <summary>Interpolación lineal entre horas; de las 23 h vuelve a las 0 h del mismo sol.</summary>
    public McdHour Sample(float horaLocal)
    {
        if (horas == null || horas.Length == 0) return null;
        int n = horas.Length;
        float h = Mathf.Repeat(horaLocal, 24f);
        int i = Mathf.Clamp(Mathf.FloorToInt(h), 0, n - 1);
        var a = horas[i];
        var b = horas[(i + 1) % n];
        float f = h - i;
        return new McdHour
        {
            horaLocal = h,
            utc = a.utc,
            tSuelo = Mathf.Lerp(a.tSuelo, b.tSuelo, f),
            tAire = Mathf.Lerp(a.tAire, b.tAire, f),
            presion = Mathf.Lerp(a.presion, b.presion, f),
            flujoSolar = Mathf.Lerp(a.flujoSolar, b.flujoSolar, f),
            viento = Mathf.Lerp(a.viento, b.viento, f),
            vientoDesde = Mathf.LerpAngle(a.vientoDesde, b.vientoDesde, f) % 360f,
            polvo = Mathf.Lerp(a.polvo, b.polvo, f),
        };
    }
}

[Serializable]
public class McdSummary
{
    public float tAireMin, tAireMax, tSueloMin, tSueloMax, presionMedia, flujoSolarMax, vientoMax, polvo;
}

[Serializable]
public class McdHour
{
    public float horaLocal;
    public string utc;
    public float tSuelo, tAire;   // K
    public float presion;         // Pa
    public float flujoSolar;      // W/m², sol que llega al suelo horizontal
    public float viento;          // m/s a 2 m del suelo
    public float vientoDesde;     // grados desde el norte, de dónde sopla
    public float polvo;           // profundidad óptica visible

    public float TAireC => tAire - 273.15f;
    public float TSueloC => tSuelo - 273.15f;
}

[Serializable]
public class RadData
{
    public string ultimaLectura;
    public float ultimaDosisE, ultimaDosisB, retrasoHoras, tendencia;
    public RadStats ultimas24h, previas24h;
    public RadReading[] lecturas;

    /// <summary>Horas entre la última lectura y ese instante (para mostrar "hace 21 h").</summary>
    public double AgeHours(DateTime utcNow) => (utcNow - MarsTime.ParseUtc(ultimaLectura)).TotalHours;
}

[Serializable] public class RadStats { public float media, min, max; public int n; }
[Serializable] public class RadReading { public string utc; public float dosisE, dosisB, dosisBBruta; } // µGy/día

/// <summary>Tiempo marciano a partir de UTC (Mars24). Hora en horas marcianas (1/24 de sol).</summary>
public static class MarsTime
{
    const double TtMinusUtc = 69.184;   // s
    const int CuriositySol0 = 49269;    // floor(MSD + lon/360) en el aterrizaje
    public const double CuriosityLon = 137.4417;

    public static double JulianDate(DateTime utc) =>
        (utc.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds / 86400.0 + 2440587.5;

    public static double MarsSolDate(DateTime utc) =>
        (JulianDate(utc) + TtMinusUtc / 86400.0 - 2451549.5) / 1.0274912517 + 44796.0 - 0.0009626;

    public static int CuriositySol(DateTime utc, double lonEast = CuriosityLon) =>
        (int)Math.Floor(MarsSolDate(utc) + lonEast / 360.0) - CuriositySol0;

    /// <summary>Hora solar media local (LMST).</summary>
    public static float LocalMeanSolarTime(DateTime utc, double lonEast)
    {
        double x = MarsSolDate(utc) + lonEast / 360.0;
        return (float)((x - Math.Floor(x)) * 24.0);
    }

    /// <summary>Hora solar verdadera (la del MCD): media + ecuación del tiempo del día.</summary>
    public static float LocalTrueSolarTime(DateTime utc, double lonEast, float ecuacionTiempoH) =>
        Mathf.Repeat(LocalMeanSolarTime(utc, lonEast) + ecuacionTiempoH, 24f);

    public static DateTime ParseUtc(string iso) =>
        DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
}
