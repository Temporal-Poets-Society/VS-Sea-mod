using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Sea;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

// Resolves the exact installed binaries, without starting a client or loading any save.
public class Proxy : DispatchProxy
{
    public static object Logger;
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name == "get_Logger") return Logger;
        if (method.DeclaringType == typeof(ILogger) && args?.Length > 0) Console.WriteLine("  log  " + args[0]);
        return method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}

static class Program
{
    static int failures;
    static void Check(bool ok, string message) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + message); if (!ok) failures++; }
    static int Main(string[] args)
    {
        string vs = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? Path.Combine(Environment.GetEnvironmentVariable("APPDATA"), "Vintagestory");
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (string dir in new[] { vs, Path.Combine(vs, "Lib"), Path.Combine(vs, "Mods") })
            {
                string path = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
        return Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "--vanilla")
        {
            Proxy.Logger = DispatchProxy.Create<ILogger, Proxy>();
            var api = DispatchProxy.Create<ICoreClientAPI, Proxy>();
            var value = RuntimeHelpers.GetUninitializedObject(typeof(CelestialRenderer));
            typeof(CelestialRenderer).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, api);
            typeof(CelestialRenderer).GetMethod("InitializeAstra", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, null);
            Check(typeof(CelestialRenderer).GetField("astraSky", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value) == null, "vanilla path initializes with no AstraTerra assembly loaded");
            return failures;
        }
        string assemblyPath = Path.GetFullPath(args.Length > 0 ? args[0] : "obj/sky-inspection/AstraTerra.dll");
        var at = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        Proxy.Logger = DispatchProxy.Create<ILogger, Proxy>();
        var client = DispatchProxy.Create<ICoreClientAPI, Proxy>();
        var renderer = RuntimeHelpers.GetUninitializedObject(typeof(CelestialRenderer));
        typeof(CelestialRenderer).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(renderer, client);
        typeof(CelestialRenderer).GetMethod("InitializeAstra", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(renderer, null);
        bool failed = (bool)typeof(CelestialRenderer).GetField("astraFailed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
        Check(!failed, "Sea's actual optional adapter initializes against the extracted installed AstraTerra 0.15.0 DLL");
        if (!failed)
        {
            var reader = (Delegate)typeof(CelestialRenderer).GetField("readStar", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
            object star = Activator.CreateInstance(at.GetType("AstraTerra.Astronomy.RenderedStar"));
            var sample = (CelestialRenderer.Star)reader.DynamicInvoke(star);
            Check(sample.X == 0 && sample.Brightness == 0, "compiled star reader accepts the real RenderedStar struct");
            var modeMethod = at.GetType("AstraTerra.Config.StarfieldModeParser").GetMethod("ShowsAstraTerra");
            Check(modeMethod != null, "custom star mode can be checked without enabling a dependency");
            var days = at.GetType("AstraTerra.Astronomy.LocalMoonTime").GetMethod("MoonTotalDays");
            Check(Math.Abs((double)days.Invoke(null, new object[] { 4.0, 90.0, true }) - 4.25) < 1e-12,
                "local moon source follows AstraTerra's longitude correction");
            // (0.4.2) the adapter's own typed delegates, bound once, call the real methods (no config loaded: AstraTerra's stars off)
            object D(string name) => typeof(CelestialRenderer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
            var localDays = (System.Func<double, double, bool, double>)D("localMoonDays");
            bool shows = ((System.Func<bool>)D("showsAstra"))(), vanilla = ((System.Func<bool>)D("vanillaStars"))();
            bool path = ((System.Func<bool>)D("starPath"))(), near = ((System.Func<bool>)D("nearMoon"))(), disc = ((System.Func<bool>)D("customMoon"))();
            Check(Math.Abs(localDays(4.0, 90.0, true) - 4.25) < 1e-12 && localDays(4.0, 90.0, false) == 4.0 && !shows && vanilla
                && D("longitude") != null && D("moonPhase") != null,
                $"the adapter's typed delegates call AstraTerra (moon days 4.25; no config: its stars {shows}, vanilla's {vanilla}; star path {path}, moons {near}/{disc})");
        }

        var field = typeof(CelestialRenderer).GetField("pixels", BindingFlags.Instance | BindingFlags.NonPublic);
        var pixels = new float[1024 * 512]; field.SetValue(renderer, pixels);
        var splat = typeof(CelestialRenderer).GetMethod("Splat", BindingFlags.Instance | BindingFlags.NonPublic);
        void Draw(float angle) => splat.Invoke(renderer, new object[] { new CelestialRenderer.Star(MathF.Cos(angle), 0, MathF.Sin(angle), 0.8f) });
        Draw(MathF.PI);
        Check(pixels[256 * 1024] > 0 && pixels[256 * 1024 + 1023] > 0, "star atlas wraps continuously across its longitude seam");
        var first = (float[])pixels.Clone(); Array.Clear(pixels); Draw(MathF.PI);
        Check(first.SequenceEqual(pixels), "star atlas is deterministic, with no frame-random flicker");
        float energy = pixels.Sum(); Array.Clear(pixels); Draw(MathF.PI + MathF.PI / 3600f);
        Check(Math.Abs(pixels.Sum() / energy - 1) < 0.01, "subpixel sky motion preserves star energy within 1 percent");
        var paint = (Action<CelestialRenderer.Star>)splat.CreateDelegate(typeof(Action<CelestialRenderer.Star>), renderer);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 6000; i++) paint(new CelestialRenderer.Star(MathF.Cos(i * 0.01f), 0, MathF.Sin(i * 0.01f), 0.15f));
        clock.Stop();
        Console.WriteLine($"       synthetic 6000-star CPU splat: {clock.Elapsed.TotalMilliseconds:0.00} ms (one rebuild, excludes GPU upload; not a frame-rate measurement)");

        var p = typeof(CelestialRenderer).GetMethod("FarFragment");
        string farPath = Path.GetFullPath("../Wmap/assets/wmap/shaders/farterrain.fsh");
        if (File.Exists(farPath))
        {
            string source = File.ReadAllText(farPath);
            string patched = (string)p.Invoke(null, new object[] { source });
            Check(patched != null && patched.Contains("seaNightFloor") && CelestialRenderer.FarFragment(patched) == null, "installed workspace Wmap distant lighting adapter applies once");
        }
        else Check(false, "Wmap source available for the optional continuity check");
        Console.WriteLine(failures == 0 ? "ALL OK" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
