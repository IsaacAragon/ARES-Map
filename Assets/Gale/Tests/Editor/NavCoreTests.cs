using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;

namespace Gale.Navigation.Tests
{
    // Pruebas del núcleo de navegación sobre mallas sintéticas (sin escena). PLAN.md §10.
    public class NavCoreTests
    {
        const float Cell = 100f;

        static NavGrid Flat(int w, int h, float cell = Cell, byte soil = 4)
        {
            var g = new NavGrid { L0 = new NavLevel(w, h, 0f, 0f, cell, cell) };
            g.Soil = new byte[w * h];
            g.Geo = new byte[w * h];
            g.Thermo = new byte[w * h];
            for (int i = 0; i < w * h; i++) { g.Soil[i] = soil; g.Thermo[i] = 200; }
            return g;
        }

        static NavPlanResult Plan(NavGrid g, (float, float) a, (float, float) b, NavWeights w = null, bool relax = false)
        {
            g.ComputeSlopes();
            return new NavPlanner(g).Plan(new List<(float x, float z)> { a, b }, NavProfile.Rover(), w ?? new NavWeights(),
                NavConditions.None, relax);
        }

        static float C(int i) => (i + 0.5f) * Cell;

        static float MaxDeviationFromLine(NavRoute r, float z)
        {
            float m = 0f;
            foreach (var p in r.Points) m = Math.Max(m, Math.Abs(p.z - z));
            return m;
        }

        [Test]
        public void FlatGrid_GivesStraightRoute()
        {
            var g = Flat(40, 20);
            var res = Plan(g, (C(2), C(10)), (C(37), C(10)));
            Assert.AreEqual(NavStatus.Ok, res.status, res.message);
            Assert.AreEqual(35 * Cell, res.route.DistanceM, 1f);
            Assert.Less(MaxDeviationFromLine(res.route, C(10)), 1f);
            Assert.IsFalse(res.route.Unsafe);
        }

        [Test]
        public void FlatGrid_DiagonalIsSmoothedToStraightLine()
        {
            var g = Flat(40, 40);
            var res = Plan(g, (C(2), C(2)), (C(30), C(9)));
            Assert.AreEqual(NavStatus.Ok, res.status);
            float straight = (float)Math.Sqrt(28 * 28 + 7 * 7) * Cell;
            Assert.AreEqual(straight, res.route.DistanceM, straight * 0.01f, "el suavizado debe dejar la recta");
        }

        static void Wall(NavGrid g, int x, int y0, int y1)
        {
            // Una franja de celdas sin relieve (bloqueadas siempre) de y0 a y1.
            for (int y = y0; y <= y1; y++) g.L0.Height[y * g.L0.W + x] = float.NaN;
        }

        [Test]
        public void Wall_IsGoneAround()
        {
            var g = Flat(30, 30);
            Wall(g, 15, 0, 24);
            var res = Plan(g, (C(5), C(5)), (C(25), C(5)));
            Assert.AreEqual(NavStatus.Ok, res.status);
            foreach (var p in res.route.Points)
                Assert.IsFalse(float.IsNaN(g.L0.Get((int)(p.x / Cell), (int)(p.z / Cell))), "la ruta pisa el muro");
            Assert.Greater(res.route.DistanceM, 20 * Cell * 1.5f, "tiene que rodear por el norte");
        }

        [Test]
        public void SteepRidge_IsBlocked_NotCrossed()
        {
            var g = Flat(30, 30);
            // Cresta de 300 m y dos celdas de ancho: sus laderas superan los 25°.
            for (int y = 0; y < 25; y++)
                for (int x = 15; x <= 16; x++) g.L0.Height[y * 30 + x] = 300f;
            var res = Plan(g, (C(5), C(5)), (C(25), C(5)));
            Assert.AreEqual(NavStatus.Ok, res.status);
            g.ComputeSlopes();
            foreach (var p in res.route.Points) Assert.LessOrEqual(p.slopeDeg, 25f);
            Assert.IsFalse(res.route.Unsafe);
        }

