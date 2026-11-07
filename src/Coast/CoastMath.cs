using System;
using System.Collections.Generic;

namespace Sea
{
    /// <summary>
    /// (0.6.0) The coast's numbers at one place, as the water shader reads them from sea_CoastTex: the depth to the sea
    /// floor (blocks; 0 on land, <see cref="CoastMath.MaxDepth"/> where it's deep or unknown), the time a shallow-water
    /// wave takes from here to the shore line (seconds), and the direction away from the shore (the way that time grows)
    /// times how much of the coast's effect is trusted here (<see cref="CoastMath.Fade"/>).
    /// </summary>
    public struct CoastSample
    {
        public double Depth, Time, AwayX, AwayZ;

        /// <summary>No coast: deep water, as everywhere before 0.6.0.</summary>
        public static readonly CoastSample Open = new() { Depth = CoastMath.MaxDepth };

        /// <summary>How much of the coast's effect is trusted here, 0 to 1 (the length of the away vector).</summary>
        public double Trust => Math.Min(1, Math.Sqrt(AwayX * AwayX + AwayZ * AwayZ));

        /// <summary>Within reach of the coast at all (shallower than <see cref="CoastMath.MaxDepth"/>).</summary>
        public bool Coastal => Depth < CoastMath.MaxDepth;
    }

    /// <summary>A coast lookup: the numbers at a world position (false: none known there, open sea).</summary>
    public delegate bool CoastLookup(double x, double z, out CoastSample sample);

    /// <summary>
    /// (0.6.0) Waves on a beach. The shore's travel-time field (a fast-marching eikonal solve at the shallow-water speed
    /// sqrt(g h)), and what the water shader and the CPU both do with it: the sea's trains hand over to shoreward "surf"
    /// trains that follow the bottom (refraction: crests turn parallel to the beach and bunch up as the water shallows),
    /// grow by shoaling (Green's law), are capped by depth-limited breaking (H_s ≤ γ h), take a breaker's shape (a peaked
    /// crest, flat trough and steep front) as they break, and drive a longshore current in the surf zone (Komar 1979).
    /// Pure: the same numbers in the game, the tests and the shader checks (WaterShader.Geometry mirrors the shape parts).
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

        // ------------------------------------------------------------ the field

        /// <summary>
        /// Solve a window's travel times. depth: Window² (row-major, z rows) depths in blocks; ≤ 0 is land (the shore's
        /// sources), NaN unknown (taken as deep). time: seconds from the shore line at sqrt(g h); dist: blocks from it.
        /// Both +∞ where no shore is in the window.
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

        /// <summary>
        /// A window's tile: per column (row-major, z rows) depth, travel time, and the away-from-shore direction times its
        /// trust, 4 floats each (the texture's RGBA). The direction is the travel time's gradient, smoothed over 3 × 3.
        /// </summary>
        public static float[] TileOf(float[] depth, float[] time, float[] dist, int w, int margin, int tile)
        {
            var gx = new float[w * w];
            var gz = new float[w * w];
            for (int z = 1; z < w - 1; z++)
                for (int x = 1; x < w - 1; x++)
                {
                    int i = z * w + x;
                    if (!(depth[i] > 0) && !float.IsNaN(depth[i]) || float.IsPositiveInfinity(time[i])) continue;
                    double dx = Slope(time, i, 1), dz = Slope(time, i, w);
                    double l = Math.Sqrt(dx * dx + dz * dz);
                    if (l > 1e-9) { gx[i] = (float)(dx / l); gz[i] = (float)(dz / l); }
                }
            var tileData = new float[tile * tile * 4];
            for (int z = 0; z < tile; z++)
                for (int x = 0; x < tile; x++)
                {
                    int wx = x + margin, wz = z + margin, i = wz * w + wx, o = (z * tile + x) * 4;
                    float h = depth[i];
                    bool land = h <= 0;
                    tileData[o] = land ? 0 : float.IsNaN(h) ? MaxDepth : Math.Min(h, MaxDepth);
                    if (land || float.IsPositiveInfinity(time[i])) continue;
                    tileData[o + 1] = time[i];
                    double sx = 0, sz = 0;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int j = (wz + dz) * w + wx + dx;
                            sx += gx[j]; sz += gz[j];
                        }
                    double l = Math.Sqrt(sx * sx + sz * sz), trust = Fade(dist[i]);
                    if (l < 1e-9 || trust <= 0) continue;
                    tileData[o + 2] = (float)(sx / l * trust);
                    tileData[o + 3] = (float)(sz / l * trust);
                }
            return tileData;
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

