using System;

namespace Gale.Navigation
{
    /// <summary>Desglose del coste de un tramo.</summary>
    public struct NavStep
    {
        public float d3;        // longitud con desnivel, m
        public float t;         // s de misión
        public float energy;    // J
        public float risk;      // adimensional (por metro)
        public float dose;      // µGy
        public float cost;      // coste generalizado, "metros de terreno fácil"
        public float cTime, cEnergy, cRisk, cDose;   // reparto del coste
        public bool blocked, unsafeStep, caution, night;
    }

    // Coste generalizado de un tramo entre dos celdas (PLAN.md §5.3):
    //   v = v_ref · k_pend(θ) · k_suelo(clase, termofísico) · k_luz · k_polvo ;  t = d₃D / v
    //   E = d · m·g · max(0, C_rr·cos θ + sin θ) / η + (P_base + P_calef(T)) · t
    //   r = r_pend(s) + r_hund(clase) + r_geo(unidad) + r_incert(nivel), con descuento en terreno probado
    //   D = Ḋ · t / 86400
    //   J = w_t·t/t₀ + w_E·E/E₀ + w_R·r·d + w_D·D/D₀   (t₀, E₀, D₀: por metro en llano firme de día)
    // La cota, la pendiente y las clases son las de la celda de llegada.
    public sealed class NavCostModel
    {
        public readonly NavProfile P;
        public readonly NavWeights W;
        public readonly NavConditions C;
        public readonly bool Relaxed;

        readonly float vRef, t0, e0, kEnv, pTotal, minPerMeter, nightMul;

        public NavCostModel(NavProfile p, NavWeights w, NavConditions c, bool relaxed = false)
        {
            P = p; W = w; C = c; Relaxed = relaxed;
            vRef = p.VRef;
            t0 = 1f / vRef;
            float firm = p.Crr(4);
            float heat = c.hasClimate ? p.heatWPerK * Math.Max(0f, p.heatRefK - c.tAirK) : 0f;
            pTotal = p.basePowerW + heat;
            e0 = p.massKg * p.gravity * firm / p.efficiency + p.basePowerW / vRef;
            float kLight = c.Daylight ? 1f : p.nightFactor;
            float kDust = c.hasClimate ? 1f - p.dustSlow * Clamp01((c.tau - 0.5f) / 1.5f) : 1f;
            kEnv = kLight * kDust;
            nightMul = c.Daylight ? 0f : w.nightPenalty;
            // Cota inferior del coste por metro (heurística admisible): todos los factores de velocidad ≤ 1,
            // la energía nunca baja de P_base·t y el riesgo nunca es negativo.
            minPerMeter = w.wTime + w.wEnergy * (p.basePowerW / vRef) / e0 + w.wDose;
        }

        /// <summary>Coste mínimo por metro horizontal (para la heurística de A*).</summary>
        public float MinCostPerMeter => minPerMeter;

        public bool Passable(in NavCell b) =>
            !float.IsNaN(b.h) && !float.IsNaN(b.slopeDeg) && (Relaxed || b.slopeDeg <= P.sMaxDeg);

        /// <summary>Coste de ir a la celda b recorriendo d m en planta con desnivel dz. Infinito si está bloqueada.</summary>
        public float Cost(in NavCell b, float d, float dz)
        {
            Evaluate(b, d, dz, out var s);
            return s.cost;
        }

        public void Evaluate(in NavCell b, float d, float dz, out NavStep s)
        {
            s = default;
            if (!Passable(b) || float.IsNaN(dz))
            {
                s.blocked = true;
                s.cost = float.PositiveInfinity;
                return;
            }
            float slope = b.slopeDeg;
            s.d3 = (float)Math.Sqrt(d * d + dz * dz);
            float theta = (float)(Math.Atan2(dz, Math.Max(d, 1e-3f)) * 180.0 / Math.PI);

            // Velocidad.
            float f = Clamp01(Math.Abs(theta) / P.sMaxDeg);
            float kPend = 1f - (1f - (theta >= 0f ? P.upFactorAtMax : P.downFactorAtMax)) * f;
            float th = b.thermo / 255f;
            float kSoil = P.SoilSpeed(b.soil) * (1f - P.thermoSpeed * (1f - th));
            float v = vRef * Math.Max(0.05f, kPend * kSoil * kEnv);
            s.t = s.d3 / v;

            // Energía: tracción (sin regeneración bajando) + consumo fijo y calefacción durante el tramo.
            double thr = theta * Math.PI / 180.0;
            float traction = (float)Math.Max(0.0, P.Crr(b.soil) * Math.Cos(thr) + Math.Sin(thr));
            s.energy = d * P.massKg * P.gravity * traction / P.efficiency + pTotal * s.t;

            // Riesgo.
            float r = 0f;
            if (slope > P.sCautionDeg)
            {
                s.caution = true;
                r += Clamp01((slope - P.sCautionDeg) / (P.sMaxDeg - P.sCautionDeg));
            }
            if (slope > P.sMaxDeg) { r += P.relaxedRisk; s.unsafeStep = true; }
            r += P.SoilRisk(b.soil) * (1.2f - 0.4f * th);
            r += P.GeoRisk(b.geo);
            if (b.level == 0) r += P.coarseRisk;
            if (b.proven) r *= 1f - P.provenDiscount;
            s.risk = r;

            // Dosis: la tasa es la misma en todo el mapa (RAD mide en un punto), así que pesa como tiempo expuesto.
            s.dose = C.doseRate * s.t / 86400f;
            s.night = !C.Daylight;

            float tn = s.t / t0;
            s.cTime = W.wTime * tn;
            s.cEnergy = W.wEnergy * s.energy / e0;
            s.cRisk = W.wRisk * r * d;
            s.cDose = W.wDose * tn * (1f + nightMul);
            s.cost = s.cTime + s.cEnergy + s.cRisk + s.cDose;
        }

        static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
    }
}
