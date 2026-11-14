using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Sea
{
    /// <summary>(SE-0046) The coast's public face: the coast map, surf, swash and spray, currents and lee.</summary>
    public partial class SeaSystem
    {
        /// <summary>(0.6.0) Client: water running up the beaches (null on a server).</summary>
        public SwashRenderer Swash => swash;

        /// <summary>(0.6.0) Client: the surf spray (null on a server).</summary>
        public SurfSpray Spray => surfSpray;
        /// <summary>(0.6.0) Client: spray off the open sea's breaking crests.</summary>
        public CrestSpray CrestSpray => crestSpray;

        /// <summary>(0.6.0) The coast's depths and shore travel times near what's watched (null before Start).</summary>
        public CoastField Coast => coast;

        /// <summary>
        /// (0.6.0) Waves feel the sea floor: the config's Surf, in the world itself (not on a Cylinder World ring, whose
        /// coordinates wrap where the coast map's don't).
        /// </summary>
        // (SE-0053) on a ring too: the coast map wraps on it (off only for a ring that doesn't line up with its tiles)
        public bool SurfOn => coast != null && Config?.Surf != false && !coast.RingMisaligned;

        /// <summary>(0.6.0) The coast's numbers at a world position (Coastal false off the coast, or before its tile is solved).</summary>
        public CoastSample CoastAt(double x, double z) =>
            SurfOn && coast.Lookup(x, z, out var c) ? c : CoastSample.Open;

        /// <summary>
        /// (0.6.0) The current at a world position (m/s, x east, z south) driven by the surf: in a surf zone the breaking
        /// waves push the water along the beach (CoastMath.Longshore, Komar 1979), and over a rip channel it runs back out
        /// to sea (CoastMath.RipCurrent); none elsewhere. From the waves of a place sampled there (a boat's own, or the
        /// camera's), less what the land upwave keeps off.
        /// </summary>
        public (double X, double Z) CurrentAt(WavePlace place, double x, double z)
        {
            if (place == null || place.Count == 0 || !SurfOn) return (0, 0);
            var c = CoastAt(x, z);
            if (!c.Coastal || c.Trust <= 0) return (0, 0);
            // the waves as they reach here (the lee, the wind sea's fetch): the longshore current as the shader's
            // surf follows it, and the rip from their height
            Span<double> reach = stackalloc double[WavePlace.MaxWaves];
            var (wx, wz) = place.CoastWind;
            CoastMath.Reach(c, place.Count, place.Kx, place.Kz, place.Omega, wx, wz, reach);
            var v = CoastMath.LongshoreAt(c, place.Count, place.Kx, place.Kz, place.Omega, place.Amp, reach);
            double e = 0;
            for (int i = 0; i < place.Count; i++) { double a = place.Amp[i] * reach[i]; e += a * a; }
            double t = c.Trust, hs = 4 * Math.Sqrt(e / 2), al = Math.Sqrt(c.AwayX * c.AwayX + c.AwayZ * c.AwayZ);
            if (al < 1e-9) return v;
            var r = CoastMath.RipCurrent(c.Rip / t, c.Depth, c.AwayX / al, c.AwayZ / al, hs);
            return (v.X + r.X * t, v.Z + r.Z * t);
        }

        /// <summary>
        /// (0.6.0) The share of the sea's height (energy-weighted over a place's trains) that gets past the land to a
        /// world position: 1 on open water, less in the lee of an island or a headland.
        /// </summary>
        public double LeeAt(WavePlace place, double x, double z)
        {
            if (place == null || place.Count == 0 || !SurfOn) return 1;
            var c = CoastAt(x, z);
            if (!c.Affects) return 1;
            // (the wind's own sea by its fetch since the land and the depth)
            Span<double> reach = stackalloc double[WavePlace.MaxWaves];
            var (wx, wz) = place.CoastWind;
            CoastMath.Reach(c, place.Count, place.Kx, place.Kz, place.Omega, wx, wz, reach);
            double e = 0, el = 0;
            for (int i = 0; i < place.Count; i++)
            {
                double a2 = place.Amp[i] * place.Amp[i], l = reach[i];
                e += a2; el += a2 * l * l;
            }
            return e > 0 ? Math.Sqrt(el / e) : 1;
        }
    }
}
