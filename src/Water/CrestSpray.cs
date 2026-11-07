using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Sea
{
    /// <summary>
    /// (0.6.0) Client: spray off the open sea's breaking crests near the camera. It comes from the crests the shader
    /// whitens (<see cref="WavePlace.CrestAt"/>, the shader's crest measure, through <see cref="WavePlace.Whitecap"/>, and a
    /// little from crests only sharpened, <see cref="WavePlace.SprayCrest"/>), more of them as the wind's whitecap coverage
    /// rises (<see cref="WaveSpectrum.WhitecapCoverage"/>, from about Beaufort 4). Only a crest or its rising front
    /// throws spray, never the falling back of a wave. Each one is climbed to its tip, and the spray is a thin curtain
    /// along the tip that travels with the crest (the strongest long train's phase speed), so it stays on the crest
    /// instead of being left behind. Soft translucent puffs that swell and fade, not specks. From a gale on, the wind
    /// also tears spindrift off the whitened tips, low and faster than the crest. Coasts are left to
    /// <see cref="SurfSpray"/>. Off with the foam (FoamView Off). Cosmetic and local: nothing is sent.
    /// </summary>
    public sealed class CrestSpray
    {
        const int Tries = 600;
        const double Reach = 40;
        /// <summary>At most this many particles a tick, so a storm stays cheap.</summary>
        const int Budget = 1200;

        readonly ICoreClientAPI capi;
        readonly SeaSystem sys;
        readonly Random rand = new();
        readonly SimpleParticleProperties spray, drift;

        public CrestSpray(ICoreClientAPI capi, SeaSystem sys)
        {
            this.capi = capi;
            this.sys = sys;
            // Vanilla's quad particles are hard-edged squares: big ones read as confetti (and big faint ones as pale
            // tiles), so the spray is many fine droplets, the squares too small to show. Vanilla's
            // QUADRATIC fade takes off (factor x age)^2 of the alpha, so a factor of -sqrt(alpha) clears it at the end of its life.
            spray = new SimpleParticleProperties(1, 1, ColorUtil.ToRgba(225, 240, 246, 248), new Vec3d(), new Vec3d(),
                new Vec3f(), new Vec3f(), 0.75f, 0.8f, 0.07f, 0.16f, EnumParticleModel.Quad)
            {
                ShouldDieInLiquid = false,
                WindAffected = false,
                OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.QUADRATIC, -15),
                // (additive in blocks: firstval + factor x age)
                SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, 0.02f),
            };
            drift = new SimpleParticleProperties(1, 1, ColorUtil.ToRgba(190, 236, 242, 246), new Vec3d(), new Vec3d(),
                new Vec3f(), new Vec3f(), 0.7f, 0.15f, 0.05f, 0.1f, EnumParticleModel.Quad)
            {
                ShouldDieInLiquid = false,
                WindAffected = false,
                OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.QUADRATIC, -13.8f),
            };
        }

        /// <summary>Spawned last tick (for `.sea`).</summary>
        public int LastSpawned { get; private set; }

        public void Tick()
        {
            LastSpawned = 0;
            var ep = capi.World?.Player?.Entity;
            if (ep == null || ep.Pos.Dimension != 0 || sys.FoamView == FoamView.Off) return;
            var place = sys.CameraWaves;
            if (place.Count == 0 || place.SignificantHeight < 1e-5) return;
            double cx = ep.CameraPos.X, cz = ep.CameraPos.Z, rest = capi.World.SeaLevel - 0.125;
            if (Math.Abs(ep.CameraPos.Y - rest) > 60) return;
            // the wind the whitecaps follow (WaterRenderer's foam wind)
            var wind = sys.Model?.SeaWind(cx, cz, sys.Hours, true) ?? place.Wind;
            double ws = wind.Strength, coverage = WaveSpectrum.WhitecapCoverage(ws);
            if (coverage < 0.002 || ws < 1e-6) return;
            // the crests run with the strongest long train; spray thrown off one travels with it
            if (!place.LeadingTrain(out double ux, out double uz, out double speed)) return;
            double share = Math.Min(1, 0.1 + 1.2 * Math.Sqrt(coverage));
            bool storm = ws >= 0.7;   // Beaufort 8 and up: spindrift
            float wx = (float)(wind.X / ws), wz = (float)(wind.Z / ws);
            int left = Budget;
            for (int i = 0; i < Tries && left > 0; i++)
            {
                double r = Reach * Math.Sqrt(rand.NextDouble()), a = rand.NextDouble() * 2 * Math.PI;
                double x = cx + r * Math.Cos(a), z = cz + r * Math.Sin(a);
                if (sys.SurfOn && sys.CoastAt(x, z).Coastal) continue;
                double factor = sys.WaveFactorAt(x, z);
                if (factor < 0.5) continue;
                var crest = place.CrestAt(x, z);
                // the crest and its front only: the falling back of a passing wave throws nothing
                if (crest.Fall > 0.35) continue;
                double w = WavePlace.SprayCrest(crest.Height, crest.Compression * factor);
                double white = WavePlace.Whitecap(crest.Height, crest.Compression * factor, coverage);
                // mostly where the shader whitens the crest; a little off crests only sharpened
                double strength = Math.Max(white, 0.35 * w);
                if (strength < 0.12 || rand.NextDouble() > share) continue;
                // up to the crest's tip along the waves' run
                double tip = place.ClimbCrest(ref x, ref z, ux, uz);
                double y = rest + tip * factor;
                double c = speed * (0.9 + 0.1 * strength);
                // fine at any range: droplets about the same few pixels near and far, never squares at the camera
                double dist = Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz) + (y - ep.CameraPos.Y) * (y - ep.CameraPos.Y));
                spray.MinSize = (float)Math.Clamp(0.004 * dist, 0.025, 0.1); spray.MaxSize = spray.MinSize * 2;
                float lift = (float)(1.2 + 3.2 * strength * Math.Min(1, ws));
                // a curtain along the tip (across the waves' run): five stretches 0.7 apart, thin across it
                for (int seg = -2; seg <= 2 && left > 0; seg++)
                {
                    double sx = x - uz * 0.7 * seg, sz = z + ux * 0.7 * seg;
                    double sy = seg == 0 ? y : rest + place.HeightAt(sx, sz) * factor;
                    int n = Math.Min(left, 3 + (int)(24 * strength * (0.4 + 0.6 * Math.Sqrt(coverage))));
                    spray.MinQuantity = n; spray.AddQuantity = n / 2f;
                    spray.MinPos.Set(sx - 0.3 * Math.Abs(uz) - 0.08, sy + 0.05, sz - 0.3 * Math.Abs(ux) - 0.08);
                    spray.AddPos.Set(0.6 * Math.Abs(uz) + 0.16, 0.2, 0.6 * Math.Abs(ux) + 0.16);
                    spray.MinVelocity.Set((float)(ux * c) - 0.3f, lift * 0.45f, (float)(uz * c) - 0.3f);
                    spray.AddVelocity.Set(0.6f + wx * 1.2f, lift * 0.55f, 0.6f + wz * 1.2f);
                    capi.World.SpawnParticles(spray);
                    left -= n; LastSpawned += n;
                }
                if (!storm || white < 0.2 || left <= 0) continue;
                // spindrift: torn off the whitened tip and blown low downwind, faster than the crest
                int m = Math.Min(left, 4 + (int)(16 * white * Math.Min(1, (ws - 0.7) / 0.6 + 0.3)));
                float gust = (float)(ws * 6);   // (vanilla wind units: 1 is about 25 m/s)
                drift.MinQuantity = m; drift.AddQuantity = m / 2f;
                drift.MinSize = spray.MinSize * 0.7f; drift.MaxSize = spray.MinSize * 1.4f;
                drift.MinPos.Set(x - 1.2 * Math.Abs(uz) - 0.2, y + 0.2, z - 1.2 * Math.Abs(ux) - 0.2);
                drift.AddPos.Set(2.4 * Math.Abs(uz) + 0.4, 0.3, 2.4 * Math.Abs(ux) + 0.4);
                drift.MinVelocity.Set((float)(ux * c) + wx * gust * 0.6f, 0.3f, (float)(uz * c) + wz * gust * 0.6f);
                drift.AddVelocity.Set(wx * gust * 0.5f + 0.3f, 0.5f, wz * gust * 0.5f + 0.3f);
                capi.World.SpawnParticles(drift);
                left -= m; LastSpawned += m;
            }
        }
    }
}