        [Test]
        public void Hill_IsSkirted_WhenEnergyWeighted()
        {
            var g = Flat(41, 41);
            // Colina suave (máx. ~12°) en el centro.
            for (int y = 0; y < 41; y++)
                for (int x = 0; x < 41; x++)
                {
                    float dx = x - 20, dy = y - 20, r2 = dx * dx + dy * dy;
                    g.L0.Height[y * 41 + x] = 150f * (float)Math.Exp(-r2 / 40f);
                }
            var energy = new NavWeights { id = "e", wTime = 0.2f, wEnergy = 5f, wRisk = 0.2f, wDose = 0f };
            var res = Plan(g, (C(2), C(20)), (C(38), C(20)), energy);
            Assert.AreEqual(NavStatus.Ok, res.status);
            Assert.Greater(MaxDeviationFromLine(res.route, C(20)), 3 * Cell, "debería bordear la colina");
            Assert.Less(res.route.AscentM, 60f);
        }

        [Test]
        public void Rock_IsPreferredOverSand_WhenDetourPays()
        {
            var g = Flat(40, 21, soil: 1);   // todo arena fina
            for (int x = 0; x < 40; x++) { g.Soil[4 * 40 + x] = 4; g.Soil[16 * 40 + x] = 4; }  // carril de roca en y=4
            for (int y = 4; y <= 10; y++) { g.Soil[y * 40 + 2] = 4; g.Soil[y * 40 + 37] = 4; }
            var res = Plan(g, (C(2), C(10)), (C(37), C(10)));
            Assert.AreEqual(NavStatus.Ok, res.status);
            Assert.Greater(res.route.SoilM[4], res.route.SoilM[1], "debe ir sobre roca");
        }

        [Test]
        public void Profiles_ChangeTheRouteOrTotals()
        {
            var g = Flat(41, 41);
            for (int y = 0; y < 41; y++)
                for (int x = 0; x < 41; x++)
                {
                    float dx = x - 20, dy = y - 20;
                    g.L0.Height[y * 41 + x] = 200f * (float)Math.Exp(-(dx * dx + dy * dy) / 60f);
                }
            var all = NavWeights.Defaults();
            var fast = Plan(g, (C(2), C(20)), (C(38), C(20)), all[1]).route;
            var eco = Plan(g, (C(2), C(20)), (C(38), C(20)), all[3]).route;
            Assert.IsTrue(Math.Abs(fast.DistanceM - eco.DistanceM) > 1f || Math.Abs(fast.EnergyJ - eco.EnergyJ) > 1f);
            Assert.LessOrEqual(eco.EnergyJ, fast.EnergyJ * 1.02f, "la de menor energía no gasta más");
        }

        // A* con la heurística debe dar el mismo coste que Dijkstra (heurística nula) en mallas pequeñas.
        [Test]
        public void AStar_MatchesDijkstra([Values(1, 2, 3, 4, 5)] int seed)
        {
            var rnd = new Random(seed);
            var g = Flat(25, 25);
            for (int i = 0; i < 625; i++)
            {
                g.L0.Height[i] = (float)rnd.NextDouble() * 40f;
                g.Soil[i] = (byte)rnd.Next(1, 5);
                if (rnd.NextDouble() < 0.12) g.L0.Height[i] = float.NaN;
            }
            g.ComputeSlopes();
            var m = new NavCostModel(NavProfile.Rover(), new NavWeights(), NavConditions.None);
            int s = g.NodeAt(C(1), C(1)), t = g.NodeAt(C(23), C(22));
            g.L0.Height[s] = 0f; g.L0.Height[t] = 0f;
            var a = new NavPathfinder(g).Search(s, t, m);
            float dj = Dijkstra(g, s, t, m);
            if (float.IsInfinity(dj)) { Assert.AreEqual(NavStatus.NoPath, a.status); return; }
            Assert.AreEqual(NavStatus.Ok, a.status);
            Assert.AreEqual(dj, a.cost, dj * 1e-4f);
        }

