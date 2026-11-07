using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Sea
{
    /// <summary>
    /// (0.6.0) Client: spray off the open sea's breaking crests near the camera. It comes from the crests the shader
    /// whitens (<see cref="WavePlace.CrestAt"/>, the shader's crest measure, through <see cref="WavePlace.SprayCrest"/>, the shader's
    /// whitecap gate widened to the sharpened upper part of a wave) and
    /// more of them as the wind's whitecap coverage rises (<see cref="WaveSpectrum.WhitecapCoverage"/>, from about
    /// Beaufort 4). From a gale on, the wind also tears spindrift off the crests: low, fast streaks downwind. Coasts are
    /// left to <see cref="SurfSpray"/>. Off with the foam (FoamView Off). Cosmetic and local: nothing is sent.
    /// </summary>
    public sealed class CrestSpray
    {
        const int Tries = 48;
        const double Reach = 48;
        /// <summary>At most this many particles a tick, so a storm stays cheap.</summary>
        const int Budget = 160;

        readonly ICoreClientAPI capi;
        readonly SeaSystem sys;
        readonly Random rand = new();
        readonly SimpleParticleProperties spray, drift;

        public CrestSpray(ICoreClientAPI capi, SeaSystem sys)
        {
            this.capi = capi;
            this.sys = sys;
            spray = new SimpleParticleProperties(1, 1, ColorUtil.ToRgba(210, 236, 244, 246), new Vec3d(), new Vec3d(),
                new Vec3f(), new Vec3f(), 1.1f, 0.5f, 0.25f, 0.6f, EnumParticleModel.Quad)
            {
                ShouldDieInLiquid = true,
                WindAffected = true,
                WindAffectednes = 0.5f,
                OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -150),
                SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, 0.4f),
            };
            drift = new SimpleParticleProperties(1, 1, ColorUtil.ToRgba(150, 230, 240, 244), new Vec3d(), new Vec3d(),
                new Vec3f(), new Vec3f(), 1.4f, 0.12f, 0.15f, 0.35f, EnumParticleModel.Quad)
            {
                ShouldDieInLiquid = false,
                WindAffected = true,
                WindAffectednes = 0.8f,
                OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -100),
                SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, 0.6f),
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
            double tx = wind.X / ws, tz = wind.Z / ws;
            // the share of eligible crests that throw spray grows with the coverage, as the shader whitens more of them
            double share = Math.Min(1, 0.25 + 1.5 * Math.Sqrt(coverage));
            bool storm = ws >= 0.7;   // Beaufort 8 and up: spindrift
            int left = Budget;
            for (int i = 0; i < Tries && left > 0; i++)
            {
                double r = Reach * Math.Sqrt(rand.NextDouble()), a = rand.NextDouble() * 2 * Math.PI;
                double x = cx + r * Math.Cos(a), z = cz + r * Math.Sin(a);
                if (sys.SurfOn && sys.CoastAt(x, z).Coastal) continue;
                double factor = sys.WaveFactorAt(x, z);
                if (factor < 0.5) continue;
                var crest = place.CrestAt(x, z);
                double w = WavePlace.SprayCrest(crest.Height, crest.Compression * factor);
                if (w < 0.2 || rand.NextDouble() > share) continue;
                double y = rest + place.HeightAt(x, z) * factor;
                float wx = (float)(wind.X * 3), wz = (float)(wind.Z * 3);   // (vanilla wind units: 1 is about 25 m/s)

                int n = Math.Min(left, 3 + (int)(10 * w * Math.Sqrt(coverage)));
                spray.MinQuantity = n; spray.AddQuantity = n / 2f;
                spray.MinPos.Set(x - 0.8, y - 0.1, z - 0.8);
                spray.AddPos.Set(1.6, 0.4, 1.6);
                spray.MinVelocity.Set(wx * 0.6f - 0.5f, 1.2f, wz * 0.6f - 0.5f);
                spray.AddVelocity.Set(1f + wx * 0.4f, (float)(1.0 + 1.6 * w * Math.Min(1, ws)), 1f + wz * 0.4f);
                capi.World.SpawnParticles(spray);
                left -= n; LastSpawned += n;

                if (!storm || left <= 0) continue;
                // spindrift: torn off the crest and blown low and fast downwind, a streak a few blocks long
                int m = Math.Min(left, 4 + (int)(14 * w * Math.Min(1, (ws - 0.7) / 0.6 + 0.3)));
                drift.MinQuantity = m; drift.AddQuantity = m / 2f;
                drift.MinPos.Set(x - 0.5, y + 0.1, z - 0.5);
                drift.AddPos.Set(1.0 + tx * 2, 0.3, 1.0 + tz * 2);
                drift.MinVelocity.Set(wx * 1.1f, 0.2f, wz * 1.1f);
                drift.AddVelocity.Set(wx * 0.5f + 0.3f, 0.7f, wz * 0.5f + 0.3f);
                capi.World.SpawnParticles(drift);
                left -= m; LastSpawned += m;
            }
        }
    }
}
