using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sea;

namespace SeaTests
{
    /// <summary>
    /// Offline checks: without a wind provider the sea follows the config's steady wind, with one the wind is the
    /// provider's unchanged (its bad answers checked, an admin's fixed wind first), the sea builds up and calms down,
    /// it's fast, the waves are the same wherever they're worked out and never jump, the water shader code goes in
    /// where it should, and bad or old config numbers are handled.
    ///   dotnet run --project tests/SeaTests
    /// </summary>
    static class Program
    {
        static int failures;

        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
            if (!ok) failures++;
        }

        // a Cylinder World planet: 256000 wide with a 1000-block inset, 128000 long, walls 4000 deep
        const double RingStart = 1000, Circumference = 254000, EquatorZ = 64000, PoleDistance = 60000;

        static WorldShape Planet() => new WorldShape
        {
            RingStart = RingStart,
            Circumference = Circumference,
            Latitude = z => Math.Clamp((EquatorZ - z) / PoleDistance, -1, 1),
            DaysPerYear = 108,
            HoursPerDay = 24
        };

        static double ZAt(double latDeg) => EquatorZ - latDeg / 90 * PoleDistance;

        static int Main()
        {
            Console.WriteLine("no wind provider (0.4.0)");
            {
                var cfg = new SeaConfig();
                var m = new WindModel(Planet(), cfg);
                double x = 50000, z = ZAt(15), h = 3000;
                var sw = m.SeaWind(x, z, h);
                var st = m.SeaAt(x, z, h);
                Check(m.Mode == WindMode.Off && !m.Decides(h) && m.At(x, z, h).Strength == 0 && m.Prevailing(x, z, h).Strength == 0 && m.BeltName(z, h) == null,
                    "without a provider Sea has no wind of its own (vanilla's then): calm, no prevailing wind, no belt");
                Check(Math.Abs(sw.X - 0.3) < 1e-6 && Math.Abs(sw.Z) < 1e-6 && Math.Abs(st.Height - m.DevelopedHeight(sw.Strength)) < 1e-9 && st.DirX > 0.999,
                    $"the sea follows the config's steady wind, a medium breeze from the west ({sw}, {st.Height:0.00} blocks running east)");
                var north = new WindModel(Planet(), new SeaConfig { SeaWindFrom = 0, SeaWindStrength = 0.6f });
                var calm = new WindModel(Planet(), new SeaConfig { SeaWindStrength = 0 });
                var sn = north.SeaAt(x, z, h);
                var sc = calm.SeaAt(x, z, h);
                Check(sn.DirZ > 0.999 && Math.Abs(sn.Height - north.DevelopedHeight(0.6)) < 1e-6 && Math.Abs(sc.Height - 0.1) < 1e-9,
                    $"the steady wind is the config's: from the north the sea runs south ({sn.Height:0.00} blocks); strength 0 is a calm sea ({sc.Height:0.00})");
            }

            Console.WriteLine("wind provider (0.4.0)");
            {
                var m = new WindModel(Planet(), new SeaConfig());
                // a wind turning with x, gusts 10 % stronger, its own prevailing wind
                Func<double, double, double, bool, ValueTuple<double, double>> windAt = (px, pz, ph, gusts) => (0.4 * Math.Cos(px / 1000) * (gusts ? 1.1 : 1), 0.4 * Math.Sin(px / 1000) * (gusts ? 1.1 : 1));
                m.Provider = new WindProvider("test", windAt, (px, pz, ph) => (0.2, -0.1));
                bool routed = true;
                var rng = new Random(7);
                for (int i = 0; i < 1000; i++)
                {
                    double x = rng.NextDouble() * 256000, z = rng.NextDouble() * 128000, h = rng.NextDouble() * 24 * 400;
                    var g = windAt(x, z, h, true);
                    var s = windAt(x, z, h, false);
                    var a = m.At(x, z, h);
                    var b = m.At(x, z, h, false);
                    var c = m.SeaWind(x, z, h, false);
                    var d = m.SeaWind(x, z, h);
                    routed &= a.X == g.Item1 && a.Z == g.Item2 && b.X == s.Item1 && b.Z == s.Item2 && c.X == s.Item1 && c.Z == s.Item2 && d.X == g.Item1 && d.Z == g.Item2;
                }
                var p = m.Prevailing(1000, 2000, 3000);
                Check(m.Mode == WindMode.Provider && m.Decides(0) && routed && p.X == 0.2 && p.Z == -0.1,
                    "with a provider the wind, the sea's wind (with and without gusts) and the prevailing wind are the provider's, unchanged");
                m.Provider = new WindProvider("test", windAt, null);
                var q = m.Prevailing(1000, 2000, 3000);
                var qw = windAt(1000, 2000, 3000, false);
                Check(q.X == qw.Item1 && q.Z == qw.Item2, "a provider without a prevailing function: its steady wind is the prevailing one");

                // its answers are checked: NaN, infinity and exceptions are calm and counted, too strong is capped
                var bad = new WindProvider("bad", (px, pz, ph, g) => px < 0 ? (double.NaN, 0.0) : px < 1 ? (0.0, double.PositiveInfinity) : px < 2 ? (3.0, 4.0) : throw new InvalidOperationException("no"), (px, pz, ph) => throw new Exception("none"));
                m.Provider = bad;
                var nan = m.At(-1, 0, 0);
                var inf = m.At(0.5, 0, 0);
                var strong = m.At(1.5, 0, 0);
                var thrown = m.SeaWind(5, 0, 0);
                var prev = m.Prevailing(5, 0, 0);
                Check(nan.Strength == 0 && inf.Strength == 0 && thrown.Strength == 0 && prev.Strength == 0 && bad.Failures == 4 && bad.LastError == "none"
                    && Math.Abs(strong.Strength - WindModel.MaxStrength) < 1e-12 && Math.Abs(strong.X / strong.Z - 0.75) < 1e-12,
                    $"a provider's NaN, infinity or exception is calm ({bad.Failures} counted, last: {bad.LastError}), a wind of 5 is capped at {WindModel.MaxStrength} keeping its direction");
                bool threw = false;
                try { new WindProvider("none", null, null); } catch (ArgumentNullException) { threw = true; }
                Check(threw, "a provider needs a wind function");

                // an admin's fixed wind wins over the provider (not over its prevailing wind: that's climate)
                m.Provider = new WindProvider("test", windAt, (px, pz, ph) => (0.2, -0.1));
                m.Override = new WindOverride { FromDegrees = 0, Strength = 1, StartHours = 100, UntilHours = 200 };
                var ow = m.At(5000, 0, 150, false);
                var osw = m.SeaWind(5000, 0, 150);
                var after = m.At(5000, 0, 250, false);
                Check(Math.Abs(ow.Z - 1) < 1e-12 && Math.Abs(osw.Z - 1) < 1e-12 && m.Prevailing(0, 0, 150).X == 0.2 && Math.Abs(after.X - windAt(5000, 0, 250, false).Item1) < 1e-12,
                    "an admin's fixed wind wins over the provider while it lasts");
                m.Override = null;
                m.Provider = null;
                Check(m.Mode == WindMode.Off && m.SeaWind(0, 0, 0).X > 0.29, "cleared: the config's steady wind again");
            }

            Console.WriteLine("sea state");
            {
                // the provider's steady wind: calm until hour t0, then a storm from the west for 16 hours
                var sm = new WindModel(Planet(), new SeaConfig());
                const double t0 = 2000;
                sm.Provider = new WindProvider("storm", (px, pz, ph, g) => ph >= t0 && ph < t0 + 16 ? (1.0, 0.0) : (0.05, 0.0), null);
                double x = 50000, z = ZAt(45);
                double before = sm.SeaAt(x, z, t0 - 0.5).Height;
                var s1 = sm.SeaAt(x, z, t0 + 1);
                var s4 = sm.SeaAt(x, z, t0 + 4);
                var s15 = sm.SeaAt(x, z, t0 + 15.5);
                var after2 = sm.SeaAt(x, z, t0 + 18);
                var after12 = sm.SeaAt(x, z, t0 + 30);
                Console.WriteLine($"         heights: before {before:0.00}, +1h {s1.Height:0.00}, +4h {s4.Height:0.00}, +15h {s15.Height:0.00}, 2h after {after2.Height:0.00}, 14h after {after12.Height:0.00}");
                Check(before < 0.2 && s1.Height < s4.Height && s4.Height < s15.Height, "a storm's sea builds up over hours");
                Check(Math.Abs(s15.Height - sm.DevelopedHeight(1.0)) < 0.05, $"and reaches the developed height ({sm.DevelopedHeight(1.0):0.0} blocks)");
                Check(after2.Height > 0.4 * s15.Height && after12.Height < after2.Height, "swell lingers after the wind drops, then fades");
                Check(s15.DirX > 0.99 && s15.Wavelength > 8, $"waves run downwind (east) and are long enough for the water mesh ({s15.Wavelength:0} blocks, {s15.Period:0.0} s)");

                // an admin's fixed wind builds the sea the same way
                sm.Provider = null;
                sm.Override = new WindOverride { FromDegrees = 270, Strength = 1.0, StartHours = t0, UntilHours = t0 + 16 };
                var o15 = sm.SeaAt(x, z, t0 + 15.5);
                Check(Math.Abs(o15.Height - s15.Height) < 0.05 && o15.DirX > 0.99, $"a fixed storm builds the same sea ({o15.Height:0.00} blocks)");
            }

            Console.WriteLine("speed");
            {
                // Sea's own share of a lookup: the cheapest provider there is
                var m = new WindModel(Planet(), new SeaConfig()) { Provider = new WindProvider("fast", (px, pz, ph, g) => (0.3 + 1e-6 * pz, 0.1), null) };
                var sw = Stopwatch.StartNew();
                int n = 200000;
                double sum = 0;
                for (int i = 0; i < n; i++) sum += m.At(i * 13.7 % 256000, 20000 + i * 0.37 % 90000, 5000 + i * 0.0002).Strength;
                double us = sw.Elapsed.TotalMilliseconds * 1000 / n;
                Check(us < 1, $"wind lookup {us:0.000} µs (mean {sum / n:0.00})");
                sw.Restart();
                for (int i = 0; i < 2000; i++) m.SeaAt(i * 97.0, 60000, 7000 + i * 0.01);
                Check(sw.Elapsed.TotalMilliseconds / 2000 < 0.05, $"sea state {sw.Elapsed.TotalMilliseconds * 1000 / 2000:0.0} µs");
            }

            Console.WriteLine("compass");
            {
                Check(Math.Abs(Wind.FromCompass(0, 1).Z - 1) < 1e-9, "a north wind blows south (+Z)");
                Check(Math.Abs(Wind.FromCompass(270, 1).X - 1) < 1e-9, "a west wind blows east (+X)");
                Check(Math.Abs(Wind.FromCompass(225, 0.5).FromDegrees - 225) < 1e-9, "from-direction round-trips");
                Check(Compass.Parse("sw") == 225 && Compass.Parse("90") == 90 && Compass.Parse("x") == null, "compass parsing");
                Check(Beaufort.Force(1.0) == 10 && Beaufort.Force(0.3) == 4 && Beaufort.Force(0) == 0, "Beaufort scale");
            }

            Waves();
            StormEvidence.Run(Check);
            FoamChecks.Run(Check);
            CrestChecks.Run(Check);
            WorldOptionsChecks.Run(Check);
            BoatChecks.Run(Check);
            ContractChecks.Run(Check);
            CoastChecks.Run(Check);

            Console.WriteLine("night sky presentation");
            {
                float full = NightSkyMath.Visibility(-0.7f, 0.7f, 1, 1, 1);
                float dark = NightSkyMath.Visibility(-0.7f, 0.7f, 0, 1, 1);
                float low = NightSkyMath.Visibility(-0.7f, -0.1f, 1, 1, 1);
                float storm = NightSkyMath.ClearSky(1, 1);
                Check(full > dark * 2 && dark > 0 && low == dark, "moonlit > moonless > black; moon below horizon contributes nothing");
                Check(storm < 0.02f && NightSkyMath.ClearSky(0, 0) == 1 && NightSkyMath.ClearSky(1, 0) < 0.1f, "overcast and rain attenuate clear sky");
                Check(NightSkyMath.ClearSky(0.5f, 0) < 0.45f && NightSkyMath.ClearSky(0.25f, 0) < 0.7f && NightSkyMath.ClearSky(0, 0, 0.001f) == 1
                    && Math.Abs(NightSkyMath.ClearSky(0, 0, 0.05f) - 0.5f) < 1e-6f,
                    "(0.4.7-night) thickening cloud darkens more than linearly; vanilla's clear-sky fog nothing, thick fog half");
                Check(Math.Abs(dark - 0.10f) < 1e-6f && Math.Abs(NightSkyMath.Visibility(-0.7f, 1, 1, 1, 1) - 0.40f) < 1e-6f
                    && Math.Abs(NightSkyMath.Visibility(-0.7f, 0, 0, 1, 1.5f) - 0.15f) < 1e-6f,
                    "(0.4.7-night) night floor at strength 1: clear moonless 0.10, high full moon 0.40; strength 1.5 = 21 October's 0.15");
                float half = NightSkyMath.MoonLight(0.5f, 1), rising = NightSkyMath.MoonLight(1, 0.02f), ten = NightSkyMath.MoonLight(1, 0.174f);
                Check(half > 0.3f && half < 0.4f && rising > 0.1f && rising < 0.35f && ten > 0.5f && ten < 0.6f && NightSkyMath.MoonLight(1, 0.6f) == 1
                    && NightSkyMath.MoonLight(1, -0.05f) == 0,
                    "(0.4.7-night) moonlight by phase (half moon about a third) and height (dim on the horizon, full from 35°)");
                float Deg(float d) => MathF.Sin(d * MathF.PI / 180);
                Check(NightSkyMath.Night(0) == 0 && NightSkyMath.Night(Deg(-6.1f)) == 1 && NightSkyMath.Night(Deg(-3)) > 0.3f && NightSkyMath.Night(Deg(-3)) < 0.7f,
                    "(0.4.7-night) the floor fades in through civil twilight, under the twilight's own light, and is all in from −6°");
                Check(NightSkyMath.TwilightSunY(0.3f) == 0.3f && NightSkyMath.TwilightSunY(0) == 0
                    && Math.Abs(NightSkyMath.TwilightSunY(Deg(-18)) - Deg(-45)) < 1e-4f && NightSkyMath.TwilightSunY(Deg(-60)) == -1,
                    "(0.4.7-night) twilight reads the sun 2.5 times as far down below the horizon, nothing above it");
                // sunlight.png's night value (its x < 0.1) needs a sun 46° down in vanilla, 18° down now
                Check(NightSkyMath.SunlightTextureX(Deg(-18)) > 0.38f && NightSkyMath.SunlightTextureX(NightSkyMath.TwilightSunY(Deg(-18))) < 0.11f,
                    "(0.4.7-night) at the end of astronomical twilight the game's sun term reads its night value");
                // (0.4.7-pre.7, issue 15) no steps at sunset: the reflect colour only darkens below the horizon, from nothing
                // (the game's step brightened it by 0.29 a tenth of a degree down); shadows fade while the casting body is low
                // and while the sun's and moon's terms are close; the shading direction turns across the same span
                {
                    bool darkens = NightSkyMath.ReflectDarkening(0) == 0 && NightSkyMath.ReflectDarkening(-0.002f) == 0
                        && NightSkyMath.ReflectDarkening(-0.06f) == 0 && NightSkyMath.ReflectDarkening(-0.2f) > 0.69f;
                    float last = 0, worstStep = 0;
                    for (float y = 0; y > -0.3f; y -= 0.0005f) { float k = NightSkyMath.ReflectDarkening(y); worstStep = Math.Max(worstStep, Math.Abs(k - last)); last = k; }
                    bool shadows = NightSkyMath.ShadowKeep(0.5f, 0.5f) == 1 && NightSkyMath.ShadowKeep(-0.01f, 0.5f) == 0
                        && NightSkyMath.ShadowKeep(0.5f, 0) == 0 && NightSkyMath.ShadowKeep(0.5f, -0.02f) > 0.4f && NightSkyMath.ShadowKeep(0.5f, -0.02f) < 0.6f
                        && NightSkyMath.ShadowKeep(0.5f, 0.02f) == NightSkyMath.ShadowKeep(0.5f, -0.02f);
                    bool turns = NightSkyMath.LightTowardMoon(-0.05f) == 0 && NightSkyMath.LightTowardMoon(0.05f) == 1 && Math.Abs(NightSkyMath.LightTowardMoon(0) - 0.5f) < 1e-6f;
                    Check(darkens && worstStep < 0.003f && shadows && turns,
                        $"sunset hand-off without steps: reflect darkening continuous (largest step {worstStep:0.0000} per 0.03°), shadows off near the horizon and around the sun/moon crossing, shading turns over ±0.04");
                }
                // the game's members the hand-off postfixes bind to (CelestialRenderer.PatchHandOff), in the installed game
                {
                    string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? System.IO.Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "Vintagestory");
                    // (its types need the game's libraries, OpenTK among them, to load)
                    AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
                    {
                        string file = System.IO.Path.Combine(game, "Lib", new System.Reflection.AssemblyName(e.Name).Name + ".dll");
                        return System.IO.File.Exists(file) ? System.Reflection.Assembly.LoadFrom(file) : null;
                    };
                    var lib = System.Reflection.Assembly.LoadFrom(System.IO.Path.Combine(game, "VintagestoryLib.dll"));
                    Type T(string n) => lib.GetType(n);
                    bool bound = HarmonyLib.AccessTools.Method(T("Vintagestory.Client.NoObf.SystemRenderShadowMap"), "OnRenderBefore") != null
                        && HarmonyLib.AccessTools.Method(T("Vintagestory.Client.NoObf.SystemRenderSunMoon"), "OnRenderFrame3D") != null
                        && HarmonyLib.AccessTools.Field(T("Vintagestory.Client.NoObf.AmbientManager"), "DropShadowIntensity")?.FieldType == typeof(float);
                    Check(bound, "the game has the shadow pass, the sun-and-moon pass and the ambient's shadow intensity the hand-off patches"
                        + (bound ? "" : $" (types: {T("Vintagestory.Client.NoObf.SystemRenderShadowMap") != null}, {T("Vintagestory.Client.NoObf.SystemRenderSunMoon") != null}, {T("Vintagestory.Client.NoObf.AmbientManager") != null})"));
                }
                Check(Math.Abs(NightSkyMath.Daylight(0.51f, 0.51f, 0, 0.06f, 0) - 0.06f) < 1e-6f && NightSkyMath.Daylight(0.33f, 0.06f, 0.33f, 0.06f, 0.33f) == 0.33f
                    && Math.Abs(NightSkyMath.Daylight(0.4f, 0.06f, 0.33f, 0.06f, 0.1f) - 0.17f) < 1e-6f && NightSkyMath.Daylight(0.2f, 0.1f, 0, 0.3f, 0) == 0.2f,
                    "(0.4.7-night) rendered daylight: only the sun's and moon's part changes, the game's extras stay, never brighter");
                Check(NightSkyMath.SkyDaylight(0, 0, 0) < 0.07f && NightSkyMath.SkyDaylight(1, 0, 0) == 1.25f && NightSkyMath.SkyDaylight(0.33f, 0.33f, 0) < 0.25f,
                    "sky daylight as the game's sky dome works it out (night: its fog colour)");
                Check(NightSkyMath.Visibility(0.2f, 1, 1, 1, 1) == 0 && NightSkyMath.Visibility(-1, 1, 1, 1, 0) == 0, "daytime and disable add no outdoor floor");
                var noon = NightSkyMath.SunThroughAir(0.8f);
                var lowSun = NightSkyMath.SunThroughAir(0.1f);
                var setting = NightSkyMath.SunThroughAir(0);
                Check(noon == (1f, 1f, 1f) && lowSun.B < lowSun.G && lowSun.G < 1 && setting.G < 0.65f && setting.B < 0.3f && setting.G < lowSun.G,
                    "the sun's glitter is white high up, yellow low, orange setting (as AstraTerra's drawn sun)");
                Check(NightSkyMath.MoonAmbientScale(0.02f, 0.33f, 0.33f, -0.2f, 1) < 0.1f
                    && NightSkyMath.MoonAmbientScale(0.02f, 0.33f, 0.7f, -0.2f, 1) == 1
                    && NightSkyMath.MoonAmbientScale(1, 0.2f, 1, -0.2f, 1) == 1
                    && NightSkyMath.MoonAmbientScale(0.02f, 0.33f, 0.33f, 0.8f, 1, 0.2f) < 0.25f,
                    "local moon removes a false vanilla ambient moon (and clouds hide it); stronger external lighting and daylight are preserved");
                string shaderRoot = Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? Path.Combine(Environment.GetEnvironmentVariable("APPDATA"), "Vintagestory"), "assets/game/shaders");
                string vf = File.ReadAllText(Path.Combine(shaderRoot, "chunkliquid.vsh"));
                string ff = File.ReadAllText(Path.Combine(shaderRoot, "chunkliquid.fsh"));
                string cv = CelestialShader.Vertex(WaterShader.Inject(vf)), cf = CelestialShader.Fragment(ff);
                Check(cv != null && cf != null && CelestialShader.Vertex(cv) == null && CelestialShader.Fragment(cf) == null, "celestial anchors apply once to installed water shader");
                // (0.4.7-pre.5) the near water fades each train by Wmap's far-sea rule, keeping the faded slope as glint roughness;
                // its sky reflection has the game's flat fog and the eye's height, and AstraTerra's sunset glow
                Check(cv.Contains("1.0-smoothstep(0.25,0.75,k*footprint)") && cv.Contains("rough +=") && cf.Contains("in float sea_SurfaceRough;")
                    && cf.Contains("1.0 - 1.0 / exp((skyPos.y - fogFrom - bias) * flatFogDensity)") && cf.Contains("0.25 * sea_AboveSea")
                    && cf.Contains("sea_SunsetShape(d, sea_Sun.xyz)") && cf.Contains("glint *= width / spread;"),
                    "near water: trains fade by the far sea's footprint rule; the sky reflection has flat fog, the eye's height and the sunset glow");
                Check(cf.Contains("smoothstep(viewDistance * 0.76, viewDistance * 0.80, length(fWorldPos.xz))") && !cf.Contains("viewDistance * 0.50"),
                    "near glints last until the game's water has faded (0.76-0.8 of the view distance), where Wmap's far sea takes the glitter on (issue 14)");
                Check(cf.Contains("if (sea_Effects.x <= 0.0)") && cf.Contains("cameraUnderwater < 0.01") && cf.Contains("skyExposed > 0") && cf.Contains("c.a = max(c.a, max(sun, moon))") && cf.Contains("texColor = sea_Glints(")
                    && cf.Contains("texColor = sea_SkyReflect(texColor, ray, fres, rgbaFog.rgb);") && !cf.Replace("flatFogStart", "").Contains("Star") && !cf.Contains("texColor.a += added"),
                    "vanilla highlight retained behind disable; water-only sky gates; glints and the Fresnel sky reflection raise alpha (premultiplied OIT); no star reflections");
                const string haze = "    haze = min(haze, clamp(dayLight, 0.0, 1.0));\n    color.rgb = mix(color.rgb, sky.rgb, haze);\n}";
                string sunLit = "void main()\n{\n    vec4 color = vec4(albedo * max(vec3(0.05), sun) * (0.55 + 0.45 * light), 1.0);\n" + haze;
                string ambientLit = "void main()\n{\n    vec4 color = vec4(albedo * clamp(wmapAmbient, 0.0, 1.0) * nb, 1.0);\n" + haze;
                string fs = CelestialShader.FarFragment(sunLit), fa = CelestialShader.FarFragment(ambientLit);
                Check(fs != null && fs.Contains("albedo * max(max(vec3(0.05), sun), sea_NightFloor)") && CelestialShader.FarFragment(fs) == null
                    && fa != null && fa.Contains("albedo * max(clamp(wmapAmbient, 0.0, 1.0), sea_NightFloor) * nb") && CelestialShader.FarFragment(fa) == null,
                    "Wmap night floor applies once to sun-lit (0.4.1/0.4.2) and ambient-lit (0.4.3) far terrain");
                string waterLit = "in vec3 wmapPos;\nvoid main()\n{\n    bool water = true;\n    vec4 color = vec4(albedo * clamp(wmapAmbient, 0.0, 1.0) * nb, 1.0);\n" + haze;
                string fw = CelestialShader.FarFragment(waterLit);
                Check(fw != null && fw.Contains(CelestialShader.FarSkyReflection) && fw.Contains("uniform float sea_SkyReflect")
                    && !fa.Contains(CelestialShader.FarSkyReflection) && CelestialShader.FarFragment(fw) == null,
                    "Wmap's distant sea gets the same Fresnel sky reflection after its sky blend; a shader without its water flag gets the floor only");
                Check(CelestialShader.FarFragment(ambientLit.Replace("albedo * clamp", "albedo2 * clamp")) == null && CelestialShader.FarFragment(sunLit.Replace("void main()", "void main(void)")) == null
                    && CelestialShader.FarFragment(sunLit.Replace(haze, "}")) == null && CelestialShader.FarFragment(null) == null,
                    "Wmap night floor: missing anchors leave the shader alone");
            }

            Console.WriteLine("water shader");
            string shaders = Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "Vintagestory"), "assets", "game", "shaders");
            foreach (string pass in WaterShader.Passes)
            {
                string file = Path.Combine(shaders, pass + ".vsh");
                if (!File.Exists(file)) { Console.WriteLine($"  skip  {pass}.vsh not found"); continue; }
                string code = File.ReadAllText(file);
                string injected = WaterShader.Inject(code, out bool waves);
                int main = injected?.IndexOf("void main(") ?? -1;
                int warp = injected?.IndexOf("worldPos = sea_Waves(", main) ?? -1;
                int clip = injected?.IndexOf("worldPos = sea_Water(worldPos, xyz + origin);") ?? -1;
                bool ok = injected != null && waves
                    && injected.IndexOf("uniform vec4 sea_Wave[24];") < main && injected.IndexOf("vec4 sea_Waves(") < main
                    && warp > main && clip > warp
                    && clip < injected.IndexOf("vec4 cameraPos = modelViewMatrix * worldPos;")
                    && injected.IndexOf("applyLiquidWarping((waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, div)") < 0
                    && WaterShader.Inject(injected) == null;
                Check(ok, $"{pass}.vsh: Sea's waves replace vanilla's, then the hull clip, once");

                // dry hulls: the vertex stage hands each water vertex's position (as drawn) to the fragment stage, which
                // discards the pixels inside a dry hull's outline before anything else
                int fragPos = injected?.IndexOf("sea_FragPos = worldPos.xyz;") ?? -1;
                bool vok = injected != null && injected.IndexOf("out vec3 sea_FragPos;") >= 0 && injected.IndexOf("out vec3 sea_FragPos;") < main
                    && fragPos > clip && fragPos < injected.IndexOf("vec4 cameraPos = modelViewMatrix * worldPos;")
                    && injected.IndexOf("if (sea_ClipD[h].w > 0.5) continue;") > 0;
                string ffile = Path.Combine(shaders, pass + ".fsh");
                if (!File.Exists(ffile)) { Console.WriteLine($"  skip  {pass}.fsh not found"); continue; }
                string fcode = File.ReadAllText(ffile);
                string finjected = WaterShader.InjectFragment(fcode);
                int fmain = finjected?.IndexOf("void main(") ?? -1;
                int body = fmain < 0 ? -1 : finjected.IndexOf('{', fmain);
                string first = body < 0 ? "" : finjected.Substring(body + 1).TrimStart();
                bool fok = finjected != null && finjected.IndexOf("in vec3 sea_FragPos;") < fmain && finjected.IndexOf("bool sea_Dry(vec3 p)") < fmain
                    && finjected.IndexOf("uniform vec4 sea_ClipD[8];") < fmain
                    && first.StartsWith("if (sea_Dry(sea_FragPos)) discard;")
                    && finjected.StartsWith(fcode.Substring(0, fcode.IndexOf('\n')))   // #version stays first
                    && WaterShader.InjectFragment(finjected) == null;
                Check(vok && fok, $"{pass}: dry hulls are cut out per pixel ({pass}.vsh passes the position, {pass}.fsh discards first thing), once");
            }

            Console.WriteLine("dry hulls");
            {
                // a hull heading east (yaw π/2: the keel along +X), footprint 30 x 10 with a 1-block margin, waterline at y 100
                var clip = new WaterClipBox
                {
                    X = 1000, Z = 2000, WaterY = 100, Yaw = Math.PI / 2, HalfLength = 15, CentreOffset = 0, HalfWidth = 5,
                    Dry = true, DryInset = 1, DryDepth = 3, DryHeight = 4
                };
                Check(clip.InDryHull(1010, 99, 2003.5) && clip.InDryHull(990, 97.5, 1996.5) && clip.InDryHull(1000, 103.9, 2000),
                    "inside the hull's outline, from its keel to its decks, it's dry");
                Check(!clip.InDryHull(1010, 99, 2004.5) && !clip.InDryHull(1014.5, 99, 2000) && !clip.InDryHull(1000, 96.5, 2000) && !clip.InDryHull(1000, 104.5, 2000),
                    "the margin around the hull (the footprint less the inset), below its keel and above its decks are not");
                var turned = clip;
                turned.Yaw = 0;   // now the keel runs along +Z
                Check(turned.InDryHull(1000, 99, 2010) && !turned.InDryHull(1010, 99, 2000), "the outline turns with the hull's heading");
                var shaped = clip;
                shaped.Profile = new float[16];
                for (int i = 0; i < 16; i++) shaped.Profile[i] = i < 8 ? 5 : 2;   // broad aft, narrow forward
                Check(shaped.InDryHull(990, 99, 2003.5) && !shaped.InDryHull(1012, 99, 2003.5) && shaped.InDryHull(1012, 99, 2000.5),
                    "the outline follows the hull's width profile along the keel");
                var wet = clip;
                wet.Dry = false;
                Check(!wet.InDryHull(1010, 99, 2003.5), "a hull that isn't dry keeps the old clip (and no water is cut)");
                Check(Math.Abs(shaped.HalfWidthAt(-1) - 5) < 1e-9 && Math.Abs(shaped.HalfWidthAt(1) - 2) < 1e-9 && Math.Abs(clip.HalfWidthAt(0.3) - 5) < 1e-9,
                    "the width profile is read the way the shader reads it");
                var rolled = clip;
                rolled.SlopeAcross = 0.2f;   // her waterline rises toward +Z: she's rolled, her masts lean toward -Z
                Check(!rolled.InDryHull(1000, 103, 2003.5) && rolled.InDryHull(1000, 103, 1995.6) && clip.InDryHull(1000, 103, 2003.5) && !clip.InDryHull(1000, 103, 1995.6),
                    "the outline leans with her roll: high up it shifts toward the side she leans to");
                var pitched = clip;
                pitched.SlopeAlong = 0.1f;   // bow up
                Check(!pitched.InDryHull(1013.9, 103, 2000) && pitched.InDryHull(1013.5, 103, 2000) && pitched.InDryHull(1013, 105.2, 2000) && !pitched.InDryHull(987, 103, 2000) &&
                      clip.InDryHull(1013.9, 103, 2000) && !clip.InDryHull(1013, 105.2, 2000) && clip.InDryHull(987, 103, 2000),
                    "and with her pitch (her ends and her deck heights move with it)");
                var detailed = clip;
                detailed.DryProfile = new float[WaterShader.DryStations * WaterShader.DryLevels];
                detailed.DryLevel0 = -2;
                detailed.DryLevelStep = 2;
                for (int j = 0; j < WaterShader.DryLevels; j++)
                    for (int i = 0; i < WaterShader.DryStations; i++) detailed.DryProfile[j * WaterShader.DryStations + i] = j < 2 ? 2 : 4;
                Check(detailed.HasDryProfile && !detailed.InDryHull(1000, 100, 2003) && detailed.InDryHull(1000, 102, 2003) &&
                      detailed.InDryHull(1000, 101, 2002.9) && !detailed.InDryHull(1000, 101, 2003.1) && detailed.InDryHull(1000, 97.1, 2001.9),
                    "a detailed outline gives the hull's width at each height (between rows it's interpolated, beyond them the nearest row holds)");
                Check(detailed.InDryHull(1013.9, 102, 2000) && !detailed.InDryHull(1014.1, 102, 2000) && detailed.InDryHull(1000, 100, 2003, false),
                    "its stations run from stern to bow (the footprint less the inset); the shader's fallback for far hulls is the footprint's outline");
                // (8 November) tops: the cut reaches 4 above the waterline all along, and 6 at her bow (a raised bow)
                var topped = detailed;
                topped.DryHeight = 4;
                topped.DryTops = new float[WaterShader.DryTopStations];
                for (int i = 0; i < topped.DryTops.Length; i++) topped.DryTops[i] = i < 12 ? 4 : 6;
                Check(topped.HasDryTops && topped.InDryHull(1013.5, 104.9, 2000) && !detailed.InDryHull(1013.5, 104.9, 2000)
                      && !topped.InDryHull(1000, 104.9, 2000) && topped.InDryHull(1000, 103.9, 2000) && !topped.InDryHull(1013.5, 106.1, 2000)
                      && !topped.InDryHull(1013.5, 104.9, 2000, false) && topped.DryCeiling(true) == 6 && topped.DryCeiling(false) == 4,
                    "the outline's tops: the cut reaches the rail at each station (higher at a raised bow, only there); far hulls keep DryHeight");
                var raked = detailed;
                raked.DryRake = -0.5f;   // a transom leaning out aft: its stern end 0.5 further aft per block up
                Check(raked.InDryHull(986.1, 100, 2000) && !raked.InDryHull(985.9, 100, 2000) && raked.InDryHull(985.1, 102, 2000) &&
                      !raked.InDryHull(984.9, 102, 2000) && !raked.InDryHull(986.9, 98, 2000) && raked.InDryHull(987.1, 98, 2000) &&
                      raked.InDryHull(1013.9, 102, 2000) && !raked.InDryHull(1014.1, 102, 2000),
                    "a raked stern end: the cut ends at the transom's plane, further aft higher up (the bow end stays)");
                var flooded = raked;
                flooded.Flooded = true;
                flooded.FloodY = 99.2;   // water in her hold, 0.8 under her waterline
                Check(!flooded.InDryHull(1000, 99.1, 2001.5) && !flooded.InDryHull(1000, 97.5, 2001.9) && flooded.InDryHull(1000, 99.3, 2001.5) &&
                      flooded.InDryHull(1000, 102, 2003) && raked.InDryHull(1000, 99.1, 2001.5) && !flooded.InDryHull(1000, 99.3, 2004.5),
                    "flooded: under the water in her hold the sea isn't cut away, over it the hull is dry as before (the outline unchanged)");
                var floodedRolled = flooded;
                floodedRolled.SlopeAcross = 0.2f;   // rolled: her outline leans, the water in her stays level
                Check(!floodedRolled.InDryHull(1000, 99.15, 2001.5) && !floodedRolled.InDryHull(1000, 99.15, 1998.5) &&
                      floodedRolled.InDryHull(1000, 99.25, 2001.5) && floodedRolled.InDryHull(1000, 99.25, 1998.5),
                    "the flood's level is level in the world however she leans (the same height both sides of her when she's rolled)");
                Check(clip.FloodUniform == WaterClipBox.NoFlood && Math.Abs(flooded.FloodUniform - (-0.8f)) < 1e-5,
                    "the shader gets the flood's height over her waterline (sea_ClipB.y), and far below it when she's dry");

                // (0.3.6) her own frame exactly: points of her outline tilted the way a ship mod draws her (roll about the
                // keel, then pitch, then her heading: here +X) are inside however far she leans, and just beyond it aren't
                double pitch = 12 * Math.PI / 180, roll = 20 * Math.PI / 180;
                var tilted = clip;
                tilted.SlopeAlong = (float)Math.Tan(pitch);
                tilted.SlopeAcross = (float)(-Math.Tan(roll) / Math.Cos(pitch));
                bool In(double lx, double ly, double lz)
                {
                    double cr = Math.Cos(roll), sr = Math.Sin(roll), cp = Math.Cos(pitch), sp = Math.Sin(pitch);
                    double y1 = ly * cr - lz * sr, z1 = ly * sr + lz * cr;
                    return tilted.InDryHull(tilted.X + lx * cp - y1 * sp, tilted.WaterY + lx * sp + y1 * cp, tilted.Z + z1);
                }
                Check(In(13.9, 0.5, 3.9) && In(-13.9, -2.9, -3.9) && In(13.9, 3.9, -3.9) && In(0, -2.9, 0) &&
                      !In(14.1, 0.5, 0) && !In(-14.1, 0.5, 0) && !In(0, 0.5, 4.1) && !In(0, 0.5, -4.1) && !In(5, 4.1, 0) && !In(5, -3.1, 0),
                    "the outline is her own however she's pitched (12°) and rolled (20°): no strip of her inside left out, nothing outside cut");
                WaterClipBox.HullFrame(tilted.SlopeAlong, tilted.SlopeAcross, 0, 0, 0, out double ox, out double oy, out double oz);
                Check(ox == 0 && oy == 0 && oz == 0, "her frame's origin is the waterline above (X, Z)");
            }
            {
                // the fragment stage leaves the sea under a flooded hull's water alone before anything else
                string fcode = File.Exists(Path.Combine(shaders, "chunkliquid.fsh")) ? WaterShader.InjectFragment(File.ReadAllText(Path.Combine(shaders, "chunkliquid.fsh"))) : null;
                int dyAt = fcode?.IndexOf("float dy = p.y - b.z;") ?? -1, floodAt = fcode?.IndexOf("if (dy < b.y) continue;") ?? -1, hlAt = fcode?.IndexOf("float hl = ") ?? -1;
                Check(fcode != null && dyAt > 0 && floodAt > dyAt && hlAt > floodAt, "the dry cut skips what's under a flooded hull's water (sea_ClipB.y) first");
            }

            DryWaterParticles();
            BadInputs();
            WaveBudget();
            GridSampling();
            AdminChanges();
            ReviewChecks.Run(Check);
            FlowLookChecks.Run(Check);

            Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        sealed class CountingParticles : Vintagestory.API.Client.IAsyncParticleManager
        {
            public int Spawned;
            public Vintagestory.API.Common.IBlockAccessor BlockAccess => null;
            public int Spawn(Vintagestory.API.Common.IParticlePropertiesProvider p) { Spawned++; return 1; }
            public int ParticlesAlive(Vintagestory.API.Common.EnumParticleModel model) => 0;
        }

        /// <summary>
        /// The particles vanilla makes in and on the world's water (DryParticles): the game's own methods have the spawns
        /// the patches replace (read from the game's code, as Harmony reads it), and a spawn in a dry place is left out.
        /// </summary>
        static void DryWaterParticles()
        {
            Console.WriteLine("water particles in dry places");
            int Replaced(Type t, string method, Func<System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction>, System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction>> transpiler, string with)
            {
                var m = HarmonyLib.AccessTools.Method(t, method);
                if (m == null) return -1;
                var spawn = HarmonyLib.AccessTools.Method(typeof(Vintagestory.API.Client.IAsyncParticleManager), "Spawn");
                var target = HarmonyLib.AccessTools.Method(typeof(DryParticles), with);
                var outIl = transpiler(HarmonyLib.PatchProcessor.GetOriginalInstructions(m)).ToList();
                return outIl.Any(ci => ci.Calls(spawn)) ? -2 : outIl.Count(ci => ci.Calls(target));
            }
            int ambient = Replaced(typeof(Vintagestory.GameContent.ModSystemAmbientParticles), "AsyncParticleSpawnTick", DryParticles.InWaterTranspiler, nameof(DryParticles.SpawnInWater));
            int rain = Replaced(typeof(Vintagestory.GameContent.WeatherSimulationParticles), "SpawnRainParticles", DryParticles.OnWaterTranspiler, nameof(DryParticles.SpawnOnWater));
            int dust = Replaced(typeof(Vintagestory.GameContent.WeatherSimulationParticles), "SpawnDustParticles", DryParticles.OnWaterTranspiler, nameof(DryParticles.SpawnOnWater));
            // ambient: summer air motes, the water specks, fireflies; rain: the drops and their splashes; storm: dust, spray, splashes
            Check(ambient == 3 && rain == 2 && dust == 3, $"vanilla's spawns found and replaced: ambient particles {ambient} (3), rain {rain} (2), storm {dust} (3)");

            var keep = DryParticles.IsDry;
            DryParticles.IsDry = (x, y, z) => x >= 0 && x <= 4 && y >= 0 && y <= 3 && z >= -2 && z <= 2;   // a ship's hold
            try
            {
                var m = new CountingParticles();
                int Spawns(Action a) { int b = m.Spawned; a(); return m.Spawned - b; }
                Vintagestory.API.Common.SimpleParticleProperties P(double x, double y, double z, bool dieInAir, double add = 0) => new()
                {
                    MinPos = new Vintagestory.API.MathTools.Vec3d(x, y, z), AddPos = new Vintagestory.API.MathTools.Vec3d(add, 0, add), MinQuantity = 1, ShouldDieInAir = dieInAir
                };
                int specInHold = Spawns(() => DryParticles.SpawnInWater(m, P(2, 1, 0, true)));
                int specOutside = Spawns(() => DryParticles.SpawnInWater(m, P(6, 1, 0, true)));
                int moteInHold = Spawns(() => DryParticles.SpawnInWater(m, P(2, 1, 0, false)));
                int splashInHold = Spawns(() => DryParticles.SpawnOnWater(m, P(1.5, 2.5, 0, false, 1)));
                int splashOutside = Spawns(() => DryParticles.SpawnOnWater(m, P(7, 2.5, 0, false, 1)));
                int rainOver = Spawns(() => DryParticles.SpawnOnWater(m, P(2 - 30, 2.5, -30, false, 60)));
                Check(specInHold == 0 && specOutside == 1 && moteInHold == 1, $"a water speck isn't spawned in a dry hold ({specInHold}), outside it is ({specOutside}); a mote of air is ({moteInHold})");
                Check(splashInHold == 0 && splashOutside == 1 && rainOver == 1, $"a rain splash isn't spawned on the water in a dry hold ({splashInHold}), outside it is ({splashOutside}); the rain over a wide box is ({rainOver})");
                // (0.3.5) judged over its life: the hold sailing on at 3 a second toward +x, a speck living two seconds
                // just ahead of her is inside her a moment later; one far ahead isn't
                double asked = -1;
                DryParticles.IsDryWithin = (x, y, z, s) =>
                {
                    asked = s;
                    for (double t = 0; t <= s + 1e-9; t += 0.25)
                        if (x >= 3 * t && x <= 4 + 3 * t && y >= 0 && y <= 3 && z >= -2 && z <= 2) return true;
                    return false;
                };
                var ahead = P(6, 1, 0, true); ahead.LifeLength = 2;
                var farAhead = P(20, 1, 0, true); farAhead.LifeLength = 2;
                int sweptIn = Spawns(() => DryParticles.SpawnInWater(m, ahead));
                double life = asked;
                int clear = Spawns(() => DryParticles.SpawnInWater(m, farAhead));
                Check(sweptIn == 0 && clear == 1 && Math.Abs(life - 2) < 1e-6,
                    $"a speck just ahead of a sailing hull isn't spawned ({sweptIn}: she'd be over it within its {life:0.0} s), one far ahead is ({clear})");
                DryParticles.IsDryWithin = null;
                DryParticles.IsDry = null;
                Check(Spawns(() => DryParticles.SpawnInWater(m, P(2, 1, 0, true))) == 1, "with no dry places known, everything is spawned");
            }
            finally { DryParticles.IsDry = keep; DryParticles.IsDryWithin = null; }
        }

        /// <summary>The drawn waves: the same everywhere they're computed, the right size, moving, and never jumping.</summary>
        static void Waves()
        {
            Console.WriteLine("waves");
            var calm = new SeaState(0.1, 1, 0, 2.6);
            var storm = new SeaState(2.5, 1, 0, 7.6);   // running east (+X)
            var gale = Wind.FromCompass(270, 1.0);
            var spec = new WaveSpectrum(4242, 0);

            // a place updated every frame at wave time t (the sea state only read when it's due, like SeaSystem does)
            void Run(WavePlace p, WaveSpectrum s, double t, SeaState sea, Wind wind) => p.Update(s, t, sea, wind);

            {
                var p = new WavePlace();
                Run(p, spec, 100, calm, default);
                double calmMax = 0;
                for (int i = 0; i < 400; i++) calmMax = Math.Max(calmMax, Math.Abs(p.HeightAt(1000 + i * 1.7, 1000 + i * 0.9)));
                Check(calmMax < 0.15, $"no wind, calm sea: the water is nearly flat (highest {calmMax:0.000})");

                for (double t = 100; t < 105; t += 0.1) Run(p, spec, t, storm, gale);
                double sum2 = 0; int n = 0;
                for (int ix = 0; ix < 80; ix++)
                    for (int iz = 0; iz < 80; iz++) { double h = p.HeightAt(1000 + ix * 3.1, 1000 + iz * 2.9); sum2 += h * h; n++; }
                double hs = 4 * Math.Sqrt(sum2 / n);
                Check(Math.Abs(hs - storm.Height) < 0.7, $"storm sea: the waves have about the sea state's height (4σ = {hs:0.00} for {storm.Height:0.00}, {p.Count} trains)");
                Check(p.Count <= WavePlace.MaxWaves && p.Count >= 10, $"a storm sea runs {p.Count} wave trains (the shader takes {WavePlace.MaxWaves})");

                // the shader's numbers give the same surface as the CPU
                var u = new float[WavePlace.MaxWaves * 4];
                int un = p.FillUniforms(u, 1000, 1000);
                double worst = 0;
                for (int i = 0; i < 200; i++)
                {
                    double rx = (i % 20 - 10) * 7.3, rz = (i / 20 - 5) * 5.1;
                    worst = Math.Max(worst, Math.Abs(WavePlace.ShaderHeight(u, un, rx, rz) - p.HeightAt(1000 + rx, 1000 + rz)));
                }
                Check(worst < 1e-3, $"shader uniforms and CPU agree on the surface (worst {worst:0.00000})");
                un = p.FillUniforms(u, 812345.5, -233007.25);
                worst = 0;
                for (int i = 0; i < 200; i++)
                {
                    double rx = (i % 20 - 10) * 7.3, rz = (i / 20 - 5) * 5.1;
                    worst = Math.Max(worst, Math.Abs(WavePlace.ShaderHeight(u, un, rx, rz) - p.HeightAt(812345.5 + rx, -233007.25 + rz)));
                }
                Check(worst < 2e-3, $"… also far from the world's origin (worst {worst:0.00000})");

                // they travel downwind (east): the pattern 2 s later matches the earlier one shifted east
                double[] Line(double t) { var a = new double[400]; for (int i = 0; i < a.Length; i++) a[i] = p.HeightAt(900 + i * 0.5, 1000, t); return a; }
                var before = Line(105);
                var after = Line(107);
                int best = 0; double bestC = double.MinValue;
                for (int sft = -60; sft <= 60; sft++)
                {
                    double c = 0;
                    for (int i = 60; i < 340; i++) c += before[i] * after[i + sft];
                    if (c > bestC) { bestC = c; best = sft; }
                }
                Check(best > 4, $"the waves move downwind, not stand still ({best * 0.5:0.0} blocks in 2 s)");

                // the main direction of the drawn swell follows the sea state's
                foreach (double deg in new[] { 0.0, 37.0, 135.0, 250.0 })
                {
                    double r = deg * Math.PI / 180;
                    var q = new WavePlace();
                    Run(q, spec, 10, new SeaState(2.0, Math.Cos(r), Math.Sin(r), 7.0), Wind.FromCompass(0, 0.0));
                    double sx = 0, sz = 0;
                    for (int i = 0; i < q.Count; i++)
                    {
                        double k = Math.Sqrt(q.Kx[i] * q.Kx[i] + q.Kz[i] * q.Kz[i]), e = q.Amp[i] * q.Amp[i];
                        if (2 * Math.PI / k < 20) continue;   // the long swell
                        sx += e * q.Kx[i] / k; sz += e * q.Kz[i] / k;
                    }
                    double got = Math.Atan2(sz, sx) * 180 / Math.PI, diff = Math.Abs(WorldShape.Mod(got - deg + 180, 360) - 180);
                    Check(diff < 12, $"swell sent toward {deg:0}° runs toward {WorldShape.Mod(got, 360):0}°");
                }
            }

            {
                // the same waves wherever they're worked out: another copy of the library, updated at another rate
                var specB = new WaveSpectrum(4242, 0);
                var a = new WavePlace();
                var b = new WavePlace();
                double worst = 0;
                double tb = 50;
                for (double t = 50; t < 70; t += 1 / 60.0)
                {
                    var sea = t < 58 ? calm : storm;
                    var wind = t < 60 ? default : gale;
                    Run(a, spec, t, sea, wind);
                    while (tb + 1 / 7.0 <= t) { tb += 1 / 7.0; Run(b, specB, tb, tb < 58 ? calm : storm, tb < 60 ? default : gale); }
                    Run(b, specB, t, sea, wind);
                    worst = Math.Max(worst, Math.Abs(a.HeightAt(3000.5, -2000.25) - b.HeightAt(3000.5, -2000.25)));
                }
                Check(worst < 1e-9, $"the same inputs give the same surface on every side, whatever the frame rate (worst {worst:e1})");
                var other = new WavePlace();
                Run(other, new WaveSpectrum(99, 0), 70, storm, gale);
                Run(a, spec, 70, storm, gale);
                Check(Math.Abs(other.HeightAt(10, 10) - a.HeightAt(10, 10)) > 1e-3 || Math.Abs(other.HeightAt(40, 70) - a.HeightAt(40, 70)) > 1e-3,
                    "another world has other waves");
            }

            {
                // a sudden change (calm to storm, and the wind swinging round) grows and turns the waves, never jumps them
                var p = new WavePlace();
                double prev = double.NaN, worstStep = 0;
                for (double t = 200; t < 212; t += 1 / 60.0)
                {
                    var sea = t < 203 ? calm : t < 207 ? storm : new SeaState(2.5, 0, 1, 7.6);
                    var wind = t < 203 ? default : t < 207 ? gale : Wind.FromCompass(0, 1.0);
                    Run(p, spec, t, sea, wind);
                    double h = p.HeightAt(500, 500);
                    if (!double.IsNaN(prev)) worstStep = Math.Max(worstStep, Math.Abs(h - prev));
                    prev = h;
                }
                Check(worstStep < 0.2, $"no jumps when the sea changes at once: largest change in one frame {worstStep:0.000} blocks");
                var q = new WavePlace();
                Run(q, spec, 300, calm, default);
                Run(q, spec, 301, storm, gale);
                double h0 = q.SignificantHeight;
                Run(q, spec, 301.5, storm, gale);
                double h1 = q.SignificantHeight;
                Run(q, spec, 302, storm, gale);
                Check(h0 < 0.3 && h1 > 0.5 && h1 < 2.2 && q.SignificantHeight > 2.2, $"a jump in the sea state is eased in over a second ({h0:0.00} → {h1:0.00} → {q.SignificantHeight:0.00})");

                // (0.3.6) after a gap the old one-end update holds the new sample instead of blending from a stale one
                var held = new WavePlace();
                Run(held, spec, 500, storm, gale);
                Run(held, spec, 510.5, calm, default);
                Check(held.SignificantHeight < 0.3, $"a place that missed seconds doesn't blend from what it saw before them ({held.SignificantHeight:0.00})");
            }

            {
                // (0.3.6) both ends of each second given (SeaSystem.SampleWaves): a place that kept up, one that missed
                // seconds, and one new every frame draw the same surface while the sea changes
                SeaState SeaN(long n) => n < 403 ? calm : storm;   // the sea at each second's start
                Wind WindN(long n) => n < 404 ? default : gale;
                void Both(WavePlace p, double t)
                {
                    long n = WavePlace.SampleIndex(t);
                    if (p.Due(spec, t)) p.Update(spec, t, SeaN(n - 1), WindN(n - 1), SeaN(n), WindN(n));
                    else p.Update(spec, t, default, default);
                }
                var kept = new WavePlace();
                var gappy = new WavePlace();
                double worst = 0;
                for (double t = 400; t < 410; t += 1 / 60.0)
                {
                    Both(kept, t);
                    var late = new WavePlace();
                    Both(late, t);
                    worst = Math.Max(worst, Math.Abs(late.HeightAt(700, 300) - kept.HeightAt(700, 300)));
                    if (t < 402 || t > 405.5)
                    {
                        Both(gappy, t);
                        worst = Math.Max(worst, Math.Abs(gappy.HeightAt(700, 300) - kept.HeightAt(700, 300)));
                    }
                }
                Check(worst < 1e-9, $"the surface doesn't depend on a place's history: new, kept up or after a gap (worst {worst:e1})");
            }

            {
                // (0.3.6) the game hour of each wave sample: the server's calendar moves in 200 ms steps, runs 20 times
                // faster while players sleep and jumps 12 hours at an admin's /time set; a client's lags a little. With
                // the server's anchors every caller on either side gets the same hours for both ends of a sample's blend
                // (n and n - 1), whenever in the second it asks, also a client that joins on an anchor's first sample
                const double perSecond = 30 / 3600.0;
                double Rate(double t) => t >= 40 && t < 60 ? 20 * perSecond : perSecond;
                // /time set: four jumps a second apart (a new anchor each time, the older ones still needed), then one more
                double[] jumps = { 6.9, 7.9, 8.9, 9.9, 26.9 };
                double Exact(double t) => 1000 + perSecond * (Math.Min(t, 40) + 20 * Math.Clamp(t - 40, 0, 20) + Math.Max(0, t - 60)) + 12 * jumps.Count(j => t >= j);
                long seq = 0;
                double Stepped(double t) => Exact(Math.Floor(t / 0.2) * 0.2);   // the calendar as the game has it
                var server = new SampleHours();
                server.Add(SampleHours.Make(0, Stepped(0), Rate(0), 0, ++seq), 0);
                // clients: when they join, and their packets in flight (sent 0.35 s before they arrive)
                var joins = new[] { 12.0, 28.25 };   // the second one joins on the first sample of the anchor made at 25 s
                var clients = joins.Select(_ => new SampleHours()).ToArray();
                var heard = new bool[joins.Length];
                var inFlight = new System.Collections.Generic.List<(int c, double at, HoursAnchor[] all)>();
                var seen = new System.Collections.Generic.Dictionary<(int side, long n), double>();
                double worstSide = 0, worstBetween = 0, worstCalendar = 0;
                void Seen(int side, long n, double h)
                {
                    if (seen.TryGetValue((side, n), out double was)) worstSide = Math.Max(worstSide, Math.Abs(h - was));
                    else seen[(side, n)] = h;
                }
                for (int f = 0; f < 90 * 60; f++)
                {
                    double t = f / 60.0;
                    bool tick = f % (5 * 60) == 0 && f > 0;
                    long now = WavePlace.SampleIndex(t);
                    if (tick) server.Add(SampleHours.Make(t, Stepped(t), Rate(t), 3, ++seq), now);
                    // and once a second: the calendar jumped or changed speed, a new anchor at once (sent like a tick's)
                    else if (f % 60 == 30 && SampleHours.Drifted(server.Newest, t, Stepped(t)))
                    {
                        server.Add(SampleHours.Make(t, Stepped(t), Rate(t), 3, ++seq), now);
                        tick = true;
                    }
                    for (int c = 0; c < joins.Length; c++)
                        if (tick && t >= joins[c] || Math.Abs(t - joins[c]) < 1e-9) inFlight.Add((c, t + 0.35, server.All));   // and one on joining
                    foreach (var p in inFlight.Where(p => p.at <= t + 1e-9).ToList())
                    {
                        foreach (var a in p.all) clients[p.c].Add(a, now);
                        heard[p.c] = true;
                        inFlight.Remove(p);
                    }
                    long n = WavePlace.SampleIndex(t);
                    for (int k = 0; k < 3; k++)   // a few callers per frame, each with its own idea of "now"
                    {
                        double estimate = Stepped(t + k * 0.07) - (t - n) * Rate(t);
                        double s1 = server.At(n, estimate, Rate(t)), s0 = server.At(n - 1, estimate - Rate(t), Rate(t));
                        Seen(-1, n, s1);
                        Seen(-1, n - 1, s0);
                        if (!jumps.Any(j => t >= j && t < j + 5)) worstCalendar = Math.Max(worstCalendar, Math.Abs(s1 - Exact(n)));   // (≤ 5 s behind a jump)
                        for (int c = 0; c < joins.Length; c++)
                        {
                            if (t < joins[c]) continue;
                            double ce = Stepped(t - 0.3) - (t - n) * Rate(t);
                            double c1 = clients[c].At(n, ce, Rate(t)), c0 = clients[c].At(n - 1, ce - Rate(t), Rate(t));
                            if (!heard[c]) continue;
                            Seen(c, n, c1);
                            Seen(c, n - 1, c0);
                            worstBetween = Math.Max(worstBetween, Math.Max(Math.Abs(c1 - s1), Math.Abs(c0 - s0)));
                        }
                    }
                }
                Check(worstSide == 0 && worstBetween == 0 && heard.All(h => h),
                    $"every caller, on the server and on clients (one joining on an anchor's first sample), gets the same game hours for both ends of a sample's blend (worst within a side {worstSide:e1}, between them {worstBetween:e1})");
                Check(worstCalendar < 5 * 20 * perSecond, $"… and they follow the calendar, sleeping included, and catch up with each /time set within 5 s (worst {worstCalendar * 60:0.0} game minutes off)");
            }

            {
                // on a Cylinder World ring the sea repeats exactly around it: no seam
                const double C = 254000;
                var ring = new WaveSpectrum(7, C);
                var p = new WavePlace();
                Run(p, ring, 1234.5, storm, gale);
                double worst = 0;
                var r = new Random(3);
                for (int i = 0; i < 200; i++)
                {
                    double x = RingStart + r.NextDouble() * 400, z = r.NextDouble() * 128000;
                    worst = Math.Max(worst, Math.Abs(p.HeightAt(x, z) - p.HeightAt(x + C, z)));
                }
                Check(worst < 1e-6, $"the sea is seamless around the ring (worst {worst:e1})");
            }

            {
                // the wave clock: snaps when it first hears from the server, then only eases (≤ 2%), and snaps when far off
                var c = new WaveClock();
                c.OnServerTime(5000, 12);
                bool snapped = Math.Abs(c.Now(12) - 5000) < 1e-9;
                c.OnServerTime(5000.3 + 1, 13);   // the server is 0.3 s ahead of us now
                double t0 = c.Now(13), worstRate = 0;
                double local = 13;
                for (int i = 0; i < 60 * 20; i++)
                {
                    double before = c.Now(local);
                    local += 1 / 60.0;
                    c.Advance(1 / 60.0);
                    worstRate = Math.Max(worstRate, Math.Abs((c.Now(local) - before) * 60 - 1));
                }
                bool caughtUp = Math.Abs(c.Now(local) - (5001.3 + local - 13)) < 1e-6;
                c.OnServerTime(9000, local);
                Check(snapped && worstRate <= WaveClock.MaxSlew + 1e-9 && caughtUp && Math.Abs(c.Now(local) - 9000) < 1e-9,
                    $"the wave clock follows the server's: snaps on joining, eases {worstRate:P1} at most, snaps when far off");
            }
        }

        /// <summary>SeaConfig as 0.3.x sent it, with Sea's weather in members 1-8.</summary>
        [ProtoBuf.ProtoContract]
        public class OldSeaConfig
        {
            [ProtoBuf.ProtoMember(1)] public string WindModel;
            [ProtoBuf.ProtoMember(2, IsRequired = true)] public float PrevailingStrength;
            [ProtoBuf.ProtoMember(3, IsRequired = true)] public float Variability;
            [ProtoBuf.ProtoMember(4, IsRequired = true)] public float Gusts;
            [ProtoBuf.ProtoMember(5, IsRequired = true)] public float StormFrequency;
            [ProtoBuf.ProtoMember(6, IsRequired = true)] public float StormRadius;
            [ProtoBuf.ProtoMember(7, IsRequired = true)] public float StormSpeed;
            [ProtoBuf.ProtoMember(8, IsRequired = true)] public float SeasonShift;
            [ProtoBuf.ProtoMember(9, IsRequired = true)] public float WaveScale;
            [ProtoBuf.ProtoMember(10, IsRequired = true)] public float CelestialReflections;
        }

        /// <summary>(0.3.7) Numbers Sea can't use (a hand-edited config, an admin's typo, a mod's degenerate hull) are refused or come back to defaults.</summary>
        static void BadInputs()
        {
            Console.WriteLine("bad inputs");
            bool parse = Compass.Parse("NaN") == null && Compass.Parse("Infinity") == null && Compass.Parse("-Infinity") == null
                && Compass.Parse("270") == 270 && Compass.Parse("sw") == 225;
            var nan = new WindOverride { FromDegrees = double.NaN, Strength = 1 };
            var inf = new WindOverride { FromDegrees = 90, Strength = 1, UntilHours = double.PositiveInfinity };
            var strong = WindOverride.Checked(new WindOverride { FromDegrees = 90, Strength = 9, StartHours = 10 });
            var weak = WindOverride.Checked(new WindOverride { FromDegrees = 90, Strength = double.NaN });
            Check(parse && WindOverride.Checked(nan) == null && WindOverride.Checked(inf) == null && weak == null && WindOverride.Checked(null) == null
                && strong != null && strong.Strength == WindModel.MaxStrength && strong.StartHours == 10,
                "a fixed wind with NaN or infinite numbers is refused (the direction, the strength, the hours), a too strong one clamped");

            var bad = new SeaConfig { WaveScale = float.PositiveInfinity, StormHeightScale = float.NaN, CelestialReflections = 8, SkyReflections = float.NaN, NightVisibility = -1, SeaWindFrom = float.NaN, SeaWindStrength = -1 }.Checked();
            var bad2 = new SeaConfig { WaveScale = 0, StormHeightScale = 99, SeaWindFrom = -90, SeaWindStrength = 9 }.Checked();
            var d = new SeaConfig();
            Check(bad.WaveScale == d.WaveScale && bad.CelestialReflections == 2 && bad.SkyReflections == 1 && bad.NightVisibility == 1
                && bad.SeaWindFrom == d.SeaWindFrom && bad.SeaWindStrength == d.SeaWindStrength && bad.StormHeightScale == 1 && bad2.StormHeightScale == 4
                && bad2.WaveScale == 0 && bad2.SeaWindFrom == 270 && bad2.SeaWindStrength == (float)WindModel.MaxStrength,
                "a config's negative, NaN or infinite numbers go back to the defaults (0 stays 0), the steady wind's bearing wraps and its strength is capped");

            // the server's settings reach clients as they are, zeros included (protobuf leaves zeros out, and the
            // client's object starts from the defaults)
            {
                var sent = new SeaParamsPacket
                {
                    Key = 7, Circumference = 0, DaysPerYear = 108, HoursPerDay = 24,
                    Config = new SeaConfig { WaveScale = 0, StormHeightScale = 0, CelestialReflections = 0, SkyReflections = 0, NightVisibility = 0, SeaWindFrom = 0, SeaWindStrength = 0 }
                };
                // (the game's own serializer, as its network channels use it)
                var got = Vintagestory.API.Util.SerializerUtil.Deserialize<SeaParamsPacket>(Vintagestory.API.Util.SerializerUtil.Serialize(sent)).Config;
                Check(got.WaveScale == 0 && got.StormHeightScale == 0 && got.CelestialReflections == 0 && got.SkyReflections == 0 && got.NightVisibility == 0 && got.SeaWindFrom == 0 && got.SeaWindStrength == 0,
                    $"a config of zeros arrives as zeros (wave scale {got.WaveScale}, steady wind {got.SeaWindStrength} from {got.SeaWindFrom})");
                sent.Config.StormHeightScale = 2.2f;
                got = Vintagestory.API.Util.SerializerUtil.Deserialize<SeaParamsPacket>(Vintagestory.API.Util.SerializerUtil.Serialize(sent)).Config;
                Check(got.StormHeightScale == 2.2f && new SeaConfig { StormHeightScale = -1 }.Checked().StormHeightScale == 1
                    && new SeaConfig { StormHeightScale = float.PositiveInfinity }.Checked().StormHeightScale == 1,
                    "storm height multiplier reaches clients unchanged; negative/non-finite values return to 1");

                // (0.4.0) a 0.3 server's config (members 1-8 were Sea's weather, now retired) still reads: those are skipped
                var old = new OldSeaConfig { WindModel = "Steady", PrevailingStrength = 2, Variability = 3, Gusts = 4, StormFrequency = 5, StormRadius = 6, StormSpeed = 7, SeasonShift = 8, WaveScale = 0.5f, CelestialReflections = 0 };
                var read = Vintagestory.API.Util.SerializerUtil.Deserialize<SeaConfig>(Vintagestory.API.Util.SerializerUtil.Serialize(old));
                Check(read.WaveScale == 0.5f && read.CelestialReflections == 0 && read.SeaWindFrom == d.SeaWindFrom && read.SeaWindStrength == d.SeaWindStrength && read.StormHeightScale == 1,
                    "a 0.3 config's retired weather members (1-8) are skipped; the rest reads as before");
            }

            // WaveScale 0 flattens the whole sea, the chop too
            var spec0 = new WaveSpectrum(4242, 0, 0);
            var amps = new double[spec0.TrainCount];
            spec0.Amplitudes(new SeaState(0, 1, 0, 2.5), Wind.FromCompass(270, 1), amps);
            double most = 0;
            foreach (var x in amps) most = Math.Max(most, Math.Abs(x));
            var spec1 = new WaveSpectrum(4242, 0, 1);
            spec1.Amplitudes(new SeaState(0, 1, 0, 2.5), Wind.FromCompass(270, 1), amps);
            double chop1 = 0;
            foreach (var x in amps) chop1 = Math.Max(chop1, x);
            Check(most == 0 && chop1 > 0.04, $"WaveScale 0: no waves at all, bounded chop included ({most:0.000}; at 1 the chop is {chop1:0.00})");

            // a hull with no outline left: nothing dry, and no exception
            bool threw = false, dry = false;
            try
            {
                foreach (bool detailed in new[] { false, true })
                {
                    var noLength = new WaterClipBox { Dry = true, HalfLength = 0, HalfWidth = 2, DryDepth = 5, DryHeight = 5, WaterY = 110 };
                    var insetAll = new WaterClipBox { Dry = true, HalfLength = 4, DryInset = 4, HalfWidth = 2, DryDepth = 5, DryHeight = 5, WaterY = 110,
                        DryProfile = new float[WaterShader.DryStations * WaterShader.DryLevels], DryLevelStep = 1 };
                    dry |= noLength.InDryHull(0, 109, 0, detailed) | insetAll.InDryHull(0, 109, 0, detailed);
                }
            }
            catch (Exception) { threw = true; }
            Check(!threw && !dry, "a dry hull with no length, or inset by all of it, has nothing dry and doesn't throw");
        }

        /// <summary>A sea that changes from place to place and hour to hour (a storm to the east), counting what it's asked.</summary>
        sealed class TestSource : IWaveSource
        {
            public int Asked;
            public long Revision { get; set; }
            public double Shift;
            public double HoursOf(long n, double t) => 1000 + n * 0.01;
            public SeaState SeaAt(double x, double z, double hours, long n)
            {
                Asked++;
                double s = Math.Clamp(0.2 + x / 4000 + 0.05 * Math.Sin(hours * 3) + Shift, 0, 1.4);
                double hgt = 0.1 + 2.4 * s * s, dir = 0.3 + z / 9000;
                return new SeaState(hgt, Math.Cos(dir), Math.Sin(dir), 2.5 + 3.2 * Math.Sqrt(hgt));
            }
            public Wind WindAt(double x, double z, double hours, long n) => Wind.FromCompass(250 + x / 100, Math.Clamp(0.3 + x / 5000 + Shift, 0, 1.5));
        }

        /// <summary>
        /// (0.4.2) The waves at a place come from the sea state at fixed grid points around it, blended by where it is: the
        /// same surface whoever asks, wherever they were before, smooth as a ship sails across the grid, and a second's
        /// start is the last second's end (asked once).
        /// </summary>
        static void GridSampling()
        {
            Console.WriteLine("waves sampled on a grid (0.4.2)");
            var spec = new WaveSpectrum(4242, 0);
            var src = new TestSource();
            // two ships at the same place and time: one came from 3 km away a second and a half ago, one has been here
            var here = new WavePlace();
            var came = new WavePlace();
            double x = 1830.5, z = -412.25;
            for (double t = 90; t <= 100.3; t += 1 / 30.0) here.Sample(spec, t, x, z, 0, src);
            came.Sample(spec, 98.8, x + 3000, z - 2000, 0, src);
            came.Sample(spec, 99.9, x - 700, z + 40, 0, src);
            came.Sample(spec, here.Time, x, z, 0, src);
            double worst = 0;
            for (int i = 0; i < 50; i++) worst = Math.Max(worst, Math.Abs(here.HeightAt(x + i * 3.3, z - i * 1.7) - came.HeightAt(x + i * 3.3, z - i * 1.7)));
            Check(here.Count > 0 && worst < 1e-12, $"a ship that sailed in from far away gets the same surface as one that was there all along (worst {worst:e1})");

            // a ship sailing at 12 blocks a second across several cells: its waves change smoothly (no step at a cell's edge)
            var sailing = new WavePlace();
            double prevA = double.NaN, worstStep = 0, ownMotion = 0;
            for (int f = 0; f <= 60 * 30; f++)
            {
                double t = 200 + f / 60.0, sx = 500 + 12 * (t - 200);
                sailing.Sample(spec, t, sx, 300, 0, src);
                double a = 0;
                for (int j = 0; j < sailing.Count; j++) a += sailing.Amp[j];
                if (!double.IsNaN(prevA)) worstStep = Math.Max(worstStep, Math.Abs(a - prevA));
                prevA = a;
            }
            // (one frame of the sea's own change over a second: the amplitudes' sum moves up to about this much)
            ownMotion = 0.02;
            Check(worstStep < ownMotion, $"a ship sailing across the grid sees its waves change smoothly (largest change in a frame {worstStep:0.0000} in the summed amplitudes)");

            // a second's start is the last second's end: a place that keeps up asks for one end a second, not two
            var kept = new WavePlace();
            kept.Sample(spec, 300.5, 100, 100, 0, src);
            src.Asked = 0;
            for (double t = 300.5; t < 310.4; t += 1 / 60.0) kept.Sample(spec, t, 100, 100, 0, src);
            kept.Sample(spec, 310.4, 100, 100, 0, src);
            int keptAsks = src.Asked;
            var fresh = new WavePlace();
            src.Asked = 0;
            fresh.Sample(spec, 310.4, 100, 100, 0, src);
            int freshAsks = src.Asked;
            Check(keptAsks == 10 * 4 && freshAsks == 2 * 4 && Math.Abs(kept.HeightAt(120, 80) - fresh.HeightAt(120, 80)) < 1e-12,
                $"a place keeping up asks for the sea state at one end of each second ({keptAsks} asks over 10 s), and gets what a new one gets (which asks both ends: {freshAsks})");
            // …unless the sea's past changed (a fixed wind, a provider): then both ends again, from the changed sea
            src.Shift = 0.3;
            src.Revision++;
            src.Asked = 0;
            kept.Sample(spec, 311.2, 100, 100, 0, src);
            var after = new WavePlace();
            after.Sample(spec, 311.2, 100, 100, 0, src);
            Check(src.Asked == 2 * 4 * 2 && Math.Abs(kept.HeightAt(120, 80) - after.HeightAt(120, 80)) < 1e-12, "a change to the sea's past makes a place start over");
            src.Shift = 0;
            src.Revision++;
            // …even within a second (Codex): a place sampled before the change and one new to it agree at once
            var early = new WavePlace();
            early.Sample(spec, 320.2, 100, 100, 0, src);
            src.Shift = 0.2;
            src.Revision++;
            early.Sample(spec, 320.4, 100, 100, 0, src);
            var late = new WavePlace();
            late.Sample(spec, 320.4, 100, 100, 0, src);
            Check(Math.Abs(early.HeightAt(130, 70) - late.HeightAt(130, 70)) < 1e-12, "a change mid-second reaches a place sampled earlier in that second at once");
            src.Shift = 0;
            src.Revision++;

            // on a Cylinder World ring: the grid fits the ring, so the sea is the same at x and x + C
            const double C = 254000;
            var ring = new WaveSpectrum(7, C);
            var a1 = new WavePlace();
            var a2 = new WavePlace();
            a1.Sample(ring, 400.3, RingStart + 77.7, 500, RingStart, src);
            a2.Sample(ring, 400.3, RingStart + 77.7 + C, 500, RingStart, src);
            // (the test sea grows with x, unlike a ring's: compare where the two are asked about the same corners)
            Check(Math.Abs(a1.HeightAt(RingStart + 80, 510) - a2.HeightAt(RingStart + 80 + C, 510)) < 1e-9,
                "on a ring the grid repeats with it: the same corners, the same surface, a lap apart");

            // the corners are worked out once for every place in a cell (the camera and the ships there), and shared with
            // the cell next door; the surface is what each place gets on its own
            long inputs = 1;
            var shared = new SharedCorners(src, () => inputs);
            var alone = new WavePlace();
            src.Asked = 0;
            alone.Sample(spec, 500.4, 130, 140, 0, src);
            int aloneAsks = src.Asked;
            var places = new WavePlace[4];
            for (int i = 0; i < places.Length; i++) places[i] = new WavePlace();
            src.Asked = 0;
            for (int i = 0; i < 3; i++) places[i].Sample(spec, 500.4, 130 + i * 9, 140 - i * 5, 0, shared);
            int cellAsks = src.Asked;
            places[3].Sample(spec, 500.4, 130 + 64, 140, 0, shared);   // the next cell east: its two west corners are known
            int nextAsks = src.Asked - cellAsks;
            double apart = 0;
            for (int i = 0; i < 3; i++)
            {
                var own = new WavePlace();
                own.Sample(spec, 500.4, 130 + i * 9, 140 - i * 5, 0, src);
                apart = Math.Max(apart, Math.Abs(own.HeightAt(150, 120) - places[i].HeightAt(150, 120)));
            }
            Check(aloneAsks == 2 * 4 && cellAsks == 2 * 4 && nextAsks == 2 * 2 && apart == 0,
                $"three places in a cell ask the sea state at its corners once ({cellAsks} asks, one place alone {aloneAsks}), the next cell only at its new corners ({nextAsks}), and each surface is the same as worked out alone");
            // …and a change to what the wind comes from is worked out afresh
            src.Shift = 0.3;
            src.Revision++;
            inputs++;
            src.Asked = 0;
            places[0].Sample(spec, 500.6, 130, 140, 0, shared);
            var changed = new WavePlace();
            changed.Sample(spec, 500.6, 130, 140, 0, src);
            Check(src.Asked == 2 * 4 * 2 && places[0].HeightAt(150, 120) == changed.HeightAt(150, 120), "shared corners are worked out again after a change to the sea's inputs");
            // …but nothing is kept while the answers aren't fixed by place and hour (no model yet: vanilla's wind)
            inputs = long.MinValue;
            src.Asked = 0;
            places[1].Reset();
            places[2].Reset();
            places[1].Sample(spec, 500.7, 130, 140, 0, shared);
            places[2].Sample(spec, 500.7, 131, 141, 0, shared);
            Check(src.Asked == 2 * 2 * 4, "without fixed inputs every place asks for itself (once per corner, as alone)");
            src.Shift = 0;
            src.Revision++;

            // the wave clock refuses a time that isn't a number: it would stay NaN for good (a player's ping is NaN without a connection)
            var clock = new WaveClock();
            clock.OnServerTime(double.NaN, 10);
            bool waiting = !clock.Synced;
            clock.OnServerTime(500, 10);
            clock.OnServerTime(double.PositiveInfinity, 11);
            clock.Advance(double.NaN);
            Check(waiting && clock.Synced && Math.Abs(clock.Now(11) - 501) < 1e-9, "the wave clock ignores a NaN or infinite server time (and a NaN frame time)");
        }

        /// <summary>
        /// (0.4.2) An admin's setwind and clearwind: in force from a wave sample a moment ahead on every side (whatever the
        /// calendar does), and the sea they leave dies down. Here sample s is at game hour s / 100.
        /// </summary>
        static void AdminChanges()
        {
            Console.WriteLine("an admin changing the fixed wind (0.4.2)");
            var m = new WindModel(Planet(), new SeaConfig { SeaWindStrength = 0.1f });
            double x = 4000, z = ZAt(30);
            WindOverride current = null;
            var history = Array.Empty<WindOverride>();
            void Change(WindOverride next, long sample, double hoursNow)
            {
                WindOverride.Change(ref current, ref history, next, sample, sample / 100.0, hoursNow);
                m.History = history;
                m.Override = current;
            }
            SeaState Sea(long sample) => m.SeaAt(x, z, sample / 100.0, sample);

            // a storm since hour 1000, cleared at hour 1100 (taking effect at sample 110001, a few seconds ahead)
            Change(new WindOverride { FromDegrees = 270, Strength = 1.2, StartHours = 1000 }, 100000, 1000);
            double storm = Sea(109900).Height;
            Change(null, 110001, 1100);
            double justBefore = Sea(110000).Height, oneHour = Sea(110101).Height, sixHours = Sea(110601).Height, day = Sea(113000).Height;
            Check(current == null && history.Length == 1 && history[0].Ended && history[0].EndSample == 110001 && m.FixedAt(1100.0, 110000) != null && m.FixedAt(1100.02, 110002) == null,
                "a clearwind ends the fixed wind at a sample a moment ahead (in force until then)");
            Check(Math.Abs(justBefore - storm) < 1e-9 && oneHour > 0.5 * storm && sixHours < oneHour && day < 0.2 * storm,
                $"the storm's sea dies down over hours instead of vanishing ({storm:0.00} → {oneHour:0.00} an hour later → {sixHours:0.00} → {day:0.00} a day later)");

            // a 'built' setwind takes over at its sample: before it the old sea stands, for every hour asked about
            Change(new WindOverride { FromDegrees = 0, Strength = 0.6, StartHours = 1200 - 24 }, 120000, 1199.97);
            var before = m.SeaWind(x, z, 1199.99, true, 119999);   // (no provider: the config's steady wind, 0.1 from the west)
            var after = m.SeaWind(x, z, 1200, true, 120000);
            double sea_Before = Sea(119999).Height, built = Sea(120000).Height;
            Check(current.ActiveSample == 120000 && Math.Abs(before.X - 0.1) < 1e-6 && Math.Abs(after.Z - 0.6) < 1e-9 && sea_Before < 0.5 && Math.Abs(built - m.DevelopedHeight(0.6)) < 1e-9,
                $"a 'built' setwind counts only from its sample, then its sea is up at once ({sea_Before:0.00} → {built:0.00})");

            // two setwinds before the first takes over (Codex): until the first one's sample the one before both stands
            var m2 = new WindModel(Planet(), new SeaConfig { SeaWindStrength = 0.1f });
            WindOverride c2 = null;
            var h2 = Array.Empty<WindOverride>();
            WindOverride.Change(ref c2, ref h2, new WindOverride { FromDegrees = 270, Strength = 1.0, StartHours = 1000 }, 100000, 1000, 1000);   // A
            WindOverride.Change(ref c2, ref h2, new WindOverride { FromDegrees = 0, Strength = 0.6, StartHours = 1200.03 }, 120003, 1200.03, 1200);   // B
            WindOverride.Change(ref c2, ref h2, new WindOverride { FromDegrees = 90, Strength = 0.3, StartHours = 1200.04 }, 120004, 1200.04, 1200.01);   // C
            m2.History = h2; m2.Override = c2;
            var a = m2.FixedAt(1200.02, 120002);
            var b = m2.FixedAt(1200.03, 120003);
            var c = m2.FixedAt(1200.05, 120005);
            Check(a?.FromDegrees == 270 && b?.FromDegrees == 0 && c?.FromDegrees == 90 && h2.Length == 2,
                "two setwinds before the first takes over: the old wind until the first's sample, the first until the second's, then the second");

            // the calendar standing still: every sample at the same hour, the change still comes at its sample (Codex)
            var m3 = new WindModel(Planet(), new SeaConfig { SeaWindStrength = 0.1f });
            WindOverride c3 = null;
            var h3 = Array.Empty<WindOverride>();
            WindOverride.Change(ref c3, ref h3, new WindOverride { FromDegrees = 270, Strength = 1.2, StartHours = 1150 - 24 }, 120000, 1150, 1150);
            WindOverride.Change(ref c3, ref h3, null, 120005, 1150, 1150);
            m3.History = h3; m3.Override = c3;
            double still4 = m3.SeaAt(x, z, 1150, 120004).Height, still5 = m3.SeaAt(x, z, 1150, 120005).Height, still0 = m3.SeaAt(x, z, 1150, 119999).Height;
            // (a fixed wind set, then cleared: from its sample the hour the calendar stands at is no longer stormy; the hours
            // before it still were, as no time passes for the sea to calm)
            Check(still0 < 0.5 && still4 > 3 && still5 < still4 - 1e-6 && m3.FixedAt(1150, 120004) != null && m3.FixedAt(1150, 120005) == null,
                $"with the calendar stopped a change still comes at its sample, not at once ({still0:0.00} → {still4:0.00} → {still5:0.00})");

            // changes within 16 hours keep every fixed wind that still shapes the sea (Codex): a storm, an hour of calm, cleared
            var m4 = new WindModel(Planet(), new SeaConfig { SeaWindStrength = 0.1f });
            WindOverride c4 = null;
            var h4 = Array.Empty<WindOverride>();
            WindOverride.Change(ref c4, ref h4, new WindOverride { FromDegrees = 270, Strength = 1.2, StartHours = 1000 }, 100000, 1000, 1000);
            WindOverride.Change(ref c4, ref h4, new WindOverride { FromDegrees = 0, Strength = 0.05, StartHours = 1300 }, 130000, 1300, 1300);
            WindOverride.Change(ref c4, ref h4, null, 130200, 1302, 1302);
            m4.History = h4; m4.Override = c4;
            double stormSea = m4.SeaAt(x, z, 1299, 129900).Height, swell = m4.SeaAt(x, z, 1303, 130300).Height;
            Check(h4.Length == 2 && swell > 0.4 * stormSea, $"a storm replaced and then cleared within the hour still leaves its swell ({stormSea:0.00} → {swell:0.00})");
            // and ended ones no sample can need are dropped
            WindOverride.Change(ref c4, ref h4, null, 140000, 1400, 1400);
            Check(h4.Length == 0, "fixed winds ended more than 16 game hours ago are dropped");

            // a huge but finite bearing is wrapped, not turned into NaN; bad end hours refused; a save's samples restart
            var huge = WindOverride.Checked(new WindOverride { FromDegrees = 1e308, Strength = 1 });
            var neg = WindOverride.Checked(new WindOverride { FromDegrees = -90, Strength = 1 });
            var hw = Wind.FromCompass(huge.FromDegrees, huge.Strength);
            Check(huge.FromDegrees >= 0 && huge.FromDegrees < 360 && double.IsFinite(hw.X) && double.IsFinite(hw.Z) && neg.FromDegrees == 270,
                $"a fixed wind from 1e308° is wrapped to {huge.FromDegrees:0.0}° (finite), from -90° to {neg.FromDegrees:0}°");
            var restarted = new WindOverride { FromDegrees = 0, Strength = 1, ActiveSample = 5000, Ended = true, EndSample = 6000, EndHours = 10 }.Restarted();
            Check(WindOverride.Checked(new WindOverride { FromDegrees = 0, Strength = 1, Ended = true, EndHours = double.NaN }) == null
                && restarted.ActiveSample == 0 && restarted.EndSample == long.MinValue && restarted.AppliesAt(9, 3) && !restarted.AppliesAt(11, 3),
                "a NaN end hour is refused; from a save a fixed wind is in force from the first sample and an ended one has ended");
            var packet = new SeaParamsPacket { Key = 1, Override = c3, History = h3 };
            var got = Vintagestory.API.Util.SerializerUtil.Deserialize<SeaParamsPacket>(Vintagestory.API.Util.SerializerUtil.Serialize(packet));
            Check(got.History?.Length == h3.Length && got.History[0].Ended && got.History[0].EndSample == 120005 && got.History[0].ActiveSample == 120000,
                "the ended fixed winds reach clients with their samples");
        }

        /// <summary>
        /// (0.3.7) While the sea turns, more trains run than the shader takes: those kept are each less the strongest left
        /// out, so the surface doesn't jump when their order changes (Codex's case: key 4242, a 2.5-block sea turning from
        /// east to south; cutting the weakest off made it jump about 0.06 blocks).
        /// </summary>
        static void WaveBudget()
        {
            Console.WriteLine("wave budget");
            var spec = new WaveSpectrum(4242, 0);
            var east = new SeaState(2.5, 1, 0, 7.6);
            var south = new SeaState(2.5, 0, 1, 7.6);
            var wind0 = Wind.FromCompass(270, 1.0);
            var wind1 = Wind.FromCompass(0, 1.0);
            var p = new WavePlace();
            const double dt = 1e-4;
            double worst = 0, worstMotion = 0;
            int most = 0;
            bool swapped = false;
            var before = new System.Collections.Generic.HashSet<(double, double)>();
            var now = new System.Collections.Generic.HashSet<(double, double)>();
            double[] prevH = new double[16];
            for (int i = 0; i <= 10000; i++)
            {
                double t = 300 + i * dt;
                p.Update(spec, Math.Min(t, 300.99999), east, wind0, south, wind1);
                most = Math.Max(most, p.Count);
                now.Clear();
                for (int j = 0; j < p.Count; j++) now.Add((p.Kx[j], p.Kz[j]));
                if (i > 0 && !now.SetEquals(before)) swapped = true;
                (before, now) = (now, before);
                // how far the surface may move in dt from the trains' own running and growing
                double motion = 0;
                for (int j = 0; j < p.Count; j++) motion += p.Amp[j] * p.Omega[j] * dt + 2.5 * dt;
                for (int k = 0; k < prevH.Length; k++)
                {
                    double h = p.HeightAt(1000 + k * 13.7, 2000 + k * 7.3);
                    if (i > 0) worst = Math.Max(worst, Math.Abs(h - prevH[k]));
                    prevH[k] = h;
                }
                worstMotion = Math.Max(worstMotion, motion);
            }
            Check(most == WavePlace.MaxWaves && swapped && worst < 0.01,
                $"the surface doesn't jump when the strongest {WavePlace.MaxWaves} trains change (over budget: {most == WavePlace.MaxWaves}, kept ones changed: {swapped}; biggest step {worst:0.0000} in {dt * 1000:0.0} ms, running alone moves up to {worstMotion:0.0000})");
        }
    }
}
