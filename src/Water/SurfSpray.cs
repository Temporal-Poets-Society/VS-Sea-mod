using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Sea
{
    /// <summary>
    /// (0.6.0) Client: spray thrown up where surf breaks near the camera, from the same CPU surface the shader draws
    /// (<see cref="WavePlace.SurfAt"/>): a few points are tried every tick, and a breaking crest throws droplets
    /// shoreward and downwind that fall back. Off with the foam (FoamView Off) and with Surf.
    /// </summary>
    public sealed class SurfSpray
    {
        const int Tries = 28;
        const double Reach = 40;

        readonly ICoreClientAPI capi;
        readonly SeaSystem sys;
        readonly Random rand = new();
        readonly SimpleParticleProperties spray;

        public SurfSpray(ICoreClientAPI capi, SeaSystem sys)
        {
            this.capi = capi;
            this.sys = sys;
            spray = new SimpleParticleProperties(1, 1, ColorUtil.ToRgba(200, 235, 242, 240), new Vec3d(), new Vec3d(),
                new Vec3f(), new Vec3f(), 0.9f, 0.55f, 0.18f, 0.45f, EnumParticleModel.Quad)
            {
                ShouldDieInLiquid = true,
                WindAffected = true,
                WindAffectednes = 0.4f,
                OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -180),
                SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, 0.3f),
            };
        }

        /// <summary>Spawned last tick (for `.sea coast`).</summary>
        public int LastSpawned { get; private set; }

        public void Tick()
        {
            LastSpawned = 0;
            var ep = capi.World?.Player?.Entity;
            if (ep == null || ep.Pos.Dimension != 0 || !sys.SurfOn || sys.FoamView == FoamView.Off) return;
            var place = sys.CameraWaves;
            if (place.Count == 0) return;
            double cx = ep.CameraPos.X, cz = ep.CameraPos.Z, rest = capi.World.SeaLevel - 0.125;
            if (Math.Abs(ep.CameraPos.Y - rest) > 60) return;
            var wind = sys.LocalWind;
            for (int i = 0; i < Tries; i++)
            {
                double r = Reach * Math.Sqrt(rand.NextDouble()), a = rand.NextDouble() * 2 * Math.PI;
                double x = cx + r * Math.Cos(a), z = cz + r * Math.Sin(a);
                var c = sys.CoastAt(x, z);
                if (!c.Coastal || c.Depth < 0.3) continue;
                var s = place.SurfAt(x, z);
                if (s.Breaking < 0.75 || s.Foam < 0.55) continue;
                double y = rest + place.HeightAt(x, z) * sys.WaveFactorAt(x, z);
                // thrown shoreward (down the travel time) and downwind; more from the whitest crests
                double t = c.Trust > 1e-6 ? 1 / c.Trust : 0, sx = -c.AwayX * t, sz = -c.AwayZ * t;
                int n = 2 + (int)(6 * (s.Foam - 0.55) / 0.45 * s.Breaking);
                spray.MinQuantity = n; spray.AddQuantity = n / 2f;
                spray.MinPos.Set(x - 0.6, y - 0.05, z - 0.6);
                spray.AddPos.Set(1.2, 0.25, 1.2);
                float wx = (float)(wind.X * 3), wz = (float)(wind.Z * 3);   // (vanilla wind units: 1 is about 25 m/s)
                spray.MinVelocity.Set((float)(sx * 0.8) + wx - 0.4f, 1.6f, (float)(sz * 0.8) + wz - 0.4f);
                spray.AddVelocity.Set(0.8f, 1.6f * (float)s.Breaking, 0.8f);
                capi.World.SpawnParticles(spray);
                LastSpawned += n;
            }
        }
    }
}
