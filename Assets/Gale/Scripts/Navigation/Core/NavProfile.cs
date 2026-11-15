using System;

namespace Gale.Navigation
{
    // Parámetros del vehículo y pesos de la ruta. Se leen de Assets/Gale/Data/Nav/nav_profiles.json.
    // Son valores iniciales del equipo, no datos medidos: hay que calibrarlos con el investigador.
    // Índices de suelo (capa "suelo" de geo_dataset.json): 0 sin dato · 1 polvo / arena fina ·
    // 2 regolito · 3 consolidado · 4 roca. Unidades geológicas (capa "geologia"): 0 sin dato · 1 mNh ·
    // 2 lNh · 3 HNt · 4 eHt · 5 Htu · 6 AHi.
    [Serializable]
    public class NavProfile
    {
        public string id = "rover";
        public string nombre = "Rover tripulado";
        public const float KmhToMs = 1f / 3.6f;

        public float vRefKmh = 10f;           // llano firme (roca), de día y sin polvo
        public float sMaxDeg = 25f;           // por encima, no transitable
        public float sCautionDeg = 15f;       // desde aquí sube el riesgo
        public float upFactorAtMax = 0.35f;   // factor de velocidad subiendo a sMax
        public float downFactorAtMax = 0.6f;  // bajando a sMax
        public float[] soilSpeed = { 0.85f, 0.6f, 0.85f, 0.95f, 1f };
        public float[] crr = { 0.12f, 0.25f, 0.12f, 0.08f, 0.05f };   // resistencia a la rodadura
        public float thermoSpeed = 0.1f;      // dentro de una clase: índice termofísico bajo = más lento
        public float nightFactor = 0.5f;      // velocidad de noche
        public float dustSlow = 0.3f;         // pérdida de velocidad con τ alto (visibilidad)
        public float massKg = 3000f;
        public float gravity = 3.71f;
        public float efficiency = 0.75f;      // tracción
        public float basePowerW = 400f;       // aviónica, soporte vital
        public float heatWPerK = 8f;          // calefacción por kelvin bajo heatRefK
        public float heatRefK = 253.15f;
        public float[] soilRisk = { 0.1f, 0.6f, 0.15f, 0f, 0.05f };   // hundimiento, por metro
        public float[] geoRisk = { 0f, 0f, 0f, 0f, 0f, 0.15f, 0.2f };
        public float coarseRisk = 0.1f;       // relieve regional (~463 m) fuera de la zona de detalle
        public float provenDiscount = 0.5f;   // terreno ya recorrido por Curiosity
        public float relaxedRisk = 6f;        // con límites relajados, celdas por encima de sMax

        public float VRef => vRefKmh * KmhToMs;

        public float SoilSpeed(int c) => soilSpeed[Clamp(c, soilSpeed.Length)];
        public float Crr(int c) => crr[Clamp(c, crr.Length)];
        public float SoilRisk(int c) => soilRisk[Clamp(c, soilRisk.Length)];
        public float GeoRisk(int u) => geoRisk[Clamp(u, geoRisk.Length)];
        static int Clamp(int i, int n) => i < 0 || i >= n ? 0 : i;

        public static NavProfile Rover() => new NavProfile();

        public static NavProfile Eva() => new NavProfile
        {
            id = "eva", nombre = "EVA a pie", vRefKmh = 2.5f,
            soilSpeed = new[] { 1f / 1.3f, 1f / 1.8f, 1f / 1.3f, 1f / 1.1f, 1f },
            crr = new[] { 0.3f, 0.5f, 0.3f, 0.22f, 0.2f },   // equivalente de marcha, no rodadura
            nightFactor = 0.3f, massKg = 180f, efficiency = 0.25f, basePowerW = 150f, heatWPerK = 2f,
            upFactorAtMax = 0.3f, downFactorAtMax = 0.5f,
        };
    }

    /// <summary>Pesos del coste generalizado (equilibrada, rápida, segura, menor energía, menor exposición).</summary>
    [Serializable]
    public class NavWeights
    {
        public string id = "equilibrada";
        public string nombre = "Equilibrada";
        public float wTime = 1f, wEnergy = 1f, wRisk = 1f, wDose = 0.5f;
        public float nightPenalty = 0f;   // coste extra por segundo de noche, en unidades de t/t0

        public static NavWeights[] Defaults() => new[]
        {
            new NavWeights(),
            new NavWeights { id = "rapida", nombre = "Más rápida", wTime = 3f, wEnergy = 0.3f, wRisk = 0.4f, wDose = 0.3f },
            new NavWeights { id = "segura", nombre = "Más segura", wTime = 0.6f, wEnergy = 0.5f, wRisk = 4f, wDose = 0.5f },
            new NavWeights { id = "energia", nombre = "Menor energía", wTime = 0.4f, wEnergy = 3f, wRisk = 0.8f, wDose = 0.3f },
            new NavWeights { id = "exposicion", nombre = "Menor exposición", wTime = 0.6f, wEnergy = 0.5f, wRisk = 1f, wDose = 3f, nightPenalty = 1f },
        };
    }

    [Serializable]
    public class NavProfilesFile
    {
        public string aviso;
        public NavProfile[] vehiculos;
        public NavWeights[] pesos;
    }

    /// <summary>Condiciones ambientales que usa el coste (las da ClimateService; sin clima, valores neutros).</summary>
    [Serializable]
    public struct NavConditions
    {
        public bool hasClimate;
        public float solarFlux;   // W/m²
        public float tau;         // profundidad óptica del polvo
        public float tAirK;
        public float doseRate;    // µGy/día (RAD, media de 24 h); 0 = sin dato

        public bool Daylight => !hasClimate || solarFlux > 20f;

        public static NavConditions None => new NavConditions { hasClimate = false, tAirK = 253.15f };
    }
}
