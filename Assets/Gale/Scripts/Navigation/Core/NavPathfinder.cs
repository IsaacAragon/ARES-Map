using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Gale.Navigation
{
    public enum NavStatus { Ok, NoPath, OutsideGrid, Cancelled }

    public sealed class NavSearchResult
    {
        public NavStatus status;
        public List<int> nodes = new List<int>();
        public float cost;
        public int expanded;
        public double ms;
    }

    // A* sobre NavGrid (8 vecinos y enlaces L0 ↔ L1), con montículo binario y arrays reutilizados entre
    // búsquedas: solo se limpian las celdas tocadas. Heurística: distancia en planta × coste mínimo por
    // metro del modelo, admisible, así que la ruta es óptima sobre la malla. No es seguro usar la misma
    // instancia desde dos hilos a la vez (NavigationSystem serializa las búsquedas).
    public sealed class NavPathfinder
    {
        readonly NavGrid g;
        float[] gScore;
        int[] parent;
        byte[] closed;
        readonly List<int> touched = new List<int>(1 << 16);
        HeapItem[] heap = new HeapItem[1 << 16];
        int heapN;
        readonly int[] nb;

        struct HeapItem { public float f; public int n; }

        public NavPathfinder(NavGrid grid)
        {
            g = grid;
            nb = new int[grid.MaxNeighbors];
        }

        void Ensure()
        {
            if (gScore != null) return;
            int n = g.NodeCount;
            gScore = new float[n];
            parent = new int[n];
            closed = new byte[n];
            for (int i = 0; i < n; i++) { gScore[i] = float.PositiveInfinity; parent[i] = -1; }
        }

        void Reset()
        {
            foreach (int i in touched) { gScore[i] = float.PositiveInfinity; parent[i] = -1; closed[i] = 0; }
            touched.Clear();
            heapN = 0;
        }

        public NavSearchResult Search(int start, int goal, NavCostModel m, CancellationToken ct = default, Action<int> progress = null)
        {
            var res = new NavSearchResult();
            var sw = Stopwatch.StartNew();
            if (start < 0 || goal < 0) { res.status = NavStatus.OutsideGrid; return res; }
            Ensure();
            Reset();
            float hk = m.MinCostPerMeter;
            g.Center(goal, out float gx, out float gz);

            gScore[start] = 0f;
            touched.Add(start);
            g.Center(start, out float sx, out float sz);
            Push(Dist(sx, sz, gx, gz) * hk, start);

            res.status = NavStatus.NoPath;
            while (heapN > 0)
            {
                var it = Pop();
                int u = it.n;
                if (closed[u] != 0) continue;
                closed[u] = 1;
                res.expanded++;
                if ((res.expanded & 8191) == 0)
                {
                    if (ct.IsCancellationRequested) { res.status = NavStatus.Cancelled; break; }
                    progress?.Invoke(res.expanded);
                }
                if (u == goal) { res.status = NavStatus.Ok; break; }

                g.Center(u, out float ux, out float uz);
                float gu = gScore[u];
                float hu = g.Cell(u).h;
                int k = g.Neighbors(u, nb);
                for (int j = 0; j < k; j++)
                {
                    int v = nb[j];
                    if (closed[v] != 0) continue;
                    if (CornerBlocked(g, u, v, m)) continue;
                    var cv = g.Cell(v);
                    g.Center(v, out float vx, out float vz);
                    float d = Dist(ux, uz, vx, vz);
                    float c = m.Cost(cv, d, cv.h - hu);
                    if (float.IsInfinity(c)) continue;
                    float ng = gu + c;
                    if (ng >= gScore[v]) continue;
                    if (float.IsPositiveInfinity(gScore[v])) touched.Add(v);
                    gScore[v] = ng;
                    parent[v] = u;
                    Push(ng + Dist(vx, vz, gx, gz) * hk, v);
                }
            }

            if (res.status == NavStatus.Ok)
            {
                res.cost = gScore[goal];
                for (int n = goal; n >= 0; n = parent[n]) res.nodes.Add(n);
                res.nodes.Reverse();
            }
            res.ms = sw.Elapsed.TotalMilliseconds;
            return res;
        }

        /// <summary>Paso en diagonal dentro de un nivel con una de las dos celdas de la esquina bloqueada
        /// (no se permite cortar la esquina de un obstáculo).</summary>
        public static bool CornerBlocked(NavGrid g, int u, int v, NavCostModel m)
        {
            bool l1 = g.IsL1(u);
            if (l1 != g.IsL1(v)) return false;
            var lv = l1 ? g.L1 : g.L0;
            int off = l1 ? g.N0 : 0, iu = u - off, iv = v - off;
            int ux = iu % lv.W, uy = iu / lv.W, vx = iv % lv.W, vy = iv / lv.W;
            if (ux == vx || uy == vy) return false;
            return !m.Passable(g.Cell(off + uy * lv.W + vx)) || !m.Passable(g.Cell(off + vy * lv.W + ux));
        }

        /// <summary>Coste acumulado de A* hasta un nodo de la última búsqueda (∞ si no se alcanzó).</summary>
        public float LastG(int n) => gScore != null ? gScore[n] : float.PositiveInfinity;

        /// <summary>Celda transitable más cercana (en planta) dentro de maxMeters; el mismo nodo si ya lo es.</summary>
        public int NearestPassable(int n, NavCostModel m, float maxMeters, out float meters)
        {
            meters = 0f;
            if (n < 0) return -1;
            if (m.Passable(g.Cell(n))) return n;
            g.Center(n, out float x0, out float z0);
            var seen = new HashSet<int> { n };
            var q = new Queue<int>();
            q.Enqueue(n);
            int best = -1;
            float bd = float.MaxValue, lim = maxMeters * maxMeters;
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                int k = g.Neighbors(u, nb);
                for (int j = 0; j < k; j++)
                {
                    int v = nb[j];
                    if (!seen.Add(v)) continue;
                    g.Center(v, out float x, out float z);
                    float d2 = (x - x0) * (x - x0) + (z - z0) * (z - z0);
                    if (d2 > lim) continue;
                    if (m.Passable(g.Cell(v))) { if (d2 < bd) { bd = d2; best = v; } }
                    else q.Enqueue(v);
                }
            }
            meters = best >= 0 ? (float)Math.Sqrt(bd) : 0f;
            return best;
        }

        static float Dist(float ax, float az, float bx, float bz)
        {
            float dx = bx - ax, dz = bz - az;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        void Push(float f, int n)
        {
            if (heapN == heap.Length) Array.Resize(ref heap, heap.Length * 2);
            int i = heapN++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (heap[p].f <= f) break;
                heap[i] = heap[p];
                i = p;
            }
            heap[i] = new HeapItem { f = f, n = n };
        }

        HeapItem Pop()
        {
            var top = heap[0];
            var last = heap[--heapN];
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1;
                if (l >= heapN) break;
                int r = l + 1, c = r < heapN && heap[r].f < heap[l].f ? r : l;
                if (heap[c].f >= last.f) break;
                heap[i] = heap[c];
                i = c;
            }
            if (heapN > 0) heap[i] = last;
            return top;
        }
    }
}
