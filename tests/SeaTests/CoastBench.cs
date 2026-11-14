using System;
using System.Diagnostics;
using Sea;

namespace SeaTests
{
    /// <summary>
    /// (SE-0041) What the CPU surface costs on a coast and at open sea, for boats, ships, spray and the contracts: a report,
    /// not a check (a busy machine would fail any bound). 24 trains of a storm, a straight beach (depth 0.05 per block).
    /// </summary>
    static class CoastBench
    {
        public static void Run()
        {
            const int S = CoastMath.Stride;
            CoastLookup beach = (double x, double z, out CoastSample s) =>
            {
                s = CoastMath.Bilinear(x, z, (ci, cj, into) =>
                {
                    double cx = ci + 0.5, h = cx < 0 ? 0 : 0.05 * cx;
                    CoastMath.OpenColumn(into);
                    into[0] = (float)Math.Min(h, CoastMath.MaxDepth);
                    into[1] = cx < 0 ? 0 : (float)(2 * Math.Sqrt(Math.Max(cx, 0.1) / (9.81 * 0.05)));
                    into[2] = cx < 0 ? 0 : (float)CoastMath.Fade(cx); into[3] = 0;
                    into[CoastMath.IShoreDist] = (float)Math.Abs(cx);
                    return true;
                }, new float[S]);
                return true;
            };
            var rng = new Random(7);
            int n = 24;
            double[] kx = new double[n], kz = new double[n], amp = new double[n], ph = new double[n];
            for (int i = 0; i < n; i++)
            {
                double k = 0.02 + 0.3 * i / n, a = -2.6 + 0.4 * (rng.NextDouble() - 0.5);
                kx[i] = k * Math.Cos(a); kz[i] = k * Math.Sin(a);
                amp[i] = 0.9 / (1 + 0.6 * i); ph[i] = rng.NextDouble() * 6.28;
            }
            var open = new WavePlace();
            var coast = new WavePlace { Coast = beach };
            open.SetTrains(1000, n, kx, kz, amp, ph);
            coast.SetTrains(1000, n, kx, kz, amp, ph);
            double Time(Func<double, double, double> f, double x0, double x1)
            {
                double sink = 0;
                for (int i = 0; i < 200; i++) sink += f(x0 + (x1 - x0) * i / 200, 3.3);   // warm up
                const int N = 4000;
                var w = Stopwatch.StartNew();
                for (int i = 0; i < N; i++) sink += f(x0 + (x1 - x0) * (i % 997) / 997.0, 3.3 + i % 7);
                double us = w.Elapsed.TotalMilliseconds * 1000 / N;
                return double.IsNaN(sink) ? -1 : us;
            }
            double hOpen = Time(open.HeightAt, 100, 400), hCoast = Time(coast.HeightAt, 2, 60);
            double pCoast = Time((x, z) => coast.ParameterAt(x, z).X, 2, 60);
            double sCoast = Time((x, z) => coast.SurfaceAt(x, z).Y, 2, 60), eCoast = Time((x, z) => coast.ElevationAt(x, z), 2, 60);
            // (SE-0054) one crest-spray try: a world point's crest (two inverses, with the wave factor's) against a mesh point's
            double tryOld = Time((x, z) => { var q = open.ParameterAt(x, z); return open.CrestAt(x, z).Height + q.X; }, 100, 400);
            double tryNew = Time((x, z) => open.CrestAtMesh(x, z).Height, 100, 400);
            Console.WriteLine($"  info crest spray, one try of 600 a tick: {tryOld:0.0} µs with two inverses (to 0.6.0-rc.4), {tryNew:0.0} µs at a mesh point");
            Console.WriteLine($"  info CPU surface, 24 trains: HeightAt {hOpen:0.0} µs open sea, {hCoast:0.0} µs on a coast; on a coast ParameterAt {pCoast:0.0} µs, SurfaceAt {sCoast:0.0} µs, its height alone (ElevationAt) {eCoast:0.0} µs");
        }
    }
}
