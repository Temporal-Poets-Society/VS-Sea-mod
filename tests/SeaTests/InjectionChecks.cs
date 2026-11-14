using System;
using System.IO;
using Sea;

namespace SeaTests
{
    /// <summary>
    /// (SE-0048) Shader injection that a game update can't turn into a broken shader: a repeated anchor line leaves the
    /// shader alone (each injector returns null) instead of taking Sea's code twice, and the GLSL array sizes are the
    /// C# constants the uploads are cut to.
    /// </summary>
    static class InjectionChecks
    {
        public static void Run(Action<bool, string> check)
        {
            string root = Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "Vintagestory"), "assets", "game", "shaders");
            string vPath = Path.Combine(root, "chunkliquid.vsh"), fPath = Path.Combine(root, "chunkliquid.fsh");
            if (!File.Exists(vPath) || !File.Exists(fPath)) { check(false, "injection: the game's water shaders are at " + root); return; }
            string v = File.ReadAllText(vPath), f = File.ReadAllText(fPath);

            // a line Sea anchors on, repeated: the injector leaves the shader alone
            string Twice(string code, string line)
            {
                int at = code.IndexOf(line, StringComparison.Ordinal);
                return at < 0 ? null : code.Insert(at, line + "\n");
            }
            string vTwice = Twice(v, "vec4 cameraPos = modelViewMatrix * worldPos;");
            check(vTwice != null && WaterShader.Inject(vTwice) == null, "injection: a doubled camera line leaves the vertex stage to vanilla (no waves twice)");
            string injected = WaterShader.Inject(v);
            check(injected != null, "injection: the installed vertex stage takes Sea's code");
            string cvTwice = injected == null ? null : Twice(injected, "fragNormal = unpackNormal(renderFlags);");
            check(cvTwice != null && CelestialShader.Vertex(cvTwice) == null, "injection: a doubled normal line leaves the celestial vertex code out");
            string fTwice = Twice(f, "texColor = applyFog(texColor, fogAmount);");
            check(fTwice != null && CelestialShader.Fragment(fTwice) == null, "injection: a doubled fog line leaves the celestial fragment code out");
            string mainTwice = f + "\nvoid main() {}\n";
            check(CelestialShader.Fragment(mainTwice) == null && FlowLook.Inject(mainTwice) == null && WaterShader.InjectFragment(mainTwice) == null,
                "injection: two main functions: no fragment injector goes in");
            check(CelestialShader.Fragment(f) != null && FlowLook.Inject(f) != null && WaterShader.InjectFragment(f) != null,
                "injection: the installed fragment stage takes each of them once");

            // the GLSL arrays as long as the C# constants say (an upload cut to a shorter array fails without an error)
            string all = (injected ?? "") + WaterShader.InjectFragment(f);
            var sizes = new (string Name, int Size)[]
            {
                ("sea_Wave", WaterShader.MaxWaves), ("sea_WaveClock", WaterShader.MaxWaves), ("sea_WaveLee", WaterShader.MaxWaves),
                ("sea_ClipA", WaterShader.MaxClips), ("sea_ClipB", WaterShader.MaxClips), ("sea_ClipC", WaterShader.MaxClips), ("sea_ClipD", WaterShader.MaxClips),
                ("sea_ClipP", WaterShader.MaxClips * WaterShader.ProfileStations / 4),
                ("sea_DryE", WaterShader.MaxDryShapes),
                ("sea_DryP", WaterShader.MaxDryShapes * WaterShader.DryStations * WaterShader.DryLevels / 4),
                ("sea_DryT", WaterShader.MaxDryShapes * WaterShader.DryTopStations / 4),
            };
            foreach (var (name, size) in sizes)
                check(all.Contains(name + "[" + size + "];"), $"injection: {name} is declared with {size} elements, as the uploads assume");
        }
    }
}
