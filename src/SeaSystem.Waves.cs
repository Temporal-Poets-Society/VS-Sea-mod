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
    /// <summary>(SE-0046) The waves: the wave clock, sampling a place, the grid corners and sample hours, wave heights and factors, and the probes.</summary>
    public partial class SeaSystem
    {
        /// <summary>
        /// Seconds on the clock the waves run by, the same on the server and every client (the server's running
        /// time; clients follow it smoothly). On clients it's read once a frame, before anything is drawn.
        /// </summary>
        public double WaveTime => api.Side == EnumAppSide.Server ? sapi.World.ElapsedMilliseconds / 1000.0 : clientWaveTime;

        /// <summary>Client: how far the local wave clock still is from the server's (seconds), and whether it heard from it yet.</summary>
        public double WaveClockError => clock.Error;
        public bool WaveClockSynced => clock.Synced;

        /// <summary>
        /// Bring a place (a ship, the camera) to the current wave time, with the sea state and wind at (x, z). Keep
        /// one <see cref="WavePlace"/> per thing that rides or looks at the waves and call this once a tick or frame.
        /// </summary>
        public void SampleWaves(WavePlace place, double x, double z) => SampleWaves(place, x, z, WaveTime);

        /// <summary>
        /// The same at wave time t. Both ends of each second's blend are taken at their own sample's game hour (the
        /// server's anchors), not whenever this caller got here, and (0.4.2) at fixed grid points around the place
        /// (<see cref="WavePlace.Sample"/>), not wherever it was: a ship new to the place, one that missed a second or
        /// sailed in from elsewhere, the server's tick and a client's frame all get the same surface there.
        /// </summary>
        internal void SampleWaves(WavePlace place, double x, double z, double t)
        {
            // (0.6.0) every place rides the beaches' surf as the shader draws it
            place.Coast = SurfOn ? coastLookup : null;
            place.Sample(Spectrum, t, x, z, Model?.Shape.RingStart ?? 0, waveSource);
        }

        // ---------------------------------------------------------------- (0.6.0) coasts

        CoastField coast;
        CoastLookup coastLookup;
        SurfSpray surfSpray;
        CrestSpray crestSpray;
        SwashRenderer swash;

        /// <summary>The wind the waves' chop follows (<see cref="WindModel.SeaWind(double, double, double, bool, long)"/>), as worked out for wave sample n.</summary>
        Wind SeaWindAt(double x, double z, double hours, long n) => Model?.SeaWind(x, z, hours, true, n) ?? WindAt(x, z, hours);

        /// <summary>The sea state as worked out for wave sample n (an admin's change of the fixed wind counts from its sample on).</summary>
        SeaState SeaAt(double x, double z, double hours, long n)
        {
            var m = Model;
            return m != null ? m.SeaAt(x, z, hours, n) : SeaAt(x, z, hours);
        }

        /// <summary>(0.4.2) What wave places sample: the sea state and wind, at the sample hours every side agrees on.</summary>
        sealed class WaveSource : IWaveSource
        {
            readonly SeaSystem sys;
            public WaveSource(SeaSystem sys) { this.sys = sys; }

            public double HoursOf(long n, double t)
            {
                double rate = sys.GameHoursPerWaveSecond, s = WavePlace.SampleSeconds;
                // the estimate (the calendar now, worked back to the sample) only counts until the server's anchors arrive
                return sys.sampleHours.At(n, sys.Hours - (t - n * s) * rate, rate * s);
            }

            public SeaState SeaAt(double x, double z, double hours, long n) => sys.SeaAt(x, z, hours, n);
            public Wind WindAt(double x, double z, double hours, long n) => sys.SeaWindAt(x, z, hours, n);
            // (the model's changes and the anchors' together: either may change what a sample reads)
            public long Revision => sys.StableRevision() + sys.sampleHours.Version;
            // (SE-0025) the model's alone: a new anchor leaves an end worked out for the same hour as it was
            public long Inputs => sys.StableRevision();
        }

        readonly SharedCorners waveSource;

        /// <summary>The game hour of each wave sample, the same on every side (the server's anchors).</summary>
        readonly SampleHours sampleHours = new();
        /// <summary>Anchors are made this many samples ahead, so clients have them before they're in force.</summary>
        const int AnchorLead = 3;

        /// <summary>Server: from `ahead` samples on, the sample hours follow the calendar as it is now (where and how fast).</summary>
        void PublishAnchor(int ahead)
        {
            double t = WaveTime;
            sampleHours.Add(SampleHours.Make(t, Hours, GameHoursPerWaveSecond, ahead, ++anchorSeq), WavePlace.SampleIndex(t));
        }

        long anchorSeq;

        /// <summary>Game hours per second of wave time (the calendar's speed; it runs faster while everyone sleeps).</summary>
        double GameHoursPerWaveSecond
        {
            get
            {
                var cal = api.World.Calendar;
                return Math.Max(0, cal.SpeedOfTime * cal.CalendarSpeedMul) / 3600.0;
            }
        }

        /// <summary>
        /// How much of the open-sea waves reaches this place: 1 on the open ocean, down to a fifth in sheltered water, the same
        /// divisor the water shader applies there (from the world's ocean map). 0.5.3: none on inland water (oceanity 0:
        /// rivers, rapids, lakes), rising to the full sheltered share by oceanity 0.25 (WaterShader's sea_OpenWater).
        /// With horizontal waves, reads the map at the inverse mesh coordinate, where the shader's flag was packed.
        /// </summary>
        public double WaveFactorAt(double x, double z)
        {
            try
            {
                // Oceanity belongs to the original mesh vertex, not the horizontally displaced position.
                // Preserve HeightAt(world) * WaveFactorAt(world) for existing ship mods across shelter gradients.
                (double X, double Z) q;
                if (api.Side == EnumAppSide.Client) q = CameraWaves.ParameterAt(x, z);
                else { lock (probes) q = ProbeAt(x, z).ParameterAt(x, z); }
                return MeshWaveFactorAt(q.X, q.Z);
            }
            catch (Exception e) { LogOnce.Error(api?.Logger, "the wave factor at a place", e); return 0.2; }
        }

        /// <summary>(SE-0054) The wave factor the game packs at a mesh coordinate (no inverse; <see cref="WaveFactorAt"/> inverts a world position first).</summary>
        internal double WaveFactorAtMesh(double qx, double qz) => MeshWaveFactorAt(qx, qz);

        double MeshWaveFactorAt(double x, double z)
        {
            try
            {
                var ba = api.World.BlockAccessor;
                int rs = ba.RegionSize;
                int cx = (int)Math.Floor(x / 32), cz = (int)Math.Floor(z / 32);
                var om = ba.GetMapRegion((int)Math.Floor(cx * 32.0 / rs), (int)Math.Floor(cz * 32.0 / rs))?.OceanMap;
                if (om == null || om.InnerSize <= 0) return 0.2;
                // like the chunk tesselator: the map at the chunk's corners, blended across the chunk
                float mx = (float)GameMath.Mod(cx * 32, rs) / rs * om.InnerSize, mz = (float)GameMath.Mod(cz * 32, rs) / rs * om.InnerSize;
                float tl = om.GetIntLerpedCorrectly(mx, mz), tr = om.GetIntLerpedCorrectly(mx + 1, mz);
                float bl = om.GetIntLerpedCorrectly(mx, mz + 1), br = om.GetIntLerpedCorrectly(mx + 1, mz + 1);
                double oceanity = (byte)GameMath.BiLerp(tl, tr, bl, br, (float)((x - cx * 32) / 32), (float)((z - cz * 32) / 32)) / 255.0;
                double div = 5 * Math.Max(0.2, 1 - oceanity), t = Math.Clamp((1 - div / 5) / 0.25, 0, 1);
                return t * t * (3 - 2 * t) / div;
            }
            catch (Exception e)
            {
                LogOnce.Error(api?.Logger, "the ocean map's wave factor", e);
                return 0.2;
            }
        }

        /// <summary>
        /// Height of the open-sea surface above its rest level at a world position, now (sheltered water scaled
        /// like the shader does). (SE-0013) The place's own waves, the same on the server and every client (the open
        /// contracts' surface.height); the drawn water uses the camera's, which differ from them only where the wind
        /// changes across the distance (<see cref="DrawnHeightAt"/>, <c>.sea drawn</c>).
        /// </summary>
        public double WaveHeightAt(double x, double z)
        {
            lock (probes) return ShelteredHeight(ProbeAt(x, z), x, z);
        }

        /// <summary>Client: the drawn water's height at a world position (the camera's wave trains), for comparing with <see cref="WaveHeightAt"/>.</summary>
        internal double DrawnHeightAt(double x, double z) => ShelteredHeight(CameraWaves, x, z);

        /// <summary>
        /// (0.6.0) The sheltered surface height at (x, z) on a place the caller sampled there: the open contracts' answer,
        /// the same on the server and every client (the camera's trains only draw the water; R5 of the contracts).
        /// </summary>
        internal double HeightOn(WavePlace place, double x, double z) => ShelteredHeight(place, x, z);

        /// <summary>(0.6.0) <see cref="WaveFactorAt"/> on a place the caller sampled at (x, z), on every side alike.</summary>
        internal double WaveFactorOn(WavePlace place, double x, double z)
        {
            try { var q = place.ParameterAt(x, z); return MeshWaveFactorAt(q.X, q.Z); }
            catch (Exception e) { LogOnce.Error(api?.Logger, "the wave factor at a place", e); return 0.2; }
        }

        double ShelteredHeight(WavePlace place, double x, double z)
        {
            var q = place.ParameterAt(x, z);
            // (SE-0041) the height only: SurfaceAt worked out the horizontal displacement too, to drop it
            return place.ElevationAt(q.X, q.Z, MeshWaveFactorAt(q.X, q.Z));
        }

        // Caller holds probes' lock. Keep the same bounded per-cell cache for heights and inverse shelter lookups.
        WavePlace ProbeAt(double x, double z)
        {
            var key = ((long)Math.Floor(x / WavePlace.GridCell), (long)Math.Floor(z / WavePlace.GridCell));
            if (!probes.TryGetValue(key, out var probe))
            {
                if (probes.Count >= MaxProbes)
                {
                    var oldest = key; long used = long.MaxValue;
                    foreach (var kv in probes) if (kv.Value.Used < used) { used = kv.Value.Used; oldest = kv.Key; }
                    probes.Remove(oldest);
                }
                probes[key] = probe = new Probe();
            }
            probe.Used = ++probeUses;
            SampleWaves(probe.Place, x, z);
            return probe.Place;
        }

        sealed class Probe { public readonly WavePlace Place = new(); public long Used; }
        readonly System.Collections.Generic.Dictionary<(long, long), Probe> probes = new();
        long probeUses;
        const int MaxProbes = 32;

        void BuildSpectrum(long key, double circumference, double waveScale)
        {
            if (Spectrum == null || Spectrum.Key != key || Spectrum.Circumference != circumference || Spectrum.ChopScale != waveScale)
                Spectrum = new WaveSpectrum(key, circumference, waveScale);
        }
    }
}
