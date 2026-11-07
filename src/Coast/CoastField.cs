using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Sea
{
    /// <summary>
    /// (0.6.0) The coast near what's watched, from the world's blocks, the same way on the server and every client:
    /// the depth under each column at sea level (and the height of the land beside it), from chunk columns scanned on
    /// the main thread, solved in world-anchored <see cref="CoastMath.Tile"/>-block tiles (each with
    /// <see cref="CoastMath.Margin"/> blocks around it) on a background task, so the surface at a place doesn't depend on
    /// who asked first. Lookups from any thread only read finished tiles. Blocks changed under the water or at the
    /// shore (a dug channel, a pier, a sea wall) mark their chunk column; it is scanned again and, if its depths
    /// changed, the tiles that see it are solved again (the old ones are kept until then).
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
        readonly HashSet<(long, long)> dirty = new();
        long uses;
        Task solving;
        int version;

        // timings (ms) for `.sea coast`
        readonly Stopwatch clock = new();
        double scanMs, solveMs, solveMax;
        int scanned, solved, rescans, resolves;

        sealed class Entry { public float[] Data; public long Used; }
        sealed class Columns { public float[] Depth; public long Used; }

        /// <summary>Goes up whenever a tile is added, redone or forgotten (the client's map is redrawn then).</summary>
        public int Version => Volatile.Read(ref version);

        public CoastField(ICoreAPI api)
        {
            this.api = api;
            api.Event.ChunkDirty += OnChunkDirty;
        }

        public void Dispose() => api.Event.ChunkDirty -= OnChunkDirty;

        /// <summary>Tiles solved so far, chunk columns scanned, tiles asked for (for `.sea coast`).</summary>
        public (int Tiles, int Columns, int Wanted) Counts { get { lock (gate) return (tiles.Count, columns.Count, wanted.Count); } }

        /// <summary>
        /// What the coast map has cost so far (for `.sea coast`): chunk columns scanned and their mean time on the main
        /// thread, tiles solved and their mean and longest time on the background task, and how many columns were scanned
        /// again and tiles solved again because blocks changed.
        /// </summary>
        public (int Scans, double ScanMs, int Solves, double SolveMs, double SolveMaxMs, int Rescans, int Resolves) Costs
        {
            get
            {
                lock (gate)
                    return (scanned, scanned > 0 ? scanMs / scanned : 0, solved, solved > 0 ? solveMs / solved : 0, solveMax, rescans, resolves);
            }
        }

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

        readonly float[] scratch = new float[CoastMath.Stride];

        /// <summary>Column (i, j)'s <see cref="CoastMath.Stride"/> floats, if its tile is solved (else it's asked for).</summary>
        public bool Cell(long i, long j, float[] into) { lock (gate) return CellLocked(i, j, into); }

        bool CellLocked(long i, long j, float[] into)
        {
            var key = (Floor(i, CoastMath.Tile), Floor(j, CoastMath.Tile));
            if (!tiles.TryGetValue(key, out var e)) { wanted.Add(key); return false; }
            e.Used = ++uses;
            int o = ((int)(j - key.Item2 * CoastMath.Tile) * CoastMath.Tile + (int)(i - key.Item1 * CoastMath.Tile)) * CoastMath.Stride;
            Array.Copy(e.Data, o, into, 0, CoastMath.Stride);
            return true;
        }

        /// <summary>A tile's data (Tile² × <see cref="CoastMath.Stride"/> floats), if solved; asks for it otherwise.</summary>
        public float[] TileData(long tx, long tz)
        {
            lock (gate)
            {
                if (tiles.TryGetValue((tx, tz), out var e)) { e.Used = ++uses; return e.Data; }
                wanted.Add((tx, tz));
                return null;
            }
        }

        /// <summary>
        /// (Server) Keep the tiles within `radius` blocks of (x, z) solved, or ask for them: so a boat near a player gets the
        /// coast on the server as soon as the client, whose water map asks for the tiles in view, does (contracts, R5).
        /// </summary>
        public void Ask(double x, double z, double radius)
        {
            long x0 = TileOf(x - radius), x1 = TileOf(x + radius), z0 = TileOf(z - radius), z1 = TileOf(z + radius);
            lock (gate)
                for (long tz = z0; tz <= z1; tz++)
                    for (long tx = x0; tx <= x1; tx++)
                    {
                        if (tiles.TryGetValue((tx, tz), out var e)) e.Used = ++uses;
                        else wanted.Add((tx, tz));
                    }
        }

        static long Floor(long v, int d) => v >= 0 ? v / d : -((-v + d - 1) / d);

        // any thread (the client marks chunks dirty from its relight thread too)
        void OnChunkDirty(Vec3i chunk, IWorldChunk _, EnumChunkDirtyReason reason)
        {
            if (reason != EnumChunkDirtyReason.MarkedDirty) return;
            int sea = api.World.SeaLevel, y0 = (sea - (int)CoastMath.MaxDepth - 3) / Chunk, y1 = (sea + 2) / Chunk;
            if (chunk.Y < y0 || chunk.Y > y1) return;   // (other dimensions are far above)
            lock (gate) if (columns.ContainsKey((chunk.X, chunk.Z))) dirty.Add((chunk.X, chunk.Z));
        }

        /// <summary>
        /// Main thread: scan again at most `scans` chunk columns marked dirty (and ask for the tiles that see one whose
        /// depths changed), scan at most `scans` more for tiles asked for, and start solving one of those tiles in the
        /// background when none is being solved.
        /// </summary>
        public void Work(int scans)
        {
            var ba = api.World.BlockAccessor;
            int sea = api.World.SeaLevel;
            Rescan(ba, sea, ref scans);
            if (solving != null && !solving.IsCompleted) return;
            (long, long)[] asked;
            lock (gate)
            {
                if (wanted.Count == 0) return;
                asked = new (long, long)[wanted.Count];
                wanted.CopyTo(asked);
            }
            int r = (CoastMath.Margin + Chunk - 1) / Chunk;
            foreach (var key in asked)
            {
                // the chunk columns under its window (tile = chunk size)
                bool ready = true;
                for (long cz = key.Item2 - r; cz <= key.Item2 + r && ready; cz++)
                    for (long cx = key.Item1 - r; cx <= key.Item1 + r && ready; cx++)
                    {
                        bool have;
                        lock (gate) have = columns.ContainsKey((cx, cz));
                        if (have) continue;
                        if (scans <= 0) { ready = false; break; }
                        var depth = TimedScan(ba, cx, cz, sea);
                        if (depth == null) { ready = false; break; }   // not loaded: try again later
                        scans--;
                        lock (gate)
                        {
                            columns[(cx, cz)] = new Columns { Depth = depth, Used = ++uses };
                            if (columns.Count > MaxColumns) Forget(columns);
                        }
                    }
                if (!ready) continue;
                var window = Window(key);
                if (window == null) continue;
                solving = Task.Run(() => Solve(key, window));
                break;
            }
        }

        void Rescan(IBlockAccessor ba, int sea, ref int scans)
        {
            (long, long)[] marked;
            lock (gate)
            {
                if (dirty.Count == 0) return;
                marked = new (long, long)[Math.Min(dirty.Count, Math.Max(scans, 0))];
                int n = 0;
                foreach (var k in dirty) { if (n == marked.Length) break; marked[n++] = k; }
                foreach (var k in marked) dirty.Remove(k);
            }
            foreach (var key in marked)
            {
                scans--;
                var depth = TimedScan(ba, key.Item1, key.Item2, sea);
                lock (gate)
                {
                    rescans++;
                    if (!columns.TryGetValue(key, out var c)) continue;
                    if (depth == null) { columns.Remove(key); continue; }   // (unloaded: scanned afresh when asked for)
                    if (Same(c.Depth, depth)) continue;
                    c.Depth = depth;
                    // every tile whose window covers this chunk column is solved again (kept as it is until then)
                    int r = (CoastMath.Margin + Chunk - 1) / Chunk;
                    for (long tz = key.Item2 - r; tz <= key.Item2 + r; tz++)
                        for (long tx = key.Item1 - r; tx <= key.Item1 + r; tx++)
                            if (tiles.ContainsKey((tx, tz)) && wanted.Add((tx, tz))) resolves++;
                }
            }
        }

        static bool Same(float[] a, float[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i] && !(float.IsNaN(a[i]) && float.IsNaN(b[i]))) return false;
            return true;
        }

        float[] TimedScan(IBlockAccessor ba, long cx, long cz, int sea)
        {
            clock.Restart();
            var depth = Scan(ba, cx, cz, sea);
            double ms = clock.Elapsed.TotalMilliseconds;
            if (depth != null) lock (gate) { scanned++; scanMs += ms; }
            return depth;
        }

        // the depths under a tile's window from the scanned columns (null: one was forgotten meanwhile; next time)
        float[] Window((long, long) key)
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
                        if (!columns.TryGetValue((Floor(bx, Chunk), Floor(bz, Chunk)), out var c)) return null;
                        c.Used = ++uses;
                        depth[z * w + x] = c.Depth[(int)(bz - Floor(bz, Chunk) * Chunk) * Chunk + (int)(bx - Floor(bx, Chunk) * Chunk)];
                    }
            }
            return depth;
        }

        // background task
        void Solve((long, long) key, float[] depth)
        {
            try
            {
                var watch = Stopwatch.StartNew();
                int w = CoastMath.Window, m = CoastMath.Margin;
                var time = new float[w * w];
                var dist = new float[w * w];
                CoastMath.Solve(depth, w, time, dist);
                var data = CoastMath.TileOf(depth, time, dist, w, m, CoastMath.Tile, key.Item1 * CoastMath.Tile - m, key.Item2 * CoastMath.Tile - m);
                double ms = watch.Elapsed.TotalMilliseconds;
                lock (gate)
                {
                    tiles[key] = new Entry { Data = data, Used = ++uses };
                    wanted.Remove(key);
                    if (tiles.Count > MaxTiles) Forget(tiles);
                    solved++; solveMs += ms; solveMax = Math.Max(solveMax, ms);
                    Interlocked.Increment(ref version);
                }
            }
            catch (Exception e)
            {
                api.Logger.Error("[sea] coast tile {0},{1} failed: {2}", key.Item1, key.Item2, e);
                lock (gate) wanted.Remove(key);
            }
        }

        // the least recently used quarter
        void Forget<T>(Dictionary<(long, long), T> d) where T : class
        {
            var order = new List<((long, long) Key, long Used)>(d.Count);
            foreach (var kv in d) order.Add((kv.Key, kv.Value is Entry e ? e.Used : ((Columns)(object)kv.Value).Used));
            order.Sort((a, b) => a.Used.CompareTo(b.Used));
            for (int i = 0; i < order.Count / 4; i++) d.Remove(order[i].Key);
            Interlocked.Increment(ref version);
        }

        /// <summary>
        /// A chunk column's depths (32 × 32, z rows): from the water at sea level down to the first block solid on top,
        /// in blocks below the water's surface (NaN: deeper than <see cref="CoastMath.MaxDepth"/>). Land is minus the
        /// height of its top over the water's surface: 0.125 (top block at the water's level: a beach), 1.125 (a block
        /// higher), 2.125 (higher still, or ice). Null if it isn't loaded.
        /// </summary>
        static float[] Scan(IBlockAccessor ba, long cx, long cz, int sea)
        {
            int top = sea - 1, bottom = Math.Max(0, top - (int)CoastMath.MaxDepth - 1);
            if (cx < 0 || cz < 0 || cx > int.MaxValue / Chunk || cz > int.MaxValue / Chunk) return null;
            int bx = (int)cx * Chunk, bz = (int)cz * Chunk;
            if (ba.GetChunkAtBlockPos(bx, top, bz) == null || ba.GetChunkAtBlockPos(bx, bottom, bz) == null
                || ba.GetChunkAtBlockPos(bx, sea + 1, bz) == null) return null;
            var depth = new float[Chunk * Chunk];
            int up = BlockFacing.indexUP;
            var pos = new BlockPos(0, 0, 0, 0);   // (dimension 0: the world itself)
            for (int z = 0; z < Chunk; z++)
                for (int x = 0; x < Chunk; x++)
                {
                    int wx = bx + x, wz = bz + z;
                    var fluid = ba.GetBlock(pos.Set(wx, top, wz), BlockLayersAccess.Fluid);
                    float h;
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
                    else if (fluid != null && fluid.Id != 0) h = -2.125f;   // ice, or something frozen or poured over it
                    else if (Solid(ba, pos.Set(wx, sea, wz), up)) h = Solid(ba, pos.Set(wx, sea + 1, wz), up) ? -2.125f : -1.125f;
                    else h = -0.125f;
                    depth[z * Chunk + x] = h;
                }
            return depth;
        }

        static bool Solid(IBlockAccessor ba, BlockPos pos, int up)
        {
            var b = ba.GetBlock(pos, BlockLayersAccess.MostSolid);
            return b != null && b.Id != 0 && b.SideSolid[up];
        }
    }
}