        static float Dijkstra(NavGrid g, int s, int t, NavCostModel m)
        {
            var dist = new float[g.NodeCount];
            for (int i = 0; i < dist.Length; i++) dist[i] = float.PositiveInfinity;
            var done = new bool[g.NodeCount];
            var nb = new int[g.MaxNeighbors];
            dist[s] = 0f;
            while (true)
            {
                int u = -1; float best = float.PositiveInfinity;
                for (int i = 0; i < dist.Length; i++) if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
                if (u < 0) return float.PositiveInfinity;
                if (u == t) return best;
                done[u] = true;
                g.Center(u, out float ux, out float uz);
                float hu = g.Cell(u).h;
                int k = g.Neighbors(u, nb);
                for (int j = 0; j < k; j++)
                {
                    int v = nb[j];
                    if (NavPathfinder.CornerBlocked(g, u, v, m)) continue;
                    var cv = g.Cell(v);
                    g.Center(v, out float vx, out float vz);
                    float d = (float)Math.Sqrt((vx - ux) * (vx - ux) + (vz - uz) * (vz - uz));
                    float c = m.Cost(cv, d, cv.h - hu);
                    if (best + c < dist[v]) dist[v] = best + c;
                }
            }
        }

        [Test]
        public void Unreachable_ReportsNoPath_AndRelaxedIsUnsafe()
        {
            var g = Flat(30, 30);
            // Isla en un pozo con paredes de 400 m por celda.
            for (int y = 0; y < 30; y++)
                for (int x = 0; x < 30; x++)
                {
                    int dx = Math.Abs(x - 22), dy = Math.Abs(y - 15);
                    g.L0.Height[y * 30 + x] = Math.Max(dx, dy) <= 3 ? -1200f + 400f * Math.Max(dx, dy) : 0f;
                }
            var strict = Plan(g, (C(3), C(15)), (C(22), C(15)));
            Assert.AreEqual(NavStatus.NoPath, strict.status);
            Assert.IsNotEmpty(strict.message);
            var relaxed = Plan(g, (C(3), C(15)), (C(22), C(15)), relax: true);
            Assert.AreEqual(NavStatus.Ok, relaxed.status);
            Assert.IsTrue(relaxed.route.Unsafe);
            Assert.Greater(relaxed.route.UnsafeM, 0f);
        }

        [Test]
        public void BlockedDestination_IsSnappedToPassableCell()
        {
            var g = Flat(30, 30);
            for (int y = 10; y <= 12; y++) for (int x = 20; x <= 22; x++) g.L0.Height[y * 30 + x] = float.NaN;
            var res = Plan(g, (C(3), C(11)), (C(21), C(11)));
            Assert.AreEqual(NavStatus.Ok, res.status);
            Assert.AreEqual(2 * Cell, res.route.SnapMeters, 1f);
        }

        [Test]
        public void OutsideGrid_IsReported()
        {
            var g = Flat(10, 10);
            var res = Plan(g, (C(3), C(3)), (5000f, 5000f));
            Assert.AreEqual(NavStatus.OutsideGrid, res.status);
        }

        [Test]
        public void Cancellation_StopsSearch()
        {
            var g = Flat(400, 400);
            g.ComputeSlopes();
            var cts = new CancellationTokenSource();
            cts.Cancel();
            // Coste plano con heurística: casi no expande. Se fuerza a explorar con pesos de riesgo altos fuera de línea.
            var r = new NavPathfinder(g).Search(g.NodeAt(C(1), C(1)), g.NodeAt(C(398), C(398)),
                new NavCostModel(NavProfile.Rover(), new NavWeights { wTime = 0f, wEnergy = 0f, wDose = 0f, wRisk = 1f }, NavConditions.None), cts.Token);
            Assert.AreEqual(NavStatus.Cancelled, r.status);
        }

        // Dos niveles: un bloque de detalle con una muralla dentro. La ruta debe entrar en L1, rodearla y salir.
        [Test]
        public void TwoLevels_RouteCrossesDetailSeamlessly()
        {
            var g = Flat(20, 10);
            g.CreateDetail(8, 2, 4, 6, 10);   // 40 × 60 celdas de 10 m
            var l1 = g.L1;
            for (int i = 0; i < l1.Count; i++) l1.Height[i] = 0f;
            for (int y = 0; y < 50; y++) l1.Height[y * l1.W + 20] = float.NaN;   // muro dentro del detalle
            var res = Plan(g, (C(2), C(5)), (C(17), C(5)));
            Assert.AreEqual(NavStatus.Ok, res.status, res.message);
            bool inDetail = false;
            foreach (var p in res.route.Points)
            {
                if (p.level == 1) inDetail = true;
                int n = g.NodeAt(p.x, p.z);
                Assert.IsFalse(float.IsNaN(g.Cell(n).h), "pisa el muro del detalle");
            }
            Assert.IsTrue(inDetail, "la ruta debería pasar por el detalle");
            Assert.Greater(res.route.DistanceM, 15 * Cell, "rodea el muro");
        }

