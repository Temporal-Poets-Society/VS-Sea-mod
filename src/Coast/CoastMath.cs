using System;
using System.Collections.Generic;

namespace Sea
{
    /// <summary>
    /// (0.6.0) The coast's numbers at one place, as the water shader reads them from sea_CoastTex: the depth to the sea
    /// floor (blocks; 0 on land, <see cref="CoastMath.MaxDepth"/> where it's deep or unknown), the time a shallow-water
    /// wave takes from here to the shore line (seconds), the direction away from the shore (the way that time grows)
    /// times how much of the coast's effect is trusted here (<see cref="CoastMath.Fade"/>), the share of the waves from
    /// each of 8 directions that reaches here past the land (the lee of islands and headlands), and the rip channel.
    /// </summary>
    public struct CoastSample
    {
        public double Depth, Time, AwayX, AwayZ;
        /// <summary>Shelter: the share of waves coming from the east (0), south-east (1), south (2) … north-east (7) that reaches here.</summary>
        public double Lee0, Lee1, Lee2, Lee3, Lee4, Lee5, Lee6, Lee7;
        /// <summary>A rip channel here, 0 to 1: the water piled up by the surf runs back out to sea through it.</summary>
        public double Rip;
        /// <summary>
        /// (0.6.0) Blocks from the shore line (over water, from the land; over land, from the water), at most
        /// <see cref="CoastMath.FarShore"/>; and over water the shore's steepness there (the floor's mean slope across the
        /// first 8 blocks off it, less a block of shallows: 0 for flats, about 4 for a wall in deep water), over land the
        /// sea floor's slope off the beach.
        /// </summary>
        public double ShoreDist, ShoreSlope;

        /// <summary>No coast: deep water open to every direction, as everywhere before 0.6.0.</summary>
        public static readonly CoastSample Open = new()
        {
            Depth = CoastMath.MaxDepth, Lee0 = 1, Lee1 = 1, Lee2 = 1, Lee3 = 1, Lee4 = 1, Lee5 = 1, Lee6 = 1, Lee7 = 1,
            ShoreDist = CoastMath.FarShore,
        };

        public double LeeAt(int d) => d switch { 0 => Lee0, 1 => Lee1, 2 => Lee2, 3 => Lee3, 4 => Lee4, 5 => Lee5, 6 => Lee6, _ => Lee7 };

        /// <summary>How much of the coast's effect is trusted here, 0 to 1 (the length of the away vector).</summary>
        public double Trust => Math.Min(1, Math.Sqrt(AwayX * AwayX + AwayZ * AwayZ));

        /// <summary>Within reach of the sea floor (shallower than <see cref="CoastMath.MaxDepth"/>).</summary>
        public bool Coastal => Depth < CoastMath.MaxDepth;

        /// <summary>Anything here changes the waves: the floor, the lee of land, a rip or a shore that throws them back.</summary>
        public bool Affects => Coastal || Rip > 0 || (ShoreSlope > 0 && Trust > 0) || Math.Min(Math.Min(Math.Min(Lee0, Lee1), Math.Min(Lee2, Lee3)), Math.Min(Math.Min(Lee4, Lee5), Math.Min(Lee6, Lee7))) < 0.999;
    }

    /// <summary>A coast lookup: the numbers at a world position (false: none known there, open sea).</summary>
    public delegate bool CoastLookup(double x, double z, out CoastSample sample);

    /// <summary>
    /// (0.6.0) Waves on a coast. The shore's travel-time field (a fast-marching eikonal solve at the shallow-water speed
    /// sqrt(g h)), the lee of land per direction, rip channels and the beach, and what the water shader and the CPU both
    /// do with them: the sea's trains are cut by the land upwave (with a soft, diffracted edge), hand over to shoreward
    /// "surf" trains that follow the bottom (refraction: crests turn parallel to the beach and bunch up as the water
    /// shallows) and meet the shore where the sea's own crests do, grow by shoaling (Green's law), are capped by
    /// depth-limited breaking (H_s ≤ γ h), take a breaker's shape (a peaked crest, flat trough and steep front) as they
    /// break, and drive a longshore current (Komar 1979), which turns their crests, and rips, whose seaward flow holds the
    /// waves back over the channel and leaves a gap in the breakers. Steep shores throw waves back (a standing sea before
    /// a cliff); behind land the wind's own sea comes back with the fetch, its longer waves last.
    /// Pure: the same numbers in the game, the tests and the shader checks (WaterShader.Geometry mirrors the wave parts).
    /// </summary>
    public static class CoastMath
    {
        public const double Gravity = 9.81;
        /// <summary>Blocks per side of a solved tile (the client's map and the server's cache are made of these).</summary>
        public const int Tile = 32;
        /// <summary>Blocks around a tile also solved, so the shore beyond its edge is found; tiles agree within <see cref="FadeFar"/>.</summary>
        public const int Margin = 64;
        public const int Window = Tile + 2 * Margin;
        /// <summary>Depths are scanned this deep; deeper is open sea (and the coast's effect fades out before it).</summary>
        public const float MaxDepth = 32f;
        /// <summary>The shallowest depth a speed or shoaling is worked out for.</summary>
        public const double MinDepth = 0.3;
        /// <summary>Depth-limited breaking: significant height at most γ times the depth (surf zones: 0.5 to 0.6).</summary>
        public const double Gamma = 0.6;
        /// <summary>The coast's effect is trusted to this distance from the shore line, and fades out by <see cref="FadeFar"/>.</summary>
        public const double FadeNear = 28, FadeFar = 56;
        /// <summary>How far upwave the land is looked for (blocks): a tile's margin, so tiles agree.</summary>
        public const int LeeReach = Margin;
        /// <summary>Rip channels: about this many blocks apart along a beach (a few surf-zone widths).</summary>
        public const double RipSpacing = 64;

