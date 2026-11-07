using System;
using System.Collections.Generic;
using System.Reflection;
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
    /// side unloads. Each patch that fails only switches its own feature off. (0.4.0) All of them are about water:
    /// the patches that put Sea's wind into vanilla's weather (wind lookups, region wind, rain direction, cloud drift,
    /// rain streaks) moved to Earth with the weather (SEA-D30).
    /// Two sets, each counted under one lock (a world starting while the last one's server is still unloading can't
    /// lose its patches): the patches both sides need (breathing, splashes), and the client's (water shaders and
    /// uniforms, the camera, particles), which only the client applies and removes, on its main thread. In singleplayer
    /// the integrated server is disposed last, on its own thread, while the main menu draws through the patched shader
    /// methods: it must not rewrite them then. A dedicated server applies the client's set too, for the self-test.
    /// </summary>
    public static class Hooks
    {
        const string HarmonyId = "sea", ClientHarmonyId = "sea.client";
        static readonly object sync = new();
        static Harmony harmony, clientHarmony;
        static int users, clientUsers;
        static readonly List<string> clientActive = new();
        static ILogger log;

        /// <summary>The water shader patch is in place (the shaders still have to be reloaded to pick it up).</summary>
        public static bool ShaderPatched;

        /// <summary>Patches that went in, for the self-test and .sea.</summary>
        public static readonly List<string> Active = new();

        /// <param name="clientPatches">the client's set too: on the client, and on a dedicated server (the self-test)</param>
        public static void Apply(ILogger logger, bool clientPatches)
        {
            lock (sync)
            {
                if (users++ == 0)
                {
                    log = logger;
                    harmony = new Harmony(HarmonyId);
                    ApplyBoth(harmony);
                }
                if (clientPatches && clientUsers++ == 0)
                {
                    log = logger;
                    clientHarmony = new Harmony(ClientHarmonyId);
                    int before = Active.Count;
                    ApplyClient(clientHarmony);
                    clientActive.AddRange(Active.GetRange(before, Active.Count - before));
                }
            }
        }

        /// <summary>The patches both sides need: creatures breathing (server), splashes (both).</summary>
        static void ApplyBoth(Harmony harmony)
        {
            Try("vanilla sailboat flooding", () => {
                harmony.Patch(AccessTools.Method(typeof(EntityBoat), nameof(EntityBoat.Initialize)), postfix: new HarmonyMethod(typeof(NativeBoats), nameof(NativeBoats.Initialized)));
                harmony.Patch(AccessTools.Method(typeof(EntityBoat), nameof(EntityBoat.OnEntityDespawn)), postfix: new HarmonyMethod(typeof(NativeBoats), nameof(NativeBoats.Despawned)));
                harmony.Patch(AccessTools.Method(typeof(EntityBoat), nameof(EntityBoat.OnGameTick)), postfix: new HarmonyMethod(typeof(NativeBoats), nameof(NativeBoats.Tick)));
                harmony.Patch(AccessTools.PropertyGetter(typeof(EntityBoat), nameof(EntityBoat.MaterialDensity)), postfix: new HarmonyMethod(typeof(NativeBoats), nameof(NativeBoats.Density)));
                harmony.Patch(AccessTools.PropertyGetter(typeof(EntityBoat), nameof(EntityBoat.SwimmingOffsetY)), postfix: new HarmonyMethod(typeof(NativeBoats), nameof(NativeBoats.Draft)));
                harmony.Patch(AccessTools.Method(typeof(EntityBoat), nameof(EntityBoat.OnInteract)), prefix: new HarmonyMethod(typeof(NativeBoats), nameof(NativeBoats.Bail)));
                harmony.Patch(AccessTools.Method(typeof(EntityBoat), nameof(EntityBoat.GetInfoText)), postfix: new HarmonyMethod(typeof(NativeBoats), nameof(NativeBoats.Info)));
                harmony.Patch(AccessTools.Method(typeof(EntityBoat), "tryPickup"), prefix: new HarmonyMethod(typeof(NativeBoats), nameof(NativeBoats.Pickup)));
            });
            Try("boat seats ride waves", () => harmony.Patch(AccessTools.PropertyGetter(typeof(EntityRideableSeat), nameof(EntityRideableSeat.SeatPosition)),
                postfix: new HarmonyMethod(typeof(BoatWaves), nameof(BoatWaves.SeatPosition))));
            Try("sailboat sails by wind", () => harmony.Patch(AccessTools.Method(typeof(EntityBoat), nameof(EntityBoat.SeatsToMotion)),
                postfix: new HarmonyMethod(typeof(BoatWaves), nameof(BoatWaves.SailMotion))));
            Try("breathing in dry places", () => harmony.Patch(AccessTools.Method(typeof(EntityBehaviorBreathe), nameof(EntityBehaviorBreathe.Check)),
                postfix: new HarmonyMethod(typeof(Hooks), nameof(BreathePostfix))));
            Try("no splashing in dry places", () =>
            {
                harmony.Patch(AccessTools.Method(typeof(EntityAgent), "SpawnFloatingSediment") ?? throw new Exception("SpawnFloatingSediment not found"),
                    prefix: new HarmonyMethod(typeof(Hooks), nameof(NotInDryPlacePrefix)));
                harmony.Patch(AccessTools.Method(typeof(Entity), nameof(Entity.OnCollideWithLiquid)),
                    prefix: new HarmonyMethod(typeof(Hooks), nameof(NotInDryPlacePrefix)));
            });
        }

        /// <summary>The client's patches: the water shaders and their uniforms, the camera in dry places, water particles.</summary>
        static void ApplyClient(Harmony harmony)
        {
            Try("vanilla sailboat dry volume", () => harmony.Patch(AccessTools.Method(typeof(EntityBehaviorHideWaterSurface), nameof(EntityBehaviorHideWaterSurface.OnRenderFrame)),
                prefix: new HarmonyMethod(typeof(NativeBoats), nameof(NativeBoats.NativeMask))));
            Try("boats ride waves", () => harmony.Patch(AccessTools.Method(typeof(EntityBoat), nameof(EntityBoat.OnRenderFrame)),
                prefix: new HarmonyMethod(typeof(BoatWaves), nameof(BoatWaves.BeforeRenderBoat)),
                postfix: new HarmonyMethod(typeof(BoatWaves), nameof(BoatWaves.RenderBoat))));
            Try("boat hulls heave", () => harmony.Patch(AccessTools.Method(typeof(EntityShapeRenderer), nameof(EntityShapeRenderer.loadModelMatrix)),
                postfix: new HarmonyMethod(typeof(BoatWaves), nameof(BoatWaves.ModelMatrix))));
            Try("boat interactions ride waves", () => harmony.Patch(AccessTools.Method(typeof(EntityBehaviorSelectionBoxes), "applyBoxTransform"),
                prefix: new HarmonyMethod(typeof(BoatWaves), nameof(BoatWaves.SelectionTransform))));
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
            // (0.4.2) Sea's water uniforms go in when the game itself uses a water program, not by using it once more
            Try("water uniforms", () =>
            {
                var program = AccessTools.TypeByName("Vintagestory.Client.NoObf.ShaderProgramBase") ?? throw new Exception("shader program base class not found");
                harmony.Patch(AccessTools.Method(program, "Use") ?? throw new Exception("Use not found"), postfix: new HarmonyMethod(typeof(Hooks), nameof(UsePostfix)));
                UniformsOnUse = true;
            });
            Try("eyes not under water in dry places", () =>
            {
                var client = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientMain") ?? throw new Exception("client class not found");
                var camera = AccessTools.TypeByName("Vintagestory.Client.NoObf.Camera") ?? throw new Exception("camera class not found");
                mainCamera = Need(client, "MainCamera");
                cameraEyePos = Need(camera, "CameraEyePos");
                harmony.Patch(AccessTools.Method(client, "EyesInWaterDepth"), postfix: new HarmonyMethod(typeof(Hooks), nameof(EyesInWaterDepthPostfix)));
                harmony.Patch(AccessTools.Method(client, "GetEyesInWaterColorShift"), postfix: new HarmonyMethod(typeof(Hooks), nameof(EyesInWaterColorShiftPostfix)));
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
                // (0.4.2) both checked before either is patched: a feature reported off is off in both
                var methods = new List<MethodInfo>();
                foreach (var name in new[] { "SpawnRainParticles", "SpawnDustParticles" })
                {
                    var m = AccessTools.Method(typeof(WeatherSimulationParticles), name) ?? throw new Exception(name + " not found");
                    if (DryParticles.SpawnCalls(m) == 0) throw new Exception("no particle spawn found in " + name);
                    methods.Add(m);
                }
                foreach (var m in methods)
                    harmony.Patch(m, transpiler: new HarmonyMethod(typeof(DryParticles), nameof(DryParticles.OnWaterTranspiler)));
            });
        }

        /// <summary>(0.4.2) Sea's water uniforms are uploaded as the game uses a water program (<see cref="UsePostfix"/>).</summary>
        public static bool UniformsOnUse;

        static void UsePostfix(object __instance)
        {
            if (__instance is IShaderProgram p) WaterRenderer.OnUse(p);
        }

        static FieldInfo Need(Type t, string name) => AccessTools.Field(t, name) ?? throw new Exception("field " + name + " not found");

        /// <param name="clientPatches">as given to <see cref="Apply"/> on this side</param>
        public static void Remove(bool clientPatches)
        {
            lock (sync)
            {
                if (clientPatches && clientUsers > 0 && --clientUsers == 0 && clientHarmony != null)
                {
                    clientHarmony.UnpatchAll(ClientHarmonyId);
                    clientHarmony = null;
                    foreach (var what in clientActive) Active.Remove(what);
                    clientActive.Clear();
                    ShaderPatched = false;
                    UniformsOnUse = false;
                    // (0.4.2) what the shaders hold is found out again next time: the game may reload them without Sea's patch in
                    // between (a graphics setting changed in the main menu), and the next world must then reload them with it
                    WaterRenderer.Injected = WaterRenderer.WavesInjected = DryHullsInjected = false;
                    injectedLevel = -1;
                }
                if (users > 0 && --users == 0 && harmony != null)
                {
                    harmony.UnpatchAll(HarmonyId);
                    harmony = null;
                    Active.Clear();
                }
            }
        }

        static void Try(string what, Action patch)
        {
            try { patch(); Active.Add(what); }
            catch (Exception e) { log?.Warning("Could not patch ({0}), that feature is off: {1}", what, e.Message); }
        }

        // ---------------------------------------------------------------- water shader

        static void LoadShaderProgramPostfix(object program)
        {
            if (!(program is IShaderProgram p) || p.VertexShader == null) return;
            if (p.PassName == "farterrain" && p.AssetDomain == "wmap" && p.FragmentShader != null && !CelestialFarDisabled)
            {
                string far = CelestialRenderer.FarFragment(p.FragmentShader.Code);
                if (far != null) p.FragmentShader.Code = far;
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
            if (p.PassName == "chunkliquid" && waves && !CelestialDisabled)
            {
                string cv = CelestialShader.Vertex(code), cf = CelestialShader.Fragment(p.FragmentShader?.Code);
                if (cv != null && cf != null)
                {
                    p.VertexShader.Code = cv;
                    p.FragmentShader.Code = cf;
                }
                else log?.Warning("[sea] Celestial reflection anchors missing; keeping vanilla highlights.");
            }
            WaterRenderer.Injected = true;
            if (waves) WaterRenderer.WavesInjected = true;
            if (!dry)
            {
                DryHullsInjected = false;
                return;
            }
            // dry hulls: the fragment stage cuts the water out inside them (the vertex stage now passes sea_FragPos)
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
        public static bool CelestialDisabled;
        public static bool CelestialFarDisabled;
        static int injectedLevel = -1;
        /// <summary>Set by the client: recompile the shaders soon (on the main thread).</summary>
        public static Action RequestShaderReload;

        static void CompilePostfix(object __instance, bool __result)
        {
            if (__result || !(__instance is IShaderProgram p)) return;
            if (!CelestialFarDisabled && p.PassName == "farterrain" && p.AssetDomain == "wmap" && p.FragmentShader?.Code is string farCode && (farCode.Contains("sea_NightFloor") || farCode.Contains("seaNightFloor")))
            {
                CelestialFarDisabled = true;
                log?.Warning("[sea] Wmap night floor shader failed; reloading Wmap without Sea's optional brightness adjustment.");
                RequestShaderReload?.Invoke();
                return;
            }
            if (Array.IndexOf(WaterShader.Passes, p.PassName) < 0) return;
            if (!CelestialDisabled && p.FragmentShader?.Code?.Contains(CelestialShader.Marker) == true)
            {
                CelestialDisabled = true;
                log?.Warning("[sea] Celestial water shader failed; reloading with vanilla highlights and keeping Sea's waves/dry hulls.");
                RequestShaderReload?.Invoke();
                return;
            }
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
            var eye = CameraEye(__instance);
            if (eye == null || !ClientInWorld()) return;
            var sea = SeaSystem.For(EnumAppSide.Client);
            if (sea?.Boats?.CameraDepth(eye, out float depth) == true) __result = depth;
            else if (sea?.IsDry(eye.X, eye.Y, eye.Z) == true) __result = 0;
        }

        static void EyesInWaterColorShiftPostfix(object __instance, ref int __result)
        {
            var eye = CameraEye(__instance);
            if (eye == null || !ClientInWorld()) return;
            var sea = SeaSystem.For(EnumAppSide.Client);
            if (sea?.Boats?.CameraDepth(eye, out float depth) == true) __result = (int)Math.Clamp(depth * 600, 0, 100);
            else if (sea?.IsDry(eye.X, eye.Y, eye.Z) == true) __result = 0;
        }

        /// <summary>
        /// A creature whose eyes are in water inside a dry place breathes (server). Vanilla's check is the block at the
        /// eyes (the same eye height it uses); only a liquid there is overruled, never a solid block.
        /// </summary>
        static void BreathePostfix(EntityBehaviorBreathe __instance)
        {
            var e = __instance.entity;
            if (e?.World == null || e.World.Side != EnumAppSide.Server) return;
            if (e is EntityPlayer player)
            {
                var mode = e.World.PlayerByUid(player.PlayerUID)?.WorldData.CurrentGameMode;
                if (mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator) return;
            }
            var sys = SeaSystem.For(EnumAppSide.Server);
            if (sys == null || sys.DrySpaceSources == 0) return;
            var pos = e.Pos;
            if (pos.Dimension != 0) return;   // dry places are in the world itself (IDrySpaces gets no dimension)
            double eyeY = pos.Y + (e.Swimming ? e.Properties.SwimmingEyeHeight : e.Properties.EyeHeight);
            double eyeX = pos.X + (e.LocalEyePos?.X ?? 0), eyeZ = pos.Z + (e.LocalEyePos?.Z ?? 0);
            var block = e.World.BlockAccessor.GetBlock(new BlockPos((int)Math.Floor(eyeX), (int)Math.Floor(eyeY), (int)Math.Floor(eyeZ), pos.Dimension), BlockLayersAccess.MostSolid);
            if (block != null && block.Id != 0 && !block.IsLiquid()) return;   // something solid at the eyes: vanilla's call stands
            if (e is EntityAgent rider && sys.Boats?.EyeDepth(rider, new Vec3d(eyeX, eyeY, eyeZ), out float depth) == true)
                __instance.HasAir = depth <= 0;
            else if (!__instance.HasAir && sys.IsDry(eyeX, eyeY, eyeZ)) __instance.HasAir = true;
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
            return pos.Dimension != 0 || !sys.IsDry(pos.X, pos.Y + 0.1, pos.Z);
        }

        /// <summary>
        /// (0.3.7) The local player is in the world itself, not in another dimension. Dry places are asked by world
        /// position only (<see cref="IDrySpaces"/> gets no dimension), and ships sail in the world itself, so elsewhere
        /// there are none: a spot at the same coordinates in another dimension isn't in a hull.
        /// </summary>
        static bool ClientInWorld() => (SeaSystem.For(EnumAppSide.Client)?.PlayerDimension ?? 0) == 0;

        /// <summary>For <see cref="DryParticles"/> (the client's particle thread).</summary>
        static bool ClientDry(double x, double y, double z)
        {
            var sys = SeaSystem.For(EnumAppSide.Client);
            return sys != null && sys.DrySpaceSources > 0 && ClientInWorld() && sys.IsDry(x, y, z);
        }

        /// <summary>For <see cref="DryParticles"/>: dry at any moment of a particle's life (the client's particle thread; 0.3.5).</summary>
        static bool ClientDryWithin(double x, double y, double z, double seconds)
        {
            var sys = SeaSystem.For(EnumAppSide.Client);
            return sys != null && sys.DrySpaceSources > 0 && ClientInWorld() && sys.IsDryWithin(x, y, z, seconds);
        }
    }
}
