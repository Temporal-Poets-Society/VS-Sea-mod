extern alias SeaDll;

using System;
using System.Collections.Generic;
using Sea;

namespace SeaTests
{
    /// <summary>(SE-0053, SE-0057) Checks for the Fable pass's coast fixes.</summary>
    static class FablePassChecks
    {
        public static void Run(Action<bool, string> check)
        {
            int w = CoastMath.Window, m = CoastMath.Margin, t = CoastMath.Tile;

            // (SE-0057) a window all deep or all land gives, without a solve, the tile the solve gives
            float[] Solved(float[] depth)
            {
                var time = new float[w * w];
                var dist = new float[w * w];
                CoastMath.Solve(depth, w, time, dist);
                return CoastMath.TileOf(depth, time, dist, w, m, t, 1000, 2000);
            }
            bool Same(float[] a, float[] b)
            {
                if (a == null || b == null || a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i] && !(float.IsNaN(a[i]) && float.IsNaN(b[i]))) return false;
                return true;
            }
            var deep = new float[w * w];
            Array.Fill(deep, float.NaN);
            check(Same(CoastMath.UniformTile(deep, w, m, t), Solved(deep)), "coast (SE-0057): an all-deep window's tile without a solve is the solved one (open sea)");
            var land = new float[w * w];
            var rand = new Random(3);
            for (int i = 0; i < land.Length; i++) land[i] = -(new[] { 0.125f, 1.125f, 2.125f })[rand.Next(3)];
            check(Same(CoastMath.UniformTile(land, w, m, t), Solved(land)), "coast (SE-0057): an all-land window's tile without a solve is the solved one");
            var mixed = (float[])deep.Clone();
            mixed[w * w / 2] = 5f;
            check(CoastMath.UniformTile(mixed, w, m, t) == null, "coast (SE-0057): a window with shallow water is solved");
            mixed = (float[])deep.Clone();
            mixed[3] = -1.125f;
            check(CoastMath.UniformTile(mixed, w, m, t) == null, "coast (SE-0057): a window with land and deep water is solved (the lee)");

            // (SE-0058) the swash's reach per cell from the per-train part (TrainLee) is CoastMath.Reach
            {
                var c = new CoastSample { Lee0 = 0.9, Lee1 = 0.3, Lee2 = 0.05, Lee3 = 0.6, Lee4 = 1, Lee5 = 0.2, Lee6 = 0.75, Lee7 = 0.4 };
                double[] lees = { c.Lee0, c.Lee1, c.Lee2, c.Lee3, c.Lee4, c.Lee5, c.Lee6, c.Lee7 };
                var tl = new float[4];
                double worst = 0;
                for (int k = 0; k < 200; k++)
                {
                    double a = k * 0.0314159 * 1.7, kk = 0.01 + 0.004 * (k % 50), om = Math.Sqrt(9.81 * kk);
                    double kx = kk * Math.Cos(a), kz = kk * Math.Sin(a), wx = 9 * Math.Cos(1.1), wz = 9 * Math.Sin(1.1);
                    CoastMath.TrainLee(kx, kz, om, wx, wz, tl, 0);
                    double r;
                    if (tl[0] < 0) r = 1;
                    else
                    {
                        int s0 = (int)tl[0], s1 = (s0 + 1) & 7;
                        double l = lees[s0] * (1 - tl[1]) + lees[s1] * tl[1];
                        r = tl[2] > 0 ? l + (Math.Pow(Math.Max(l, 0), tl[3]) - l) * tl[2] : l;
                    }
                    worst = Math.Max(worst, Math.Abs(r - CoastMath.Reach(c, kx, kz, om, wx, wz)));
                }
                check(worst < 1e-5, $"coast (SE-0058): the swash's reach from the per-train part equals CoastMath.Reach (largest difference {worst:E1})");
            }

            // (SE-0064) a channel 40 blocks wide and 4 deep (shores at x = -20 and +20, running along z): across its middle the
            // surf's phase used to tear (a step of about 0.5 blocks between neighbouring points); now it fades out there
            {
                var depth = new float[w * w];
                for (int j = 0; j < w; j++)
                    for (int i = 0; i < w; i++)
                    {
                        double x = i - m - t / 2 + 0.5;   // the channel's middle at the tile's middle
                        depth[j * w + i] = Math.Abs(x) > 20 ? -1.125f : 4f;
                    }
                var time = new float[w * w];
                var dist = new float[w * w];
                CoastMath.Solve(depth, w, time, dist);
                var tile = CoastMath.TileOf(depth, time, dist, w, m, t);
                // the tile's columns at blocks (x, z) = (i - t/2, j - t/2); open sea off the tile
                CoastLookup look = (double x, double z, out CoastSample cs) =>
                {
                    cs = CoastMath.Bilinear(x, z, (ci, cj, into) =>
                    {
                        long ti = ci + t / 2, tj = cj + t / 2;
                        if (ti < 0 || tj < 0 || ti >= t || tj >= t) return false;
                        Array.Copy(tile, (tj * t + ti) * CoastMath.Stride, into, 0, CoastMath.Stride);
                        return true;
                    }, new float[CoastMath.Stride]);
                    return true;
                };
                var place = new WavePlace { Coast = look };
                double kk = 2 * Math.PI / 45;
                double worstMid = 0, worstSide = 0;
                foreach (double a in new[] { 0.0, 0.6, 1.2 })
                {
                    place.SetTrains(1000 + a * 10, 1, new[] { kk * Math.Sin(a) }, new[] { kk * Math.Cos(a) }, new[] { 0.05 }, new[] { 0.3 });
                    for (double z = -8; z <= 8; z += 2)
                        for (double x = -17; x < 17; x += 0.5)
                        {
                            double step = Math.Abs(place.HeightAt(x + 0.5, z) - place.HeightAt(x, z));
                            if (Math.Abs(x + 0.25) < 3) worstMid = Math.Max(worstMid, step); else worstSide = Math.Max(worstSide, step);
                        }
                }
                check(worstMid < 2 * worstSide + 0.002,
                    $"coast (SE-0064): no tear along a channel's midline (largest step between points half a block apart {worstMid:0.000} at the middle, {worstSide:0.000} elsewhere)");
            }

            // (SE-0053) on a ring the coast map's tiles wrap: the tile past the seam is the tile at its start
            var cache = new Dictionary<string, object>();
            var field = new SeaDll::Sea.CoastField(FakeApi.Make(cache, out _));
            field.SetRing(16384, 4007936);
            long start = 16384 / 32, n = 4007936 / 32;
            check(!field.RingMisaligned && field.WrapTile(start + n) == start && field.WrapTile(start - 1) == start + n - 1 && field.WrapTile(start + 5) == start + 5,
                "coast (SE-0053): Cylinder World's ring (4007936 blocks from 16384) fits the tiles, and tiles past the seam are those at its start");
            field.SetRing(100, 1000);
            check(field.RingMisaligned, "coast (SE-0053): a ring that doesn't fit the 32-block tiles keeps the coast off");
            field.SetRing(0, 0);
            check(!field.RingMisaligned && field.WrapTile(-7) == -7, "coast (SE-0053): without a ring nothing wraps");
        }
    }
}