        [Test]
        public void TwoLevels_NeighborsAreSymmetric()
        {
            var g = Flat(12, 12);
            g.CreateDetail(4, 4, 3, 3, 5);
            var nb = new int[g.MaxNeighbors];
            var nb2 = new int[g.MaxNeighbors];
            for (int n = 0; n < g.NodeCount; n++)
            {
                if (!g.IsL1(n) && g.Covered(n % 12, n / 12)) continue;
                int k = g.Neighbors(n, nb);
                for (int j = 0; j < k; j++)
                {
                    int v = nb[j];
                    Assert.IsFalse(!g.IsL1(v) && g.Covered(v % 12, v / 12), "enlaza con una celda L0 cubierta");
                    int k2 = g.Neighbors(v, nb2);
                    Assert.IsTrue(Array.IndexOf(nb2, n, 0, k2) >= 0, $"{n}→{v} sin vuelta");
                }
            }
        }

        [Test]
        public void MultiStop_VisitsEveryStop()
        {
            var g = Flat(30, 30);
            g.ComputeSlopes();
            var pts = new List<(float x, float z)> { (C(2), C(2)), (C(25), C(5)), (C(5), C(25)) };
            var res = new NavPlanner(g).Plan(pts, NavProfile.Rover(), new NavWeights(), NavConditions.None, false);
            Assert.AreEqual(NavStatus.Ok, res.status);
            float near = float.MaxValue;
            foreach (var p in res.route.Points) near = Math.Min(near, (float)Math.Sqrt((p.x - C(25)) * (p.x - C(25)) + (p.z - C(5)) * (p.z - C(5))));
            Assert.Less(near, 1f);
            Assert.AreEqual(1, res.route.Points[res.route.Points.Count - 1].leg);
        }

        [Test]
        public void FileRoundTrip_KeepsHeights()
        {
            var g = Flat(16, 9);
            var rnd = new Random(7);
            for (int i = 0; i < g.L0.Count; i++) g.L0.Height[i] = -4500f + (float)rnd.NextDouble() * 6000f;
            g.L0.Height[5] = float.NaN;
            g.CreateDetail(3, 2, 2, 3, 4);
            for (int i = 0; i < g.L1.Count; i++) g.L1.Height[i] = (float)rnd.NextDouble() * 100f;
            g.Fingerprint = "huella";
            var ms = new MemoryStream();
            g.Write(ms);
            ms.Position = 0;
            var r = NavGrid.Read(ms);
            Assert.AreEqual("huella", r.Fingerprint);
            Assert.AreEqual(g.SubX0, r.SubX0); Assert.AreEqual(g.Sub, r.Sub);
            Assert.IsTrue(float.IsNaN(r.L0.Height[5]));
            for (int i = 0; i < g.L0.Count; i++) if (i != 5) Assert.AreEqual(g.L0.Height[i], r.L0.Height[i], 0.1f);
            for (int i = 0; i < g.L1.Count; i++) Assert.AreEqual(g.L1.Height[i], r.L1.Height[i], 0.01f);
        }

        [Test]
        public void Night_SlowsAndCostsMore()
        {
            var day = new NavConditions { hasClimate = true, solarFlux = 400f, tau = 0.4f, tAirK = 240f, doseRate = 200f };
            var night = day; night.solarFlux = 0f; night.tAirK = 190f;
            var cell = new NavCell { h = 0f, slopeDeg = 2f, soil = 4, thermo = 200, level = 1 };
            var p = NavProfile.Rover();
            new NavCostModel(p, new NavWeights(), day).Evaluate(cell, 10f, 0f, out var sd);
            new NavCostModel(p, new NavWeights(), night).Evaluate(cell, 10f, 0f, out var sn);
            Assert.Greater(sn.t, sd.t);
            Assert.Greater(sn.energy, sd.energy);
            Assert.Greater(sn.dose, 0f);
        }
    }
}
