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
        /// <summary>Con los límites relajados, pendiente máxima de una arista (más, ni a pie).</summary>
        public const float RelaxedEdgeDeg = 60f;

        readonly float vRef, t0, e0, kEnv, pTotal, nightMul, tanMax;
        readonly float hLinear, hTraction, hRoll, cosLim;

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
            // Pendiente máxima de una arista: s_max, o RelaxedEdgeDeg con los límites relajados (nada se conduce a más).
            double edgeMax = relaxed ? Math.Max(p.sMaxDeg, RelaxedEdgeDeg) : p.sMaxDeg;
            tanMax = (float)Math.Tan(edgeMax * Math.PI / 180.0);

            // Heurística admisible (cota inferior del coste hasta el destino), ver Heuristic:
            //  - tiempo y dosis: todos los factores de velocidad son ≤ 1 → t/t₀ ≥ D;
            //  - energía fija: (P_base + P_calef)·t ≥ (P_base + P_calef)·D/v_ref;
            //  - tracción: sin aristas de más de s_max, Σ d·max(0, C_rr·cos θ + sin θ) ≥ c·max(0, C_rr,mín·c·D + Δh)
            //    con c = cos de la pendiente máxima de arista (se baja gratis, pero cada metro de bajada hay que
            //    haberlo subido antes);
            //  - riesgo ≥ 0.
            hLinear = w.wTime + w.wDose + w.wEnergy * (pTotal / vRef) / e0;
            cosLim = (float)Math.Cos(edgeMax * Math.PI / 180.0);
            float crrMin = float.MaxValue;
            foreach (float k in p.crr) crrMin = Math.Min(crrMin, k);
            hRoll = Math.Max(0f, crrMin) * cosLim;
            hTraction = w.wEnergy * p.massKg * p.gravity / p.efficiency / e0 * cosLim;
        }

        /// <summary>Cota inferior del coste para recorrer dist m en planta con una subida neta dh (puede ser negativa).</summary>
        public float Heuristic(float dist, float dh)
        {
            float tr = hRoll * dist + dh;
            return hLinear * dist + (tr > 0f ? hTraction * tr : 0f);
        }

        /// <summary>Coste mínimo por metro recorrido en el nivel regional (riesgo de relieve impreciso, con el
        /// descuento de terreno probado). La heurística lo cobra sobre el tramo que seguro queda fuera del detalle.</summary>
        public float MinCoarsePerMeter => W.wRisk * P.coarseRisk * (1f - P.provenDiscount);

        /// <summary>Coste mínimo por metro horizontal en llano (la parte lineal de la heurística).</summary>
        public float MinCostPerMeter => hLinear + hTraction * hRoll;

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
            float dd = Math.Max(d, 1e-3f);
            // Una arista más empinada que s_max tampoco se recorre (lo exige la cota de tracción de la heurística).
            if (Math.Abs(dz) > tanMax * dd)
            {
                s.blocked = true;
                s.cost = float.PositiveInfinity;
                return;
            }
            s.d3 = (float)Math.Sqrt(d * d + dz * dz);
            float theta = AtanDeg(dz / dd);   // pendiente en el sentido de la marcha

            // Velocidad.
            float f = Clamp01(Math.Abs(theta) / P.sMaxDeg);
            float kPend = 1f - (1f - (theta >= 0f ? P.upFactorAtMax : P.downFactorAtMax)) * f;
            float th = b.thermo / 255f;
            float kSoil = P.SoilSpeed(b.soil) * (1f - P.thermoSpeed * (1f - th));
            float v = vRef * Math.Max(0.05f, kPend * kSoil * kEnv);
            s.t = s.d3 / v;

            // Energía: tracción (sin regeneración bajando) + consumo fijo y calefacción durante el tramo.
            float traction = Math.Max(0f, (P.Crr(b.soil) * dd + dz) / s.d3);   // C_rr·cos θ + sin θ
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

        /// <summary>atan en grados, aproximada (error &lt; 0,1°): evita la trigonometría en el bucle de A*.</summary>
        public static float AtanDeg(float x)
        {
            const float R = 57.29578f;
            float a = Math.Abs(x);
            float r = a <= 1f ? Core(a) : 1.5707963f - Core(1f / a);
            return (x < 0f ? -r : r) * R;
        }

        static float Core(float a) => 0.7853982f * a - a * (a - 1f) * (0.2447f + 0.0663f * a);
    }
}
