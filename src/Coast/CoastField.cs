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
        // (SE-0056) for one player; the server raises them with the players it keeps the coast for (Watchers)
        const int MinTiles = 512, MinColumns = 1024;
        int MaxTiles = MinTiles, MaxColumns = MinColumns;
        int uniformTiles;

        /// <summary>(SE-0056) How many places the coast is kept around (the server's players near sea level): the caches hold
        /// their 81 tiles and 169 chunk columns each with room to spare, instead of dropping tiles asked for again in a second.</summary>
        public int Watchers
        {
            set
            {
                int n = Math.Max(1, value);
                lock (gate) { MaxTiles = Math.Max(MinTiles, 128 * n); MaxColumns = Math.Max(MinColumns, 256 * n); }
            }
        }

        /// <summary>(SE-0057) Tiles made without a solve (all deep or all land), for `.sea coast`.</summary>
        public int UniformTiles => Volatile.Read(ref uniformTiles);

        readonly ICoreAPI api;
        readonly object gate = new();
        readonly Dictionary<(long, long), Entry> tiles = new();
        readonly Dictionary<(long, long), Columns> columns = new();
        // (SE-0040) tiles asked for, with when they were last asked for: one not asked for in StaleMs (where the camera
        // passed while travelling, chunks that never load) is dropped, and the most recently asked are solved first
        readonly Dictionary<(long, long), long> wanted = new();
        const long StaleMs = 10000;
        // (SE-0045) a tile whose solve threw, and when: it is not tried again for a minute (it was retried at once, each time logged)
        readonly Dictionary<(long, long), long> failedAt = new();
        const long RetryFailedMs = 60000;
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
                // (SE-0040) one delegate made once, its "anything found" in a field under the lock: no closure per lookup
                lookupAny = false;
                sample = CoastMath.Bilinear(x, z, cellForLookup ??= CellForLookup, scratch);
                return lookupAny;
            }
        }

        bool lookupAny;
        System.Func<long, long, float[], bool> cellForLookup;

        bool CellForLookup(long i, long j, float[] into)
        {
            bool got = CellLocked(i, j, into);
            lookupAny |= got;
            return got;
        }

        void Want((long, long) key) => wanted[key] = Environment.TickCount64;

        // (SE-0068) tiles to solve again after their blocks changed: first in line, and not dropped for not being asked for
        readonly HashSet<(long, long)> redo = new();

        readonly List<(long, long)> staleKeys = new(), askedKeys = new();
        readonly List<KeyValuePair<(long, long), long>> askOrder = new();
        static readonly Comparison<KeyValuePair<(long, long), long>> NewestFirst = (a, b) => b.Value.CompareTo(a.Value);

        readonly float[] scratch = new float[CoastMath.Stride];

        /// <summary>Column (i, j)'s <see cref="CoastMath.Stride"/> floats, if its tile is solved (else it's asked for).</summary>
        public bool Cell(long i, long j, float[] into) { lock (gate) return CellLocked(i, j, into); }

        bool CellLocked(long i, long j, float[] into)
        {
            i = WrapBlock(i);
            var key = (Floor(i, CoastMath.Tile), Floor(j, CoastMath.Tile));
            if (!tiles.TryGetValue(key, out var e)) { Want(key); return false; }
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
                tx = WrapTile(tx);
                if (tiles.TryGetValue((tx, tz), out var e)) { e.Used = ++uses; return e.Data; }
                Want((tx, tz));
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
                        long wx = WrapTile(tx);
                        if (tiles.TryGetValue((wx, tz), out var e)) e.Used = ++uses;
                        else Want((wx, tz));
                    }
        }

        static long Floor(long v, int d) => v >= 0 ? v / d : -((-v + d - 1) / d);

        // (SE-0053) a Cylinder World ring: tile and chunk-column keys (the same 32-block unit) and block columns are taken
        // around it, so the tiles on both sides of the seam are the same tiles and the map runs on across it
        long ringStartTile, ringTiles;

        /// <summary>The world's ring didn't line up with the 32-block tiles: the coast stays off (Cylinder World's does).</summary>
        public bool RingMisaligned { get; private set; }

        /// <summary>(SE-0053) The ring the world wraps on (circumference 0: none). Tiles solved before are forgotten if it changes.</summary>
        public void SetRing(double start, double circumference)
        {
            long t = 0, n = 0;
            bool misaligned = false;
            if (circumference > 0)
            {
                misaligned = start % CoastMath.Tile != 0 || circumference % CoastMath.Tile != 0;
                if (!misaligned) { t = (long)(start / CoastMath.Tile); n = (long)(circumference / CoastMath.Tile); }
            }
            lock (gate)
            {
                RingMisaligned = misaligned;
                if (t == ringStartTile && n == ringTiles) return;
                ringStartTile = t; ringTiles = n;
                tiles.Clear(); columns.Clear(); wanted.Clear(); dirty.Clear(); failedAt.Clear(); redo.Clear();
                Interlocked.Increment(ref version);
            }
        }

        /// <summary>(SE-0053) A tile or chunk-column index around the ring (unchanged without one).</summary>
        public long WrapTile(long t) => ringTiles > 0 ? ringStartTile + Mod(t - ringStartTile, ringTiles) : t;

        long WrapBlock(long b)
        {
            if (ringTiles <= 0) return b;
            long start = ringStartTile * CoastMath.Tile, n = ringTiles * CoastMath.Tile;
            return start + Mod(b - start, n);
        }

        static long Mod(long a, long m) { long r = a % m; return r < 0 ? r + m : r; }

        // any thread (the client marks chunks dirty from its relight thread too)
        void OnChunkDirty(Vec3i chunk, IWorldChunk _, EnumChunkDirtyReason reason)
        {
            // (SE-0068) a chunk that comes back loaded may have changed while it was away: its column is scanned again too
            if (reason != EnumChunkDirtyReason.MarkedDirty && reason != EnumChunkDirtyReason.NewlyLoaded) return;
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
            // (SE-0040 review) the lists are kept between calls (Work runs on the main thread only): nothing allocated per call
            var asked = askedKeys;
            lock (gate)
            {
                if (wanted.Count == 0 && redo.Count == 0) return;
                long cut = Environment.TickCount64 - StaleMs;
                staleKeys.Clear();
                foreach (var kv in wanted) if (kv.Value < cut) staleKeys.Add(kv.Key);
                foreach (var k in staleKeys) wanted.Remove(k);
                askOrder.Clear();
                foreach (var kv in wanted) askOrder.Add(kv);
                askOrder.Sort(NewestFirst);
                asked.Clear();
                foreach (var k in redo) asked.Add(k);
                foreach (var kv in askOrder) if (!redo.Contains(kv.Key)) asked.Add(kv.Key);
                if (asked.Count == 0) return;
            }
            int r = (CoastMath.Margin + Chunk - 1) / Chunk;
            long now = Environment.TickCount64;
            foreach (var key in asked)
            {
                lock (gate) if (failedAt.TryGetValue(key, out long at) && now - at < RetryFailedMs) continue;
                // the chunk columns under its window (tile = chunk size)
                bool ready = true;
                for (long cz = key.Item2 - r; cz <= key.Item2 + r && ready; cz++)
                    for (long ux = key.Item1 - r; ux <= key.Item1 + r && ready; ux++)
                    {
                        long cx = WrapTile(ux);
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
                        for (long ux = key.Item1 - r; ux <= key.Item1 + r; ux++)
                        {
                            long tx = WrapTile(ux);
                            if (tiles.ContainsKey((tx, tz)) && redo.Add((tx, tz))) resolves++;
                        }
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
            // (SE-0040) the chunk columns under the window looked up once under the lock (a column's depths are replaced, never
            // changed in place), the depths copied outside it: about 25 lookups, not one per block
            long cx0 = Floor(x0, Chunk), cz0 = Floor(z0, Chunk), cx1 = Floor(x0 + w - 1, Chunk), cz1 = Floor(z0 + w - 1, Chunk);
            int nx = (int)(cx1 - cx0 + 1), nz = (int)(cz1 - cz0 + 1);
            var cols = new float[nx * nz][];
            lock (gate)
                for (int j = 0; j < nz; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        if (!columns.TryGetValue((WrapTile(cx0 + i), cz0 + j), out var c)) return null;
                        c.Used = ++uses;
                        cols[j * nx + i] = c.Depth;
                    }
            for (int z = 0; z < w; z++)
            {
                long bz = z0 + z, cz = Floor(bz, Chunk);
                int rz = (int)(bz - cz * Chunk) * Chunk;
                for (int x = 0; x < w; x++)
                {
                    long bx = x0 + x, cx = Floor(bx, Chunk);
                    depth[z * w + x] = cols[(int)(cz - cz0) * nx + (int)(cx - cx0)][rz + (int)(bx - cx * Chunk)];
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
                // (SE-0057) a window all deep or all land needs no solve (most of them: the open sea, inland): the same tile,
                // without the solve's 1 MB of arrays and 1 to 2 ms
                var data = CoastMath.UniformTile(depth, w, m, CoastMath.Tile);
                if (data == null)
                {
                    var time = new float[w * w];
                    var dist = new float[w * w];
                    CoastMath.Solve(depth, w, time, dist);
                    data = CoastMath.TileOf(depth, time, dist, w, m, CoastMath.Tile, key.Item1 * CoastMath.Tile - m, key.Item2 * CoastMath.Tile - m);
                }
                else Interlocked.Increment(ref uniformTiles);
                double ms = watch.Elapsed.TotalMilliseconds;
                lock (gate)
                {
                    tiles[key] = new Entry { Data = data, Used = ++uses };
                    wanted.Remove(key);
                    redo.Remove(key);
                    failedAt.Remove(key);
                    if (tiles.Count > MaxTiles) Forget(tiles);
                    solved++; solveMs += ms; solveMax = Math.Max(solveMax, ms);
                    Interlocked.Increment(ref version);
                }
            }
            catch (Exception e)
            {
                LogOnce.Error(api.Logger, "solving a coast tile", new Exception("tile " + key.Item1 + "," + key.Item2 + ": " + e.Message, e));
                lock (gate)
                {
                    wanted.Remove(key);
                    redo.Remove(key);
                    failedAt[key] = Environment.TickCount64;
                    if (failedAt.Count > 256) failedAt.Clear();
                }
            }
        }

        // the least recently used quarter
        void Forget<T>(Dictionary<(long, long), T> d) where T : class
        {
            var order = new List<((long, long) Key, long Used)>(d.Count);
            foreach (var kv in d) order.Add((kv.Key, kv.Value is Entry e ? e.Used : ((Columns)(object)kv.Value).Used));
            order.Sort((a, b) => a.Used.CompareTo(b.Used));
            for (int i = 0; i < order.Count / 4; i++) d.Remove(order[i].Key);
            // (SE-0040) only forgotten tiles change the map; forgotten columns are scanned again when needed
            if (typeof(T) == typeof(Entry)) Interlocked.Increment(ref version);
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
