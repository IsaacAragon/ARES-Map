using System;
using System.Collections.Generic;
using System.Threading;

namespace Gale.Navigation
{
    /// <summary>Punto de la ruta remuestreada (m reales; X este, Z norte, Y cota).</summary>
    public struct NavPoint
    {
        public float x, z, h;
        public float dist, time, energy, dose;   // acumulados desde el origen
        public float slopeDeg;
        public byte soil, geo, level;
        public bool caution, unsafeStep, proven;
        public int leg;
    }

    public sealed class NavRoute
    {
        public readonly List<NavPoint> Points = new List<NavPoint>();
        public readonly List<string> Warnings = new List<string>();
        public string profileId, weightsId;

        public float DistanceM, TimeS, EnergyJ, DoseUGy, AscentM, DescentM, MaxSlopeDeg;
        public float CostTime, CostEnergy, CostRisk, CostDose;
        public float CautionM, UnsafeM, CoarseM, ProvenM, NightM;
        public readonly float[] SoilM = new float[5];
        public readonly float[] GeoM = new float[7];
        public bool Unsafe;            // se calculó con los límites relajados
        public float SnapMeters;       // cuánto se movió el destino a terreno transitable
        public int Expanded;
        public double SearchMs, TotalMs;

        public float Cost => CostTime + CostEnergy + CostRisk + CostDose;
    }

    public sealed class NavPlanResult
    {
        public NavStatus status;
        public NavRoute route;
        public string message;
    }

    // Planificación completa: ajuste de origen y paradas a terreno transitable, A* por tramo,
    // suavizado (acorta esquinas si el tramo recto no cruza celdas bloqueadas y su coste no empeora más
    // de un 2 %) y remuestreo con el desglose de coste por punto.
    public sealed class NavPlanner
    {
        public readonly NavGrid Grid;
        readonly NavPathfinder astar;
        public float snapMaxMeters = 500f;
        /// <summary>Ver NavPathfinder.heuristicWeight (1 = óptimo exacto).</summary>
        public float HeuristicWeight { get => astar.heuristicWeight; set => astar.heuristicWeight = value; }
        public float smoothTolerance = 1.02f;

        public NavPlanner(NavGrid grid)
        {
            Grid = grid;
            astar = new NavPathfinder(grid);
        }

        // Componentes conexas del terreno transitable (con los límites del perfil): si origen y destino no
        // comparten componente no hay ruta segura, y se sabe sin explorar toda la región. 0 = bloqueada.
        int[] comp;
        float compSMax = float.NaN;
        public int ComponentCount { get; private set; }
        public double ComponentsMs { get; private set; }

