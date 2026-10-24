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
    public static object Logger, ModLoader;
    /// <summary>Whether this world has AstraTerra (the mod loader's answer; its Mod itself isn't made here).</summary>
    public static bool AstraEnabled;
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name == "get_Logger") return Logger;
        if (method.Name == "get_ModLoader") return ModLoader;
        if (method.Name == "IsModEnabled") return AstraEnabled && (string)args[0] == "astraterra";
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
            Proxy.ModLoader = DispatchProxy.Create<IModLoader, Proxy>();
            var api = DispatchProxy.Create<ICoreClientAPI, Proxy>();
            var value = RuntimeHelpers.GetUninitializedObject(typeof(CelestialRenderer));
            typeof(CelestialRenderer).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, api);
            typeof(CelestialRenderer).GetMethod("InitializeAstra", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, null);
            Check(!(bool)typeof(CelestialRenderer).GetField("astraPresent", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value), "vanilla path initializes with no AstraTerra assembly loaded");
            return failures;
        }
        string assemblyPath = Path.GetFullPath(args.Length > 0 ? args[0] : "obj/sky-inspection/AstraTerra.dll");
        var at = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        Proxy.Logger = DispatchProxy.Create<ILogger, Proxy>();
        Proxy.ModLoader = DispatchProxy.Create<IModLoader, Proxy>();
        // an earlier world's AstraTerra stays loaded: a world without it doesn't use it
        {
            var without = RuntimeHelpers.GetUninitializedObject(typeof(CelestialRenderer));
            typeof(CelestialRenderer).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(without, DispatchProxy.Create<ICoreClientAPI, Proxy>());
            typeof(CelestialRenderer).GetMethod("InitializeAstra", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(without, null);
            Check(!(bool)typeof(CelestialRenderer).GetField("astraPresent", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(without),
                "AstraTerra's assembly loaded but the mod not in this world: not used");
        }
        Proxy.AstraEnabled = true;
        var client = DispatchProxy.Create<ICoreClientAPI, Proxy>();
        var renderer = RuntimeHelpers.GetUninitializedObject(typeof(CelestialRenderer));
        typeof(CelestialRenderer).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(renderer, client);
        typeof(CelestialRenderer).GetMethod("InitializeAstra", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(renderer, null);
        bool failed = (bool)typeof(CelestialRenderer).GetField("astraFailed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
        Check(!failed, "Sea's actual optional adapter initializes against the extracted installed AstraTerra 0.15.0 DLL");
        if (!failed)
        {
            var days = at.GetType("AstraTerra.Astronomy.LocalMoonTime").GetMethod("MoonTotalDays");
            Check(Math.Abs((double)days.Invoke(null, new object[] { 4.0, 90.0, true }) - 4.25) < 1e-12,
                "local moon source follows AstraTerra's longitude correction");
            // (0.4.2) the adapter's own typed delegates, bound once, call the real methods (no config loaded: AstraTerra's stars off)
            object D(string name) => typeof(CelestialRenderer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
            var localDays = (System.Func<double, double, bool, double>)D("localMoonDays");
            bool near = ((System.Func<bool>)D("nearMoon"))(), disc = ((System.Func<bool>)D("customMoon"))();
            Check(Math.Abs(localDays(4.0, 90.0, true) - 4.25) < 1e-12 && localDays(4.0, 90.0, false) == 4.0
                && D("longitude") != null && D("moonPhase") != null,
                $"the adapter's typed delegates call AstraTerra (moon days 4.25; moons replaced: near body {near}, disc {disc})");
        }

        var p = typeof(CelestialRenderer).GetMethod("FarFragment");
        // Wmap's working tree next door, or another checkout of it (WMAP_ROOT, its project folder).
        string wmapRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("WMAP_ROOT") ?? "../Wmap");
        string farPath = Path.Combine(wmapRoot, "assets/wmap/shaders/farterrain.fsh");
        if (File.Exists(farPath))
        {
            // As the game hands it to Sea: Wmap's own includes expanded (from 0.5 the shading is in them).
            var seen = new System.Collections.Generic.HashSet<string>();
            string Expand(string code) => System.Text.RegularExpressions.Regex.Replace(code, "^#include\\s+(.*)", m =>
            {
                string name = m.Groups[1].Value.Trim().ToLowerInvariant(), file = Path.Combine(wmapRoot, "assets/wmap/shaderincludes", name);
                return seen.Add(name) && File.Exists(file) ? Expand(File.ReadAllText(file)) : "";
            }, System.Text.RegularExpressions.RegexOptions.Multiline);
            string source = Expand(File.ReadAllText(farPath));
            string patched = (string)p.Invoke(null, new object[] { source });
            if (source.Contains("seaNightFloor"))
                Check(patched == null, "workspace Wmap 0.5: its own far-water contract, left unpatched (Sea sets the uniforms)");
            else
                Check(patched != null && patched.Contains("seaNightFloor") && patched.Contains(CelestialShader.FarSkyReflection) && CelestialRenderer.FarFragment(patched) == null,
                    "workspace Wmap: distant night floor and sea sky reflection apply once");
        }
        else Check(false, "Wmap source available for the optional continuity check");
        Console.WriteLine(failures == 0 ? "ALL OK" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