        /// <summary>The farthest a shore distance is kept (blocks).</summary>
        public const float FarShore = 2 * Margin;

        /// <summary>
        /// Floats per column in a tile and in sea_CoastTex (4 texels): depth, time, away × trust; lee 0–3; lee 4–7; rip,
        /// shore distance, land height, shore slope (<see cref="CoastSample.ShoreDist"/>, <see cref="CoastSample.ShoreSlope"/>).
        /// </summary>
        public const int Stride = 16;
        public const int IDepth = 0, ITime = 1, IAwayX = 2, IAwayZ = 3, ILee = 4, IRip = 12, IShoreDist = 13, ILandHeight = 14, IShoreSlope = 15;
        /// <summary>Over land the shore distance is the distance from the water, the shore slope the floor's off the beach.</summary>
        public const int ILandDist = IShoreDist, IBeachSlope = IShoreSlope;

        // ------------------------------------------------------------ the field

        /// <summary>
        /// Solve a window's travel times. depth: Window² (row-major, z rows) depths in blocks; ≤ 0 is land (minus its top's
        /// height over the water: the shore's sources), NaN unknown (taken as deep). time: seconds from the shore line at
        /// sqrt(g h); dist: blocks from it. Both +∞ where no shore is in the window.
        /// </summary>
        public static void Solve(float[] depth, int w, float[] time, float[] dist)
        {
            int n = w * w;
            var known = new bool[n];
            var queue = new PriorityQueue<int, float>();
            for (int i = 0; i < n; i++)
            {
                if (depth[i] <= 0) { time[i] = 0; dist[i] = 0; known[i] = true; }
                else { time[i] = float.PositiveInfinity; dist[i] = float.PositiveInfinity; }
            }
            for (int i = 0; i < n; i++)
            {
                if (!known[i]) continue;
                int x = i % w, z = i / w;
                if (x > 0) Relax(i - 1, depth, w, time, dist, known, queue);
                if (x < w - 1) Relax(i + 1, depth, w, time, dist, known, queue);
                if (z > 0) Relax(i - w, depth, w, time, dist, known, queue);
                if (z < w - 1) Relax(i + w, depth, w, time, dist, known, queue);
            }
            while (queue.TryDequeue(out int i, out float t))
            {
                if (known[i] || t > time[i]) continue;
                known[i] = true;
                int x = i % w, z = i / w;
                if (x > 0) Relax(i - 1, depth, w, time, dist, known, queue);
                if (x < w - 1) Relax(i + 1, depth, w, time, dist, known, queue);
                if (z > 0) Relax(i - w, depth, w, time, dist, known, queue);
                if (z < w - 1) Relax(i + w, depth, w, time, dist, known, queue);
            }
        }

        /// <summary>Shallow-water speed (m/s) at a depth; unknown depth is deep.</summary>
        public static double Speed(double h) => Math.Sqrt(Gravity * Math.Clamp(double.IsNaN(h) ? MaxDepth : h, MinDepth, MaxDepth));

        static void Relax(int i, float[] depth, int w, float[] time, float[] dist, bool[] known, PriorityQueue<int, float> queue)
        {
            if (known[i]) return;
            int x = i % w, z = i / w;
            float t = Upwind(i, x, z, w, time, known, 1 / Speed(depth[i]));
            float d = Upwind(i, x, z, w, dist, known, 1);
            if (d < dist[i]) dist[i] = d;
            if (t < time[i]) { time[i] = t; queue.Enqueue(i, t); }
        }

        // the first-order upwind update of the eikonal |grad u| = slowness from the known neighbours
        static float Upwind(int i, int x, int z, int w, float[] u, bool[] known, double slowness)
        {
            double a = double.PositiveInfinity, b = double.PositiveInfinity;
            if (x > 0 && known[i - 1]) a = u[i - 1];
            if (x < w - 1 && known[i + 1]) a = Math.Min(a, u[i + 1]);
            if (z > 0 && known[i - w]) b = u[i - w];
            if (z < w - 1 && known[i + w]) b = Math.Min(b, u[i + w]);
            if (double.IsPositiveInfinity(a) && double.IsPositiveInfinity(b)) return float.PositiveInfinity;
            if (double.IsPositiveInfinity(a) || double.IsPositiveInfinity(b) || Math.Abs(a - b) >= slowness)
                return (float)(Math.Min(a, b) + slowness);
            return (float)(0.5 * (a + b + Math.Sqrt(2 * slowness * slowness - (a - b) * (a - b))));
        }