        /// <summary>Trust in the coast's numbers this far (blocks) from the shore line: 1 near it, 0 from <see cref="FadeFar"/>.</summary>
        public static double Fade(double dist) => 1 - SmoothStep(FadeNear, FadeFar, dist);

        /// <summary>
        /// The numbers at a world position, bilinear between column centres exactly as the shader's sea_Coast does it.
        /// cell(i, j) gives column (i, j)'s four floats (false: no map there; taken as <see cref="CoastSample.Open"/>).
        /// </summary>
        public static CoastSample Bilinear(double x, double z, Func<long, long, float[], bool> cell, float[] scratch)
        {
            double fx = x - 0.5, fz = z - 0.5, ix = Math.Floor(fx), iz = Math.Floor(fz);
            double tx = fx - ix, tz = fz - iz;
            double h = 0, t = 0, ax = 0, az = 0;
            for (int c = 0; c < 4; c++)
            {
                double wgt = ((c & 1) == 1 ? tx : 1 - tx) * ((c & 2) == 2 ? tz : 1 - tz);
                if (!cell((long)ix + (c & 1), (long)iz + (c >> 1), scratch))
                {
                    scratch[0] = MaxDepth; scratch[1] = 0; scratch[2] = 0; scratch[3] = 0;
                }
                h += wgt * scratch[0]; t += wgt * scratch[1]; ax += wgt * scratch[2]; az += wgt * scratch[3];
            }
            return new CoastSample { Depth = h, Time = t, AwayX = ax, AwayZ = az };
        }

        // ------------------------------------------------------------ waves on it (WaterShader.Geometry mirrors these)

        public static double SmoothStep(double e0, double e1, double x)
        {
            double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
            return t * t * (3 - 2 * t);
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

        /// <summary>
        /// Depth-limited breaking at a place with these trains (deep-water wavenumber, angular speed, amplitude): the factor
        /// every train's amplitude is cut by (H_s ≤ γ h), and how far the waves are breaking, 0 (not) to 1.
        /// </summary>
        public static (double Cap, double Breaking) Breaking(in CoastSample c, int count, double[] kx, double[] kz, double[] omega, double[] amp)
        {
            if (!(c.Depth < MaxDepth)) return (1, 0);
            double e = 0;
            for (int i = 0; i < count; i++)
            {
                double k = Math.Sqrt(kx[i] * kx[i] + kz[i] * kz[i]), s = SurfShare(k, c), ks = s > 0 ? Shoaling(omega[i], c.Depth) : 1;
                e += 0.5 * amp[i] * amp[i] * ((1 - s) * (1 - s) + s * s * ks * ks);
            }
            double hs = 4 * Math.Sqrt(e), limit = Gamma * Math.Max(c.Depth, 0);
            double ratio = hs / Math.Max(limit, 1e-4);
            return (ratio > 1 ? 1 / ratio : 1, SmoothStep(0.6, 1.0, ratio));
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
        /// (towardX, towardZ): unit direction the waves go; hs: offshore significant height; omega: their angular speed.
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
            double v = 1.17 * Math.Sqrt(Gravity * hs) * sinb * cosb * Math.Sin(Math.PI * h / hb);
            return (v * tx, v * tz);
        }
    }
}
