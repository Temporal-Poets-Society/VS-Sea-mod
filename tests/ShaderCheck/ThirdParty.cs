using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using OpenTK.Graphics.OpenGL4;
using Sea;

namespace ShaderCheck
{
    /// <summary>
    /// (SE-0034) Shader mods that replace the game's water shader files whole (SheyderMod's chunkliquid.fsh, OptiTime's):
    /// Sea's code must still go in, every anchor once, and the result compile on this driver. Their files are read from
    /// where they are installed when the check runs and never copied into this repository: the zips or folders in
    /// SEA_SHADER_MODS (separated by ';'), else sheydermod and optitime zips in the game's Mods folder and the unpacked
    /// copies in the suite's devtools\vs-ref\mods\_unpacked. A mod that isn't on this machine is reported as not checked.
    /// </summary>
    static partial class Program
    {
        static readonly string[] ShaderMods = { "sheydermod", "optitime" };

        static List<string> ShaderModSources()
        {
            var found = new List<string>();
            string listed = Environment.GetEnvironmentVariable("SEA_SHADER_MODS");
            if (!string.IsNullOrWhiteSpace(listed))
            {
                foreach (string s in listed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) found.Add(s);
                return found;
            }
            string mods = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "VintagestoryData", "Mods");
            foreach (string id in ShaderMods)
                if (Directory.Exists(mods)) found.AddRange(Directory.GetFiles(mods, id + "*.zip"));
            // the suite's reference copies (tools/Update-VsRef.ps1 -Mod): walk up to the folder that holds devtools
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
            {
                string unpacked = Path.Combine(dir.FullName, "devtools", "vs-ref", "mods", "_unpacked");
                if (!Directory.Exists(unpacked)) continue;
                foreach (string id in ShaderMods) found.AddRange(Directory.GetDirectories(unpacked, id + "*"));
                break;
            }
            return found;
        }

        /// <summary>The mod's shader files by file name: its game/ files over the rest, as they replace the game's.</summary>
        static Dictionary<string, string> ShaderModFiles(string source)
        {
            var files = new Dictionary<string, string>();
            var game = new Dictionary<string, string>();
            void Add(string path, Func<string> read)
            {
                string p = path.Replace('\\', '/').ToLowerInvariant();
                if (!(p.EndsWith(".fsh") || p.EndsWith(".vsh") || p.EndsWith(".ash")) || !p.Contains("assets/")) return;
                (p.Contains("assets/game/") ? game : files)[Path.GetFileName(p)] = read();
            }
            if (Directory.Exists(source))
                foreach (string f in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) Add(f, () => File.ReadAllText(f));
            else
            {
                using var zip = ZipFile.OpenRead(source);
                foreach (var e in zip.Entries)
                    Add("/" + e.FullName, () => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); });
            }
            foreach (var kv in game) files[kv.Key] = kv.Value;
            return files;
        }

        static void ThirdPartyWater(string shaders, Dictionary<string, int> defs)
        {
            var sources = ShaderModSources();
            if (sources.Count == 0) { Console.WriteLine("  --   shader mods not checked (no SheyderMod or OptiTime found; SEA_SHADER_MODS names them)"); return; }
            foreach (string source in sources)
            {
                string name = Path.GetFileName(source.TrimEnd('\\', '/'));
                Dictionary<string, string> mod;
                try { mod = ShaderModFiles(source); }
                catch (Exception e) { Check(false, name + ": readable", e.Message); continue; }
                if (!mod.ContainsKey("chunkliquid.fsh") && !mod.ContainsKey("chunkliquid.vsh")) { Console.WriteLine("  --   " + name + ": replaces no water shader"); continue; }
                // the mod's files over the game's, for this mod only
                var saved = new Dictionary<string, string>(includes);
                try
                {
                    foreach (var kv in mod) includes[kv.Key] = kv.Value;
                    string v = Includes(mod.TryGetValue("chunkliquid.vsh", out string mv) ? mv : File.ReadAllText(Path.Combine(shaders, "chunkliquid.vsh")), new HashSet<string>());
                    string f = Includes(mod.TryGetValue("chunkliquid.fsh", out string mf) ? mf : File.ReadAllText(Path.Combine(shaders, "chunkliquid.fsh")), new HashSet<string>());
                    // as the game loads it (Hooks.LoadShaderProgramPostfix): waves, sky reflections, even flowing water, dry cut
                    string wv = WaterShader.Inject(v, out bool waves, true);
                    string cv = wv == null ? null : CelestialShader.Vertex(wv);
                    string cf = CelestialShader.Fragment(f);
                    string ef = cf == null ? null : FlowLook.Inject(cf);
                    string df = ef == null ? null : WaterShader.InjectFragment(ef);
                    Check(wv != null && waves, name + ": Sea's waves go into its water");
                    Check(cv != null && cf != null, name + ": Sea's sky reflections, glints and foam go in");
                    Check(ef != null, name + ": even flowing water goes in");
                    Check(df != null, name + ": the dry-hull cut goes in");
                    if (cv == null || df == null) continue;
                    Check(CelestialShader.Fragment(df) == null && WaterShader.InjectFragment(df) == null, name + ": each anchor is used once");
                    int prog = Build(name + ": Sea's water code in its chunkliquid", cv, df, defs);
                    if (prog == 0) continue;
                    var missing = new List<string>();
                    foreach (string u in new[] { "sea_Effects", "sea_Sun", "sea_Storm", "sea_FadeDistance", "sea_ClipCount", "sea_DryE", "sea_WaveCount" })
                        if (GL.GetUniformLocation(prog, u) < 0) missing.Add(u);
                    Check(missing.Count == 0, name + ": Sea's uniforms are live" + (missing.Count > 0 ? " (missing " + string.Join(", ", missing) + ")" : ""));
                    GL.DeleteProgram(prog);
                }
                finally
                {
                    includes.Clear();
                    foreach (var kv in saved) includes[kv.Key] = kv.Value;
                }
            }
        }
    }
}
