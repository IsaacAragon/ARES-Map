using System;
using Gale.Navigation;
using UnityEngine;

// Carga climate_dataset.json (Tools/build_climate_dataset.py) y da el clima y la tasa de dosis para una
// hora dada. Es la única pieza de la escena que lee el dataset; si no lo cubre (caducado), Sample devuelve
// condiciones sin clima y Status lo explica, y la navegación sigue funcionando sin esos términos.
[DefaultExecutionOrder(-70)]
public class ClimateService : MonoBehaviour
{
    public static ClimateService Instance { get; private set; }

    [Tooltip("Assets/Gale/Data/climate_dataset.json")]
    public TextAsset dataset;
    [Tooltip("Usar el escenario de polvo (hoyPolvo) en vez del climatológico.")]
    public bool dustScenario;

    public ClimateDataset Data { get; private set; }

    void Awake()
    {
        Instance = this;
        if (!dataset) { Debug.LogWarning("[Clima] falta climate_dataset.json: la navegación irá sin clima"); return; }
        try { Data = ClimateDataset.Parse(dataset.text); }
        catch (Exception e) { Debug.LogError("[Clima] no se pudo leer climate_dataset.json: " + e.Message); }
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>Hora local (hora solar verdadera) de Curiosity ahora, o null sin dataset.</summary>
    public float? LocalHour(DateTime utc)
    {
        var p = Point();
        var day = p?.DayForSol(MarsTime.CuriositySol(utc, p.lon));
        return day != null ? MarsTime.LocalTrueSolarTime(utc, p.lon, day.ecuacionTiempoH) : (float?)null;
    }

    McdPoint Point() => Data?.NearestPoint(MarsTime.CuriosityLon, -4.6);

    /// <summary>Condiciones para el coste en ese instante. Sin cobertura: hasClimate = false (con la dosis, si hay).</summary>
    public NavConditions Sample(DateTime utc, out string status)
    {
        var c = NavConditions.None;
        if (Data == null) { status = "Sin datos de clima ni radiación: ruta calculada sin esos términos."; return c; }
        if (Data.rad?.ultimas24h != null && Data.rad.ultimas24h.n > 0) c.doseRate = Data.rad.ultimas24h.media;
        var h = Point()?.At(utc, dustScenario);
        if (h == null)
        {
            status = $"El dataset de clima (generado {Data.generado}) no cubre la fecha actual: ruta sin clima. " +
                     "Regenera climate_dataset.json.";
            return c;
        }
        c.hasClimate = true;
        c.solarFlux = h.flujoSolar;
        c.tau = h.polvo;
        c.tAirK = h.tAire;
        status = $"Clima MCD {h.horaLocal:00.0} h local · {(c.Daylight ? "de día" : "de noche")} · τ {h.polvo:0.00} · {h.TAireC:0} °C";
        return c;
    }

    /// <summary>Horas desde la última lectura de RAD (para "dato RAD de hace 21 h").</summary>
    public double RadAgeHours(DateTime utc) => Data?.rad != null && !string.IsNullOrEmpty(Data.rad.ultimaLectura) ? Data.rad.AgeHours(utc) : double.NaN;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}
