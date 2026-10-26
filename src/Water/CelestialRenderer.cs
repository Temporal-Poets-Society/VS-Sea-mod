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
        // (0.4.7) AstraTerra's sun as it draws it: whether it does, its colour through the air and its sunset glow in the
        // sky (dev.19 and later; older builds have none of them and the moon adapter does not need them)
        System.Func<bool> customSun;
        System.Func<double, (float, float, float)> sunThroughAir;
        System.Func<Vec4f> sunsetGlow;
        bool astraFailed, astraSunFailed;
        float clear = 1, night, phase, floor, roughness, skyLight = 1;
        float ambientScale = 1, aboveSea;
        Vec3f sun = new(0, 1, 0), moon = new(0, -1, 0);
        readonly Vec4f sunset = new();
        float sunR = 1, sunG = 1, sunB = 1;
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
            Patch("real twilight", PatchTwilight);
            Patch("shadow and shading hand-off", PatchHandOff);
            api.Event.LevelFinalize += InitializeAstra;
        }

        // (0.4.7-night) The client calendar's rendered copy of the day's light, written after the game's (and AstraTerra's)
        // own update each frame. Bound once as typed delegates: no boxing in the frame.
        static System.Action<object, float> setSunLight, setMoonLight, setDayLight;
        static System.Func<object, float> sunsetMod;
        static System.Func<object, float, float, int> sunlightPixel;

        void PatchTwilight()
        {
            var calendar = AccessTools.TypeByName("Vintagestory.Common.ClientGameCalendar") ?? throw new Exception("ClientGameCalendar missing");
            var update = AccessTools.Method(calendar, "Update") ?? throw new Exception("ClientGameCalendar.Update missing");
            System.Action<object, float> Setter(string name)
            {
                var set = AccessTools.PropertySetter(calendar, name) ?? throw new Exception(name + " has no setter");
                var o = Expression.Parameter(typeof(object)); var v = Expression.Parameter(typeof(float));
                return Expression.Lambda<System.Action<object, float>>(Expression.Call(Expression.Convert(o, calendar), set, v), o, v).Compile();
            }
            setSunLight = Setter("SunLightStrength");
            setMoonLight = Setter("MoonLightStrength");
            setDayLight = Setter("DayLightStrength");
            var mod = AccessTools.PropertyGetter(calendar, "SunsetMod") ?? throw new Exception("SunsetMod missing");
            var cal = Expression.Parameter(typeof(object));
            sunsetMod = Expression.Lambda<System.Func<object, float>>(Expression.Call(Expression.Convert(cal, calendar), mod), cal).Compile();
            var pixel = AccessTools.Method(calendar, "getSunlightPixelRel", new[] { typeof(float), typeof(float) }) ?? throw new Exception("getSunlightPixelRel missing");
            var argb = pixel.ReturnType.GetMethod("ToArgb", Type.EmptyTypes) ?? throw new Exception("sunlight pixel has no ToArgb");
            var x = Expression.Parameter(typeof(float)); var y = Expression.Parameter(typeof(float));
            sunlightPixel = Expression.Lambda<System.Func<object, float, float, int>>(
                Expression.Call(Expression.Call(Expression.Convert(cal, calendar), pixel, x, y), argb), cal, x, y).Compile();
            // After every other postfix (AstraTerra's local moon and near-body light), so this reads the light they leave.
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(CelestialRenderer), nameof(CalendarPostfix)) { priority = Priority.Low });
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
            customSun = null; sunThroughAir = null; sunsetGlow = null;
            astraSunFailed = false;
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
            try
            {
                Type Need(string name) => asm.GetType(name) ?? throw new Exception(name + " missing");
                customSun = Getter(Need("AstraTerra.Client.Rendering.SunDiscRenderer"), "HidesVanillaSun");
                sunThroughAir = Bind<System.Func<double, (float, float, float)>>(Need("AstraTerra.Astronomy.Twilight"), "SunColour");
                var shown = Need("AstraTerra.Astronomy.SunsetScatter").GetProperty("Shown", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new Exception("SunsetScatter.Shown missing");
                sunsetGlow = Expression.Lambda<System.Func<Vec4f>>(Expression.Property(null, shown)).Compile();
                api.Logger.Notification("[sea] AstraTerra's sun read too: the glint takes its colour, the sky reflection its sunset glow.");
            }
            catch (Exception ex)
            {
                customSun = null; sunThroughAir = null; sunsetGlow = null;
                api.Logger.Notification("[sea] AstraTerra's sun colour and sunset glow not available ({0}); the glint uses Sea's own table.", ex.Message);
            }
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
                ? NightSkyMath.MoonAmbientScale(cal.SunLightStrength, cal.MoonLightStrength, cal.DayLightStrength, moon.Y, phase, clear) : 1;
            var am = api.Ambient;
            if (api.ElapsedMilliseconds - weatherAt > 250)
            {
                weather ??= api.ModLoader.GetModSystem<WeatherSystemClient>();
                rain = weather?.GetPrecipitation(ep.Pos.X, api.World.SeaLevel, ep.Pos.Z) ?? 0;
                weatherAt = api.ElapsedMilliseconds;
            }
            clear = NightSkyMath.ClearSky(am.BlendedCloudDensity, rain, am.BlendedFogDensity);
            skyLight = NightSkyMath.SkyDaylight(cal.DayLightStrength, cal.MoonLightStrength, ep.Pos.Y - api.World.SeaLevel);
            night = NightSkyMath.Night(sun.Y);
            floor = NightSkyMath.Visibility(sun.Y, moon.Y, phase, clear, NightStrength);
            if (ep.Pos.Dimension != 0 || api.Render.ShaderUniforms.CameraUnderwater > 0.01f) { floor = 0; ambientScale = 1; }
            roughness = (float)Math.Clamp(sea.CameraWaves.SignificantHeight / 3, 0, 1);
            roughness *= (float)sea.WaveFactorAt(ep.Pos.X, ep.Pos.Z);
            aboveSea = (float)ep.Pos.Y - api.World.SeaLevel;
            SunAndGlow(cal);
        }

        /// <summary>
        /// (0.4.7) The glint's colour and the sky's sunset glow, from one source. While AstraTerra draws the sun, its colour
        /// through the air (Twilight.SunColour), so the glitter path is the disc's colour; otherwise the game's reflect
        /// colour reddened by Sea's copy of that table, as in pre.4. The glow is AstraTerra's (SunsetScatter.Shown), zero
        /// without it: the game's own sky has none.
        /// </summary>
        void SunAndGlow(IClientGameCalendar cal)
        {
            sunset.Set(0, 0, 0, 0);
            if (astraPresent && !astraSunFailed && sunThroughAir != null)
            {
                try
                {
                    var glow = sunsetGlow();
                    if (glow != null) sunset.Set(glow.X, glow.Y, glow.Z, glow.W);
                    if (customSun())
                    {
                        (sunR, sunG, sunB) = sunThroughAir(sun.Y);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    astraSunFailed = true;
                    sunset.Set(0, 0, 0, 0);
                    api.Logger.Warning("[sea] AstraTerra's sun colour and sunset glow disabled after an error: {0}", ex.Message);
                }
            }
            var reflect = cal.ReflectColor;
            var (airR, airG, airB) = NightSkyMath.SunThroughAir(sun.Y);
            (sunR, sunG, sunB) = (Math.Min(reflect.R, airR), Math.Min(reflect.G, airG), Math.Min(reflect.B, airB));
        }

        // Harmony postfixes run inside vanilla's render loop: an exception there ends the game, so each one switches
        // its effect off and logs once instead.
        static bool ambientFailed, usesFailed, twilightFailed, handOffFailed;
        static AccessTools.FieldRef<object, float> ambientShadows;

        /// <summary>
        /// (0.4.7-pre.7) The game's light hand-off from the sun to the moon, made smooth (issue 15). Its shadow map is cast
        /// from whichever of the two has the larger light term and stays on while the moon is up; with real twilight the
        /// terms cross soon after sunset, so the shadows were cast from a sun at or under the horizon and then flipped to the
        /// moon in one frame, and the shading direction swung within a 0.02 difference. Shadows now fade out while the
        /// casting body is low and around the crossing (NightSkyMath.ShadowKeep), and the shading direction turns over the
        /// same span (LightTowardMoon). Rendering only; off with night visibility 0, as the twilight is.
        /// </summary>
        void PatchHandOff()
        {
            var shadows = AccessTools.TypeByName("Vintagestory.Client.NoObf.SystemRenderShadowMap") ?? throw new Exception("SystemRenderShadowMap missing");
            var before = AccessTools.Method(shadows, "OnRenderBefore") ?? throw new Exception("SystemRenderShadowMap.OnRenderBefore missing");
            var sunMoon = AccessTools.TypeByName("Vintagestory.Client.NoObf.SystemRenderSunMoon") ?? throw new Exception("SystemRenderSunMoon missing");
            var frame = AccessTools.Method(sunMoon, "OnRenderFrame3D") ?? throw new Exception("SystemRenderSunMoon.OnRenderFrame3D missing");
            ambientShadows = AccessTools.FieldRefAccess<float>(AccessTools.TypeByName("Vintagestory.Client.NoObf.AmbientManager"), "DropShadowIntensity");
            harmony.Patch(before, postfix: new HarmonyMethod(typeof(CelestialRenderer), nameof(ShadowPostfix)));
            harmony.Patch(frame, postfix: new HarmonyMethod(typeof(CelestialRenderer), nameof(LightPositionPostfix)));
        }

        static bool HandOffActive(out CelestialRenderer c)
        {
            c = current;
            return !handOffFailed && !twilightFailed && c != null && c.NightStrength > 0 && c.api.World?.Calendar != null;
        }

        static void ShadowPostfix()
        {
            if (!HandOffActive(out var c)) return;
            try
            {
                var cal = c.api.World.Calendar;
                float diff = cal.MoonLightStrength - cal.SunLightStrength;
                // the body the game casts them from (SystemRenderShadowMap.PrepareForShadowRendering)
                float castingY = diff > 0 ? cal.MoonPosition.Y / Math.Max(cal.MoonPosition.Length(), 1e-6f) : cal.SunPositionNormalized.Y;
                float keep = NightSkyMath.ShadowKeep(castingY, diff);
                if (keep >= 1) return;
                var su = c.api.Render.ShaderUniforms;
                su.DropShadowIntensity *= keep;
                ref float held = ref ambientShadows(c.api.Ambient);
                held *= keep;
            }
            catch (Exception ex) { Fail(ref handOffFailed, "Shadow hand-off", ex); }
        }

        static void LightPositionPostfix()
        {
            if (!HandOffActive(out var c)) return;
            try
            {
                var cal = c.api.World.Calendar;
                float t = NightSkyMath.LightTowardMoon(cal.MoonLightStrength - cal.SunLightStrength);
                var sun = cal.SunPositionNormalized;
                var moon = cal.MoonPosition;
                float len = Math.Max(moon.Length(), 1e-6f);
                c.api.Render.ShaderUniforms.LightPosition3D.Set(
                    sun.X + (moon.X / len - sun.X) * t, sun.Y + (moon.Y / len - sun.Y) * t, sun.Z + (moon.Z / len - sun.Z) * t);
            }
            catch (Exception ex) { Fail(ref handOffFailed, "Shading hand-off", ex); }
        }
        static void Fail(ref bool failed, string what, Exception ex)
        {
            failed = true;
            if (current != null) { current.floor = 0; current.ambientScale = 1; }
            current?.api.Logger.Error("[sea] {0} disabled after an error: {1}", what, ex);
        }

        /// <summary>
        /// (0.4.7-night) Real twilight and weather on the client's rendered day's light, the one number the ground's ambient,
        /// the sky dome, the clouds, the fog's brightness and Wmap's far terrain all read: the sun's term as for a sun 2.5
        /// times as far below the horizon (NightSkyMath.TwilightSunY), with its colours, and the moon's term cut by cloud,
        /// rain and fog. Rendering only: the server, spawning and every gameplay light query keep the game's own value
        /// (GameCalendar.GetDayLightStrength is not touched). Never brighter than the game's; off with night visibility 0.
        /// </summary>
        static void CalendarPostfix(object __instance)
        {
            var c = current;
            if (twilightFailed || ambientFailed || c == null || c.NightStrength <= 0 || __instance is not IClientGameCalendar cal
                || !ReferenceEquals(c.api.World?.Calendar, __instance)) return;
            try { c.Twilight(cal, __instance); }
            catch (Exception ex) { Fail(ref twilightFailed, "Real twilight", ex); }
        }

        void Twilight(IClientGameCalendar cal, object raw)
        {
            float sunY = cal.SunPositionNormalized.Y;
            float sunV = cal.SunLightStrength, moonV = cal.MoonLightStrength;
            float sunL = sunV, moonL = moonV * Math.Clamp(clear, 0, 1);
            if (sunY < 0)
            {
                float y = NightSkyMath.TwilightSunY(sunY), x = NightSkyMath.SunlightTextureX(y), mod = sunsetMod(raw);
                int s = sunlightPixel(raw, Math.Clamp(x + mod, 0, 1), 0.01f), r = sunlightPixel(raw, Math.Clamp(x - mod, 0, 1), 0.01f);
                float sr = (s >> 16 & 255) / 255f, sg = (s >> 8 & 255) / 255f, sb = (s & 255) / 255f;
                float twilight = (sr + sg + sb) / 3;
                // (0.4.7-pre.7) Always below the horizon. Up to pre.6 only where the table read darker than the game's own
                // value: just after sunset both are one texel apart in an 8-bit table, often equal, so the colours switched
                // between the two each tick, and with them the game's 0.29 step below (issue 15: the light flickered).
                sunL = Math.Min(twilight, sunV);
                cal.SunColor.Set(sr, sg, sb);
                // The game's reflect colour (the ambient's tint) for the same sun, darkened below the horizon toward
                // ClientGameCalendar.nightColor, without the game's brightening step (NightSkyMath.ReflectDarkening)
                float rr = (r >> 16 & 255) / 255f, rg = (r >> 8 & 255) / 255f, rb = (r & 255) / 255f;
                float k = NightSkyMath.ReflectDarkening(y);
                if (k > 0) { rr = Math.Max(rr - k, 0); rg = Math.Max(rg - k, 0.0627451f); rb = Math.Max(rb - k, 2f / 15f); }
                cal.ReflectColor.Set(rr, rg, rb);
            }
            if (sunL == sunV && moonL == moonV) return;
            float day = NightSkyMath.Daylight(cal.DayLightStrength, sunV, moonV, sunL, moonL);
            setSunLight(raw, sunL);
            setMoonLight(raw, moonL);
            setDayLight(raw, day);
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
                    if (p.HasUniform("seaSunset")) p.Uniform("seaSunset", 0f, 0f, 0f, 0f);
                    if (p.HasUniform("seaGlint")) p.Uniform("seaGlint", 0f);
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
                // (0.4.7) Wmap's far haze and far-sea reflection take the same sunset glow as the dome and the near water
                if (p.HasUniform("seaSunset")) p.Uniform("seaSunset", c.sunset.X, c.sunset.Y, c.sunset.Z, c.sunset.W);
                // (0.4.7-pre.7) the glitter path on Wmap's far sea: the near water's glint, the same numbers (issue 14)
                if (p.HasUniform("seaGlint"))
                {
                    p.Uniform("seaGlint", c.api.World?.Player?.Entity?.Pos.Dimension == 0 ? c.GlintStrength : 0f);
                    p.Uniform("seaSun", c.sun.X, c.sun.Y, c.sun.Z, 1.6f);
                    p.Uniform("seaMoon", c.moon.X, c.moon.Y, c.moon.Z, 0.75f * c.phase * c.night);
                    p.Uniform("seaSunColor", c.sunR, c.sunG, c.sunB);
                    p.Uniform("seaWeather", c.clear, c.night, c.api.Ambient.BlendedCloudDensity, c.roughness);
                }
            }
            if (!p.HasUniform("seaEffects")) return;
            c.WaterPatched = true;
            bool enabled = c.api.World?.Player?.Entity?.Pos.Dimension == 0;
            p.Uniform("seaEffects", enabled ? c.GlintStrength : 0, enabled ? c.SkyStrength : 0);
            p.Uniform("seaEye", c.eye);
            p.Uniform("seaSun", c.sun.X, c.sun.Y, c.sun.Z, 1.6f);  // saturates the glint core, as vanilla's does
            p.Uniform("seaMoon", c.moon.X, c.moon.Y, c.moon.Z, 0.75f * c.phase * c.night);
            // (0.4.7) The drawn sun's colour (SunAndGlow)
            p.Uniform("seaSunColor", c.sunR, c.sunG, c.sunB);
            p.Uniform("seaAboveSea", c.aboveSea);
            p.Uniform("seaSunset", c.sunset.X, c.sunset.Y, c.sunset.Z, c.sunset.W);
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
