using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Gale.Navigation
{
    /// <summary>Un nivel de la malla: rejilla regular en metros reales (X este, Z norte), fila 0 = sur.</summary>
    public sealed class NavLevel
    {
        public int W, H;
        public float X0, Z0;          // esquina SO de la celda (0,0), m
        public float CellX, CellZ;    // m
        public float[] Height;        // m; NaN = sin relieve
        public byte[] Slope;          // pendiente máxima en medios grados; 255 = sin dato
        public byte[] Flags;          // bit 0: terreno probado (ruta de Curiosity cerca)

        public const byte FlagProven = 1;

        public int Count => W * H;

        public NavLevel(int w, int h, float x0, float z0, float cx, float cz)
        {
            W = w; H = h; X0 = x0; Z0 = z0; CellX = cx; CellZ = cz;
            Height = new float[w * h];
            Slope = new byte[w * h];
            Flags = new byte[w * h];
        }

        public float CenterX(int x) => X0 + (x + 0.5f) * CellX;
        public float CenterZ(int y) => Z0 + (y + 0.5f) * CellZ;

        public bool TryCell(float x, float z, out int cx, out int cy)
        {
            cx = (int)Math.Floor((x - X0) / CellX);
            cy = (int)Math.Floor((z - Z0) / CellZ);
            return cx >= 0 && cy >= 0 && cx < W && cy < H;
        }

        /// <summary>Pendiente máxima (gradiente) por diferencias centrales; de un lado en los bordes y junto a huecos.</summary>
        public void ComputeSlopes()
        {
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    float h = Height[i];
                    if (float.IsNaN(h)) { Slope[i] = 255; continue; }
                    float gx = Grad(x, y, 1, 0, CellX), gz = Grad(x, y, 0, 1, CellZ);
                    double deg = Math.Atan(Math.Sqrt(gx * gx + gz * gz)) * 180.0 / Math.PI;
                    Slope[i] = (byte)Math.Min(254, (int)Math.Round(deg * 2.0));
                }
        }

        float Grad(int x, int y, int dx, int dy, float cell)
        {
            float c = Height[y * W + x];
            float a = Get(x - dx, y - dy), b = Get(x + dx, y + dy);
            bool ha = !float.IsNaN(a), hb = !float.IsNaN(b);
            if (ha && hb) return (b - a) / (2f * cell);
            if (hb) return (b - c) / cell;
            if (ha) return (c - a) / cell;
            return 0f;
        }

        public float Get(int x, int y) => x < 0 || y < 0 || x >= W || y >= H ? float.NaN : Height[y * W + x];

        /// <summary>Cota bilineal entre centros de celda (NaN si falta alguna de las cuatro, salvo la más cercana).</summary>
        public float Bilinear(float x, float z)
        {
            float fx = (x - X0) / CellX - 0.5f, fz = (z - Z0) / CellZ - 0.5f;
            int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
            float tx = fx - ix, tz = fz - iz;
            float a = Get(ix, iz), b = Get(ix + 1, iz), c = Get(ix, iz + 1), d = Get(ix + 1, iz + 1);
            if (float.IsNaN(a) || float.IsNaN(b) || float.IsNaN(c) || float.IsNaN(d))
                return Get(tx < 0.5f ? ix : ix + 1, tz < 0.5f ? iz : iz + 1);
            return (a * (1 - tx) + b * tx) * (1 - tz) + (c * (1 - tx) + d * tx) * tz;
        }
    }

    /// <summary>Datos de una celda para el coste.</summary>
    public struct NavCell
    {
        public float h;          // m, NaN = sin relieve
        public float slopeDeg;   // pendiente máxima; NaN = sin dato
        public byte soil, geo, thermo;   // clases (0 = sin dato) e índice termofísico 0..255
        public bool proven;
        public byte level;       // 0 regional, 1 detalle
    }

    // Malla de dos niveles en un solo grafo.
    //  - L0: regional, la rejilla de los mapas geológicos (cada celda = un píxel de geo_dataset.json).
    //  - L1: detalle, un bloque de L0 (SubX0.., SubY0.., SubW × SubH celdas) subdividido Sub × Sub.
    //    Las celdas L0 cubiertas por L1 no son nodos; los bordes de L1 enlazan con las L0 contiguas.
    // Nodos: 0..N0-1 = L0 (y·W0 + x); N0.. = L1 (N0 + y·W1 + x).
    public sealed class NavGrid
    {
        public NavLevel L0, L1;
        public int SubX0, SubY0, SubW, SubH, Sub = 1;
        public byte[] Soil, Geo, Thermo;   // raster L0 (W0·H0) o null
        public string Fingerprint = "";

        public int N0 => L0.Count;
        public int NodeCount => N0 + (L1 != null ? L1.Count : 0);
        public bool HasDetail => L1 != null;

        public bool Covered(int x, int y) =>
            L1 != null && x >= SubX0 && y >= SubY0 && x < SubX0 + SubW && y < SubY0 + SubH;

        /// <summary>Crea L1 sobre el bloque de L0 indicado (Height a NaN para rellenar).</summary>
        public void CreateDetail(int x0, int y0, int w, int h, int sub)
        {
            SubX0 = x0; SubY0 = y0; SubW = w; SubH = h; Sub = sub;
            L1 = new NavLevel(w * sub, h * sub, L0.X0 + x0 * L0.CellX, L0.Z0 + y0 * L0.CellZ, L0.CellX / sub, L0.CellZ / sub);
        }

        public bool IsL1(int n) => n >= N0;

        /// <summary>Nodo bajo un punto (m); L1 si cae dentro del detalle. -1 fuera de la malla.</summary>
        public int NodeAt(float x, float z)
        {
            if (L1 != null && L1.TryCell(x, z, out int x1, out int y1)) return N0 + y1 * L1.W + x1;
            if (L0.TryCell(x, z, out int x0, out int y0)) return y0 * L0.W + x0;
            return -1;
        }

        public void Center(int n, out float x, out float z)
        {
            if (n >= N0) { int i = n - N0; x = L1.CenterX(i % L1.W); z = L1.CenterZ(i / L1.W); }
            else { x = L0.CenterX(n % L0.W); z = L0.CenterZ(n / L0.W); }
        }

        /// <summary>Índice del raster L0 (geología, suelo) de un nodo.</summary>
        public int RasterIndex(int n)
        {
            if (n < N0) return n;
            int i = n - N0, x1 = i % L1.W, y1 = i / L1.W;
            return (SubY0 + y1 / Sub) * L0.W + SubX0 + x1 / Sub;
        }

        public NavCell Cell(int n)
        {
            NavLevel lv = n >= N0 ? L1 : L0;
            int i = n >= N0 ? n - N0 : n;
            int r = RasterIndex(n);
            byte s = lv.Slope[i];
            return new NavCell
            {
                h = lv.Height[i],
                slopeDeg = s == 255 ? float.NaN : s * 0.5f,
                soil = Soil != null ? Soil[r] : (byte)0,
                geo = Geo != null ? Geo[r] : (byte)0,
                thermo = Thermo != null ? Thermo[r] : (byte)128,
                proven = (lv.Flags[i] & NavLevel.FlagProven) != 0,
                level = (byte)(n >= N0 ? 1 : 0),
            };
        }

        /// <summary>Cota bajo un punto (detalle si lo hay). NaN sin relieve.</summary>
        public float HeightAt(float x, float z)
        {
            if (L1 != null && L1.TryCell(x, z, out _, out _))
            {
                float h = L1.Bilinear(x, z);
                if (!float.IsNaN(h)) return h;
            }
            return L0.Bilinear(x, z);
        }

        public void ComputeSlopes()
        {
            L0.ComputeSlopes();
            L1?.ComputeSlopes();
        }

        /// <summary>Vecinos de un nodo (máx. 8 + Sub + 2). Devuelve cuántos escribió.</summary>
        public int Neighbors(int n, int[] buf)
        {
            int k = 0;
            if (n >= N0)
            {
                int i = n - N0, w = L1.W, h = L1.H, x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h) { buf[k++] = N0 + ny * w + nx; continue; }
                        // Fuera del detalle: la celda L0 que contiene esa posición.
                        int lx = SubX0 + FloorDiv(nx, Sub), ly = SubY0 + FloorDiv(ny, Sub);
                        if (lx < 0 || ly < 0 || lx >= L0.W || ly >= L0.H) continue;
                        int ln = ly * L0.W + lx;
                        if (!Contains(buf, k, ln)) buf[k++] = ln;
                    }
            }
            else
            {
                int w = L0.W, h = L0.H, x = n % w, y = n / w;
                bool nearDetail = false;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        if (Covered(nx, ny)) { nearDetail = true; continue; }
                        buf[k++] = ny * w + nx;
                    }
                if (nearDetail)
                {
                    // Celdas L1 cuyo vecino de fuera cae en esta celda L0 (simétrico con el caso anterior).
                    int bx0 = Math.Max(0, (x - SubX0) * Sub - 1), bx1 = Math.Min(L1.W - 1, (x - SubX0) * Sub + Sub);
                    int by0 = Math.Max(0, (y - SubY0) * Sub - 1), by1 = Math.Min(L1.H - 1, (y - SubY0) * Sub + Sub);
                    for (int yy = by0; yy <= by1; yy++)
                        for (int xx = bx0; xx <= bx1; xx++)
                            buf[k++] = N0 + yy * L1.W + xx;
                }
            }
            return k;
        }

        public int MaxNeighbors => 8 + (Sub + 2) * 3;

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        static bool Contains(int[] buf, int k, int v)
        {
            for (int i = 0; i < k; i++) if (buf[i] == v) return true;
            return false;
        }

        // ---------- Fichero (.bytes, GZip) ----------
        // Cabecera "ANAV" v1, huella, L0 y (opcional) L1 con su colocación. Cota en 16 bit: h = min + v·escala,
        // 0xFFFF = sin relieve. Las pendientes, el suelo y la geología se derivan o se leen al cargar.

        const uint Magic = 0x5641_4E41;   // "ANAV"
        const int Version = 1;

        public void Write(Stream s)
        {
            using var gz = new GZipStream(s, CompressionLevel.Optimal, true);
            using var w = new BinaryWriter(gz, Encoding.UTF8, true);
            w.Write(Magic); w.Write(Version); w.Write(Fingerprint ?? "");
            WriteLevel(w, L0);
            w.Write(L1 != null);
            if (L1 != null)
            {
                w.Write(SubX0); w.Write(SubY0); w.Write(SubW); w.Write(SubH); w.Write(Sub);
                WriteLevel(w, L1);
            }
        }

        public static NavGrid Read(Stream s)
        {
            using var gz = new GZipStream(s, CompressionMode.Decompress, true);
            using var r = new BinaryReader(new BufferedStream(gz, 1 << 16), Encoding.UTF8, true);
            if (r.ReadUInt32() != Magic) throw new InvalidDataException("no es una malla de navegación ANAV");
            int v = r.ReadInt32();
            if (v != Version) throw new InvalidDataException($"versión de malla {v}, se esperaba {Version}");
            var g = new NavGrid { Fingerprint = r.ReadString() };
            g.L0 = ReadLevel(r);
            if (r.ReadBoolean())
            {
                g.SubX0 = r.ReadInt32(); g.SubY0 = r.ReadInt32(); g.SubW = r.ReadInt32(); g.SubH = r.ReadInt32(); g.Sub = r.ReadInt32();
                g.L1 = ReadLevel(r);
            }
            return g;
        }

        static void WriteLevel(BinaryWriter w, NavLevel lv)
        {
            w.Write(lv.W); w.Write(lv.H); w.Write(lv.X0); w.Write(lv.Z0); w.Write(lv.CellX); w.Write(lv.CellZ);
            float min = float.MaxValue, max = float.MinValue;
            foreach (float h in lv.Height)
                if (!float.IsNaN(h)) { if (h < min) min = h; if (h > max) max = h; }
            if (min > max) { min = 0; max = 1; }
            float scale = Math.Max(1e-4f, (max - min) / 65534f);
            w.Write(min); w.Write(scale);
            var buf = new byte[lv.Count * 2];
            for (int i = 0; i < lv.Count; i++)
            {
                float h = lv.Height[i];
                ushort q = float.IsNaN(h) ? (ushort)0xFFFF : (ushort)Math.Min(65534, Math.Round((h - min) / scale));
                buf[2 * i] = (byte)q; buf[2 * i + 1] = (byte)(q >> 8);
            }
            w.Write(buf);
        }

        static NavLevel ReadLevel(BinaryReader r)
        {
            int w = r.ReadInt32(), h = r.ReadInt32();
            var lv = new NavLevel(w, h, r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float min = r.ReadSingle(), scale = r.ReadSingle();
            byte[] buf = r.ReadBytes(w * h * 2);
            if (buf.Length != w * h * 2) throw new EndOfStreamException("malla de navegación truncada");
            for (int i = 0; i < lv.Count; i++)
            {
                int q = buf[2 * i] | (buf[2 * i + 1] << 8);
                lv.Height[i] = q == 0xFFFF ? float.NaN : min + q * scale;
            }
            return lv;
        }
    }
}
