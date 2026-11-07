using System;
using System.Diagnostics;
using Sea;

namespace SeaTests
{
    /// <summary>
    /// (0.6.0) Waves on a coast: the shore's travel-time field, shoaling, breaking, the breaker shape, the currents, the
    /// lee of land, rip channels, the beach and run-up, and what a tile costs to solve.
    /// </summary>
    static class CoastChecks
    {
        const int W = CoastMath.Window, M = CoastMath.Margin, T = CoastMath.Tile, S = CoastMath.Stride;

        // a window with land where land(x, z) and depth(x, z) elsewhere (window coordinates)
        static float[] Tile(Func<int, int, bool> land, Func<int, int, float> depth, out float[] time, out float[] dist, out float[] raw, long ox = 0, long oz = 0)
        {
            raw = new float[W * W];
            for (int z = 0; z < W; z++) for (int x = 0; x < W; x++) raw[z * W + x] = land(x, z) ? -0.125f : depth(x, z);
            time = new float[W * W]; dist = new float[W * W];
            CoastMath.Solve(raw, W, time, dist);
            return CoastMath.TileOf(raw, time, dist, W, M, T, ox, oz);
        }

        // tile column (x, z) in window coordinates
        static int At(int wx, int wz) => ((wz - M) * T + (wx - M)) * S;

        static CoastSample Sample(float[] tile, int wx, int wz)
        {
            var c = new CoastSample();
            int o = At(wx, wz);
            c.Depth = tile[o]; c.Time = tile[o + 1]; c.AwayX = tile[o + 2]; c.AwayZ = tile[o + 3];
            c.Lee0 = tile[o + 4]; c.Lee1 = tile[o + 5]; c.Lee2 = tile[o + 6]; c.Lee3 = tile[o + 7];
            c.Lee4 = tile[o + 8]; c.Lee5 = tile[o + 9]; c.Lee6 = tile[o + 10]; c.Lee7 = tile[o + 11];
            c.Rip = tile[o + CoastMath.IRip];
            return c;
        }

