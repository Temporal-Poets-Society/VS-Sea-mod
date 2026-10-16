using System;

namespace Sea
{
    /// <summary>
    /// Hashing and smooth value noise in (x, z, t). Pure and deterministic, so the server and every client get the
    /// same numbers from the same key. The lattice can repeat exactly along x (Cylinder World's ring).
    /// </summary>
    public static class Noise
    {
        /// <summary>splitmix64 finalizer.</summary>
        public static ulong Mix(ulong a)
        {
            a += 0x9E3779B97F4A7C15UL;
            a = (a ^ (a >> 30)) * 0xBF58476D1CE4E5B9UL;
            a = (a ^ (a >> 27)) * 0x94D049BB133111EBUL;
            return a ^ (a >> 31);
        }

        public static ulong Hash(long key, long a, long b, long c) => Mix((ulong)key ^ Mix((ulong)a ^ Mix((ulong)b ^ Mix((ulong)c))));

        /// <summary>0 ≤ u &lt; 1 from a hash.</summary>
        public static double Unit(ulong h) => (h >> 11) * (1.0 / (1UL << 53));

        /// <summary>The n-th uniform number (0..1) of a hashed stream.</summary>
        public static double Unit(ulong h, int n) => Unit(Mix(h + (ulong)n * 0x632BE59BD9B4E019UL));

        public static long Wrap(long i, long period) => period > 0 ? ((i % period) + period) % period : i;

        static double Corner(long key, long ix, long iz, long it) => Unit(Hash(key, ix, iz, it)) * 2 - 1;

        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

        /// <summary>Value noise (-1..1) at lattice coordinates. With periodX &gt; 0 the lattice repeats every periodX cells along x.</summary>
        public static double Value(long key, double x, double z, double t, long periodX)
        {
            double fx0 = Math.Floor(x), fz0 = Math.Floor(z), ft0 = Math.Floor(t);
            long x0 = (long)fx0, z0 = (long)fz0, t0 = (long)ft0;
            double u = Fade(x - fx0), v = Fade(z - fz0), w = Fade(t - ft0);
            long xa = Wrap(x0, periodX), xb = Wrap(x0 + 1, periodX);

            double c000 = Corner(key, xa, z0, t0), c100 = Corner(key, xb, z0, t0);
            double c010 = Corner(key, xa, z0 + 1, t0), c110 = Corner(key, xb, z0 + 1, t0);
            double c001 = Corner(key, xa, z0, t0 + 1), c101 = Corner(key, xb, z0, t0 + 1);
            double c011 = Corner(key, xa, z0 + 1, t0 + 1), c111 = Corner(key, xb, z0 + 1, t0 + 1);

            double a0 = Lerp(Lerp(c000, c100, u), Lerp(c010, c110, u), v);
            double a1 = Lerp(Lerp(c001, c101, u), Lerp(c011, c111, u), v);
            return Lerp(a0, a1, w);
        }

        /// <summary>Two octaves of <see cref="Value"/>, normalised back to about -1..1.</summary>
        public static double Fbm(long key, double x, double z, double t, long periodX)
        {
            double a = Value(key, x, z, t, periodX);
            double b = Value(key + 7919, x * 2 + 0.37, z * 2 + 0.61, t * 2 + 0.13, periodX * 2);
            return (a + 0.5 * b) / 1.5;
        }

        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        public static double SmoothStep(double e0, double e1, double x)
        {
            double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
            return t * t * (3 - 2 * t);
        }

        /// <summary>0 below a, rising to 1 at b, 1 until c, falling to 0 at d.</summary>
        public static double Band(double x, double a, double b, double c, double d) => SmoothStep(a, b, x) * (1 - SmoothStep(c, d, x));
    }
}