        static bool IsLand(float h) => h <= 0;
        static float Water(float h) => float.IsNaN(h) ? MaxDepth : Math.Min(h, MaxDepth);

        /// <summary>
        /// A window's tile: per column (row-major, z rows) <see cref="Stride"/> floats. (ox, oz): the world block of the
        /// window's column (0, 0), for the rip channels' places. The away direction is the travel time's gradient,
        /// smoothed over 3 × 3; the lee is looked for along fans of rays upwave; rips sit at fixed places along beaches.
        /// </summary>
        public static float[] TileOf(float[] depth, float[] time, float[] dist, int w, int margin, int tile, long ox = 0, long oz = 0)
        {
            int n = w * w;
            var gx = new float[n];
            var gz = new float[n];
            var slope = new float[n];
            for (int z = 1; z < w - 1; z++)
                for (int x = 1; x < w - 1; x++)
                {
                    int i = z * w + x;
                    if (IsLand(depth[i])) continue;
                    double hx = FloorSlope(depth, i, 1), hz = FloorSlope(depth, i, w);
                    slope[i] = (float)Math.Sqrt(hx * hx + hz * hz);
                    if (float.IsPositiveInfinity(time[i])) continue;
                    double dx = Slope(time, i, 1), dz = Slope(time, i, w);
                    double l = Math.Sqrt(dx * dx + dz * dz);
                    if (l > 1e-9) { gx[i] = (float)(dx / l); gz[i] = (float)(dz / l); }
                }
            Beach(depth, slope, w, out var landDist, out var landSlope);
            var steep = Steepness(depth, dist, w);
            var rays = Rays();
            var tileData = new float[tile * tile * Stride];
            for (int z = 0; z < tile; z++)
                for (int x = 0; x < tile; x++)
                {
                    int wx = x + margin, wz = z + margin, i = wz * w + wx, o = (z * tile + x) * Stride;
                    float h = depth[i];
                    bool land = IsLand(h);
                    tileData[o + IDepth] = land ? 0 : Water(h);
                    // the lee: from the water (on a beach, from the first water toward each direction)
                    bool near = land ? landDist[i] <= 12 : dist[i] <= LeeReach;
                    for (int d = 0; d < 8; d++) tileData[o + ILee + d] = near ? Lee(depth, w, wx, wz, d, land, rays) : (land ? 0 : 1);
                    if (land)
                    {
                        tileData[o + ILandDist] = landDist[i];
                        tileData[o + ILandHeight] = -h;
                        tileData[o + IBeachSlope] = landSlope[i];
                        continue;
                    }
                    tileData[o + IShoreDist] = Math.Min(dist[i], FarShore);
                    tileData[o + IShoreSlope] = steep[i];
                    if (float.IsPositiveInfinity(time[i])) continue;
                    tileData[o + ITime] = time[i];
                    double sx = 0, sz = 0;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int j = (wz + dz) * w + wx + dx;
                            sx += gx[j]; sz += gz[j];
                        }
                    double l = Math.Sqrt(sx * sx + sz * sz), trust = Fade(dist[i]);
                    if (l < 1e-9 || trust <= 0) continue;
                    double ax = sx / l, az = sz / l;
                    tileData[o + IAwayX] = (float)(ax * trust);
                    tileData[o + IAwayZ] = (float)(az * trust);
                    tileData[o + IRip] = (float)(RipChannel(ox + wx + 0.5 - ax * dist[i], oz + wz + 0.5 - az * dist[i], dist[i], slope[i]) * trust);
                }
            return tileData;
        }

        // the sea floor's slope along one axis: between the water on either side (not down the step to the land)
        static double FloorSlope(float[] depth, int i, int step)
        {
            float a = depth[i - step], b = depth[i + step];
            bool wa = !IsLand(a), wb = !IsLand(b);
            if (wa && wb) return 0.5 * (Water(b) - Water(a));
            if (wb) return Water(b) - Water(depth[i]);
            if (wa) return Water(depth[i]) - Water(a);
            return 0;
        }

        // central difference where both neighbours are water with a time, else the one-sided one there is
        static double Slope(float[] time, int i, int step)
        {
            float a = time[i - step], b = time[i + step], c = time[i];
            bool ha = !float.IsPositiveInfinity(a), hb = !float.IsPositiveInfinity(b);
            if (ha && hb) return 0.5 * (b - a);
            if (hb) return b - c;
            if (ha) return c - a;
            return 0;
        }

        // land's distance (blocks, chamfer 1 / 1.414) from the water, and the slope of the sea floor where that water is
        static void Beach(float[] depth, float[] slope, int w, out float[] landDist, out float[] landFloor)
        {
            int n = w * w;
            var dist = landDist = new float[n];
            var floor = landFloor = new float[n];
            for (int i = 0; i < n; i++)
            {
                bool land = IsLand(depth[i]);
                dist[i] = land ? float.PositiveInfinity : 0;
                floor[i] = land ? 0 : Math.Clamp(slope[i], 0.02f, 0.5f);
            }
            const float D = 1.41421356f;
            for (int pass = 0; pass < 2; pass++)
            {
                bool forward = pass == 0;
                for (int k = 0; k < n; k++)
                {
                    int i = forward ? k : n - 1 - k, x = i % w, z = i / w;
                    if (dist[i] == 0) continue;
                    int s = forward ? -1 : 1;
                    Take(i, x + s, z, 1); Take(i, x, z + s, 1); Take(i, x + s, z + s, D); Take(i, x - s, z + s, D);
                }
            }
            void Take(int i, int x, int z, float step)
            {
                if (x < 0 || z < 0 || x >= w || z >= w) return;
                int j = z * w + x;
                if (dist[j] + step < dist[i]) { dist[i] = dist[j] + step; floor[i] = floor[j]; }
            }
        }

