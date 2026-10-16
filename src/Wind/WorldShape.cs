using System;

namespace Sea
{
    /// <summary>
    /// What the wind model needs to know about the world: where the latitudes are, whether x wraps around a ring
    /// (Cylinder World), and how long a year is. Everything the model computes is a pure function of these, a key,
    /// the position and the game time.
    /// </summary>
    public sealed class WorldShape
    {
        /// <summary>x where the ring starts (Cylinder World's jump inset).</summary>
        public double RingStart;
        /// <summary>Length of the ring along x; 0 = the world doesn't wrap.</summary>
        public double Circumference;
        /// <summary>z → latitude, -1 south pole .. 0 equator .. 1 north pole (the game calendar's OnGetLatitude).</summary>
        public Func<double, double> Latitude = _ => 0.5;
        public double DaysPerYear = 108;
        public double HoursPerDay = 24;

        public bool Wraps => Circumference > 0;

        /// <summary>x folded into the ring [RingStart, RingStart + Circumference).</summary>
        public double RingX(double x) => Wraps ? RingStart + Mod(x - RingStart, Circumference) : x;

        /// <summary>Shortest signed distance from x `from` to x `to` (across the seam if that's shorter).</summary>
        public double DeltaX(double from, double to)
        {
            double d = to - from;
            return Wraps ? Mod(d + Circumference / 2, Circumference) - Circumference / 2 : d;
        }

        /// <summary>Latitude in degrees, -90..90 (north positive).</summary>
        public double LatitudeDeg(double z) => Math.Clamp(Latitude(z), -1, 1) * 90;

        /// <summary>0..1 through the year (0 = start of January, like the game calendar's YearRel).</summary>
        public double YearRel(double totalHours) => Mod(totalHours / HoursPerDay, DaysPerYear) / DaysPerYear;

        /// <summary>How many lattice cells of about `scale` blocks go round the ring (0 when x doesn't wrap).</summary>
        public long Cells(double scale) => Wraps ? Math.Max(1, (long)Math.Round(Circumference / scale)) : 0;

        /// <summary>x in lattice units for a noise of about `scale` blocks; exactly periodic when the world wraps.</summary>
        public double LatticeX(double x, double scale, long cells) => Wraps ? (x - RingStart) / (Circumference / cells) : x / scale;

        public static double Mod(double a, double m) => a - m * Math.Floor(a / m);
    }
}
