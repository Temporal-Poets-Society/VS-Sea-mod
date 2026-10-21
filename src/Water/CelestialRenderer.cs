using System;
using System.Collections;
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
        const int Width = 1024, Height = 512;
        const string Id = "sea.celestial";
        static CelestialRenderer current;
        readonly ICoreClientAPI api;
        readonly SeaSystem sea;
        readonly Harmony harmony = new(Id);
        readonly float[] pixels = new float[Width * Height];
        int atlas, vanillaCube, filteredCube, copiedCube;
        readonly float[] starMatrix = Mat4f.Create();
        FieldInfo cubeField, matrixField;
        Type astraSky;
        FieldInfo astraStars, astraSidereal, astraLatitude, astraBias;
        // (0.4.2) AstraTerra's calls, bound once as typed delegates: no argument arrays or boxing every frame
        System.Func<bool> vanillaStars, customMoon, nearMoon, starPath, showsAstra;
        System.Func<double, IWorldAccessor, double> longitude;
        System.Func<double, double, bool, double> localMoonDays;
        System.Func<IGameCalendar, double, double> moonPhase;
        System.Func<object, Star> readStar;
        double lastSidereal = double.NaN, lastLatitude = double.NaN, lastBias = double.NaN;
        bool astraFailed, textureFailed;
        bool showVanilla = true, showCustom;
        float clear = 1, night, phase, floor, roughness;
        float ambientScale = 1;
        Vec3f sun = new(0, 1, 0), moon = new(0, -1, 0);
        readonly Vec3f eye = new();
        readonly double[] inverseView = new double[16];
        WeatherSystemClient weather;
        long weatherAt;
        long atlasAt;
        float rain;
        public int StarCount { get; private set; }
        public bool WaterPatched { get; private set; }
        public bool FarPatched { get; private set; }
        public string Status => $"Celestial water: {WaterPatched}; Wmap lighting: {FarPatched}; sky source {(astraSky == null ? "vanilla" : astraFailed ? "AstraTerra adapter FAILED (see log)" : "AstraTerra 0.15.0")}; native cube ready {filteredCube > 0}; custom stars {StarCount}; clear {clear:0.00}; sun altitude {Math.Asin(sun.Y) * 180 / Math.PI:0.0}°, moon {Math.Asin(moon.Y) * 180 / Math.PI:0.0}°; moon fraction {phase:0.00}; outdoor floor {floor:0.000}; total days {api.World.Calendar.TotalDays:0.00000}.";

        public CelestialRenderer(ICoreClientAPI api, SeaSystem sea)
        {
            this.api = api;
            this.sea = sea;
            current = this;
            ambientFailed = usesFailed = starsFailed = false;
            Patch("shader uniforms", () => harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Vintagestory.Client.NoObf.ShaderProgramBase"), "Use"), postfix: new HarmonyMethod(typeof(CelestialRenderer), nameof(UsePostfix))));
            Patch("outdoor night lighting", () => harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Vintagestory.Client.NoObf.AmbientManager"), "UpdateAmbient"), postfix: new HarmonyMethod(typeof(CelestialRenderer), nameof(AmbientPostfix))));
            Patch("vanilla star source", () =>
            {
                var type = AccessTools.TypeByName("Vintagestory.Client.NoObf.SystemRenderNightSky");
                cubeField = AccessTools.Field(type, "textureId") ?? throw new Exception("star cubemap missing");
                matrixField = AccessTools.Field(type, "modelMatrix") ?? throw new Exception("star rotation missing");
                harmony.Patch(AccessTools.Method(type, "OnRenderFrame3D"), postfix: new HarmonyMethod(typeof(CelestialRenderer), nameof(StarsPostfix)));
            });
            api.Event.LevelFinalize += InitializeAstra;
        }

        void Patch(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { api.Logger.Warning("[sea] {0} unavailable: {1}", name, ex.Message); }
        }

        void InitializeAstra()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AstraTerra");
            if (asm == null) return;
            astraSky = asm.GetType("AstraTerra.Client.Rendering.SkyStarSunMoonRenderer");
            try
            {
                // Explicit capability checks against the installed 0.15.0; no assembly reference or loader dependency.
                astraStars = NeedField(astraSky, "DrawableStars");
                astraSidereal = NeedField(astraSky, "cachedStarSiderealDeg");
                astraLatitude = NeedField(astraSky, "cachedStarLatitudeDeg");
                astraBias = NeedField(astraSky, "cachedStarBrightnessBias");
                Type Need(string name) => asm.GetType(name) ?? throw new Exception(name + " missing");
                vanillaStars = Getter(astraSky, "ShouldRenderVanillaStarfield");
                customMoon = Getter(Need("AstraTerra.Client.Rendering.MoonDiscRenderer"), "HidesVanillaMoon");
                nearMoon = Getter(Need("AstraTerra.Client.Rendering.NearBodyRenderer"), "HidesVanillaMoon");
                // config != null && StarfieldModeParser.ShowsAstraTerra(config.GetStarfieldMode())
                var configField = NeedField(astraSky, "config");
                var getStarMode = configField.FieldType.GetMethod("GetStarfieldMode") ?? throw new Exception("GetStarfieldMode missing");
                var shows = Need("AstraTerra.Config.StarfieldModeParser").GetMethod("ShowsAstraTerra") ?? throw new Exception("ShowsAstraTerra missing");
                var config = Expression.Field(null, configField);
                showsAstra = Expression.Lambda<System.Func<bool>>(Expression.AndAlso(Expression.NotEqual(config, Expression.Constant(null, configField.FieldType)),
                    Expression.Call(shows, Expression.Call(config, getStarMode)))).Compile();
                // SkyRenderPaths.IsEnabled(SkyRenderPath.Stars)
                var isEnabled = Need("AstraTerra.Client.Rendering.SkyRenderPaths").GetMethod("IsEnabled") ?? throw new Exception("IsEnabled missing");
                var pathType = isEnabled.GetParameters()[0].ParameterType;
                starPath = Expression.Lambda<System.Func<bool>>(Expression.Call(isEnabled, Expression.Constant(Enum.Parse(pathType, "Stars"), pathType))).Compile();
                longitude = Bind<System.Func<double, IWorldAccessor, double>>(Need("AstraTerra.Astronomy.ObserverLongitude"), "ForObserver");
                localMoonDays = Bind<System.Func<double, double, bool, double>>(Need("AstraTerra.Astronomy.LocalMoonTime"), "MoonTotalDays");
                moonPhase = Bind<System.Func<IGameCalendar, double, double>>(Need("AstraTerra.Astronomy.VanillaMoonPhase"), "ExactAt");
                var type = Need("AstraTerra.Astronomy.RenderedStar");
                var item = Expression.Parameter(typeof(object), "star");
                Expression Get(string n) => Expression.Convert(Expression.Property(Expression.Convert(item, type), n), typeof(float));
                readStar = Expression.Lambda<System.Func<object, Star>>(Expression.New(typeof(Star).GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(float) }), Get("DirectionX"), Get("DirectionY"), Get("DirectionZ"), Get("Brightness")), item).Compile();
                api.Logger.Notification("[sea] Optional AstraTerra 0.15.0 sky adapter ready; source stars and local moon, no lighting patch on AstraTerra.");
            }
            catch (Exception ex) { FailAstra(ex); }
        }

        static FieldInfo NeedField(Type t, string name) => AccessTools.Field(t, name) ?? throw new Exception(name + " missing");

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
            showCustom = false;
            showVanilla = false;
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
        readonly Vec3d observer = new();

        void Sky(Vintagestory.API.Common.Entities.Entity ep)
        {
            var cal = api.World.Calendar;
            sun.Set(cal.SunPositionNormalized).Normalize();
            moon.Set(cal.MoonPosition).Normalize();
            double exact = cal.MoonPhaseExact;
            bool phaseFromCalendar = false;
            if (astraSky != null)
            {
                if (astraFailed) moon.Set(0, -1, 0);
                else try
                {
                    showVanilla = vanillaStars();
                    showCustom = showsAstra() && starPath();
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
            ambientScale = phaseFromCalendar && sea.Config.NightVisibility > 0
                ? NightSkyMath.MoonAmbientScale(cal.SunLightStrength, cal.MoonLightStrength, cal.DayLightStrength, moon.Y, phase) : 1;
            var am = api.Ambient;
            if (api.ElapsedMilliseconds - weatherAt > 250)
            {
                weather ??= api.ModLoader.GetModSystem<WeatherSystemClient>();
                rain = weather?.GetPrecipitation(ep.Pos.X, api.World.SeaLevel, ep.Pos.Z) ?? 0;
                weatherAt = api.ElapsedMilliseconds;
            }
            clear = NightSkyMath.ClearSky(am.BlendedCloudDensity, am.BlendedFogDensity, rain);
            night = NightSkyMath.Night(sun.Y);
            floor = NightSkyMath.Visibility(sun.Y, moon.Y, phase, clear, sea.Config.NightVisibility);
            if (ep.Pos.Dimension != 0 || api.Render.ShaderUniforms.CameraUnderwater > 0.01f) { floor = 0; ambientScale = 1; }
            roughness = (float)Math.Clamp(sea.CameraWaves.SignificantHeight / 3, 0, 1);
            roughness *= (float)sea.WaveFactorAt(ep.Pos.X, ep.Pos.Z);
        }

        // Harmony postfixes run inside vanilla's render loop: an exception there ends the game, so each one switches
        // its effect off and logs once instead.
        static bool ambientFailed, usesFailed, starsFailed;
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

        static void StarsPostfix(object __instance)
        {
            var c = current;
            if (starsFailed || c?.cubeField == null) return;
            try
            {
                c.vanillaCube = (int)c.cubeField.GetValue(__instance);
                if (c.matrixField.GetValue(__instance) is float[] mat) Array.Copy(mat, c.starMatrix, 16);
            }
            catch (Exception ex) { c.vanillaCube = 0; Fail(ref starsFailed, "Vanilla star reflections", ex); }
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
            catch (Exception ex) { Fail(ref ambientFailed, "Celestial sky state", ex); usesFailed = true; return; }
            if (textureFailed || sea.Config.SkyReflections <= 0) return;
            try
            {
                if (atlas == 0) CreateAtlas();
                if (showVanilla && vanillaCube > 0 && copiedCube != vanillaCube)
                {
                    // (0.4.2) the new copy first: if it fails, the old one is still whole (and deleted only once)
                    int made = StarCubemap.Create(vanillaCube), old = filteredCube;
                    filteredCube = made;
                    copiedCube = vanillaCube;
                    if (old != 0) GL.DeleteTexture(old);
                }
                if (astraSky == null || astraFailed || !showCustom || night <= 0) return;
                if (api.ElapsedMilliseconds - atlasAt < 250) return;
                double sid = (double)astraSidereal.GetValue(null), lat = (double)astraLatitude.GetValue(null), bias = (double)astraBias.GetValue(null);
                // (0.4.2) redrawn once the sky has turned a quarter of an atlas pixel, not at every new sidereal angle:
                // the stars' kernel is smooth, so steps that small don't show, and the redraw (thousands of stars) is costly
                double turned = Math.Abs(WorldShape.Mod(sid - lastSidereal + 180, 360) - 180);
                if (lat == lastLatitude && bias == lastBias && turned < MinSiderealStep) return;
                if (!double.IsFinite(sid)) return;
                Array.Clear(pixels);
                StarCount = 0;
                foreach (object item in (IEnumerable)astraStars.GetValue(null))
                {
                    var s = readStar(item);
                    if (!float.IsFinite(s.X + s.Y + s.Z + s.Brightness)) continue;
                    Splat(s);
                    StarCount++;
                }
                UploadAtlas();
                atlasAt = api.ElapsedMilliseconds;
                lastSidereal = sid; lastLatitude = lat; lastBias = bias;
            }
            catch (Exception ex)
            {
                textureFailed = true;
                showCustom = false;
                api.Logger.Warning("[sea] Star atlas disabled after texture/source failure: {0}", ex.Message);
            }
        }

        /// <summary>A quarter of an atlas pixel of sky rotation, in degrees.</summary>
        const double MinSiderealStep = 360.0 / Width / 4;

        public readonly struct Star
        {
            public readonly float X, Y, Z, Brightness;
            public Star(float x, float y, float z, float brightness) { X = x; Y = y; Z = z; Brightness = brightness; }
        }

        void Splat(Star s)
        {
            double x = (Math.Atan2(s.Z, s.X) / (2 * Math.PI) + 0.5) * Width - 0.5;
            double y = (Math.Asin(Math.Clamp(s.Y, -1, 1)) / Math.PI + 0.5) * Height - 0.5;
            // A smooth subpixel kernel avoids snapping as the sky rotates. Mips integrate the faint field on rough seas.
            for (int iy = (int)Math.Floor(y) - 2; iy <= (int)Math.Floor(y) + 3; iy++)
                for (int ix = (int)Math.Floor(x) - 2; ix <= (int)Math.Floor(x) + 3; ix++)
                {
                    if (iy < 0 || iy >= Height) continue;
                    double dx = (ix - x) * Math.Max(0.08, Math.Sqrt(1 - s.Y * s.Y)), dy = iy - y;
                    int i = iy * Width + ((ix % Width + Width) % Width);
                    pixels[i] = Math.Min(1, pixels[i] + s.Brightness * (float)Math.Exp(-(dx * dx + dy * dy) / 1.5));
                }
        }

        void CreateAtlas()
        {
            atlas = GL.GenTexture();
            GL.GetInteger(GetPName.TextureBinding2D, out int previous);
            GL.BindTexture(TextureTarget.Texture2D, atlas);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R16f, Width, Height, 0, PixelFormat.Red, PixelType.Float, pixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, previous);
        }

        void UploadAtlas()
        {
            GL.GetInteger(GetPName.TextureBinding2D, out int previous);
            GL.BindTexture(TextureTarget.Texture2D, atlas);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, Width, Height, PixelFormat.Red, PixelType.Float, pixels);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, previous);
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
                if (p.HasUniform("seaNightEnabled")) p.Uniform("seaNightEnabled", c.sea.Config.NightVisibility > 0 ? 1f : 0f);
            }
            if (!p.HasUniform("seaEffects")) return;
            c.WaterPatched = true;
            var cfg = c.sea.Config;
            bool enabled = c.api.World?.Player?.Entity?.Pos.Dimension == 0;
            p.Uniform("seaEffects", enabled ? cfg.CelestialReflections : 0, enabled ? cfg.SkyReflections : 0);
            p.Uniform("seaEye", c.eye);
            p.Uniform("seaSun", c.sun.X, c.sun.Y, c.sun.Z, 1.6f);  // saturates the glint core, as vanilla's does
            p.Uniform("seaMoon", c.moon.X, c.moon.Y, c.moon.Z, 0.75f * c.phase * c.night);
            p.Uniform("seaSunColor", c.api.World.Calendar.ReflectColor);
            p.Uniform("seaWeather", c.clear, c.night, 0, c.roughness);
            p.Uniform("seaStarMode", c.showVanilla && c.filteredCube > 0 && !c.textureFailed ? 1 : 0, c.showCustom && c.StarCount > 0 && !c.textureFailed ? 1 : 0);
            p.UniformMatrix("seaStarMatrix", c.starMatrix);
            GL.GetInteger(GetPName.ActiveTexture, out int active);
            p.BindTextureCube("seaVanillaStars", Math.Max(0, c.filteredCube), 10);
            p.BindTexture2D("seaCustomStars", c.atlas, 11);
            GL.ActiveTexture((TextureUnit)active);
        }

        public static string FarFragment(string code)
            => CelestialShader.FarFragment(code);

        public void Dispose()
        {
            api.Event.LevelFinalize -= InitializeAstra;
            harmony.UnpatchAll(Id);
            if (atlas != 0) GL.DeleteTexture(atlas);
            if (filteredCube != 0) GL.DeleteTexture(filteredCube);
            atlas = filteredCube = copiedCube = 0;
            if (current == this) current = null;
        }
    }
}