        // Over the water, the steepness of the shore nearest each column: the floor's mean slope from the shore line out to
        // the water 7 to 9 blocks off it (less a block of shallows, which every beach has), carried to the rest of the
        // water from the nearest of those. 0 where no water is that far out (a narrow channel).
        static float[] Steepness(float[] depth, float[] dist, int w)
        {
            int n = w * w;
            var value = new float[n];
            var reach = new float[n];
            for (int i = 0; i < n; i++)
            {
                reach[i] = float.PositiveInfinity;
                if (IsLand(depth[i])) continue;
                float d = dist[i];
                if (d >= 7 && d <= 9) { reach[i] = 0; value[i] = Math.Max(Water(depth[i]) - 1, 0) / d; }
            }
            const float D = 1.41421356f;
            for (int pass = 0; pass < 2; pass++)
            {
                bool forward = pass == 0;
                for (int k = 0; k < n; k++)
                {
                    int i = forward ? k : n - 1 - k, x = i % w, z = i / w;
                    if (reach[i] == 0 || IsLand(depth[i])) continue;
                    int s = forward ? -1 : 1;
                    Take(i, x + s, z, 1); Take(i, x, z + s, 1); Take(i, x + s, z + s, D); Take(i, x - s, z + s, D);
                }
            }
            for (int i = 0; i < n; i++) if (float.IsPositiveInfinity(reach[i])) value[i] = 0;
            return value;
            void Take(int i, int x, int z, float step)
            {
                if (x < 0 || z < 0 || x >= w || z >= w) return;
                int j = z * w + x;
                if (IsLand(depth[j])) return;
                if (reach[j] + step < reach[i]) { reach[i] = reach[j] + step; value[i] = value[j]; }
            }
        }

        static readonly double[] RayAngles ={ -20, -10, 0, 10, 20 }, RayWeights = { 1, 2, 3, 2, 1 };

        // per direction d (waves coming from angle d·45°), the unit steps of its fan of rays
        static (double X, double Z, double W)[][] Rays()
        {
            var r = new (double, double, double)[8][];
            for (int d = 0; d < 8; d++)
            {
                r[d] = new (double, double, double)[RayAngles.Length];
                for (int k = 0; k < RayAngles.Length; k++)
                {
                    double a = d * Math.PI / 4 + RayAngles[k] * Math.PI / 180;
                    r[d][k] = (Math.Cos(a), Math.Sin(a), RayWeights[k] / 9.0);
                }
            }
            return r;
        }

        // The share of waves from direction d reaching column (x, z): each ray upwave that meets land x blocks out lets
        // sqrt(x / reach) through (waves bend in behind it and the wind builds them again), a ray in the clear all of it.
        // From a beach the rays start at the first water they cross.
        static float Lee(float[] depth, int w, int x, int z, int d, bool land, (double X, double Z, double W)[][] rays)
        {
            double sum = 0;
            foreach (var (rx, rz, wt) in rays[d])
            {
                double px = x + 0.5, pz = z + 0.5, from = 0;
                bool inWater = !land, hit = false;
                for (int s = 1; s <= LeeReach + 12; s++)
                {
                    int cx = (int)Math.Floor(px + rx * s), cz = (int)Math.Floor(pz + rz * s);
                    if (cx < 0 || cz < 0 || cx >= w || cz >= w) break;
                    bool l = IsLand(depth[cz * w + cx]);
                    if (!inWater)
                    {
                        if (!l) { inWater = true; from = s; }
                        else if (s > 12) { hit = true; from = s; break; }
                        continue;
                    }
                    if (s - from > LeeReach) break;
                    if (l) { hit = true; from = s - from; break; }
                }
                if (!inWater && !hit) hit = true;   // (a beach facing nothing)
                sum += wt * (hit ? Math.Sqrt(Math.Clamp(from / LeeReach, 0, 1)) : 1);
            }
            return (float)sum;
        }

        /// <summary>
        /// A rip channel at a water column whose nearest shore is at (shoreX, shoreZ) and which is `dist` blocks out on a
        /// floor of this slope: channels are fixed places along a beach (value noise over the shore, about
        /// <see cref="RipSpacing"/> apart), running straight out to sea, on gentle sandy floors only (not at cliffs).
        /// </summary>
        public static double RipChannel(double shoreX, double shoreZ, double dist, double floorSlope) =>
            SmoothStep(0.6, 0.72, ValueNoise(shoreX / RipSpacing, shoreZ / RipSpacing))
            * (1 - SmoothStep(30, 48, dist)) * (1 - SmoothStep(0.12, 0.25, floorSlope));

