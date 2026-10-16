using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Sea
{
    /// <summary>
    /// Harmony patches. Applied once per process (singleplayer runs server and client in one), removed when the last
    /// side unloads. Each patch that fails only switches its own feature off.
    /// </summary>
    public static class Hooks
    {
        const string HarmonyId = "sea";
        static Harmony harmony;
        static int users;
        static ILogger log;

        /// <summary>The water shader patch is in place (the shaders still have to be reloaded to pick it up).</summary>
        public static bool ShaderPatched;

        /// <summary>
        /// The wind at the local player for rain, snow and clouds: (toward x, toward z) unit direction, strength at sea
        /// level, strength at the player's height. Null = leave vanilla's.
        /// </summary>
        public static volatile float[] LocalWind;
        /// <summary>The client's ElapsedMilliseconds at the last update (vanilla's cloud timers count in it).</summary>
        public static long ClientMs;
        /// <summary>Patches that went in, for the self-test and .sea.</summary>
        public static readonly List<string> Active = new();

        public static void Apply(ILogger logger)
        {
            users++;
            if (harmony != null) return;
            log = logger;
            harmony = new Harmony(HarmonyId);

            Try("water shader", () =>
            {
                var registry = AccessTools.TypeByName("Vintagestory.Client.NoObf.ShaderRegistry");
                harmony.Patch(AccessTools.Method(registry, "LoadShaderProgram"),
                    postfix: new HarmonyMethod(typeof(Hooks), nameof(LoadShaderProgramPostfix)));
                ShaderPatched = true;
            });
            Try("water shader fallback", () =>
            {
                var program = AccessTools.TypeByName("Vintagestory.Client.NoObf.ShaderProgram") ?? throw new Exception("shader program class not found");
                harmony.Patch(AccessTools.Method(program, "Compile"), postfix: new HarmonyMethod(typeof(Hooks), nameof(CompilePostfix)));
            });
            Try("wind sound and weather regions", () => harmony.Patch(AccessTools.Method(typeof(WeatherSimulationRegion), nameof(WeatherSimulationRegion.GetWindSpeed)),
                postfix: new HarmonyMethod(typeof(Hooks), nameof(RegionWindPostfix))));
            Try("rain blowing with the wind", () => harmony.Patch(AccessTools.Method(typeof(WeatherSimulationParticles), "asyncParticleSpawn"),
                transpiler: new HarmonyMethod(typeof(Hooks), nameof(RainWindTranspiler))));
            Try("eyes not under water in dry places", () =>
            {
                var client = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientMain") ?? throw new Exception("client class not found");
                var camera = AccessTools.TypeByName("Vintagestory.Client.NoObf.Camera") ?? throw new Exception("camera class not found");
                mainCamera = Need(client, "MainCamera");
                cameraEyePos = Need(camera, "CameraEyePos");
                harmony.Patch(AccessTools.Method(client, "EyesInWaterDepth"), postfix: new HarmonyMethod(typeof(Hooks), nameof(EyesInWaterDepthPostfix)));
                harmony.Patch(AccessTools.Method(client, "GetEyesInWaterColorShift"), postfix: new HarmonyMethod(typeof(Hooks), nameof(EyesInWaterColorShiftPostfix)));
            });
            Try("breathing in dry places", () => harmony.Patch(AccessTools.Method(typeof(EntityBehaviorBreathe), nameof(EntityBehaviorBreathe.Check)),
                postfix: new HarmonyMethod(typeof(Hooks), nameof(BreathePostfix))));
            Try("no splashing in dry places", () =>
            {
                harmony.Patch(AccessTools.Method(typeof(EntityAgent), "SpawnFloatingSediment") ?? throw new Exception("SpawnFloatingSediment not found"),
                    prefix: new HarmonyMethod(typeof(Hooks), nameof(NotInDryPlacePrefix)));
                harmony.Patch(AccessTools.Method(typeof(Entity), nameof(Entity.OnCollideWithLiquid)),
                    prefix: new HarmonyMethod(typeof(Hooks), nameof(NotInDryPlacePrefix)));
            });
            // the particles vanilla makes in and on the world's water, which the dry clip doesn't cut (DryParticles)
            DryParticles.IsDry ??= ClientDry;
            DryParticles.IsDryWithin ??= ClientDryWithin;
            Try("no water specks in dry places", () =>
            {
                harmony.Patch(AccessTools.Method(typeof(ModSystemAmbientParticles), "AsyncParticleSpawnTick") ?? throw new Exception("AsyncParticleSpawnTick not found"),
                    transpiler: new HarmonyMethod(typeof(DryParticles), nameof(DryParticles.InWaterTranspiler)));
                if (DryParticles.LastReplaced == 0) throw new Exception("no particle spawn found in it");
            });
            Try("no rain splashes or spray in dry places", () =>
            {
                foreach (var name in new[] { "SpawnRainParticles", "SpawnDustParticles" })
                {
                    harmony.Patch(AccessTools.Method(typeof(WeatherSimulationParticles), name) ?? throw new Exception(name + " not found"),
                        transpiler: new HarmonyMethod(typeof(DryParticles), nameof(DryParticles.OnWaterTranspiler)));
                    if (DryParticles.LastReplaced == 0) throw new Exception("no particle spawn found in " + name);
                }
            });
            foreach (var name in new[] { "FluffyClouds.CloudRendererMap", "Vintagestory.GameContent.CloudRenderer" })
            {
                Try("clouds drifting with the wind (" + name + ")", () =>
                {
                    var t = AccessTools.TypeByName(name) ?? throw new Exception("type not found");
                    cloudFields[t] = (Need(t, "windChangeTimer"), Need(t, "targetCloudSpeedX"), Need(t, "targetCloudSpeedZ"));
                    harmony.Patch(AccessTools.Method(t, "CloudTick"), prefix: new HarmonyMethod(typeof(Hooks), nameof(CloudTickPrefix)));
                });
            }
        }

        static FieldInfo Need(Type t, string name) => AccessTools.Field(t, name) ?? throw new Exception("field " + name + " not found");

        public static void Remove()
        {
            if (--users > 0 || harmony == null) return;
            harmony.UnpatchAll(HarmonyId);
            harmony = null;
            ShaderPatched = false;
            LocalWind = null;
            Active.Clear();
            cloudFields.Clear();
        }

        static void Try(string what, Action patch)
        {
            try { patch(); Active.Add(what); }
            catch (Exception e) { log?.Warning("Could not patch ({0}), that feature is off: {1}", what, e.Message); }
        }

        // ---------------------------------------------------------------- water shader

        /// <summary>Set once the particle shader was loaded with rain streaks along the drops' paths.</summary>
        public static bool RainStreaksInjected;

        static void LoadShaderProgramPostfix(object program)
        {
            if (!(program is IShaderProgram p) || p.VertexShader == null) return;
            if (p.PassName == RainShader.Pass)
            {
                string rain = RainShader.Inject(p.VertexShader.Code);
                if (rain != null)
                {
                    p.VertexShader.Code = rain;
                    RainStreaksInjected = true;
                }
                else if (!p.VertexShader.Code.Contains(RainShader.Marker)) log?.Warning("Particle shader has changed; rain streaks keep vanilla's slant (always toward east)");
                return;
            }
            if (Array.IndexOf(WaterShader.Passes, p.PassName) < 0) return;
            injectedLevel = WaterCodeLevel;
            if (WaterCodeLevel >= 2)
            {
                // this machine's driver wouldn't take Sea's water code: vanilla water
                WaterRenderer.Injected = WaterRenderer.WavesInjected = DryHullsInjected = false;
                return;
            }
            bool dry = WaterCodeLevel == 0;
            string code = WaterShader.Inject(p.VertexShader.Code, out bool waves, dry);
            if (code == null)
            {
                if (!p.VertexShader.Code.Contains(WaterShader.Marker)) log?.Warning("Water shader {0} has changed; Sea can't add its water code", p.PassName);
                return;
            }
            if (!waves) log?.Warning("Water shader {0}: vanilla's wave call has changed; it keeps vanilla's waves", p.PassName);
            p.VertexShader.Code = code;
            WaterRenderer.Injected = true;
            if (waves) WaterRenderer.WavesInjected = true;
            if (!dry)
            {
                DryHullsInjected = false;
                return;
            }
            // dry hulls: the fragment stage cuts the water out inside them (the vertex stage now passes seaFragPos)
            string frag = p.FragmentShader?.Code == null ? null : WaterShader.InjectFragment(p.FragmentShader.Code);
            if (frag != null)
            {
                p.FragmentShader.Code = frag;
                DryHullsInjected = true;
            }
            else if (p.FragmentShader?.Code?.Contains(WaterShader.DryMarker) != true) log?.Warning("Water shader {0}: no fragment stage to cut dry hulls out of; water shows inside them", p.PassName);
        }

        /// <summary>Set once a water fragment shader was loaded with the dry-hull cut.</summary>
        public static bool DryHullsInjected;

        /// <summary>
        /// How much of Sea's water code this machine's graphics driver takes: 0 all of it, 1 all but the dry-hull cut,
        /// 2 none (vanilla water). Sea steps down, logs it and reloads the shaders when a water shader fails to compile
        /// with its code, so a driver that won't take it never leaves the sea undrawn.
        /// </summary>
        public static int WaterCodeLevel;
        static int injectedLevel = -1;
        /// <summary>Set by the client: recompile the shaders soon (on the main thread).</summary>
        public static Action RequestShaderReload;

        static void CompilePostfix(object __instance, bool __result)
        {
            if (__result || !(__instance is IShaderProgram p) || Array.IndexOf(WaterShader.Passes, p.PassName) < 0) return;
            if (p.VertexShader?.Code?.Contains(WaterShader.Marker) != true) return;   // vanilla's own code failed: not ours to fix
            if (WaterCodeLevel != injectedLevel || WaterCodeLevel >= 2) return;      // already stepped down for this load
            WaterCodeLevel = injectedLevel + 1;
            log?.Warning("Water shader {0} didn't compile with Sea's code (the error is above); trying again {1}", p.PassName,
                WaterCodeLevel == 1 ? "without cutting the water out of dry hulls" : "with vanilla's water (no Sea waves, water may show inside ships)");
            RequestShaderReload?.Invoke();
        }

        // ---------------------------------------------------------------- dry places below the waterline

        static FieldInfo mainCamera, cameraEyePos;

        /// <summary>The client's camera eye (world position), or null.</summary>
        static Vec3d CameraEye(object clientMain)
        {
            if (mainCamera?.GetValue(clientMain) is object cam) return cameraEyePos?.GetValue(cam) as Vec3d;
            return null;
        }

        /// <summary>Eyes in a dry hull aren't under water: no fog, tint, muffled sound or underwater ambience.</summary>
        static void EyesInWaterDepthPostfix(object __instance, ref float __result)
        {
            if (__result <= 0) return;
            var eye = CameraEye(__instance);
            if (eye != null && SeaSystem.For(EnumAppSide.Client)?.IsDry(eye.X, eye.Y, eye.Z) == true) __result = 0;
        }

        static void EyesInWaterColorShiftPostfix(object __instance, ref int __result)
        {
            if (__result <= 0) return;
            var eye = CameraEye(__instance);
            if (eye != null && SeaSystem.For(EnumAppSide.Client)?.IsDry(eye.X, eye.Y, eye.Z) == true) __result = 0;
        }

        /// <summary>
        /// A creature whose eyes are in water inside a dry place breathes (server). Vanilla's check is the block at the
        /// eyes (the same eye height it uses); only a liquid there is overruled, never a solid block.
        /// </summary>
        static void BreathePostfix(EntityBehaviorBreathe __instance)
        {
            if (__instance.HasAir) return;
            var e = __instance.entity;
            if (e?.World == null || e.World.Side != EnumAppSide.Server) return;
            var sys = SeaSystem.For(EnumAppSide.Server);
            if (sys == null || sys.DrySpaceSources == 0) return;
            var pos = e.Pos;
            double eyeY = pos.Y + (e.Swimming ? e.Properties.SwimmingEyeHeight : e.Properties.EyeHeight);
            double eyeX = pos.X + (e.LocalEyePos?.X ?? 0), eyeZ = pos.Z + (e.LocalEyePos?.Z ?? 0);
            var block = e.World.BlockAccessor.GetBlock(new BlockPos((int)Math.Floor(eyeX), (int)Math.Floor(eyeY), (int)Math.Floor(eyeZ), pos.Dimension), BlockLayersAccess.MostSolid);
            if (block != null && block.Id != 0 && !block.IsLiquid()) return;   // something solid at the eyes: vanilla's call stands
            if (sys.IsDry(eyeX, eyeY, eyeZ)) __instance.HasAir = true;
        }

        /// <summary>
        /// Someone walking where the world has water but a dry place says it's dry (a ship's hold): no splashes, sediment,
        /// glowing plankton or splash sound. Vanilla makes them for anyone moving with their feet in water, and aboard a
        /// sailing ship everyone is always moving.
        /// </summary>
        static bool NotInDryPlacePrefix(Entity __instance)
        {
            var e = __instance;
            if (e?.World == null) return true;
            var sys = SeaSystem.For(e.World.Side);
            if (sys == null || sys.DrySpaceSources == 0) return true;
            var pos = e.Pos;
            return !sys.IsDry(pos.X, pos.Y + 0.1, pos.Z);
        }

        /// <summary>For <see cref="DryParticles"/> (the client's particle thread).</summary>
        static bool ClientDry(double x, double y, double z)
        {
            var sys = SeaSystem.For(EnumAppSide.Client);
            return sys != null && sys.DrySpaceSources > 0 && sys.IsDry(x, y, z);
        }

        /// <summary>For <see cref="DryParticles"/>: dry at any moment of a particle's life (the client's particle thread; 0.3.5).</summary>
        static bool ClientDryWithin(double x, double y, double z, double seconds)
        {
            var sys = SeaSystem.For(EnumAppSide.Client);
            return sys != null && sys.DrySpaceSources > 0 && sys.IsDryWithin(x, y, z, seconds);
        }

        // ---------------------------------------------------------------- wind

        /// <summary>Vanilla's wind strength by height (WeatherSimulationRegion.GetWindSpeed): stronger up high, weak underground.</summary>
        public static double AltitudeScale(double strength, double y, int seaLevel)
        {
            if (y > seaLevel) return Math.Min(strength * Math.Max(1.0, 0.9 + (y - seaLevel) / 100.0), WindModel.MaxStrength);
            return strength / (1.0 + (seaLevel - y) / 4.0);
        }

        /// <summary>
        /// Each 512-block weather region keeps its own wind, which drives the wind sound and the /weather readouts.
        /// Make it Sea's wind at the region's centre.
        /// </summary>
        static void RegionWindPostfix(WeatherSimulationRegion __instance, WeatherSystemBase ___ws, double posY, ref double __result)
        {
            var api = ___ws?.api;
            if (api == null) return;
            var sys = SeaSystem.For(api.Side);
            var m = sys?.Model;
            if (m == null) return;
            double hours = api.World.Calendar.TotalHours;
            if (!sys.Controls(hours)) return;
            int rs = sys.RegionSize;
            double s = m.At((__instance.regionX + 0.5) * rs, (__instance.regionZ + 0.5) * rs, hours, false).Strength;
            __result = AltitudeScale(s, posY, api.World.SeaLevel);
        }

        /// <summary>Rain, snow and hail particles take their drift from GetWindSpeedAt(...).ToVec3f(); turn it to Sea's direction.</summary>
        static IEnumerable<CodeInstruction> RainWindTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var getWind = AccessTools.Method(typeof(IBlockAccessor), nameof(IBlockAccessor.GetWindSpeedAt), new[] { typeof(Vec3d) });
            var toVec3f = AccessTools.Method(typeof(Vec3d), nameof(Vec3d.ToVec3f));
            var replacement = AccessTools.Method(typeof(Hooks), nameof(RainWind));
            bool afterGetWind = false, done = false;
            foreach (var ci in instructions)
            {
                if (!done && ci.Calls(getWind)) afterGetWind = true;
                else if (!done && afterGetWind && ci.Calls(toVec3f))
                {
                    yield return new CodeInstruction(OpCodes.Call, replacement).WithLabels(ci.labels).WithBlocks(ci.blocks);
                    done = true;
                    continue;
                }
                yield return ci;
            }
            if (!done) log?.Warning("Wind lookup not found in weather particles; rain keeps vanilla's direction");
        }

        public static Vec3f RainWind(Vec3d w)
        {
            var d = LocalWind;
            if (d == null) return w.ToVec3f();
            // vanilla puts the whole strength in X
            return new Vec3f((float)(w.X * d[0]), (float)w.Y, (float)(w.X * d[1] + w.Z));
        }

        // ---------------------------------------------------------------- clouds

        static readonly Dictionary<Type, (FieldInfo timer, FieldInfo tx, FieldInfo tz)> cloudFields = new();

        /// <summary>
        /// Vanilla's cloud renderers pick a random eastward drift every 20-120 s and add 3 × the wind strength to X.
        /// Keep their timer from firing and give them Sea's drift instead (they ease toward it themselves).
        /// </summary>
        static void CloudTickPrefix(object __instance)
        {
            var d = LocalWind;
            if (d == null || !cloudFields.TryGetValue(__instance.GetType(), out var f)) return;
            float speed = 1f + 6f * d[2];
            f.timer.SetValue(__instance, ClientMs + 60000);
            f.tx.SetValue(__instance, d[0] * speed - 3f * d[3]);
            f.tz.SetValue(__instance, d[1] * speed);
        }
    }
}
