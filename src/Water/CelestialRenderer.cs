using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Sea
{
    /// <summary>Client presentation: source sky data, never a camera framebuffer or a second calendar.</summary>
    public sealed class CelestialRenderer : IRenderer
    {
        public double RenderOrder => 0.32; // after AstraTerra's moon, before Wmap and chunks
        public int RenderRange => 9999;
        const string Id = "sea.celestial";
        static CelestialRenderer current;
        readonly ICoreClientAPI api;
        readonly SeaSystem sea;
        readonly Harmony harmony = new(Id);
        // (0.4.4) AstraTerra is read for its moon only: the star reflections are gone (they showed as blotches on small
        // waves; on 21 October the user chose a smooth sky reflection instead)
        bool astraPresent;
        // (0.4.2) AstraTerra's calls, bound once as typed delegates: no argument arrays or boxing every frame
        System.Func<bool> customMoon, nearMoon;
        System.Func<double, IWorldAccessor, double> longitude;
        System.Func<double, double, bool, double> localMoonDays;
        System.Func<IGameCalendar, double, double> moonPhase;
        bool astraFailed;
        float clear = 1, night, phase, floor, roughness, skyLight = 1;
        float ambientScale = 1;
        Vec3f sun = new(0, 1, 0), moon = new(0, -1, 0);
        readonly Vec3f eye = new();
        readonly double[] inverseView = new double[16];
        WeatherSystemClient weather;
        long weatherAt;
        float rain;
        public bool WaterPatched { get; private set; }
        public bool FarPatched { get; private set; }
        public string Status => $"Celestial water: {WaterPatched}; Wmap lighting: {FarPatched}; moon from {(!astraPresent ? "vanilla" : astraFailed ? "vanilla (AstraTerra adapter FAILED, see log)" : "AstraTerra when it draws one")}; clear {clear:0.00}; sun altitude {Math.Asin(sun.Y) * 180 / Math.PI:0.0}°, moon {Math.Asin(moon.Y) * 180 / Math.PI:0.0}°; moon fraction {phase:0.00}; sky daylight {skyLight:0.00}; strengths: night {NightStrength:0.##}, sky {SkyStrength:0.##}, glint {GlintStrength:0.##}; outdoor floor {floor:0.000}; total days {api.World.Calendar.TotalDays:0.00000}.";

        public CelestialRenderer(ICoreClientAPI api, SeaSystem sea)
        {
            this.api = api;
            this.sea = sea;
            current = this;
            ambientFailed = usesFailed = false;
            Patch("shader uniforms", () => harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Vintagestory.Client.NoObf.ShaderProgramBase"), "Use"), postfix: new HarmonyMethod(typeof(CelestialRenderer), nameof(UsePostfix))));
            Patch("outdoor night lighting", () => harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Vintagestory.Client.NoObf.AmbientManager"), "UpdateAmbient"), postfix: new HarmonyMethod(typeof(CelestialRenderer), nameof(AmbientPostfix))));
            api.Event.LevelFinalize += InitializeAstra;
        }

        void Patch(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { api.Logger.Warning("[sea] {0} unavailable: {1}", name, ex.Message); }
        }

        void InitializeAstra()
        {
            // (this world's AstraTerra: an earlier world's stays loaded in the process, so the AppDomain isn't asked)
            astraPresent = false;
            if (!api.ModLoader.IsModEnabled("astraterra")) return;
            var asm = api.ModLoader.GetMod("astraterra")?.Systems?.FirstOrDefault()?.GetType().Assembly
                ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AstraTerra");
            if (asm == null) return;
            astraPresent = true;
            try
            {
                // Explicit capability checks against the installed 0.15.0; no assembly reference or loader dependency.
                Type Need(string name) => asm.GetType(name) ?? throw new Exception(name + " missing");
                customMoon = Getter(Need("AstraTerra.Client.Rendering.MoonDiscRenderer"), "HidesVanillaMoon");
                nearMoon = Getter(Need("AstraTerra.Client.Rendering.NearBodyRenderer"), "HidesVanillaMoon");
                longitude = Bind<System.Func<double, IWorldAccessor, double>>(Need("AstraTerra.Astronomy.ObserverLongitude"), "ForObserver");
                localMoonDays = Bind<System.Func<double, double, bool, double>>(Need("AstraTerra.Astronomy.LocalMoonTime"), "MoonTotalDays");
                moonPhase = Bind<System.Func<IGameCalendar, double, double>>(Need("AstraTerra.Astronomy.VanillaMoonPhase"), "ExactAt");
                api.Logger.Notification("[sea] Optional AstraTerra sky adapter ready: its local moon; no lighting patch on AstraTerra.");
            }
            catch (Exception ex) { FailAstra(ex); }
        }

        /// <summary>A static bool property as a delegate.</summary>
        static System.Func<bool> Getter(Type t, string name)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static) ?? throw new Exception(name + " missing");
            return Expression.Lambda<System.Func<bool>>(Expression.Convert(Expression.Property(null, p), typeof(bool))).Compile();
        }

        /// <summary>A static method as a typed delegate (its parameters converted from the delegate's), or an exception saying what's missing.</summary>
        static D Bind<D>(Type t, string name) where D : Delegate
        {
            var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new Exception(name + " missing");
            var invoke = typeof(D).GetMethod("Invoke");
            var args = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var ps = m.GetParameters();
            if (ps.Length != args.Length) throw new Exception(name + " has other parameters");
            var call = Expression.Call(m, ps.Select((p, i) => (Expression)Expression.Convert(args[i], p.ParameterType)));
            return Expression.Lambda<D>(Expression.Convert(call, invoke.ReturnType), args).Compile();
        }
        void FailAstra(Exception ex)
        {
            astraFailed = true;
            api.Logger.Warning("[sea] Optional AstraTerra sky adapter disabled: {0}. Custom moon reflections suppressed to avoid a false trail.", ex.Message);
        }

        /// <summary>
        /// The sky's state for this frame, worked out once (the ambient update and this renderer both need it; 0.4.2),
        /// and the eye each time it's asked (the ambient update runs before the camera moves).
        /// </summary>
        void Frame()
        {
            var ep = api.World?.Player?.Entity;
            if (ep == null) return;
            if (skyFrame != renderFrame)
            {
                skyFrame = renderFrame;
                Sky(ep);
            }
            // Inverse view gives the actual eye relative to chunk origins, including third person and camera offsets.
            // The camera matrix stays null until the camera's first update (the ambient update runs before it).
            var view = api.Render.CameraMatrixOrigin;
            if (view != null && Mat4d.Invert(inverseView, view) != null)
                eye.Set((float)inverseView[12], (float)inverseView[13], (float)inverseView[14]);
        }

        long renderFrame, skyFrame = -1;

        /// <summary>(0.4.3) This client's live overrides of the strengths (.sea night/sky/glint), for tuning by eye; they last
        /// until the world is left. Null = the server's sea.json.</summary>
        public float? NightOverride, SkyOverride, GlintOverride;
        public float NightStrength => NightOverride ?? sea.Config.NightVisibility;
        public float SkyStrength => SkyOverride ?? sea.Config.SkyReflections;
        public float GlintStrength => GlintOverride ?? sea.Config.CelestialReflections;
        readonly Vec3d observer = new();

        void Sky(Vintagestory.API.Common.Entities.Entity ep)
        {
            var cal = api.World.Calendar;
            sun.Set(cal.SunPositionNormalized).Normalize();
            moon.Set(cal.MoonPosition).Normalize();
            double exact = cal.MoonPhaseExact;
            bool phaseFromCalendar = false;
            if (astraPresent)
            {
                if (astraFailed) moon.Set(0, -1, 0);
                else try
                {
                    if (nearMoon()) moon.Set(0, -1, 0); // a replaced near-body moon is not vanilla's moon
                    else if (customMoon())
                    {
                        double days = localMoonDays(cal.TotalDays, longitude(ep.Pos.X, api.World), true);
                        observer.Set(ep.Pos.X, ep.Pos.InternalY, ep.Pos.Z);
                        moon.Set(cal.GetMoonPosition(observer, days)).Normalize();
                        exact = moonPhase(cal, days);
                        phaseFromCalendar = true;
                    }
                }
                catch (Exception ex) { FailAstra(ex); moon.Set(0, -1, 0); }
            }
            phase = phaseFromCalendar ? (float)(0.5 - 0.5 * Math.Cos(exact * Math.PI / 4))
                : NightSkyMath.Illuminated(sun.Dot(moon)); // vanilla shades from sun/moon directions; AstraTerra uses ExactAt
            ambientScale = phaseFromCalendar && NightStrength > 0
                ? NightSkyMath.MoonAmbientScale(cal.SunLightStrength, cal.MoonLightStrength, cal.DayLightStrength, moon.Y, phase) : 1;
            var am = api.Ambient;
            if (api.ElapsedMilliseconds - weatherAt > 250)
            {
                weather ??= api.ModLoader.GetModSystem<WeatherSystemClient>();
                rain = weather?.GetPrecipitation(ep.Pos.X, api.World.SeaLevel, ep.Pos.Z) ?? 0;
                weatherAt = api.ElapsedMilliseconds;
            }
            clear = NightSkyMath.ClearSky(am.BlendedCloudDensity, rain);
            skyLight = NightSkyMath.SkyDaylight(cal.DayLightStrength, cal.MoonLightStrength, ep.Pos.Y - api.World.SeaLevel);
            night = NightSkyMath.Night(sun.Y);
            floor = NightSkyMath.Visibility(sun.Y, moon.Y, phase, clear, NightStrength);
            if (ep.Pos.Dimension != 0 || api.Render.ShaderUniforms.CameraUnderwater > 0.01f) { floor = 0; ambientScale = 1; }
            roughness = (float)Math.Clamp(sea.CameraWaves.SignificantHeight / 3, 0, 1);
            roughness *= (float)sea.WaveFactorAt(ep.Pos.X, ep.Pos.Z);
        }

        // Harmony postfixes run inside vanilla's render loop: an exception there ends the game, so each one switches
        // its effect off and logs once instead.
        static bool ambientFailed, usesFailed;
        static void Fail(ref bool failed, string what, Exception ex)
        {
            failed = true;
            if (current != null) { current.floor = 0; current.ambientScale = 1; }
            current?.api.Logger.Error("[sea] {0} disabled after an error: {1}", what, ex);
        }

        static void AmbientPostfix(object __instance)
        {
            var c = current;
            if (ambientFailed || c == null || !ReferenceEquals(c.api.Ambient, __instance)) return;
            try
            {
                c.Frame();
                // A floor, not a sum: existing vanilla/AstraTerra brightness wins. Each surface still multiplies by
                // its propagated sunlight (lightColor.a), leaving unlit caves and sealed rooms dark.
                var color = c.api.Ambient.BlendedAmbientColor;
                color.Set(Math.Max(color.R * c.ambientScale, c.floor * 0.78f), Math.Max(color.G * c.ambientScale, c.floor * 0.88f), Math.Max(color.B * c.ambientScale, c.floor));
            }
            catch (Exception ex) { Fail(ref ambientFailed, "Night visibility and celestial sky state", ex); usesFailed = true; }
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            try { Render(); }
            finally { renderFrame++; }
        }

        void Render()
        {
            if (ambientFailed) return;
            try { Frame(); }
            catch (Exception ex) { Fail(ref ambientFailed, "Celestial sky state", ex); usesFailed = true; }
        }

        static void UsePostfix(object __instance)
        {
            var c = current;
            if (c == null || __instance is not IShaderProgram p) return;
            if (usesFailed)
            {
                // Uniforms keep their last values, so switch the effects off explicitly.
                try
                {
                    if (p.HasUniform("seaNightFloor")) p.Uniform("seaNightFloor", 0f, 0f, 0f);
                    if (p.HasUniform("seaNightEnabled")) p.Uniform("seaNightEnabled", 0f);
                    if (p.HasUniform("seaSkyReflect")) p.Uniform("seaSkyReflect", 0f);
                    if (p.HasUniform("seaEffects")) p.Uniform("seaEffects", 0f, 0f);
                }
                catch { }
                return;
            }
            try { SetUniforms(c, p); }
            catch (Exception ex) { Fail(ref usesFailed, "Celestial water and Wmap night uniforms", ex); }
        }

        static void SetUniforms(CelestialRenderer c, IShaderProgram p)
        {
            if (p.HasUniform("seaNightFloor"))
            {
                c.FarPatched = true;
                p.Uniform("seaNightFloor", c.floor * 0.78f, c.floor * 0.88f, c.floor);
                if (p.HasUniform("seaNightEnabled")) p.Uniform("seaNightEnabled", c.NightStrength > 0 ? 1f : 0f);
                if (p.HasUniform("seaSkyReflect")) p.Uniform("seaSkyReflect", c.api.World?.Player?.Entity?.Pos.Dimension == 0 ? c.SkyStrength : 0f);
            }
            if (!p.HasUniform("seaEffects")) return;
            c.WaterPatched = true;
            bool enabled = c.api.World?.Player?.Entity?.Pos.Dimension == 0;
            p.Uniform("seaEffects", enabled ? c.GlintStrength : 0, enabled ? c.SkyStrength : 0);
            p.Uniform("seaEye", c.eye);
            p.Uniform("seaSun", c.sun.X, c.sun.Y, c.sun.Z, 1.6f);  // saturates the glint core, as vanilla's does
            p.Uniform("seaMoon", c.moon.X, c.moon.Y, c.moon.Z, 0.75f * c.phase * c.night);
            p.Uniform("seaSunColor", c.api.World.Calendar.ReflectColor);
            var am = c.api.Ambient;
            p.Uniform("seaWeather", c.clear, c.night, am.BlendedCloudDensity, c.roughness);
            var su = c.api.Render.ShaderUniforms;
            p.Uniform("seaSky", c.skyLight, su.SunsetMod, am.BlendedFogMin, am.BlendedFogDensity);
            GL.GetInteger(GetPName.ActiveTexture, out int active);
            p.BindTexture2D("seaSkyTex", su.SkyTextureId, 10);
            p.BindTexture2D("seaGlowTex", su.GlowTextureId, 11);
            GL.ActiveTexture((TextureUnit)active);
        }

        public static string FarFragment(string code)
            => CelestialShader.FarFragment(code);

        public void Dispose()
        {
            api.Event.LevelFinalize -= InitializeAstra;
            harmony.UnpatchAll(Id);
            if (current == this) current = null;
        }
    }
}