        static double ValueNoise(double x, double z)
        {
            double fx = Math.Floor(x), fz = Math.Floor(z), tx = x - fx, tz = z - fz;
            long ix = (long)fx, iz = (long)fz;
            tx = tx * tx * (3 - 2 * tx); tz = tz * tz * (3 - 2 * tz);
            double a = Hash(ix, iz), b = Hash(ix + 1, iz), c = Hash(ix, iz + 1), e = Hash(ix + 1, iz + 1);
            return (a * (1 - tx) + b * tx) * (1 - tz) + (c * (1 - tx) + e * tx) * tz;
        }

        static double Hash(long x, long z)
        {
            ulong h = (ulong)x * 0x9E3779B97F4A7C15UL ^ (ulong)z * 0xC2B2AE3D27D4EB4FUL ^ 0x5EA5EA5EA5EA5EA5UL;
            h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
            return (h >> 11) * (1.0 / (1UL << 53));
        }

        /// <summary>Trust in the coast's numbers this far (blocks) from the shore line: 1 near it, 0 from <see cref="FadeFar"/>.</summary>
        public static double Fade(double dist) => 1 - SmoothStep(FadeNear, FadeFar, dist);

        /// <summary>
        /// The numbers at a world position, bilinear between column centres exactly as the shader's sea_CoastAt does it.
        /// cell(i, j) gives column (i, j)'s <see cref="Stride"/> floats (false: no map there; taken as open sea).
        /// </summary>
        public static CoastSample Bilinear(double x, double z, Func<long, long, float[], bool> cell, float[] scratch)
        {
            double fx = x - 0.5, fz = z - 0.5, ix = Math.Floor(fx), iz = Math.Floor(fz);
            double tx = fx - ix, tz = fz - iz;
            var s = new CoastSample();
            for (int c = 0; c < 4; c++)
            {
                double wgt = ((c & 1) == 1 ? tx : 1 - tx) * ((c & 2) == 2 ? tz : 1 - tz);
                if (!cell((long)ix + (c & 1), (long)iz + (c >> 1), scratch)) OpenColumn(scratch);
                s.Depth += wgt * scratch[IDepth]; s.Time += wgt * scratch[ITime];
                s.AwayX += wgt * scratch[IAwayX]; s.AwayZ += wgt * scratch[IAwayZ];
                s.Lee0 += wgt * scratch[ILee]; s.Lee1 += wgt * scratch[ILee + 1]; s.Lee2 += wgt * scratch[ILee + 2]; s.Lee3 += wgt * scratch[ILee + 3];
                s.Lee4 += wgt * scratch[ILee + 4]; s.Lee5 += wgt * scratch[ILee + 5]; s.Lee6 += wgt * scratch[ILee + 6]; s.Lee7 += wgt * scratch[ILee + 7];
                s.Rip += wgt * scratch[IRip];
                s.ShoreDist += wgt * scratch[IShoreDist]; s.ShoreSlope += wgt * scratch[IShoreSlope];
            }
            return s;
        }

        /// <summary>A column of open sea: deep, no shore, open to every direction.</summary>
        public static void OpenColumn(float[] into, int at = 0)
        {
            Array.Clear(into, at, Stride);
            into[at + IDepth] = MaxDepth;
            for (int d = 0; d < 8; d++) into[at + ILee + d] = 1;
            into[at + IShoreDist] = FarShore;
        }

        // ------------------------------------------------------------ waves on it (WaterShader.Geometry mirrors these)

        public static double SmoothStep(double e0, double e1, double x)
        {
            double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
            return t * t * (3 - 2 * t);
        }

        /// <summary>The share of a train (wave vector k, so coming from −k) that reaches here past the land.</summary>
        public static double Lee(in CoastSample c, double kx, double kz)
        {
            if (kx * kx + kz * kz < 1e-14) return 1;
            double a = Math.Atan2(-kz, -kx);
            if (a < 0) a += 6.28318531;
            double u = a / 0.785398163, d = Math.Floor(u), f = u - d;
            int i = (int)d % 8, j = (i + 1) % 8;
            return c.LeeAt(i) * (1 - f) + c.LeeAt(j) * f;
        }

        /// <summary>
        /// How much of a train of deep-water wavenumber k runs as a surf train here: none in deep water (k h ≥ 1.6), all
        /// where it feels the bottom strongly (k h ≤ 0.4); faded out toward <see cref="MaxDepth"/> and by the trust.
        /// </summary>
        public static double SurfShare(double k, in CoastSample c)
        {
            if (!(c.Depth < MaxDepth)) return 0;
            return (1 - SmoothStep(0.4, 1.6, k * c.Depth)) * (1 - SmoothStep(0.6 * MaxDepth, MaxDepth, c.Depth)) * c.Trust;
        }

        /// <summary>Shoaling (Green's law): sqrt(deep group speed / shallow speed), 1 to 2.5.</summary>
        public static double Shoaling(double omega, double h) =>
            Math.Clamp(Math.Sqrt(Gravity / 2 / Math.Max(omega, 1e-6) / Math.Sqrt(Gravity * Math.Max(h, MinDepth))), 1, 2.5);

