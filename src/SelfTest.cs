using System;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Sea
{
    public class SeaSelfTestConfig
    {
        public bool Enabled;
        public bool Shutdown = true;
    }

    /// <summary>
    /// Headless server check (tools/run_selftest.sh): the wind model is built, vanilla's wind lookups and weather
    /// regions give Sea's wind, a fixed wind takes over and clears, the ring lines up at Cylinder World's seam, the
    /// waves are ready on the server and the same for everyone who asks, and it's fast enough. Writes SELFTEST lines
    /// to the server log.
    /// </summary>
    public class SeaSelfTest : ModSystem
    {
        ICoreServerAPI sapi;
        int failures;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            SeaSelfTestConfig cfg;
            try { cfg = api.LoadModConfig<SeaSelfTestConfig>("sea-selftest.json"); } catch { cfg = null; }
            if (cfg?.Enabled != true) return;
            api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => api.Event.RegisterCallback(_ =>
            {
                void Finish()
                {
                    Log(failures == 0 ? "ALL OK" : $"{failures} FAILED");
                    if (cfg.Shutdown) api.Event.RegisterCallback(__ => api.Server.ShutDown(), 1000);
                }
                try { Run(); }
                catch (Exception e) { Check(false, "self-test crashed: " + e); Finish(); return; }
                try { DryPlaces(Finish); }
                catch (Exception e) { Check(false, "dry places test crashed: " + e); Finish(); }
            }, 2000));
        }

        /// <summary>A place registered dry has air though the world has water there (a ship's hold below the waterline).</summary>
        sealed class DryBox : IDrySpaces
        {
            public double X0, Y0, Z0, X1, Y1, Z1;
            public bool IsDry(double x, double y, double z) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1 && z >= Z0 && z <= Z1;
        }

        /// <summary>
        /// A chicken with its head under water can't breathe; inside a place a mod says is dry, it can. Built in the sky
        /// over spawn: a small tank of water, loaded first.
        /// </summary>
        void DryPlaces(Action then)
        {
            var sys = sapi.ModLoader.GetModSystem<SeaSystem>();
            Check(Hooks.Active.Contains("breathing in dry places") && Hooks.Active.Contains("eyes not under water in dry places") && Hooks.Active.Contains("water shader fallback"),
                "dry places: breathing, the camera's underwater checks and the shader fallback are patched");
            var water = sapi.World.GetBlock(new AssetLocation("game", "water-still-7"));
            EntityProperties type = null;
            foreach (string code in new[] { "chicken-rooster", "chicken-hen", "chicken-adult", "chicken-baby" })
                if ((type = sapi.World.GetEntityType(new AssetLocation("game", code))) != null) break;
            if (water == null || type == null)
            {
                Check(false, $"dry places: water ({water != null}) and a chicken to drown ({type != null}) found");
                then();
                return;
            }
            var spawn = sapi.World.DefaultSpawnPosition.AsBlockPos;
            int bx = spawn.X, bz = spawn.Z, by = sapi.WorldManager.MapSizeY - 12;
            sapi.WorldManager.LoadChunkColumnPriority(bx >> 5, bz >> 5, new ChunkLoadOptions
            {
                KeepLoaded = true,
                OnLoaded = () => sapi.Event.EnqueueMainThreadTask(() =>
                {
                    try { Drown(sys, water, type, bx, by, bz); }
                    catch (Exception e) { Check(false, "dry places test crashed: " + e); }
                    then();
                }, "sea-selftest-dry")
            });
        }

        void Drown(SeaSystem sys, Block water, EntityProperties type, int bx, int by, int bz)
        {
            var ba = sapi.World.BlockAccessor;
            var bp = new BlockPos(0);
            for (int x = bx - 1; x <= bx + 1; x++)
                for (int y = by; y <= by + 3; y++)
                    for (int z = bz - 1; z <= bz + 1; z++)
                    {
                        bp.Set(x, y, z);
                        ba.SetBlock(0, bp);
                        ba.SetBlock(water.Id, bp, BlockLayersAccess.Fluid);
                    }
            var chicken = sapi.World.ClassRegistry.CreateEntity(type);
            chicken.Pos.SetPos(bx + 0.5, by + 1, bz + 0.5);
            chicken.Pos.Dimension = 0;
            sapi.World.SpawnEntity(chicken);
            var breathe = chicken.GetBehavior<EntityBehaviorBreathe>();
            if (breathe == null) { Check(false, "dry places: the chicken breathes (has the breathe behavior)"); chicken.Die(EnumDespawnReason.Removed); return; }

            breathe.Check();
            bool underWater = !breathe.HasAir;
            var box = new DryBox { X0 = bx - 1, Y0 = by, Z0 = bz - 1, X1 = bx + 2, Y1 = by + 4, Z1 = bz + 2 };
            sys.AddDrySpaces(box);
            breathe.Check();
            bool dry = breathe.HasAir;
            Check(sys.IsDry(bx + 0.5, by + 1.3, bz + 0.5) && !sys.IsDry(bx + 5.5, by + 1.3, bz + 0.5), "a mod's dry places are known to Sea on the server");
            WaterParticles(sys, bx, by, bz);
            sys.RemoveDrySpaces(box);
            breathe.Check();
            bool wetAgain = !breathe.HasAir;
            Check(underWater && dry && wetAgain, $"a chicken with its head under water can't breathe ({underWater}), inside a dry place it can ({dry}), and once that's gone it can't again ({wetAgain})");
            chicken.Die(EnumDespawnReason.Removed);
        }

        sealed class CountingParticles : Vintagestory.API.Client.IAsyncParticleManager
        {
            public int Spawned;
            public IBlockAccessor BlockAccess => null;
            public int Spawn(IParticlePropertiesProvider p) { Spawned++; return 1; }
            public int ParticlesAlive(EnumParticleModel model) => 0;
        }

        /// <summary>
        /// Vanilla's particles in and on the world's water are left out in a dry place (the client's particle thread runs
        /// the same patched code): the ambient specks in the water and rain's splashes on it, inside the dry tank; outside it,
        /// and the rain itself over a wide box, they're kept.
        /// </summary>
        void WaterParticles(SeaSystem sys, int bx, int by, int bz)
        {
            var ambient = AccessTools.Method(typeof(ModSystemAmbientParticles), "AsyncParticleSpawnTick");
            var inWater = AccessTools.Method(typeof(DryParticles), nameof(DryParticles.SpawnInWater));
            var onWater = AccessTools.Method(typeof(DryParticles), nameof(DryParticles.SpawnOnWater));
            var rain = AccessTools.Method(typeof(WeatherSimulationParticles), "SpawnRainParticles");
            var dust = AccessTools.Method(typeof(WeatherSimulationParticles), "SpawnDustParticles");
            int Count(System.Reflection.MethodBase m, System.Reflection.MethodInfo call) => m == null ? 0 : PatchProcessor.GetCurrentInstructions(m).Count(ci => ci.Calls(call));
            int nAmbient = Count(ambient, inWater), nRain = Count(rain, onWater), nDust = Count(dust, onWater);
            Check(Hooks.Active.Contains("no water specks in dry places") && Hooks.Active.Contains("no rain splashes or spray in dry places") && nAmbient > 0 && nRain > 0 && nDust > 0,
                $"water specks, rain splashes and spray are judged by Sea's dry places (spawns patched: ambient {nAmbient}, rain {nRain}, storm {nDust})");
            var keep = DryParticles.IsDry;
            var keepWithin = DryParticles.IsDryWithin;
            DryParticles.IsDry = sys.IsDry;
            // (since 0.3.5 a spawn is judged over its life first; the client's hook finds no client system here)
            DryParticles.IsDryWithin = sys.IsDryWithin;
            try
            {
                var m = new CountingParticles();
                SimpleParticleProperties Speck(double x, double y, double z, bool dieInAir) =>
                    new SimpleParticleProperties { MinPos = new Vec3d(x, y, z), MinQuantity = 1, ShouldDieInAir = dieInAir };
                DryParticles.SpawnInWater(m, Speck(bx + 0.5, by + 1.5, bz + 0.5, true));
                int inDry = m.Spawned;
                DryParticles.SpawnInWater(m, Speck(bx + 5.5, by + 1.5, bz + 0.5, true));
                int outside = m.Spawned - inDry;
                DryParticles.SpawnInWater(m, Speck(bx + 0.5, by + 1.5, bz + 0.5, false));
                int airMote = m.Spawned - inDry - outside;
                var splash = new SimpleParticleProperties { MinPos = new Vec3d(bx, by + 2.8, bz), AddPos = new Vec3d(1, 0.25, 0), MinQuantity = 1 };
                var rainBox = new SimpleParticleProperties { MinPos = new Vec3d(bx - 30, by + 2, bz - 30), AddPos = new Vec3d(60, 0, 60), MinQuantity = 1 };
                int before = m.Spawned;
                DryParticles.SpawnOnWater(m, splash);
                int splashes = m.Spawned - before;
                DryParticles.SpawnOnWater(m, rainBox);
                int rainKept = m.Spawned - before - splashes;
                Check(inDry == 0 && outside == 1 && airMote == 1 && splashes == 0 && rainKept == 1,
                    $"in a dry place a water speck isn't spawned ({inDry}), outside it is ({outside}), a mote of air is ({airMote}); a splash on the water there isn't ({splashes}), the rain over a wide box is ({rainKept})");
            }
            finally { DryParticles.IsDry = keep; DryParticles.IsDryWithin = keepWithin; }
        }

        void Log(string s) => sapi.Logger.Notification("SELFTEST " + s);

        void Check(bool ok, string what)
        {
            Log((ok ? "ok   " : "FAIL ") + what);
            if (!ok) failures++;
        }

        void Run()
        {
            var sys = sapi.ModLoader.GetModSystem<SeaSystem>();
            var m = sys?.Model;
            Check(m != null, $"wind model built ({sys?.Config.Mode})");
            if (m == null) return;

            var spawn = sapi.World.DefaultSpawnPosition;
            double x = spawn.X, z = spawn.Z, h = sys.Hours;
            int sea = sapi.World.SeaLevel;
            Log($"spawn {x:0},{z:0}: latitude {m.Shape.LatitudeDeg(z):0.0}°, {m.BeltName(z, h)}; wind {m.At(x, z, h)}; waves {m.SeaAt(x, z, h).Height:0.00}");

            // vanilla's lookup (what trees, waves, rain and wind chill read) gives Sea's strength
            var v = sapi.World.BlockAccessor.GetWindSpeedAt(new Vec3d(x, sea, z));
            double want = m.At(x, z, sys.Hours).Strength;
            Check(Math.Abs(v.X - want) < 1e-6, $"GetWindSpeedAt gives Sea's wind ({v.X:0.0000} vs {want:0.0000})");
            var hi = sapi.World.BlockAccessor.GetWindSpeedAt(new Vec3d(x, sea + 60, z));
            Check(hi.X >= v.X - 1e-9 && hi.X <= WindModel.MaxStrength + 1e-9, $"stronger higher up, capped ({hi.X:0.000} at +60)");

            // the weather region (wind sound) agrees
            var ws = sapi.ModLoader.GetModSystem<WeatherSystemServer>();
            int rs = sapi.World.BlockAccessor.RegionSize;
            var region = ws?.getOrCreateWeatherSimForRegion((int)(x / rs), (int)(z / rs));
            if (region != null)
            {
                double rw = region.GetWindSpeed(sea);
                double rwant = m.At(((int)(x / rs) + 0.5) * rs, ((int)(z / rs) + 0.5) * rs, sys.Hours, false).Strength;
                Check(Math.Abs(rw - rwant) < 1e-6, $"weather region wind is Sea's ({rw:0.0000} vs {rwant:0.0000})");
            }
            else Log("skip weather region (none)");

            // an admin's fixed wind takes over and clears
            sys.SetOverride(new WindOverride { FromDegrees = 90, Strength = 0.8, StartHours = sys.Hours - 0.01 });
            var fixedW = sys.WindAt(x, z);
            var fv = sapi.World.BlockAccessor.GetWindSpeedAt(new Vec3d(x, sea, z));
            Check(Math.Abs(fixedW.Strength - 0.8) < 1e-9 && Math.Abs(fixedW.FromDegrees - 90) < 1e-6 && Math.Abs(fv.X - 0.8) < 1e-9,
                $"fixed wind from the east at 0.8 ({fixedW}, vanilla sees {fv.X:0.00})");
            sys.SetOverride(null);
            Check(Math.Abs(sys.WindAt(x, z).Strength - m.At(x, z, sys.Hours).Strength) < 1e-9, "cleared fixed wind");

            // the same everywhere around the ring
            if (m.Shape.Wraps)
            {
                double c = m.Shape.Circumference;
                var a = m.At(x, z, h);
                var b = m.At(x + c, z, h);
                Check(Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Z - b.Z) < 1e-9, $"wind repeats around the {c:0}-block ring");
            }
            else Log("skip ring (the world doesn't wrap)");

            // the waves: the same trains here as on every client, running on the server's wave clock
            Check(sys.Spectrum != null && sys.Spectrum.Circumference == m.Shape.Circumference, $"wave trains built ({sys.Spectrum?.TrainCount} of them)");
            Check(sys.WaveTime > 0.5, $"the wave clock runs ({sys.WaveTime:0.0} s)");
            var p1 = new WavePlace();
            var p2 = new WavePlace();
            sys.SampleWaves(p1, x, z);
            sys.SampleWaves(p2, x, z);   // someone else, new to the place (0.3.6: whatever either saw before)
            // (it read the running clock a moment later: blend it at the first one's time; within a sample that only re-blends)
            bool sameSample = WavePlace.SampleIndex(p2.Time) == WavePlace.SampleIndex(p1.Time);
            p2.Update(sys.Spectrum, p1.Time, default, default);
            double h1 = p1.HeightAt(x + 3.5, z - 7.25), h2 = p2.HeightAt(x + 3.5, z - 7.25);
            Check(p1.Count > 0 && (!sameSample || Math.Abs(h1 - h2) < 1e-12),
                $"everyone asking about the same place and time gets the same waves ({p1.Count} trains, {h1:0.000}, off by {Math.Abs(h1 - h2):e1}{(sameSample ? "" : "; straddled a sample, not compared")})");
            double f = sys.WaveFactorAt(x, z);
            Check(f >= 0.2 - 1e-9 && f <= 1 + 1e-9, $"sheltered water scales the waves like the shader does ({f:0.00} at spawn)");
            double wh = sys.WaveHeightAt(x, z);
            Check(!double.IsNaN(wh) && Math.Abs(wh) < 6, $"wave height at spawn {wh:0.00}");
            if (m.Shape.Wraps)
            {
                sys.SampleWaves(p1, x, z);
                double c = m.Shape.Circumference;
                Check(Math.Abs(p1.HeightAt(x, z) - p1.HeightAt(x + c, z)) < 1e-6, "the waves repeat around the ring: no seam");
            }

            // cost: vanilla asks for wind often (particles, sound, every region every 25 ms)
            var sw = Stopwatch.StartNew();
            int n = 20000;
            double sum = 0;
            for (int i = 0; i < n; i++) sum += m.At(x + i * 7.3, z + i * 3.1, h + i * 0.0001).Strength;
            double us = sw.Elapsed.TotalMilliseconds * 1000 / n;
            Check(us < 50, $"wind lookup takes {us:0.0} µs (average {sum / n:0.00})");
            sw.Restart();
            for (int i = 0; i < 200; i++) m.SeaAt(x + i * 50, z, h);
            Check(sw.Elapsed.TotalMilliseconds / 200 < 2, $"sea state takes {sw.Elapsed.TotalMilliseconds * 1000 / 200:0} µs");

            // the client-side patches are applied here too (same code): check they went in
            var spawnRain = AccessTools.Method(typeof(WeatherSimulationParticles), "asyncParticleSpawn");
            var rainWind = AccessTools.Method(typeof(Hooks), nameof(Hooks.RainWind));
            Check(PatchProcessor.GetCurrentInstructions(spawnRain).Any(ci => ci.Calls(rainWind)), "rain, snow and hail take Sea's wind direction");
            Check(Hooks.Active.Count(a => a.StartsWith("clouds")) == 2, $"both cloud renderers drift with Sea's wind ({string.Join(", ", Hooks.Active)})");
            Check(Hooks.ShaderPatched, "water shader loader patched");

            Log(SeaCommands.Report(sys, new Vintagestory.API.Common.Entities.EntityPos(x, sea, z)).Replace("\n", " | "));
        }
    }
}