        public static void Run(Action<bool, string> check)
        {
            // a straight north-south shore: land west of window x = 70, 4 blocks of water east of it
            var tile = Tile((x, z) => x < 70, (x, z) => 4, out var time, out var dist, out _);
            double c = Math.Sqrt(9.81 * 4);
            int i = (M + 10) * W + 90;   // window (90, 74): 21 blocks east of the first water column (70)
            check(Math.Abs(time[i] - 21 / c) < 0.5 / c, $"coast: travel time from a straight shore is distance / sqrt(g h) ({time[i]:0.000} vs {21 / c:0.000})");
            check(Math.Abs(dist[i] - 21) < 0.5, "coast: distance from the shore line");
            int o = At(90, M + 10);
            check(Math.Abs(tile[o + 2] - 1) < 1e-3 && Math.Abs(tile[o + 3]) < 1e-3 && tile[o] == 4, "coast: away from a straight west shore is east, trusted, depth kept");
            int land = At(66, M + 10);
            check(tile[land] == 0 && tile[land + 1] == 0 && tile[land + 2] == 0, "coast: land is depth 0, no time, no direction");
            check(Math.Abs(tile[land + CoastMath.ILandDist] - 4) < 1e-4 && Math.Abs(tile[land + CoastMath.ILandHeight] - 0.125f) < 1e-6,
                "beach: land knows how far it is from the water and how high its top is");
            // open water facing the open east: every direction from the sea side is open; from the land side (west) it's sheltered
            var open = Sample(tile, 90, M + 10);
            check(open.Lee0 == 1 && open.Lee1 == 1 && open.Lee7 == 1 && open.Lee4 < 0.6,
                $"lee: open to the sea (east {open.Lee0:0.00}), sheltered from the land (west {open.Lee4:0.00})");

            // a diagonal shore (land where x + z < 150): away is (1, 1) / sqrt 2 within a few degrees
            tile = Tile((x, z) => x + z < 150, (x, z) => 6, out time, out _, out _);
            o = At(80, 80);   // window (80, 80): 10 blocks from x + z = 150 along the diagonal
            double ang = Math.Atan2(tile[o + 3], tile[o + 2]) * 180 / Math.PI;
            check(Math.Abs(ang - 45) < 4, $"coast: a diagonal shore turns the crests with it (away {ang:0.0} degrees)");

            // trust fades with distance from the shore; a window with no shore has none
            check(CoastMath.Fade(5) == 1 && CoastMath.Fade(CoastMath.FadeFar + 1) == 0, "coast: trusted near the shore, not beyond FadeFar");
            tile = Tile((x, z) => false, (x, z) => 10, out time, out _, out _);
            var none = Sample(tile, M + 5, M + 5);
            check(float.IsPositiveInfinity(time[80 * W + 80]) && none.AwayX == 0 && none.AwayZ == 0 && none.Lee3 == 1 && none.Rip == 0,
                "coast: open water with no shore in reach has no time, no direction, no lee, no rip");
            tile = Tile((x, z) => x < 70, (x, z) => float.NaN, out _, out _, out _);
            check(tile[At(M + 20, M + 3)] == CoastMath.MaxDepth, "coast: unknown depth counts as deep");

            // the lee of an island: deep open sea with a round island 9 blocks across; waves from the east (direction 0)
            tile = Tile((x, z) => (x - 96) * (x - 96) + (z - 80) * (z - 80) < 20, (x, z) => float.NaN, out _, out _, out _);
            var behind = Sample(tile, 80, 80);       // 16 blocks west of its centre: in the lee of waves from the east
            var beside = Sample(tile, 80, 66);       // 14 north of the line: out of it
            var far = Sample(tile, M + 1, 80);       // 31 behind it
            check(behind.Lee0 < 0.7 && behind.Lee4 == 1 && beside.Lee0 > 0.9,
                $"lee: behind an island the waves from beyond it are cut ({behind.Lee0:0.00}), not those from the other side or beside it ({beside.Lee0:0.00})");
            check(far.Lee0 > behind.Lee0 && far.Lee0 < 1, $"lee: the waves come back with distance behind it ({behind.Lee0:0.00} at 16, {far.Lee0:0.00} at 31)");
            check(behind.Affects && !behind.Coastal, "lee: deep water in a lee is affected though it isn't coastal");
            double ky = -0.05;   // a train going west: from the east
            check(Math.Abs(CoastMath.Lee(behind, ky, 0) - behind.Lee0) < 1e-12 && Math.Abs(CoastMath.Lee(behind, -ky, 0) - behind.Lee4) < 1e-12
                && Math.Abs(CoastMath.Lee(behind, ky, ky) - behind.Lee7) < 1e-9, "lee: a train takes its own direction's share (k toward the west is from the east)");
            double half = CoastMath.Lee(behind, ky * Math.Cos(Math.PI / 8), ky * Math.Sin(Math.PI / 8));
            check(Math.Abs(half - 0.5 * (behind.Lee0 + behind.Lee7)) < 1e-9, "lee: between two directions it's interpolated");

            // rip channels: fixed places along a gentle beach, about RipSpacing apart, straight out to sea; none at cliffs
            int found = 0, steep = 0;
            double first = double.NaN, spacing = 0;
            int gaps = 0;
            bool inRip = false;
            for (int z = 0; z < 4000; z++)
            {
                double r = CoastMath.RipChannel(0, z, 10, 0.05);
                if (r > 0.5 && !inRip) { if (found > 0) { spacing += z - first; gaps++; } first = z; found++; }
                inRip = r > 0.5;
                if (CoastMath.RipChannel(0, z, 10, 0.4) > 0) steep++;
            }
            check(found > 10 && steep == 0, $"rip: channels along a gentle beach ({found} in 4000 blocks), none off a steep floor");
            check(gaps > 0 && spacing / gaps > 70 && spacing / gaps < 400, $"rip: about {spacing / Math.Max(gaps, 1):0} blocks apart");
            check(CoastMath.RipChannel(0, first, 60, 0.05) == 0, "rip: a channel ends beyond the surf zone");
            // a tile places them the same wherever it's solved from (world-anchored)
            // (the shore at world x 1070 both times; rows at a world z where the beach has a channel)
            long rz = 0;
            while (CoastMath.RipChannel(1070, rz + M + 16, 10, 0.06) < 0.9) rz++;
            var a = Tile((x, z) => x < 70, (x, z) => 0.4f + 0.06f * (x - 70), out _, out _, out _, 1000, rz);
            var b = Tile((x, z) => x < 78, (x, z) => 0.4f + 0.06f * (x - 78), out _, out _, out _, 992, rz);
            // the same world column is window x in the first and x + 8 in the second
            double ripDiff = 0, ripMax = 0;
            for (int z = M; z < M + T; z++)
                for (int x = 70; x + 8 < M + T; x++)
                {
                    ripMax = Math.Max(ripMax, a[At(x, z) + CoastMath.IRip]);
                    ripDiff = Math.Max(ripDiff, Math.Abs(a[At(x, z) + CoastMath.IRip] - b[At(x + 8, z) + CoastMath.IRip]));
                }
            check(ripMax > 0.5, $"rip: a tile over a channel has it ({ripMax:0.00})");
            check(ripDiff < 1e-5, $"rip: the same channels wherever the tile's window starts (max difference {ripDiff:E1})");

            // what a rip does: a gap in the breakers, the surf held back, a seaward current
            double[] kx = { 0.06, 0.1 }, kz = { 0, 0.02 }, om = { Math.Sqrt(9.81 * 0.06), Math.Sqrt(9.81 * Math.Sqrt(0.0104)) }, amp = { 1.0, 0.4 };
            var shallow = new CoastSample { Depth = 1.5, Time = 3, AwayX = 1, Lee0 = 1, Lee1 = 1, Lee2 = 1, Lee3 = 1, Lee4 = 1, Lee5 = 1, Lee6 = 1, Lee7 = 1 };
            var ripped = shallow; ripped.Rip = 1;
            var br0 = CoastMath.Breaking(shallow, 2, kx, kz, om, amp);
            var br1 = CoastMath.Breaking(ripped, 2, kx, kz, om, amp);
            check(br1.Breaking < 0.5 * br0.Breaking && br1.Cap > br0.Cap, $"rip: less breaking over the channel ({br1.Breaking:0.00} vs {br0.Breaking:0.00})");
            check(CoastMath.SurfTime(shallow, 2) == 3 && CoastMath.SurfTime(ripped, 2) > 3.3 && CoastMath.SurfTime(ripped, 0) == 3,
                $"rip: the current holds the waves back ({CoastMath.SurfTime(ripped, 2):0.00} s of travel instead of 3)");
            var rc = CoastMath.RipCurrent(1, 1.5, 1, 0, 2);
            check(rc.X > 0.4 && rc.X < 1.6 && rc.Z == 0 && CoastMath.RipCurrent(1, 8, 1, 0, 2) == (0, 0),
                $"rip: the current runs straight out to sea ({rc.X:0.00} m/s for H_s 2 m) and spreads out beyond the surf");

            // run-up (Stockdon et al. 2006)
            double ru = CoastMath.RunUp(1, 8, 0.1), flat = CoastMath.RunUp(1, 8, 0.02), big = CoastMath.RunUp(2, 8, 0.1);
            check(ru > 0.7 && ru < 1.2 && flat < ru && big > ru && CoastMath.RunUp(0, 8, 0.1) == 0,
                $"swash: run-up {ru:0.00} m for 1 m waves at 8 s on a 1:10 beach, less on a flat one ({flat:0.00}), more for higher waves ({big:0.00})");
            tile = Tile((x, z) => x < 70, (x, z) => 0.2f + 0.08f * (x - 70), out _, out _, out _);
            double slope = tile[At(66, M + 10) + CoastMath.IBeachSlope];
            check(Math.Abs(slope - 0.08) < 0.02, $"swash: a beach knows the slope of the floor off it ({slope:0.000})");

            // the breaker shape
            double maxErr = 0, mean = 0, top = double.MinValue, front = 0, back = 0;
            const int N = 4000;
            for (int k = 0; k < N; k++)
            {
                double th = 2 * Math.PI * k / N;
                maxErr = Math.Max(maxErr, Math.Abs(CoastMath.Breaker(th, 0).Value - Math.Sin(th)) + Math.Abs(CoastMath.Breaker(th, 0).Slope - Math.Cos(th)));
                var bk = CoastMath.Breaker(th, 1);
                mean += bk.Value / N;
                top = Math.Max(top, bk.Value);
                // shoreward is increasing phase: the front is where the shape falls with theta
                front = Math.Min(front, bk.Slope); back = Math.Max(back, bk.Slope);
                double d = 1e-5, fd = (CoastMath.Breaker(th + d, 0.7).Value - CoastMath.Breaker(th - d, 0.7).Value) / (2 * d);
                maxErr = Math.Max(maxErr, Math.Abs(fd - CoastMath.Breaker(th, 0.7).Slope) * 1e-3);
            }
            check(maxErr < 1e-6, "breaker: unbroken it's a sine; its slope is its derivative");
            check(Math.Abs(mean) < 0.005, $"breaker: keeps about the still-water level (mean {mean:0.000})");
            check(top > 1.25 && top < 1.45, $"breaker: a peaked crest ({top:0.000} of the amplitude)");
            check(-front > 1.5 * back, $"breaker: the shoreward face is steeper than the back ({-front:0.00} vs {back:0.00})");

            // shoaling and depth-limited breaking
            check(CoastMath.Shoaling(0.6, 30) == 1 && CoastMath.Shoaling(0.6, 1) > 1.3, "shoaling: none offshore, waves grow as the water shallows");
            var br = CoastMath.Breaking(shallow, 2, kx, kz, om, amp);
            double e = 0;
            for (int k = 0; k < 2; k++)
            {
                double s = CoastMath.SurfShare(Math.Sqrt(kx[k] * kx[k] + kz[k] * kz[k]), shallow), ks = CoastMath.Shoaling(om[k], 1.5);
                e += 0.5 * Math.Pow(amp[k] * br.Cap, 2) * ((1 - s) * (1 - s) + s * s * ks * ks);
            }
            check(4 * Math.Sqrt(e) <= CoastMath.Gamma * 1.5 + 1e-9 && br.Breaking == 1, "breaking: significant height capped at gamma x depth, fully breaking");
            check(CoastMath.Breaking(CoastSample.Open, 2, kx, kz, om, amp) == (1, 0), "breaking: none on the open sea");
            var lee = shallow; lee.Lee4 = 0; lee.Lee5 = 0; lee.Lee3 = 0;   // sheltered from the west: these trains (k east) come from there
            check(CoastMath.Breaking(lee, 2, kx, kz, om, amp).Breaking < 0.01, "breaking: none where the land keeps the waves off");

            // the CPU surface on a beach: a lookup over a straight shore (land x < 0, depth 0.05 per block east)
            CoastLookup beach = (double x, double z, out CoastSample s) =>
            {
                s = CoastMath.Bilinear(x, z, (ci, cj, into) =>
                {
                    double cx = ci + 0.5, h = cx < 0 ? 0 : 0.05 * cx;
                    CoastMath.OpenColumn(into);
                    into[0] = (float)Math.Min(h, CoastMath.MaxDepth);
                    into[1] = cx < 0 ? 0 : (float)(2 * Math.Sqrt(Math.Max(cx, 0.1) / (9.81 * 0.05)));   // ∫ dx / sqrt(g s x)
                    into[2] = cx < 0 ? 0 : (float)CoastMath.Fade(cx); into[3] = 0;
                    return true;
                }, new float[S]);
                return true;
            };
            var openPlace = new WavePlace();
            var coast = new WavePlace { Coast = beach };
            // a swell coming in from the east-north-east toward the beach (k pointing west, a little south)
            double[] kx1 = { -0.05 * Math.Cos(0.5) }, kz1 = { 0.05 * Math.Sin(0.5) }, a1 = { 0.8 }, ph = { 0.3 };
            openPlace.SetTrains(1000, 1, kx1, kz1, a1, ph);
            coast.SetTrains(1000, 1, kx1, kz1, a1, ph);
            check(Math.Abs(openPlace.HeightAt(5000, 30) - coast.HeightAt(5000, 30)) < 1e-12, "beach: far offshore (deep) the surface is the open sea's");
            check(Math.Abs(coast.HeightAt(0.3, 7)) < 0.05, "beach: at the water's edge the waves have run out");
            // in the surf zone (s = 1) crests are parallel to the beach: the same height all along a line of equal depth
            double h1 = coast.HeightAt(6, 0), h2 = coast.HeightAt(6, 37), h3 = coast.HeightAt(6, -81);
            check(Math.Abs(h1 - h2) < 1e-6 && Math.Abs(h1 - h3) < 1e-6, "beach: refracted, the surf crests run along the beach");
            double maxH = 0;
            for (int k = 0; k < 400; k++) { coast.SetTrains(1000 + k * 0.05, 1, kx1, kz1, a1, ph); maxH = Math.Max(maxH, coast.HeightAt(10, 0)); }
            check(maxH <= 1.45 * CoastMath.Gamma * 0.5 / (2 * Math.Sqrt(2)) + 0.005 && maxH > 0.05, $"beach: breakers are no higher than the depth allows ({maxH:0.000} at 0.5 deep)");
            coast.SetTrains(1000, 1, kx1, kz1, a1, ph);
            bool inverse = true;
            for (double x = 1; x < 400; x += 37.3)
            {
                var q = coast.ParameterAt(x, 11);
                var p = coast.SurfaceAt(q.X, q.Z);
                if (Math.Abs(p.X - x) > 1e-6 || Math.Abs(p.Z - 11) > 1e-6) { inverse = false; break; }
            }
            check(inverse, "beach: the inverse lookup lands on its point across the shoals");
            var surf = coast.SurfAt(8, 0);
            check(surf.Breaking > 0.9 && surf.Foam > 0 && surf.Depth > 0.3 && surf.Depth < 0.5, "beach: whitewater where the surf breaks");
            check(openPlace.SurfAt(8, 0) == (0, 0, CoastMath.MaxDepth), "beach: no surf without a coast");

            // the same beach, sheltered from the east: the swell from the east-north-east is cut there
            CoastLookup lee2 = (double x, double z, out CoastSample s) =>
            {
                beach(x, z, out s);
                s.Lee0 = 0.2; s.Lee7 = 0.2;
                return true;
            };
            var sheltered = new WavePlace { Coast = lee2 };
            sheltered.SetTrains(1000, 1, kx1, kz1, a1, ph);
            double hOpen = 0, hLee = 0;
            for (int k = 0; k < 200; k++)
            {
                coast.SetTrains(1000 + k * 0.1, 1, kx1, kz1, a1, ph); sheltered.SetTrains(1000 + k * 0.1, 1, kx1, kz1, a1, ph);
                hOpen = Math.Max(hOpen, Math.Abs(coast.HeightAt(5000, 30))); hLee = Math.Max(hLee, Math.Abs(sheltered.HeightAt(5000, 30)));
            }
            check(Math.Abs(hLee / hOpen - 0.2) < 0.01, $"lee: the surface in a lee carries its share of the waves ({hLee / hOpen:0.000})");

            // the longshore current
            var still = CoastMath.Longshore(1, 1, 0, 2, 0.6, -1, 0);
            check(still == (0, 0), "current: waves square on to the beach push no water along it");
            var v = CoastMath.Longshore(1.6, 1, 0, 2, 0.6, -Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6));
            double speed = Math.Sqrt(v.X * v.X + v.Z * v.Z);
            check(Math.Abs(v.X) < 1e-12 && v.Z > 0 && speed > 0.3 && speed < 1.5,
                $"current: oblique waves drive water along the beach, the way they lean ({speed:0.00} m/s for H_s 2 m at 30 degrees)");
            check(CoastMath.Longshore(4, 1, 0, 2, 0.6, -0.8, 0.6) == (0, 0), "current: none beyond the breaker line");

            // what a tile costs: a beach with an island off it, solved as the game does (background task)
            var watch = Stopwatch.StartNew();
            const int runs = 6;
            for (int r = 0; r < runs; r++)
                Tile((x, z) => x < 60 + r || (x - 110) * (x - 110) + (z - 70) * (z - 70) < 50, (x, z) => 0.3f + 0.1f * (x - 60), out _, out _, out _, r * 77, 0);
            double ms = watch.Elapsed.TotalMilliseconds / runs;
            Console.WriteLine($"  (a coast tile solves in {ms:0.0} ms on this machine, off the main thread)");
            check(ms < 250, $"cost: a coast tile solves in {ms:0.0} ms (background; the game asks for a few as the camera moves)");
        }
    }
}