        /// <summary>A rip's seaward speed (m/s) for a sea of significant height hs: about 0.35 sqrt(g H_s), 1–2 m/s in a big surf.</summary>
        public static double RipSpeed(double hs) => 0.35 * Math.Sqrt(Gravity * Math.Max(hs, 0));

        /// <summary>
        /// A surf train's travel time from here to the shore line: over a rip channel the seaward current holds the waves
        /// back (speed c − U instead of c, to first order T (1 + U / c)), so their crests bow seaward round it.
        /// </summary>
        public static double SurfTime(in CoastSample c, double hs)
        {
            double speed = Math.Sqrt(Gravity * Math.Max(c.Depth, MinDepth));
            return c.Time * (1 + c.Rip * Math.Min(RipSpeed(hs), 0.8 * speed) / speed);
        }

        // ------------------------------------------------------------ the wind's own sea and the fetch (0.6.0)

        /// <summary>Winds under this (m/s) raise no sea of their own worth shaping.</summary>
        public const double CalmWind = 0.5;

        /// <summary>
        /// How much of a train (wave vector k, angular speed ω) is the local wind's own sea that the land the coast map
        /// sees can shelter, for a wind (wx, wz) in m/s: waves slower than about 1.2 times the wind (the peak of a fully
        /// grown sea, Pierson and Moskowitz) and running with it, and short beside that land (under 32 blocks long;
        /// none from 128: a longer wave bends round land of that size, so it keeps the lee's share like swell). None of a
        /// train faster than 1.6 times the wind, or running across or against it.
        /// </summary>
        public static double WindSea(double kx, double kz, double omega, double wx, double wz)
        {
            double u = Math.Sqrt(wx * wx + wz * wz), k = Math.Sqrt(kx * kx + kz * kz);
            if (u < CalmWind || k < 1e-7 || !(omega > 0)) return 0;
            double c = Gravity / omega;
            return (1 - SmoothStep(1.2, 1.6, c / u)) * SmoothStep(0, 0.5, (kx * wx + kz * wz) / (k * u))
                * (1 - SmoothStep(32, 128, 2 * Math.PI / k));
        }

        /// <summary>
        /// The power of the lee a train of the wind's own sea takes (1 to 4) in a wind of u m/s. Behind land the lee's
        /// sqrt(x / reach) is already the growth of a sea with its fetch, H_s ∝ sqrt(F) (JONSWAP, Hasselmann et al. 1973),
        /// taken as grown at the coast map's reach, beyond which the land is not seen. The fetch also moves the sea's peak
        /// to longer waves, f_p = 3.5 (g/U)(gF/U²)^−0.33: a train at or above the peak the reach's fetch raises keeps the
        /// lee's share (power 1); a longer one, which needs more fetch than that, is held back more, up to the lee's fourth
        /// power for one 3 times slower than that peak. A smooth rule that stays continuous where the reach ends.
        /// </summary>
        public static double FetchPower(double omega, double u)
        {
            if (!(u >= CalmWind) || !(omega > 0)) return 1;
            double f = omega / (2 * Math.PI), peak = 3.5 * Gravity / u * Math.Pow(Gravity * LeeReach / (u * u), -0.33);
            return 1 + 3 * SmoothStep(1, 3, peak / f);
        }

        /// <summary>
        /// The share of a train that gets past the land to here: swell (and waves long beside the land) by the lee, which
        /// it bends in by; the wind's own short sea by the lee to the power its need of fetch gives (<see cref="WindSea"/>,
        /// <see cref="FetchPower"/>). What the shader's sea_Reach gives. (wx, wz): the wind in m/s.
        /// </summary>
        public static double Reach(in CoastSample c, double kx, double kz, double omega, double wx, double wz)
        {
            double l = Lee(c, kx, kz), w = WindSea(kx, kz, omega, wx, wz);
            return w > 0 ? l + (Math.Pow(Math.Max(l, 0), FetchPower(omega, Math.Sqrt(wx * wx + wz * wz))) - l) * w : l;
        }

        /// <summary>Every train's share that gets past the land to here (<see cref="Reach(in CoastSample, double, double, double, double, double)"/>).</summary>
        public static void Reach(in CoastSample c, int count, double[] kx, double[] kz, double[] omega, double wx, double wz, Span<double> reach)
        {
            for (int i = 0; i < count; i++) reach[i] = Reach(c, kx[i], kz[i], omega[i], wx, wz);
        }

        // ------------------------------------------------------------ the shore line, reflection, the current's bending (0.6.0)

        /// <summary>
        /// A train's phase at the shore line nearest here less its phase here: −d (k · n), n the unit direction away from
        /// the shore, d the shore distance. A surf train's phase counts back from it by the travel time, so its crests
        /// meet the shore where the sea's own do (at an angle, along the beach); a reflected train's phase is the open
        /// one's plus twice it (the wave come back from the shore).
        /// </summary>
        public static double ShoreOffset(in CoastSample c, double kx, double kz)
        {
            double l = Math.Sqrt(c.AwayX * c.AwayX + c.AwayZ * c.AwayZ);
            return l > 1e-9 ? -c.ShoreDist * (kx * c.AwayX + kz * c.AwayZ) / l : 0;
        }