        public void PrepareComponents(NavProfile p, CancellationToken ct = default)
        {
            if (comp != null && compSMax == p.sMaxDeg) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var m = new NavCostModel(p, new NavWeights(), NavConditions.None);
            var c = new int[Grid.NodeCount];
            var nb = new int[Grid.MaxNeighbors];
            var stack = new Stack<int>(1 << 16);
            int id = 0;
            for (int s = 0; s < c.Length; s++)
            {
                if (c[s] != 0 || (!Grid.IsL1(s) && Grid.Covered(s % Grid.L0.W, s / Grid.L0.W))) continue;
                var cs = Grid.Cell(s);
                if (!m.Passable(cs)) continue;
                if ((s & 0xFFFF) == 0 && ct.IsCancellationRequested) return;
                c[s] = ++id;
                stack.Push(s);
                while (stack.Count > 0)
                {
                    int u = stack.Pop();
                    Grid.Center(u, out float ux, out float uz);
                    float hu = Grid.Cell(u).h;
                    int k = Grid.Neighbors(u, nb);
                    for (int j = 0; j < k; j++)
                    {
                        int v = nb[j];
                        if (c[v] != 0) continue;
                        var cv = Grid.Cell(v);
                        Grid.Center(v, out float vx, out float vz);
                        if (float.IsInfinity(m.Cost(cv, Dist(ux, uz, vx, vz), cv.h - hu)) || NavPathfinder.CornerBlocked(Grid, u, v, m)) continue;
                        c[v] = id;
                        stack.Push(v);
                    }
                }
            }
            comp = c;
            compSMax = p.sMaxDeg;
            ComponentCount = id;
            ComponentsMs = sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>Ruta por los puntos dados (m: x, z), el primero es el origen. Con allowRelax, si no hay
        /// ruta segura repite relajando la pendiente máxima y marca la ruta como no segura.</summary>
        public NavPlanResult Plan(IList<(float x, float z)> pts, NavProfile p, NavWeights w, NavConditions c,
            bool allowRelax, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = PlanWith(pts, new NavCostModel(p, w, c), ct);
            if (res.status == NavStatus.NoPath && allowRelax && !ct.IsCancellationRequested)
            {
                var relaxed = PlanWith(pts, new NavCostModel(p, w, c, true), ct);
                if (relaxed.status == NavStatus.Ok)
                {
                    relaxed.route.Unsafe = true;
                    relaxed.route.Warnings.Insert(0, $"Ruta NO SEGURA: no hay camino con pendientes ≤ {p.sMaxDeg:0}°; " +
                                                     $"{relaxed.route.UnsafeM / 1000f:0.##} km superan el límite.");
                    relaxed.message = res.message;
                    res = relaxed;
                }
            }
            if (res.route != null) { res.route.profileId = p.id; res.route.weightsId = w.id; res.route.TotalMs = sw.Elapsed.TotalMilliseconds; }
            return res;
        }

        NavPlanResult PlanWith(IList<(float x, float z)> pts, NavCostModel m, CancellationToken ct)
        {
            var res = new NavPlanResult();
            var route = new NavRoute();
            var nodes = new List<int>();
            var legOf = new List<int>();
            int startComp = 0;
            bool useComp = comp != null && !m.Relaxed && compSMax == m.P.sMaxDeg;
            for (int i = 0; i < pts.Count; i++)
            {
                int n = Grid.NodeAt(pts[i].x, pts[i].z);
                if (n < 0 || (i == 0 && float.IsNaN(Grid.Cell(n).h)))
                {
                    res.status = NavStatus.OutsideGrid;
                    res.message = i == 0 ? "El rover está fuera del terreno con relieve." : "El punto elegido está fuera del terreno con relieve.";
                    return res;
                }
                // El origen puede estar en una celda bloqueada (se le deja salir); las paradas se ajustan.
                if (i == 0 && useComp) startComp = comp[n];   // 0 si el rover está en una celda bloqueada
                if (i > 0)
                {
                    int s = astar.NearestPassable(n, m, snapMaxMeters, out float moved, startComp != 0 ? comp : null, startComp);
                    if (s < 0 && startComp != 0 && astar.NearestPassable(n, m, snapMaxMeters, out _) >= 0)
                    {
                        // Hay terreno transitable, pero ningún camino seguro llega hasta él desde el rover.
                        res.status = NavStatus.NoPath;
                        res.message = $"El destino está en una zona aislada: no se llega sin pendientes de más de {m.P.sMaxDeg:0}°.";
                        return res;
                    }
                    if (s < 0)
                    {
                        res.status = NavStatus.NoPath;
                        res.message = float.IsNaN(Grid.Cell(n).h)
                            ? "El punto elegido está fuera del terreno con relieve."
                            : $"No hay terreno transitable a menos de {snapMaxMeters:0} m del punto elegido (pendiente > {m.P.sMaxDeg:0}°).";
                        return res;
                    }
                    if (i == pts.Count - 1) route.SnapMeters = moved;
                    else if (moved > 1f) route.Warnings.Add($"Parada {i} movida {moved:0} m a terreno transitable.");
                    n = s;
                }
                nodes.Add(n);
            }

            var path = new List<int>();
            for (int i = 0; i + 1 < nodes.Count; i++)
            {
                var r = astar.Search(nodes[i], nodes[i + 1], m, ct);
                route.Expanded += r.expanded;
                route.SearchMs += r.ms;
                if (r.status != NavStatus.Ok)
                {
                    res.status = r.status;
                    res.message = r.status == NavStatus.Cancelled ? "Cálculo cancelado."
                        : nodes.Count > 2 ? $"No hay ruta transitable en el tramo {i + 1}." : "No hay ruta transitable hasta ese punto.";
                    return res;
                }
                var smoothed = Smooth(r.nodes, m);
                for (int j = path.Count == 0 ? 0 : 1; j < smoothed.Count; j++) { path.Add(smoothed[j]); legOf.Add(i); }
            }
            if (path.Count == 1) { path.Add(path[0]); legOf.Add(0); }
            Resample(path, legOf, m, route);
            if (route.SnapMeters > 1f) route.Warnings.Add($"Destino movido {route.SnapMeters:0} m a terreno transitable.");
            res.status = NavStatus.Ok;
            res.route = route;
            return res;
        }

        // ---------- Suavizado ----------

        List<int> Smooth(List<int> nodes, NavCostModel m)
        {
            int n = nodes.Count;
            if (n < 3) return new List<int>(nodes);
            // Coste acumulado de la ruta de A* (mismo cálculo que las aristas).
            var acc = new float[n];
            for (int i = 1; i < n; i++)
            {
                Grid.Center(nodes[i - 1], out float ax, out float az);
                Grid.Center(nodes[i], out float bx, out float bz);
                var a = Grid.Cell(nodes[i - 1]); var b = Grid.Cell(nodes[i]);
                acc[i] = acc[i - 1] + m.Cost(b, Dist(ax, az, bx, bz), b.h - a.h);
            }
            var outp = new List<int> { nodes[0] };
            int anchor = 0;
            while (anchor < n - 1)
            {
                int best = anchor + 1;
                // Saltos de 2, 4, 8… nodos hasta el primero que no compensa (o el final).
                for (int step = 2; ; step *= 2)
                {
                    int j = Math.Min(anchor + step, n - 1);
                    if (j <= best) break;
                    float c = StraightCost(nodes[anchor], nodes[j], m);
                    if (c > (acc[j] - acc[anchor]) * smoothTolerance) break;
                    best = j;
                    if (j == n - 1) break;
                }
                outp.Add(nodes[best]);
                anchor = best;
            }
            return outp;
        }

        /// <summary>Coste integrado del tramo recto entre dos nodos (∞ si cruza una celda bloqueada).</summary>
        float StraightCost(int a, int b, NavCostModel m)
        {
            Grid.Center(a, out float ax, out float az);
            Grid.Center(b, out float bx, out float bz);
            float len = Dist(ax, az, bx, bz);
            float step = StepFor(a, b);
            int k = Math.Max(1, (int)Math.Ceiling(len / step));
            float total = 0f, ph = Grid.Cell(a).h;
            for (int i = 1; i <= k; i++)
            {
                float t = (float)i / k;
                float x = ax + (bx - ax) * t, z = az + (bz - az) * t;
                int nd = Grid.NodeAt(x, z);
                if (nd < 0) return float.PositiveInfinity;
                var c = Grid.Cell(nd);
                float h = Grid.HeightAt(x, z);
                if (float.IsNaN(h)) h = c.h;
                float cost = m.Cost(c, len / k, h - ph);
                if (float.IsInfinity(cost)) return cost;
                total += cost;
                ph = h;
            }
            return total;
        }

        float StepFor(int a, int b)
        {
            bool detail = Grid.IsL1(a) || Grid.IsL1(b);
            var lv = detail ? Grid.L1 : Grid.L0;
            return Math.Min(lv.CellX, lv.CellZ) * 0.5f;
        }

        // ---------- Remuestreo y totales ----------

        void Resample(List<int> path, List<int> legOf, NavCostModel m, NavRoute r)
        {
            Grid.Center(path[0], out float px, out float pz);
            var c0 = Grid.Cell(path[0]);
            float ph = Grid.HeightAt(px, pz);
            if (float.IsNaN(ph)) ph = c0.h;
            var cur = new NavPoint { x = px, z = pz, h = ph, slopeDeg = Nz(c0.slopeDeg), soil = c0.soil, geo = c0.geo, level = c0.level, proven = c0.proven };
            r.Points.Add(cur);
            for (int i = 1; i < path.Count; i++)
            {
                Grid.Center(path[i], out float bx, out float bz);
                float len = Dist(px, pz, bx, bz);
                if (len < 1e-3f) continue;
                float step = StepFor(path[i - 1], path[i]);
                int k = Math.Max(1, (int)Math.Ceiling(len / step));
                for (int s = 1; s <= k; s++)
                {
                    float t = (float)s / k;
                    float x = px + (bx - px) * t, z = pz + (bz - pz) * t;
                    int nd = Grid.NodeAt(x, z);
                    var c = nd >= 0 ? Grid.Cell(nd) : Grid.Cell(path[i]);
                    float h = Grid.HeightAt(x, z);
                    if (float.IsNaN(h)) h = c.h;
                    float d = len / k;
                    m.Evaluate(c, d, h - cur.h, out var st);
                    if (st.blocked)
                    {
                        // El tramo recto solo puede rozar un hueco en el remuestreo: se cuenta con la celda de A*.
                        var cb = Grid.Cell(path[i]);
                        m.Evaluate(cb, d, h - cur.h, out st);
                        if (st.blocked) { st = default; st.d3 = d; st.unsafeStep = true; }
                        c = cb;
                    }
                    Accumulate(r, st, c, d, h - cur.h);
                    cur = new NavPoint
                    {
                        x = x, z = z, h = h,
                        dist = cur.dist + d, time = cur.time + st.t, energy = cur.energy + st.energy, dose = cur.dose + st.dose,
                        slopeDeg = Nz(c.slopeDeg), soil = c.soil, geo = c.geo, level = c.level,
                        caution = st.caution, unsafeStep = st.unsafeStep, proven = c.proven, leg = legOf[i],
                    };
                    r.Points.Add(cur);
                }
                px = bx; pz = bz;
            }
            r.DistanceM = cur.dist; r.TimeS = cur.time; r.EnergyJ = cur.energy; r.DoseUGy = cur.dose;

            float km(float v) => v / 1000f;
            if (r.CoarseM > 200f) r.Warnings.Add($"{km(r.CoarseM):0.#} km con relieve regional (~463 m): no ve obstáculos pequeños.");
            if (r.SoilM[1] > 100f) r.Warnings.Add($"{km(r.SoilM[1]):0.##} km de polvo o arena fina (riesgo de hundimiento).");
            if (r.CautionM > 50f) r.Warnings.Add($"{km(r.CautionM):0.##} km con pendiente de precaución (> {m.P.sCautionDeg:0}°).");
            if (r.NightM > 0f) r.Warnings.Add("Tramos de noche: menos velocidad y más riesgo.");
        }

        static void Accumulate(NavRoute r, in NavStep st, in NavCell c, float d, float dz)
        {
            r.CostTime += st.cTime; r.CostEnergy += st.cEnergy; r.CostRisk += st.cRisk; r.CostDose += st.cDose;
            if (dz > 0) r.AscentM += dz; else r.DescentM -= dz;
            if (!float.IsNaN(c.slopeDeg) && c.slopeDeg > r.MaxSlopeDeg) r.MaxSlopeDeg = c.slopeDeg;
            if (st.caution) r.CautionM += d;
            if (st.unsafeStep) r.UnsafeM += d;
            if (st.night) r.NightM += d;
            if (c.level == 0) r.CoarseM += d;
            if (c.proven) r.ProvenM += d;
            r.SoilM[c.soil < 5 ? c.soil : 0] += d;
            r.GeoM[c.geo < 7 ? c.geo : 0] += d;
        }

        static float Nz(float v) => float.IsNaN(v) ? 0f : v;

        static float Dist(float ax, float az, float bx, float bz)
        {
            float dx = bx - ax, dz = bz - az;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
