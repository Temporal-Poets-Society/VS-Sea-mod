using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Sea
{
    /// <summary>
    /// (0.6.0) The coast near what's watched, from the world's blocks, the same way on the server and every client:
    /// the depth under each column at sea level (from chunk columns scanned once), solved in world-anchored
    /// <see cref="CoastMath.Tile"/>-block tiles (each with <see cref="CoastMath.Margin"/> blocks around it), so the
    /// surface at a place doesn't depend on who asked first. Work is done in small steps on the main thread
    /// (<see cref="Work"/>); lookups from any thread only read finished tiles. Not yet followed: blocks changed after a
    /// tile was solved (a dug channel, a built pier) — the tile is solved again only once it's forgotten.
    /// </summary>
    public sealed class CoastField
    {
        const int Chunk = 32;
        const int MaxTiles = 512, MaxColumns = 1024;

        readonly ICoreAPI api;
        readonly object gate = new();
        readonly Dictionary<(long, long), Entry> tiles = new();
        readonly Dictionary<(long, long), Columns> columns = new();
        readonly HashSet<(long, long)> wanted = new();
        long uses;

        sealed class Entry { public float[] Data; public long Used; }
        sealed class Columns { public float[] Depth; public long Used; }

        /// <summary>Goes up whenever a tile is added or forgotten (the client's map is redrawn then).</summary>
        public int Version { get; private set; }

        public CoastField(ICoreAPI api) { this.api = api; }

        /// <summary>Tiles solved so far, chunk columns scanned (for `.sea coast`).</summary>
        public (int Tiles, int Columns, int Wanted) Counts { get { lock (gate) return (tiles.Count, columns.Count, wanted.Count); } }

        public static long TileOf(double block) => (long)Math.Floor(block / CoastMath.Tile);

        /// <summary>A world position's coast numbers (false: no solved tile under it yet; asked for).</summary>
        public bool Lookup(double x, double z, out CoastSample sample)
        {
            lock (gate)
            {
                bool any = false;
                sample = CoastMath.Bilinear(x, z, (i, j, into) =>
                {
                    bool got = CellLocked(i, j, into);
                    any |= got;
                    return got;
                }, scratch);
                return any;
            }
        }

        readonly float[] scratch = new float[4];

        /// <summary>Column (i, j)'s four floats, if its tile is solved (else it's asked for).</summary>
        public bool Cell(long i, long j, float[] into) { lock (gate) return CellLocked(i, j, into); }

        bool CellLocked(long i, long j, float[] into)
        {
            var key = (Floor(i, CoastMath.Tile), Floor(j, CoastMath.Tile));
            if (!tiles.TryGetValue(key, out var e)) { wanted.Add(key); return false; }
            e.Used = ++uses;
            int o = ((int)(j - key.Item2 * CoastMath.Tile) * CoastMath.Tile + (int)(i - key.Item1 * CoastMath.Tile)) * 4;
            into[0] = e.Data[o]; into[1] = e.Data[o + 1]; into[2] = e.Data[o + 2]; into[3] = e.Data[o + 3];
            return true;
        }

        /// <summary>A tile's data (Tile² × 4 floats), if solved; asks for it otherwise.</summary>
        public float[] TileData(long tx, long tz)
        {
            lock (gate)
            {
                if (tiles.TryGetValue((tx, tz), out var e)) { e.Used = ++uses; return e.Data; }
                wanted.Add((tx, tz));
                return null;
            }
        }

        static long Floor(long v, int d) => v >= 0 ? v / d : -((-v + d - 1) / d);

        /// <summary>Main thread: scan at most `scans` chunk columns and solve at most `solves` tiles of those asked for.</summary>
        public void Work(int scans, int solves)
        {
            (long, long)[] asked;
            lock (gate)
            {
                if (wanted.Count == 0) return;
                asked = new (long, long)[wanted.Count];
                wanted.CopyTo(asked);
            }
            var ba = api.World.BlockAccessor;
            int sea = api.World.SeaLevel;
            int r = (CoastMath.Margin + Chunk - 1) / Chunk;
            foreach (var key in asked)
            {
                if (solves <= 0) break;
                // the chunk columns under its window (tile = chunk size)
                bool ready = true;
                for (long cz = key.Item2 - r; cz <= key.Item2 + r && ready; cz++)
                    for (long cx = key.Item1 - r; cx <= key.Item1 + r && ready; cx++)
                    {
                        bool have;
                        lock (gate) have = columns.ContainsKey((cx, cz));
                        if (have) continue;
                        if (scans <= 0) { ready = false; break; }
                        var depth = Scan(ba, cx, cz, sea);
                        if (depth == null) { ready = false; break; }   // not loaded: try again later
                        scans--;
                        lock (gate)
                        {
                            columns[(cx, cz)] = new Columns { Depth = depth, Used = ++uses };
                            if (columns.Count > MaxColumns) Forget(columns);
                        }
                    }
                if (!ready) continue;
                Solve(key);
                solves--;
            }
        }

        void Solve((long, long) key)
        {
            int w = CoastMath.Window, m = CoastMath.Margin;
            var depth = new float[w * w];
            long x0 = key.Item1 * CoastMath.Tile - m, z0 = key.Item2 * CoastMath.Tile - m;
            lock (gate)
            {
                for (int z = 0; z < w; z++)
                    for (int x = 0; x < w; x++)
                    {
                        long bx = x0 + x, bz = z0 + z;
                        if (!columns.TryGetValue((Floor(bx, Chunk), Floor(bz, Chunk)), out var c)) return;   // (forgotten meanwhile: next time)
                        c.Used = ++uses;
                        depth[z * w + x] = c.Depth[(int)(bz - Floor(bz, Chunk) * Chunk) * Chunk + (int)(bx - Floor(bx, Chunk) * Chunk)];
                    }
            }
            var time = new float[w * w];
            var dist = new float[w * w];
            CoastMath.Solve(depth, w, time, dist);
            var data = CoastMath.TileOf(depth, time, dist, w, m, CoastMath.Tile);
            lock (gate)
            {
                tiles[key] = new Entry { Data = data, Used = ++uses };
                wanted.Remove(key);
                if (tiles.Count > MaxTiles) Forget(tiles);
                Version++;
            }
        }

        // the least recently used quarter
        void Forget<T>(Dictionary<(long, long), T> d) where T : class
        {
            var order = new List<((long, long) Key, long Used)>(d.Count);
            foreach (var kv in d) order.Add((kv.Key, kv.Value is Entry e ? e.Used : ((Columns)(object)kv.Value).Used));
            order.Sort((a, b) => a.Used.CompareTo(b.Used));
            for (int i = 0; i < order.Count / 4; i++) d.Remove(order[i].Key);
            Version++;
        }

        /// <summary>
        /// A chunk column's depths (32 × 32, z rows): from the water at sea level down to the first block solid on top,
        /// in blocks below the water's surface (0: land, ice or no water at sea level; NaN: deeper than
        /// <see cref="CoastMath.MaxDepth"/>). Null if it isn't loaded.
        /// </summary>
        static float[] Scan(IBlockAccessor ba, long cx, long cz, int sea)
        {
            int top = sea - 1, bottom = Math.Max(0, top - (int)CoastMath.MaxDepth - 1);
            if (cx < 0 || cz < 0 || cx > int.MaxValue / Chunk || cz > int.MaxValue / Chunk) return null;
            int bx = (int)cx * Chunk, bz = (int)cz * Chunk;
            if (ba.GetChunkAtBlockPos(bx, top, bz) == null || ba.GetChunkAtBlockPos(bx, bottom, bz) == null) return null;
            var depth = new float[Chunk * Chunk];
            int up = BlockFacing.indexUP;
            var pos = new BlockPos(0, 0, 0, 0);   // (dimension 0: the world itself)
            for (int z = 0; z < Chunk; z++)
                for (int x = 0; x < Chunk; x++)
                {
                    int wx = bx + x, wz = bz + z;
                    var fluid = ba.GetBlock(pos.Set(wx, top, wz), BlockLayersAccess.Fluid);
                    float h = 0;
                    if (fluid != null && fluid.IsLiquid() && fluid.LiquidCode != "lava")
                    {
                        h = float.NaN;
                        for (int y = top; y >= bottom; y--)
                        {
                            var b = ba.GetBlock(pos.Set(wx, y, wz), BlockLayersAccess.MostSolid);
                            if (b != null && b.Id != 0 && b.SideSolid[up])
                            {
                                // the water's surface is 1/8 under the block's top; the floor is this block's top
                                h = Math.Max(0, (top + 0.875f) - (y + 1));
                                break;
                            }
                        }
                    }
                    depth[z * Chunk + x] = h;
                }
            return depth;
        }
    }
}