        /// <summary>
        /// How much a surf train's phase rate across the surf zone changes in a current (vx, vz) along the shore: the wave
        /// keeps its frequency on the ground, so in the water it runs at ω − k·V (Doppler), and its crests turn: with
        /// the current they lengthen, against it they shorten and bunch up. At most half of ω.
        /// </summary>
        public static double Bend(double kx, double kz, double omega, double vx, double vz) =>
            Math.Clamp(kx * vx + kz * vz, -0.5 * omega, 0.5 * omega);

        /// <summary>
        /// The share of a train (wave vector k, angular speed ω) that the shore throws back, in a sea of significant height
        /// hs: Seelig and Ahrens' K_r = ξ²/(5.5 + ξ²) with the shore's Iribarren number ξ = tan α / sqrt(H/L₀), at most 0.9
        /// (a sea wall's), only of waves running at the shore, and faded out with the coast's trust (by
        /// <see cref="FadeFar"/>). A beach throws back a few per cent, a cliff in deep water nearly all.
        /// </summary>
        public static double Reflection(in CoastSample c, double kx, double kz, double omega, double hs)
        {
            double l = Math.Sqrt(c.AwayX * c.AwayX + c.AwayZ * c.AwayZ), k = Math.Sqrt(kx * kx + kz * kz);
            if (!(c.ShoreSlope > 0) || l <= 1e-9 || k < 1e-7 || !(omega > 0)) return 0;
            double toward = -(kx * c.AwayX + kz * c.AwayZ) / (k * l);
            double l0 = 2 * Math.PI * Gravity / (omega * omega), xi = c.ShoreSlope / Math.Sqrt(Math.Max(hs, 0.05) / l0);
            return Math.Min(0.9, xi * xi / (5.5 + xi * xi)) * Math.Min(l, 1) * SmoothStep(0, 0.3, toward);
        }

        /// <summary>
        /// The longshore current here (m/s, world x/z; <see cref="Longshore(double, double, double, double, double, double, double)"/>)
        /// from these trains' amplitudes times what reaches here (<see cref="Reach"/>): their energy-weighted direction and
        /// angular speed, times the coast's trust. The shader's sea_CoastAt works out the same.
        /// </summary>
        public static (double X, double Z) LongshoreAt(in CoastSample c, int count, double[] kx, double[] kz, double[] omega, double[] amp, ReadOnlySpan<double> reach)
        {
            double t = c.Trust;
            if (!c.Coastal || t <= 0) return (0, 0);
            double ex = 0, ez = 0, e = 0, w = 0;
            for (int i = 0; i < count; i++)
            {
                double k = Math.Sqrt(kx[i] * kx[i] + kz[i] * kz[i]);
                if (k < 1e-7) continue;
                double a = amp[i] * reach[i], a2 = a * a;
                ex += a2 * kx[i] / k; ez += a2 * kz[i] / k; e += a2; w += a2 * omega[i];
            }
            double l = Math.Sqrt(ex * ex + ez * ez), al = Math.Sqrt(c.AwayX * c.AwayX + c.AwayZ * c.AwayZ);
            if (e <= 0 || l < 1e-9 || al < 1e-9) return (0, 0);
            var v = Longshore(c.Depth, c.AwayX / al, c.AwayZ / al, 4 * Math.Sqrt(e / 2), w / e, ex / l, ez / l);
            return (v.X * t, v.Z * t);
        }

        /// <summary>
        /// Depth-limited breaking at a place with these trains (deep-water wavenumber, angular speed, amplitude): the factor
        /// every train's amplitude is cut by (H_s ≤ γ h, more in a rip's deeper channel), and how far the waves are
        /// breaking, 0 (not) to 1 (less in a rip: the gap in the breakers that shows one). Each train as the lee lets it
        /// through (no wind sea of its own).
        /// </summary>
        public static (double Cap, double Breaking) Breaking(in CoastSample c, int count, double[] kx, double[] kz, double[] omega, double[] amp)
        {
            Span<double> reach = stackalloc double[Math.Max(count, 1)];
            for (int i = 0; i < count; i++) reach[i] = Lee(c, kx[i], kz[i]);
            return Breaking(c, count, kx, kz, omega, amp, reach);
        }

        /// <summary>Depth-limited breaking (as above) with each train's share that reaches here given (<see cref="Reach"/>).</summary>
        public static (double Cap, double Breaking) Breaking(in CoastSample c, int count, double[] kx, double[] kz, double[] omega, double[] amp, ReadOnlySpan<double> reach)
        {
            if (!(c.Depth < MaxDepth)) return (1, 0);
            double e = 0;
            for (int i = 0; i < count; i++)
            {
                double k = Math.Sqrt(kx[i] * kx[i] + kz[i] * kz[i]), s = SurfShare(k, c), ks = s > 0 ? Shoaling(omega[i], c.Depth) : 1;
                double a = amp[i] * reach[i];
                e += 0.5 * a * a * ((1 - s) * (1 - s) + s * s * ks * ks);
            }
            double hs = 4 * Math.Sqrt(e), limit = Gamma * Math.Max(c.Depth, 0) * (1 + 0.5 * c.Rip);
            double ratio = hs / Math.Max(limit, 1e-4);
            return (ratio > 1 ? 1 / ratio : 1, SmoothStep(0.6, 1.0, ratio) * (1 - 0.6 * c.Rip));
        }

