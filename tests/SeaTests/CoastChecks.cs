using System;
using Sea;

namespace SeaTests
{
    /// <summary>(0.6.0) Waves on a beach: the shore's travel-time field, shoaling, breaking, the breaker shape, the current.</summary>
    static class CoastChecks
    {
        const int W = CoastMath.Window, M = CoastMath.Margin, T = CoastMath.Tile;

        // a window with land where land(x, z) and depth(x, z) elsewhere (window coordinates)
        static float[] Tile(Func<int, int, bool> land, Func<int, int, float> depth, out float[] time, out float[] dist, out float[] raw)
        {
            raw = new float[W * W];
            for (int z = 0; z < W; z++) for (int x = 0; x < W; x++) raw[z * W + x] = land(x, z) ? 0 : depth(x, z);
            time = new float[W * W]; dist = new float[W * W];
            CoastMath.Solve(raw, W, time, dist);
            return CoastMath.TileOf(raw, time, dist, W, M, T);
        }

        public static void Run(Action<bool, string> check)
        {
            // a straight north-south shore: land west of window x = 70, 4 blocks of water east of it
            var tile = Tile((x, z) => x < 70, (x, z) => 4, out var time, out var dist, out _);
            double c = Math.Sqrt(9.81 * 4);
            int i = (M + 10) * W + 90;   // window (90, 74): 21 blocks east of the first water column (70)
            check(Math.Abs(time[i] - 21 / c) < 0.5 / c, $"coast: travel time from a straight shore is distance / sqrt(g h) ({time[i]:0.000} vs {21 / c:0.000})");
            check(Math.Abs(dist[i] - 21) < 0.5, "coast: distance from the shore line");
            int o = (10 * T + (90 - M)) * 4;
            check(Math.Abs(tile[o + 2] - 1) < 1e-3 && Math.Abs(tile[o + 3]) < 1e-3 && tile[o] == 4, "coast: away from a straight west shore is east, trusted, depth kept");
            int land = (10 * T + 2) * 4;   // window x = 66: land
            check(tile[land] == 0 && tile[land + 1] == 0 && tile[land + 2] == 0, "coast: land is depth 0, no time, no direction");

            // a diagonal shore (land where x + z < 150): away is (1, 1) / sqrt 2 within a few degrees
            tile = Tile((x, z) => x + z < 150, (x, z) => 6, out time, out _, out _);
            o = (16 * T + 16) * 4;   // window (80, 80): 10 blocks from x + z = 150 along the diagonal
            double ang = Math.Atan2(tile[o + 3], tile[o + 2]) * 180 / Math.PI;
            check(Math.Abs(ang - 45) < 4, $"coast: a diagonal shore turns the crests with it (away {ang:0.0} degrees)");

            // trust fades with distance from the shore; a window with no shore has none
            check(CoastMath.Fade(5) == 1 && CoastMath.Fade(CoastMath.FadeFar + 1) == 0, "coast: trusted near the shore, not beyond FadeFar");
            tile = Tile((x, z) => false, (x, z) => 10, out time, out _, out _);
            check(float.IsPositiveInfinity(time[80 * W + 80]) && tile[(5 * T + 5) * 4 + 2] == 0 && tile[(5 * T + 5) * 4 + 3] == 0,
                "coast: open water with no shore in reach has no time and no direction");
            tile = Tile((x, z) => x < 70, (x, z) => float.NaN, out _, out _, out _);
            check(tile[(3 * T + 20) * 4] == CoastMath.MaxDepth, "coast: unknown depth counts as deep");

            // the breaker shape
            double maxErr = 0, mean = 0, top = double.MinValue, front = 0, back = 0;
            const int N = 4000;
            for (int k = 0; k < N; k++)
            {
                double th = 2 * Math.PI * k / N;
                maxErr = Math.Max(maxErr, Math.Abs(CoastMath.Breaker(th, 0).Value - Math.Sin(th)) + Math.Abs(CoastMath.Breaker(th, 0).Slope - Math.Cos(th)));
                var b = CoastMath.Breaker(th, 1);
                mean += b.Value / N;
                top = Math.Max(top, b.Value);
                // shoreward is increasing phase: the front is where the shape falls with theta
                front = Math.Min(front, b.Slope); back = Math.Max(back, b.Slope);
                double d = 1e-5, fd = (CoastMath.Breaker(th + d, 0.7).Value - CoastMath.Breaker(th - d, 0.7).Value) / (2 * d);
                maxErr = Math.Max(maxErr, Math.Abs(fd - CoastMath.Breaker(th, 0.7).Slope) * 1e-3);
            }
            check(maxErr < 1e-6, "breaker: unbroken it's a sine; its slope is its derivative");
            check(Math.Abs(mean) < 0.005, $"breaker: keeps about the still-water level (mean {mean:0.000})");
            check(top > 1.25 && top < 1.45, $"breaker: a peaked crest ({top:0.000} of the amplitude)");
            check(-front > 1.5 * back, $"breaker: the shoreward face is steeper than the back ({-front:0.00} vs {back:0.00})");

            // shoaling and depth-limited breaking
            check(CoastMath.Shoaling(0.6, 30) == 1 && CoastMath.Shoaling(0.6, 1) > 1.3, "shoaling: none offshore, waves grow as the water shallows");
            var shallow = new CoastSample { Depth = 1.5, Time = 3, AwayX = 1 };
            double[] kx = { 0.06, 0.1 }, kz = { 0, 0.02 }, om = { Math.Sqrt(9.81 * 0.06), Math.Sqrt(9.81 * Math.Sqrt(0.0104)) }, amp = { 1.0, 0.4 };
            var br = CoastMath.Breaking(shallow, 2, kx, kz, om, amp);
            double e = 0;
            for (int k = 0; k < 2; k++)
            {
                double s = CoastMath.SurfShare(Math.Sqrt(kx[k] * kx[k] + kz[k] * kz[k]), shallow), ks = CoastMath.Shoaling(om[k], 1.5);
                e += 0.5 * Math.Pow(amp[k] * br.Cap, 2) * ((1 - s) * (1 - s) + s * s * ks * ks);
            }
            check(4 * Math.Sqrt(e) <= CoastMath.Gamma * 1.5 + 1e-9 && br.Breaking == 1, "breaking: significant height capped at gamma x depth, fully breaking");
            check(CoastMath.Breaking(CoastSample.Open, 2, kx, kz, om, amp) == (1, 0), "breaking: none on the open sea");

            // the CPU surface on a beach: a lookup over a straight shore (land x < 0, depth 0.05 per block east)
            CoastLookup beach = (double x, double z, out CoastSample s) =>
            {
                s = CoastMath.Bilinear(x, z, (ci, cj, into) =>
                {
                    double cx = ci + 0.5, h = cx < 0 ? 0 : 0.05 * cx;
                    into[0] = (float)Math.Min(h, CoastMath.MaxDepth);
                    into[1] = cx < 0 ? 0 : (float)(2 * Math.Sqrt(Math.Max(cx, 0.1) / (9.81 * 0.05)));   // ∫ dx / sqrt(g s x)
                    into[2] = cx < 0 ? 0 : (float)CoastMath.Fade(cx); into[3] = 0;
                    return true;
                }, new float[4]);
                return true;
            };
            var open = new WavePlace();
            var coast = new WavePlace { Coast = beach };
            // a swell coming in from the east-north-east toward the beach (k pointing west, a little south)
            double[] kx1 = { -0.05 * Math.Cos(0.5) }, kz1 = { 0.05 * Math.Sin(0.5) }, a1 = { 0.8 }, ph = { 0.3 };
            open.SetTrains(1000, 1, kx1, kz1, a1, ph);
            coast.SetTrains(1000, 1, kx1, kz1, a1, ph);
            check(Math.Abs(open.HeightAt(5000, 30) - coast.HeightAt(5000, 30)) < 1e-12, "beach: far offshore (deep) the surface is the open sea's");
            check(Math.Abs(coast.HeightAt(0.3, 7)) < 0.05, "beach: at the water's edge the waves have run out");
            // in the surf zone (s = 1) crests are parallel to the beach: the same height all along a line of equal depth
            double h1 = coast.HeightAt(6, 0), h2 = coast.HeightAt(6, 37), h3 = coast.HeightAt(6, -81);
            check(Math.Abs(h1 - h2) < 1e-6 && Math.Abs(h1 - h3) < 1e-6, "beach: refracted, the surf crests run along the beach");
            double maxH = 0;
            for (int k = 0; k < 400; k++) { coast.SetTrains(1000 + k * 0.05, 1, kx1, kz1, a1, ph); maxH = Math.Max(maxH, coast.HeightAt(10, 0)); }
            check(maxH <= 1.45 * CoastMath.Gamma * 0.5 / (2 * Math.Sqrt(2)) + 0.005 && maxH > 0.05, $"beach: breakers are no higher than the depth allows ({maxH:0.000} at 0.5 deep)");
            coast.SetTrains(1000, 1, kx1, kz1, a1, ph);
            for (double x = 1; x < 400; x += 37.3)
            {
                var q = coast.ParameterAt(x, 11);
                var p = coast.SurfaceAt(q.X, q.Z);
                if (Math.Abs(p.X - x) > 1e-6 || Math.Abs(p.Z - 11) > 1e-6) { check(false, $"beach: the inverse lookup lands on its point (x {x})"); break; }
            }
            check(true, "beach: the inverse lookup lands on its point across the shoals");
            var surf = coast.SurfAt(8, 0);
            check(surf.Breaking > 0.9 && surf.Foam > 0 && surf.Depth > 0.3 && surf.Depth < 0.5, "beach: whitewater where the surf breaks");
            check(open.SurfAt(8, 0) == (0, 0, CoastMath.MaxDepth), "beach: no surf without a coast");

            // the longshore current
            var none = CoastMath.Longshore(1, 1, 0, 2, 0.6, -1, 0);
            check(none == (0, 0), "current: waves square on to the beach push no water along it");
            var v = CoastMath.Longshore(1.6, 1, 0, 2, 0.6, -Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6));
            double speed = Math.Sqrt(v.X * v.X + v.Z * v.Z);
            check(Math.Abs(v.X) < 1e-12 && v.Z > 0 && speed > 0.3 && speed < 1.5,
                $"current: oblique waves drive water along the beach, the way they lean ({speed:0.00} m/s for H_s 2 m at 30 degrees)");
            check(CoastMath.Longshore(4, 1, 0, 2, 0.6, -0.8, 0.6) == (0, 0), "current: none beyond the breaker line");
        }
    }
}