        /// <summary>
        /// The mean over a wave of the breaker's (½ + ½ sin ψ)^p when breaking b (so the shape keeps the still-water level):
        /// a cubic fitted to it within 0.0012 (0.5 exactly unbroken).
        /// </summary>
        public static double ProfileMean(double b) => 0.5 + b * (-0.3517 + b * (0.2331 - 0.0985 * b));

        /// <summary>
        /// A surf train's shape at phase θ when breaking b (0 to 1): sin θ when not breaking; then a peaked crest (to about 1.43),
        /// a flat trough and a front steepened toward the shore. Returns the shape, its derivative by θ, and how near
        /// the crest (0 trough to 1 crest) this is, for foam.
        /// </summary>
        public static (double Value, double Slope, double Crest) Breaker(double theta, double b)
        {
            double p = 1 + 2 * b, al = 0.6 * b;
            double psi = theta - al * Math.Sin(theta);
            double u = 0.5 + 0.5 * Math.Sin(psi);
            double value = 2 * (Math.Pow(Math.Max(u, 0), p) - ProfileMean(b));
            double slope = p * Math.Pow(Math.Max(u, 1e-6), p - 1) * Math.Cos(psi) * (1 - al * Math.Cos(theta));
            return (value, slope, u);
        }

        /// <summary>
        /// Whitewater at a place breaking b, from its surf trains' summed amplitude and their amplitude-weighted crest
        /// nearness cubed: a thin film over the surf zone, white on and just behind the breaking crests.
        /// </summary>
        public static double SurfFoam(double b, double sum, double crest) =>
            sum > 1e-4 ? b * (0.2 + 0.8 * SmoothStep(0.35, 0.85, crest / sum)) : 0;

        /// <summary>
        /// The longshore current (m/s, world x/z) at depth h in a surf zone, Komar (1979): V = 1.17 sqrt(g H_b) sin α_b cos α_b
        /// at the breaker line, the angle refracted there by Snell's law from deep water (speed g/ω), spread across the
        /// surf zone as sin(π h / h_b), with h_b = H_s / γ. (awayX, awayZ): unit direction away from the shore;
        /// (towardX, towardZ): unit direction the waves go; hs: significant height reaching the coast; omega: their angular speed.
        /// </summary>
        public static (double X, double Z) Longshore(double h, double awayX, double awayZ, double hs, double omega, double towardX, double towardZ)
        {
            if (!(hs > 0) || !(h > 0) || !(omega > 0)) return (0, 0);
            double hb = hs / Gamma;
            if (h >= hb) return (0, 0);
            // the shore-normal (into the beach) and the tangent
            double nx = -awayX, nz = -awayZ, tx = -nz, tz = nx;
            double cos0 = towardX * nx + towardZ * nz, sin0 = towardX * tx + towardZ * tz;
            if (cos0 <= 0) return (0, 0);
            double c0 = Gravity / omega, cb = Math.Sqrt(Gravity * hb);
            double sinb = Math.Clamp(sin0 * cb / c0, -1, 1), cosb = Math.Sqrt(1 - sinb * sinb);
            // (waves running along the shore without reaching it drive nothing: faded in, so the current has no jump)
            double v = 1.17 * Math.Sqrt(Gravity * hs) * sinb * cosb * Math.Sin(Math.PI * h / hb) * SmoothStep(0, 0.2, cos0);
            return (v * tx, v * tz);
        }

        /// <summary>
        /// A rip's current (m/s, world x/z) at depth h: straight out to sea (awayX, awayZ: unit), from the beach through
        /// the surf zone (h_b = H_s / γ) and fading out by twice its depth, where the jet spreads.
        /// </summary>
        public static (double X, double Z) RipCurrent(double rip, double h, double awayX, double awayZ, double hs)
        {
            if (!(rip > 0) || !(hs > 0) || !(h > 0)) return (0, 0);
            double hb = hs / Gamma, v = rip * RipSpeed(hs) * (1 - SmoothStep(hb, 2 * hb, h)) * SmoothStep(0, 0.4, h);
            return (v * awayX, v * awayZ);
        }

        /// <summary>
        /// Run-up on a beach (Stockdon et al. 2006, the 2 % level, m above the still water): offshore height h0 and peak
        /// period, on a beach of this slope.
        /// </summary>
        public static double RunUp(double h0, double period, double beachSlope)
        {
            if (!(h0 > 0) || !(period > 0)) return 0;
            double l0 = Gravity * period * period / (2 * Math.PI), hl = Math.Sqrt(h0 * l0);
            double b = Math.Clamp(beachSlope, 0.01, 0.5);
            return 1.1 * (0.35 * b * hl + 0.5 * Math.Sqrt(h0 * l0 * (0.563 * b * b + 0.004)));
        }
    }
}
